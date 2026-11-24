using Microsoft.Data.Sqlite;
using KitsuneSophonDownloader.Models;

namespace KitsuneSophonDownloader.Services;

public sealed class LegacyManifestService
{
    private const string DataApiUrl = "https://gitlab.com/api/v4/projects/GesthosNetwork%2Fhoyo-files/repository/tree";
    private const string RawBaseUrl = "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailedRefreshCooldown = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const string ObsoleteJsonCachePath = "legacy-manifests-v1.json";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static CachedManifestSnapshot? _memoryCache;
    private static DateTimeOffset _lastOnlineRefreshAttemptUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan RefreshAttemptCooldown = TimeSpan.FromMinutes(2);

    public async Task<List<GameOption>> LoadGamesAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        RemoveObsoleteJsonCache();
        CachedManifestSnapshot? cached = await LoadCachedSnapshotAsync(cancellationToken);

        if (IsRefreshSuppressed(cached))
        {
            Logger.Info($"Using cached Legacy manifests because the previous online refresh failed. " + $"Next refresh attempt is allowed after {cached!.RefreshNotBeforeUtc:O}.");
            return cached.Games;
        }

        if (!forceRefresh && IsFresh(cached))
        {
            Logger.Info($"Using cached Legacy manifests: {cached!.Games.Count:N0} game(s), " + $"cached at {cached.CachedAtUtc:O}.");
            return cached.Games;
        }

        await LoadGate.WaitAsync(cancellationToken);
        try
        {
            cached = await LoadCachedSnapshotAsync(cancellationToken);
            if (IsRefreshSuppressed(cached))
            {
                Logger.Info($"Using cached Legacy manifests because the previous online refresh failed. " + $"Next refresh attempt is allowed after {cached!.RefreshNotBeforeUtc:O}.");
                return cached.Games;
            }

            if (!forceRefresh && IsFresh(cached))
            {
                Logger.Info($"Using cached Legacy manifests: {cached!.Games.Count:N0} game(s).");
                return cached.Games;
            }

            if (forceRefresh && cached is { Games.Count: > 0 } &&
                DateTimeOffset.UtcNow - _lastOnlineRefreshAttemptUtc < RefreshAttemptCooldown)
            {
                Logger.Info("Skipping Legacy manifest refresh because a recent online refresh was already attempted. Using the local snapshot.");
                return cached.Games;
            }

            _lastOnlineRefreshAttemptUtc = DateTimeOffset.UtcNow;

            try
            {
                List<GameOption> games = await LoadOnlineGamesAsync(cancellationToken, allowIndexFallback: cached is null);
                if (games.Count == 0)
                    throw new InvalidDataException("The online Legacy manifest catalogue contains no valid games.");

                var snapshot = new CachedManifestSnapshot
                {
                    SchemaVersion = 1,
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    Games = games
                };

                _memoryCache = snapshot;
                await SaveCachedSnapshotAsync(snapshot, cancellationToken);
                Logger.Info($"Online Legacy manifests loaded and cached: {games.Count:N0} game(s).");
                return games;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (cached is { Games.Count: > 0 })
            {
                cached.RefreshNotBeforeUtc = DateTimeOffset.UtcNow.Add(FailedRefreshCooldown);
                await SaveCachedSnapshotAsync(cached, CancellationToken.None);
                Logger.Warn(ex,
                    $"Online Legacy manifest refresh failed. Falling back to the last valid local snapshot " +
                    $"({cached.Games.Count:N0} game(s), cached at {cached.CachedAtUtc:O}). " +
                    $"Another online attempt will be suppressed until {cached.RefreshNotBeforeUtc:O}.");
                return cached.Games;
            }
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KitsuneSophonDownloader/3.0.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static bool IsFresh(CachedManifestSnapshot? snapshot) =>
        snapshot is { SchemaVersion: 1, Games.Count: > 0 } &&
        DateTimeOffset.UtcNow - snapshot.CachedAtUtc < CacheLifetime;

    private static bool IsRefreshSuppressed(CachedManifestSnapshot? snapshot) =>
        snapshot is { Games.Count: > 0 } &&
        snapshot.RefreshNotBeforeUtc > DateTimeOffset.UtcNow;

    private static Task<CachedManifestSnapshot?> LoadCachedSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_memoryCache is not null)
            return Task.FromResult<CachedManifestSnapshot?>(_memoryCache);

        try
        {
            using var connection = OpenDatabase();
            EnsureCacheTable(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT cached_at_utc, refresh_not_before_utc, payload_json
                FROM legacy_manifest_cache
                WHERE cache_id = 1;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<CachedManifestSnapshot?>(null);

            string cachedAtText = reader.GetString(0);
            string refreshNotBeforeText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string payloadJson = reader.GetString(2);

            List<GameOption>? games = JsonSerializer.Deserialize<List<GameOption>>(
                payloadJson, CacheJsonOptions);
            if (games is null || games.Count == 0 ||
                !DateTimeOffset.TryParse(cachedAtText, out DateTimeOffset cachedAtUtc))
            {
                Logger.Warn("The Legacy manifest row in sophon.db is invalid and will be refreshed.");
                return Task.FromResult<CachedManifestSnapshot?>(null);
            }

            DateTimeOffset refreshNotBeforeUtc =
                DateTimeOffset.TryParse(refreshNotBeforeText, out DateTimeOffset parsedRefresh)
                    ? parsedRefresh
                    : DateTimeOffset.MinValue;

            var snapshot = new CachedManifestSnapshot
            {
                SchemaVersion = 1,
                CachedAtUtc = cachedAtUtc,
                RefreshNotBeforeUtc = refreshNotBeforeUtc,
                Games = games
            };

            _memoryCache = snapshot;
            Logger.Debug($"Loaded Legacy manifest snapshot from sophon.db: {games.Count:N0} game(s).");
            return Task.FromResult<CachedManifestSnapshot?>(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to read the Legacy manifest snapshot from sophon.db. An online refresh will be attempted.");
            return Task.FromResult<CachedManifestSnapshot?>(null);
        }
    }

    private static Task SaveCachedSnapshotAsync(
        CachedManifestSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = OpenDatabase();
            EnsureCacheTable(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO legacy_manifest_cache
                    (cache_id, cached_at_utc, refresh_not_before_utc, payload_json)
                VALUES
                    (1, $cached_at, $refresh_not_before, $payload)
                ON CONFLICT(cache_id) DO UPDATE SET
                    cached_at_utc = excluded.cached_at_utc,
                    refresh_not_before_utc = excluded.refresh_not_before_utc,
                    payload_json = excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$cached_at", snapshot.CachedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$refresh_not_before",
                snapshot.RefreshNotBeforeUtc == DateTimeOffset.MinValue
                    ? string.Empty
                    : snapshot.RefreshNotBeforeUtc.ToString("O"));
            command.Parameters.AddWithValue("$payload",
                JsonSerializer.Serialize(snapshot.Games, CacheJsonOptions));
            command.ExecuteNonQuery();
            transaction.Commit();

            _memoryCache = snapshot;
            Logger.Debug($"Updated the single Legacy manifest snapshot row in {AppSettingsStore.DatabaseFilePath}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to save the Legacy manifest snapshot in sophon.db. The current session can still use the loaded manifests.");
        }

        return Task.CompletedTask;
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection($"Data Source={AppSettingsStore.DatabaseFilePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static void EnsureCacheTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS legacy_manifest_cache
            (
                cache_id INTEGER PRIMARY KEY CHECK (cache_id = 1),
                cached_at_utc TEXT NOT NULL,
                refresh_not_before_utc TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void RemoveObsoleteJsonCache()
    {
        try
        {
            string oldCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GesthosNetwork", "SophonDownloader", "Cache", ObsoleteJsonCachePath);
            if (File.Exists(oldCachePath))
            {
                File.Delete(oldCachePath);
                Logger.Info("Removed the obsolete standalone Legacy JSON cache; the snapshot is now stored in sophon.db.");
            }

            string oldTemporaryPath = oldCachePath + ".tmp";
            if (File.Exists(oldTemporaryPath))
                File.Delete(oldTemporaryPath);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to remove the obsolete standalone Legacy JSON cache.");
        }
    }

    private static async Task<List<GameOption>> LoadOnlineGamesAsync(
        CancellationToken cancellationToken,
        bool allowIndexFallback)
    {
        Logger.Info("Refreshing online Legacy manifest index.");
        List<string> manifestFiles = await LoadManifestFilesAsync(cancellationToken, allowIndexFallback);
        Logger.Info($"Legacy manifest file list resolved: {manifestFiles.Count:N0} JSON file(s).");

        if (manifestFiles.Count == 0)
            throw new InvalidDataException("The Legacy manifest file list contains no JSON files.");

        var games = new List<GameOption>();
        int completedFiles = 0;

        foreach (string fileName in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (completedFiles > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

            string url = $"{RawBaseUrl}/{fileName}";
            Logger.Info($"Loading online Legacy manifest: {fileName} <- {url}");

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    string retryAfter = response.Headers.RetryAfter?.ToString() ?? "not provided";
                    throw new HttpRequestException(
                        $"GitLab raw manifest request was rate limited (HTTP 429). Retry-After: {retryAfter}.",
                        null, response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("_metadata", out JsonElement metadata))
                    throw new InvalidDataException($"Legacy manifest '{fileName}' does not contain '_metadata'.");

                string gameId = metadata.GetProperty("gameId").GetString()
                    ?? throw new InvalidDataException($"Legacy manifest '{fileName}' has an invalid 'gameId'.");
                string name = metadata.GetProperty("name").GetString()
                    ?? throw new InvalidDataException($"Legacy manifest '{fileName}' has an invalid 'name'.");

                LegacyManifest? manifest = JsonSerializer.Deserialize<LegacyManifest>(json);
                if (manifest is null || manifest.Count == 0)
                    throw new InvalidDataException($"Legacy manifest for {name} is empty.");

                games.Add(new GameOption
                {
                    Code = gameId,
                    Name = name,
                    Manifest = manifest
                });

                completedFiles++;
                Logger.Info($"Online Legacy manifest loaded: {name}, versions={manifest.Count:N0}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load online Legacy manifest: {fileName} <- {url}");
                throw new InvalidOperationException(
                    $"Unable to load the online manifest '{fileName}' from '{url}'.", ex);
            }
        }

        return games;
    }

    private static async Task<List<string>> LoadManifestFilesAsync(
        CancellationToken cancellationToken,
        bool allowFallback)
    {
        string url = $"{DataApiUrl}?path=data&ref=main&per_page=100";

        try
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                string retryAfter = response.Headers.RetryAfter?.ToString() ?? "not provided";
                string message = $"GitLab manifest index returned HTTP 429 Too Many Requests. Retry-After: {retryAfter}.";
                if (!allowFallback)
                    throw new HttpRequestException(message, null, response.StatusCode);

                Logger.Warn(message + " No local snapshot exists, so the known manifest file list will be used.");
                return GetFallbackManifestFiles();
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);

            List<string> files = document.RootElement
                .EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "blob" &&
                            x.GetProperty("name").GetString()?
                            .EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true)
                .Select(x => x.GetProperty("name").GetString()!)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count > 0)
                return files;

            if (allowFallback)
            {
                Logger.Warn("GitLab returned an empty Legacy manifest index. Using the known manifest file list.");
                return GetFallbackManifestFiles();
            }

            throw new InvalidDataException("GitLab returned an empty Legacy manifest index while a local snapshot is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (
            !allowFallback && ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!allowFallback)
                throw;

            Logger.Warn(ex, "Unable to retrieve the GitLab Legacy manifest index. No local snapshot exists, so the known manifest file list will be used.");
            return GetFallbackManifestFiles();
        }
    }

    private static List<string> GetFallbackManifestFiles() =>
        Enumerable.Range(1, 12)
            .Select(index => $"{index:D2}.json")
            .ToList();

    private sealed class CachedManifestSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset CachedAtUtc { get; set; }
        public DateTimeOffset RefreshNotBeforeUtc { get; set; }
        public List<GameOption> Games { get; set; } = [];
    }
}
