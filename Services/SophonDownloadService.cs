using ProtoBuf;
using KitsuneSophonDownloader;
using KitsuneSophonDownloader.Core;
using KitsuneSophonDownloader.Models;
using KitsuneSophonDownloader.Utilities;
using ZstdSharp;

namespace KitsuneSophonDownloader.Services;

public sealed class SophonDownloadService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly SophonCoreDownloader _downloader = new();
    private readonly ILdiffMetadataProvider _ldiffMetadataProvider = new LdiffMetadataProvider();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private ManifestConfig? _currentManifest;
    private SophonContentSet? _currentContent;
    private string? _currentSaveDirectory;

    private bool _disposed;
    private string _logJobId = "n/a";
    private string _logJobTitle = "n/a";

    public void SetLogContext(string jobId, string jobTitle)
    {
        _logJobId = string.IsNullOrWhiteSpace(jobId) ? "n/a" : jobId;
        _logJobTitle = string.IsNullOrWhiteSpace(jobTitle) ? "n/a" : jobTitle;
    }

    private string JobContext => $"Job={_logJobId}; Title=\"{_logJobTitle}\"";
    private bool _chunkDownloadCancelledPending;
    private bool _chunkDownloadRunning;

    public ManifestConfig? CurrentManifest => _currentManifest;
    public SophonContentSet? CurrentContent => _currentContent;
    public string? CurrentSaveDirectory => _currentSaveDirectory;
    public bool IsPaused => _downloader.IsPaused;

    public Action<ChunkDownloadProgress>? ChunkProgressCallback { get; set; }
    public Action? DownloadCancelledCallback { get; set; }

    public SophonDownloadService()
    {
        ConfigureCallbacks();
    }

    public async Task<ManifestConfig> LoadManifestAsync(
        GameInfo game, string version, string branch = "main",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        BranchesGameBranch branchInfo =
            await SophonGameService.GetGameBranches(game.GameId, game.Region, "SophonDownloadService.LoadManifestAsync");

        BranchesMain? branchData =
            branch.Equals("predownload", StringComparison.OrdinalIgnoreCase)
                ? branchInfo.pre_download
                : branchInfo.main;

        if (branchData is null)
            throw new InvalidOperationException($"Branch '{branch}' is not available.");

        bool useLatestPreDownloadTag =
            branch.Equals("predownload", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(version, branchData.tag, StringComparison.OrdinalIgnoreCase);

        string url = SophonGameService.BuildGetBuildUrl(
            game.GameId, game.Region, branchData.package_id, branchData.password,
            useLatestPreDownloadTag ? null : version, branch);

        Logger.Info($"Loading Sophon build manifest. {JobContext}; URL={url}");

        using HttpResponseMessage response =
            await HttpClient.GetAsync(url, cancellationToken);

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        ManifestConfig? config = JsonSerializer.Deserialize<ManifestConfig>(json);

        if (config is null)
            throw new InvalidDataException("Sophon manifest could not be parsed.");

        if (config.retcode != 0)
            throw new InvalidOperationException($"Sophon manifest error: {config.message}");

        if (string.IsNullOrWhiteSpace(config.data.tag))
            throw new InvalidDataException("Sophon manifest does not contain a version tag.");

        _currentManifest = config;
        Logger.Info($"Sophon build manifest loaded. {JobContext}; Tag={config.data.tag}");

        return config;
    }

    public IReadOnlyList<SophonContentOption> BuildContentOptions(ManifestConfig manifest)
    {
        ThrowIfDisposed();

        return manifest.data.manifests
            .Select(category => new SophonContentOption(category) { IsSelected = false }).ToList();
    }

    public async Task<SophonContentSet> LoadSelectedPatchContentAsync(
        GameInfo game,
        ManifestConfig fromManifest,
        ManifestConfig toManifest,
        IEnumerable<SophonContentOption> selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        List<SophonContentOption> selectedList =
            selected.Where(option => option.IsSelected).ToList();

        if (selectedList.Count == 0)
            throw new InvalidOperationException("No Sophon content was selected.");

        var allFiles = new List<SophonChunkFile>();
        var fileManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<SophonManifestArtifact>();
        var contentFilePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var loadedContent = new List<SophonContentOption>();
        var unavailableCategories = new List<string>();

        foreach (SophonContentOption option in selectedList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string matchingField = option.Category.matching_field;
            if (string.IsNullOrWhiteSpace(matchingField))
                throw new InvalidOperationException($"Sophon category '{option.Category.category_id}' does not provide a matching field.");

            LdiffSource? source = await _ldiffMetadataProvider.ResolveAsync(
                game, toManifest.data.tag, matchingField, option.Category.category_id,
                fromManifest.data.tag, cancellationToken);

            var categoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int patchArtifactCount = 0;

            if (source is not null)
            {
                Logger.Info(
                    $"Loading LDiff manifest. Game={game.GameId}, Package={matchingField}, " +
                    $"Category={option.Category.category_id}, From={fromManifest.data.tag}, " +
                    $"To={toManifest.data.tag}, URL={source.DiffManifestUrl}");

                byte[] diffManifestBytes = await DownloadBytesAsync(source.DiffManifestUrl, cancellationToken);
                manifestFiles.Add(new SophonManifestArtifact
                {
                    FileName = GetUrlFileName(source.DiffManifestUrl),
                    SourceUrl = source.DiffManifestUrl,
                    Data = diffManifestBytes
                });

                SophonPatchManifest patchManifest = ParsePatchManifest(diffManifestBytes);

                foreach (SophonPatchFile patchFile in patchManifest.Patches)
                {
                    SophonPatchInfo? patchInfo = patchFile.Patches.FirstOrDefault(x =>
                        string.Equals(x.Tag, fromManifest.data.tag, StringComparison.OrdinalIgnoreCase));

                    if (patchInfo?.Patch is null || string.IsNullOrWhiteSpace(patchInfo.Patch.Id))
                        continue;

                    SophonPatch patch = patchInfo.Patch;
                    string outputFile = BuildPatchArtifactPath(patchFile.File, patch.OriginalFileName);
                    categoryPaths.Add(outputFile);

                    var artifact = new SophonChunkFile
                    {
                        File = outputFile,
                        IsFolder = false,
                        Size = patch.PatchFileSize,
                        Md5 = patch.PatchFileMd5
                    };

                    artifact.Chunks.Add(new SophonChunk
                    {
                        Id = patch.Id,
                        CompressedSize = patch.PatchFileSize,
                        CompressedMd5 = patch.PatchFileMd5 ?? string.Empty,
                        UncompressedSize = patch.PatchFileSize,
                        UncompressedMd5 = patch.PatchFileMd5 ?? string.Empty,
                        Offset = patch.PatchOffset
                    });

                    allFiles.Add(artifact);
                    fileManifest[artifact.File] = source.DiffChunkUrlPrefix;
                    patchArtifactCount++;
                }
            }
            else
            {
                Logger.Warn(
                    $"No category-specific LDiff manifest was found; checking the target manifest " +
                    $"for full-file additions. Game={game.GameId}; Category={option.Category.category_id}; " +
                    $"MatchingField={matchingField}; From={fromManifest.data.tag}; To={toManifest.data.tag}");
            }

            ManifestCategory targetCategory = FindManifestCategory(
                toManifest, option.Category.category_id, matchingField);
            ManifestCategory? sourceCategory = TryFindManifestCategory(
                fromManifest, option.Category.category_id, matchingField);

            HashSet<string> sourcePaths = new(StringComparer.OrdinalIgnoreCase);
            int sourceFileCount = 0;
            string sourceUrl = string.Empty;

            if (sourceCategory is not null)
            {
                var sourceResult = await DownloadChunkManifestAsync(sourceCategory, cancellationToken);
                SophonChunkManifest sourceChunkManifest = sourceResult.Manifest;
                sourceUrl = sourceResult.Url;
                sourceFileCount = sourceChunkManifest.Chuncks.Count;
                sourcePaths = sourceChunkManifest.Chuncks
                    .Where(file => !file.IsFolder && !string.IsNullOrWhiteSpace(file.File))
                    .Select(file => NormalizeSophonPath(file.File))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            SophonChunkManifest targetChunkManifest =
                (await DownloadChunkManifestAsync(targetCategory, cancellationToken)).Manifest;

            var addedFiles = targetChunkManifest.Chuncks
                .Where(file => !file.IsFolder && !string.IsNullOrWhiteSpace(file.File))
                .Where(file => !sourcePaths.Contains(NormalizeSophonPath(file.File)))
                .ToList();

            string targetChunkPrefix = Utility.EnsureTrailingSlash(targetCategory.chunk_download.url_prefix);
            int addedFileCount = 0;
            foreach (SophonChunkFile targetFile in addedFiles)
            {
                string normalizedPath = NormalizeSophonPath(targetFile.File);
                if (categoryPaths.Any(path => string.Equals(
                        NormalizeSophonPath(path), normalizedPath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                categoryPaths.Add(targetFile.File);
                if (fileManifest.ContainsKey(targetFile.File))
                    continue;

                allFiles.Add(targetFile);
                fileManifest[targetFile.File] = targetChunkPrefix;
                addedFileCount++;
            }

            if (categoryPaths.Count == 0)
            {
                unavailableCategories.Add($"{option.Category.category_id} ({matchingField}; no changed or added files)");
                Logger.Warn(
                    $"No patch files are applicable to this category. Game={game.GameId}; " +
                    $"Category={option.Category.category_id}; From={fromManifest.data.tag}; " +
                    $"To={toManifest.data.tag}");
                continue;
            }

            loadedContent.Add(option);
            contentFilePaths[option.Category.category_id] = categoryPaths.ToList();

            Logger.Info(
                $"LDiff category resolved. Game={game.GameId}; Category={option.Category.category_id}; " +
                $"SourceFiles={sourceFileCount:N0}; TargetFiles={targetChunkManifest.Chuncks.Count:N0}; " +
                $"HdiffArtifacts={patchArtifactCount:N0}; AddedFullFiles={addedFileCount:N0}; " +
                $"ExploreFiles={categoryPaths.Count:N0}; SourceManifest={sourceUrl}");
        }

        if (allFiles.Count == 0)
        {
            string details = unavailableCategories.Count == 0
                ? string.Empty
                : " Unavailable categories: " + string.Join(", ", unavailableCategories);
            throw new InvalidOperationException(
                $"No applicable LDiff artifacts or added files were found for " +
                $"{fromManifest.data.tag} → {toManifest.data.tag}.{details}");
        }

        var content = new SophonContentSet
        {
            AllFiles = allFiles,
            FileManifest = fileManifest,
            SelectedContent = loadedContent,
            ManifestFiles = manifestFiles,
            ContentFilePaths = contentFilePaths,
            IsPatch = true,
            IsLdiffPatch = true,
            PatchFromVersion = fromManifest.data.tag,
            PatchToVersion = toManifest.data.tag
        };

        _currentContent = content;
        Logger.Info(
            $"Sophon LDiff patch loaded. From={content.PatchFromVersion}, To={content.PatchToVersion}, " +
            $"Files={content.FileCount:N0}, Artifacts={content.UniqueChunkCount:N0}, " +
            $"Size={Utility.FormatFileSize(content.UniqueCompressedSize)}");
        return content;
    }

    private static ManifestCategory FindManifestCategory(
        ManifestConfig manifest, string categoryId, string matchingField) =>
        TryFindManifestCategory(manifest, categoryId, matchingField)
        ?? throw new InvalidDataException(
            $"Manifest version '{manifest.data.tag}' does not contain category " +
            $"'{categoryId}' (matching field '{matchingField}').");

    private static ManifestCategory? TryFindManifestCategory(
        ManifestConfig manifest, string categoryId, string matchingField)
    {
        ManifestCategory? exact = manifest.data.manifests.FirstOrDefault(item =>
            string.Equals(item.category_id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        List<ManifestCategory> matching = manifest.data.manifests
            .Where(item => string.Equals(item.matching_field, matchingField, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matching.Count == 1 ? matching[0] : null;
    }

    private static async Task<(SophonChunkManifest Manifest, string Url)>
        DownloadChunkManifestAsync(ManifestCategory category, CancellationToken cancellationToken)
    {
        string url = Utility.EnsureTrailingSlash(category.manifest_download.url_prefix) + category.manifest.id;
        byte[] compressed = await DownloadBytesAsync(url, cancellationToken);
        byte[] decompressed = DecompressZstd(compressed);
        using MemoryStream stream = new(decompressed);
        SophonChunkManifest manifest = Serializer.Deserialize<SophonChunkManifest>(stream);
        return (manifest, url);
    }

    private static SophonPatchManifest ParsePatchManifest(byte[] manifestBytes)
    {
        byte[] payload = IsZstdFrame(manifestBytes)
            ? DecompressZstd(manifestBytes)
            : manifestBytes;

        using MemoryStream stream = new(payload);
        return Serializer.Deserialize<SophonPatchManifest>(stream);
    }

    private static bool IsZstdFrame(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0x28 && bytes[1] == 0xB5 &&
        bytes[2] == 0x2F && bytes[3] == 0xFD;

    private static string NormalizeSophonPath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static byte[] DecompressZstd(byte[] compressed)
    {
        using Decompressor decompressor = new();
        return decompressor.Unwrap(compressed).ToArray();
    }

    private static string GetUrlFileName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        throw new InvalidDataException($"Unable to determine the original manifest filename from URL: {url}");
    }

    private static string BuildPatchArtifactPath(string file, string? originalFileName)
    {
        string safe = file.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(originalFileName) ? safe : safe + ".hdiff";
    }

    public async Task<SophonContentSet> LoadSelectedContentAsync(
        ManifestConfig manifest, IEnumerable<SophonContentOption> selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        List<SophonContentOption> selectedList =
            selected.Where(option => option.IsSelected).ToList();

        if (selectedList.Count == 0)
            throw new InvalidOperationException("No Sophon content was selected.");

        var allFiles = new List<SophonChunkFile>();
        var fileManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestFiles = new List<SophonManifestArtifact>();
        var contentFilePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonContentOption option in selectedList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ManifestCategory category = option.Category;
            string manifestPrefix = Utility.EnsureTrailingSlash(category.manifest_download.url_prefix);
            string manifestId = category.manifest.id;
            string chunkPrefix = Utility.EnsureTrailingSlash(category.chunk_download.url_prefix);
            string manifestUrl = manifestPrefix + manifestId;

            Logger.Info($"Downloading Sophon file manifest: {manifestUrl}");

            using HttpResponseMessage response = await HttpClient.GetAsync(manifestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] compressedManifest = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            manifestFiles.Add(new SophonManifestArtifact
            {
                FileName = GetUrlFileName(manifestUrl),
                SourceUrl = manifestUrl,
                Data = compressedManifest
            });

            using Decompressor decompressor = new();
            byte[] decompressedManifest = decompressor.Unwrap(compressedManifest).ToArray();
            SophonChunkManifest parsedManifest;

            using MemoryStream stream = new(decompressedManifest);
            parsedManifest = Serializer.Deserialize<SophonChunkManifest>(stream);

            var categoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SophonChunkFile file in parsedManifest.Chuncks)
            {
                categoryPaths.Add(file.File);
                if (fileManifest.ContainsKey(file.File)) continue;
                allFiles.Add(file);
                fileManifest[file.File] = chunkPrefix;
            }

            contentFilePaths[category.category_id] = categoryPaths.ToList();
        }

        var content = new SophonContentSet
        {
            AllFiles = allFiles,
            FileManifest = fileManifest,
            SelectedContent = selectedList,
            ManifestFiles = manifestFiles,
            ContentFilePaths = contentFilePaths
        };

        _currentContent = content;

        Logger.Info($"Sophon content loaded. Files={content.FileCount:N0}, Chunks={content.UniqueChunkCount:N0}, Manifests={content.ManifestFiles.Count:N0}");

        return content;
    }

    public async Task StartChunkDownloadAsync(SophonContentSet content, string saveDirectory, int maxConcurrency = 0)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        await _operationLock.WaitAsync();

        try
        {
            if (_chunkDownloadRunning)
                throw new InvalidOperationException("A Sophon chunk download is already running.");

            Directory.CreateDirectory(saveDirectory);

            _currentContent = content;
            _currentSaveDirectory = Path.GetFullPath(saveDirectory);
            _chunkDownloadCancelledPending = false;
            _chunkDownloadRunning = true;

            AppSettings settings = AppSettingsStore.Load();
            int configuredConcurrency = maxConcurrency > 0 ? maxConcurrency : settings.Threads;
            int concurrency = string.Equals(settings.DownloadMode, "Sequential", StringComparison.OrdinalIgnoreCase)
                ? 1
                : Math.Clamp(configuredConcurrency, 1, 64);

            Logger.Info($"Starting Sophon chunk download. " + $"Destination={_currentSaveDirectory}, " + $"Concurrency={concurrency}");

            string[] files = content.AllFiles
                .Where(file => !file.IsFolder && !string.IsNullOrWhiteSpace(file.File))
                .Select(file => file.File)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length > 0)
            {
                const int previewCount = 8;
                string preview = string.Join(", ", files.Take(previewCount));
                if (files.Length > previewCount)
                    preview += $", ... (+{files.Length - previewCount:N0} more)";

                Logger.Info($"Sophon files queued: {files.Length:N0}. Files={preview}");
            }

            try
            {
                SaveManifestFiles(content, _currentSaveDirectory);
                await _downloader.StartDownload(content.AllFiles, content.FileManifest, _currentSaveDirectory, concurrency);
            }
            catch
            {
                _chunkDownloadRunning = false;
                throw;
            }

            _chunkDownloadRunning = false;

            if (_chunkDownloadCancelledPending)
            {
                _chunkDownloadCancelledPending = false;
                Logger.Info("Sophon chunk download fully cancelled. " + "Dispatching cancellation callback.");
                DownloadCancelledCallback?.Invoke();
            }
        }
        finally
        {
            _chunkDownloadRunning = false;
            _operationLock.Release();
        }
    }

    private static void SaveManifestFiles(SophonContentSet content, string saveDirectory)
    {
        if (content.ManifestFiles.Count == 0)
        {
            Logger.Warn("No companion Sophon manifests were attached to this download.");
            return;
        }

        foreach (SophonManifestArtifact manifest in content.ManifestFiles)
        {
            if (string.IsNullOrWhiteSpace(manifest.FileName))
                throw new InvalidDataException("A companion manifest has an empty file name.");

            string fileName = Path.GetFileName(manifest.FileName);
            if (!string.Equals(fileName, manifest.FileName, StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid companion manifest file name: {manifest.FileName}");

            string destination = Path.Combine(saveDirectory, fileName);
            string temporary = destination + $".{Guid.NewGuid():N}.tmp";

            if (File.Exists(destination))
            {
                try
                {
                    if (new FileInfo(destination).Length == manifest.Data.LongLength &&
                        File.ReadAllBytes(destination).AsSpan().SequenceEqual(manifest.Data))
                    {
                        Logger.Debug($"Companion manifest already exists: {destination}");
                        continue;
                    }
                }
                catch (IOException) {}
            }

            try
            {
                File.WriteAllBytes(temporary, manifest.Data);
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException ex) { Logger.Warn(ex, $"Unable to remove temporary manifest file: {temporary}"); }
                }
            }

            Logger.Info($"Companion manifest saved. File={destination}; Size={Utility.FormatFileSize(manifest.Data.LongLength)}; Source={manifest.SourceUrl}");
        }
    }

    public void TogglePause()
    {
        ThrowIfDisposed();
        _downloader.TogglePause();
    }

    public void Cancel()
    {
        if (_disposed) return;
        Logger.Info($"Sophon cancellation requested. {JobContext}");
        _downloader.CancelDownload();
    }

    private void ConfigureCallbacks()
    {
        _downloader.ChunkProgressUpdateCallback =
            progress => ChunkProgressCallback?.Invoke(progress);

        _downloader.DownloadCancelledCallback =
            () => _chunkDownloadCancelledPending = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SophonDownloadService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Logger.Info($"Disposing SophonDownloadService. {JobContext}");

        try
        {
            _downloader.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to dispose Sophon downloader.");
        }

        _operationLock.Dispose();
    }
}
