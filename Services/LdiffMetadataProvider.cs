using KitsuneSophonDownloader.Models;

namespace KitsuneSophonDownloader.Services;

public interface ILdiffMetadataProvider
{
    Task<LdiffSource?> ResolveAsync(
        GameInfo game,
        string targetVersion,
        string matchingField,
        string categoryId,
        string fromVersion,
        CancellationToken cancellationToken = default);
}

public sealed record LdiffSource(
    string DiffChunkUrlPrefix,
    string DiffManifestUrl,
    string? Language);

public sealed class LdiffMetadataProvider : ILdiffMetadataProvider
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<LdiffSource?> ResolveAsync(
        GameInfo game,
        string targetVersion,
        string matchingField,
        string categoryId,
        string fromVersion,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument repository =
            await LoadRepositoryManifestAsync(game.GameId, cancellationToken);

        JsonElement versionEntry = repository.RootElement
            .GetProperty("game_versions")
            .EnumerateArray()
            .FirstOrDefault(x =>
                string.Equals(
                    x.GetProperty("metadata").GetProperty("version").GetString(),
                    targetVersion, StringComparison.OrdinalIgnoreCase));

        if (versionEntry.ValueKind == JsonValueKind.Undefined)
            return null;

        string? categoryUrlKey = ResolveUrlKey(matchingField, versionEntry);
        if (string.IsNullOrWhiteSpace(categoryUrlKey))
            return null;

        JsonElement diffListUrls = versionEntry
            .GetProperty("metadata")
            .GetProperty("diff_list_url");

        if (!diffListUrls.TryGetProperty(
                categoryUrlKey,
                out JsonElement listUrlElement))
            return null;

        string? diffListUrl = listUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(diffListUrl))
            return null;

        JsonElement diffContainer =
            ResolveDiffContainer(versionEntry, categoryUrlKey);

        string? requestedLanguage = ResolveLanguage(matchingField);
        List<JsonElement> candidates = diffContainer
            .GetProperty("diff")
            .EnumerateArray()
            .Where(x => MatchesVersionAndLanguage(x, fromVersion, requestedLanguage))
            .ToList();

        if (candidates.Count == 0)
            return null;

        JsonElement diffEntry = candidates.FirstOrDefault(x =>
            HasCategoryPath(x, categoryId));

        if (diffEntry.ValueKind == JsonValueKind.Undefined)
        {
            if (candidates.Count != 1)
                return null;

            diffEntry = candidates[0];
        }

        if (!diffEntry.TryGetProperty("file_url", out JsonElement manifestUrlElement))
            return null;

        string? diffManifestUrl = manifestUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(diffManifestUrl))
            return null;

        string? chunkUrlPrefix = TryGetString(diffEntry, "file_path");
        if (string.IsNullOrWhiteSpace(chunkUrlPrefix))
            chunkUrlPrefix = diffListUrl;

        if (string.IsNullOrWhiteSpace(chunkUrlPrefix) ||
            !Uri.TryCreate(chunkUrlPrefix, UriKind.Absolute, out Uri? chunkUri) ||
            (chunkUri.Scheme != Uri.UriSchemeHttp && chunkUri.Scheme != Uri.UriSchemeHttps))
            return null;

        return new LdiffSource(
            EnsureTrailingSlash(chunkUrlPrefix), diffManifestUrl, requestedLanguage);
    }

    private static bool MatchesVersionAndLanguage(
        JsonElement diffEntry, string fromVersion, string? requestedLanguage)
    {
        if (!string.Equals(
                TryGetString(diffEntry, "original_version"),
                fromVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        string? entryLanguage = TryGetString(diffEntry, "language");
        return requestedLanguage is null
            ? string.IsNullOrWhiteSpace(entryLanguage)
            : string.Equals(entryLanguage, requestedLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCategoryPath(JsonElement diffEntry, string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
            return false;

        string? filePath = TryGetString(diffEntry, "file_path");
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string path = filePath.TrimEnd('/', '\\');
        string leaf = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        return string.Equals(leaf, categoryId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static async Task<JsonDocument> LoadRepositoryManifestAsync(
        string gameId, CancellationToken cancellationToken)
    {
        string url = $"https://gitlab.com/GesthosNetwork/game-manifests/-/raw/main/{gameId}.json";
        return await LoadJsonAsync(url, cancellationToken);
    }

    private static string? ResolveUrlKey(
        string matchingField, JsonElement versionEntry)
    {
        string normalized = matchingField.Trim().ToLowerInvariant();

        if (normalized is "game" or "en-us" or "zh-cn" or "ja-jp" or "ko-kr")
            return normalized.Replace('-', '_');

        if (normalized.Contains("en"))
            return "en_us";

        if (normalized.Contains("zh"))
            return "zh_cn";

        if (normalized.Contains("ja"))
            return "ja_jp";

        if (normalized.Contains("ko"))
            return "ko_kr";

        if (normalized.Contains("game"))
            return "game";

        if (versionEntry
            .GetProperty("metadata")
            .GetProperty("diff_list_url")
            .TryGetProperty(normalized, out _))
            return normalized;

        return null;
    }

    private static JsonElement ResolveDiffContainer(
        JsonElement versionEntry,
        string categoryKey)
    {
        if (categoryKey == "game")
            return versionEntry.GetProperty("game");

        return versionEntry.GetProperty("audio");
    }

    private static string? ResolveLanguage(string matchingField)
    {
        string normalized = matchingField.Trim().ToLowerInvariant().Replace('_', '-');

        if (normalized.Contains("en-us", StringComparison.Ordinal)) return "en-us";
        if (normalized.Contains("zh-cn", StringComparison.Ordinal)) return "zh-cn";
        if (normalized.Contains("ja-jp", StringComparison.Ordinal)) return "ja-jp";
        if (normalized.Contains("ko-kr", StringComparison.Ordinal)) return "ko-kr";

        return null;
    }

    private static async Task<JsonDocument> LoadJsonAsync(
        string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return JsonDocument.Parse(bytes);
    }
}
