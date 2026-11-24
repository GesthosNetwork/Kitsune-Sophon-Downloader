using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using KitsuneSophonDownloader.Models;

namespace KitsuneSophonDownloader.Services;

internal static class SophonCategoryTranslationService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> PendingCacheEntries = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim CacheLoadLock = new(1, 1);
    private static readonly SemaphoreSlim CacheSaveLock = new(1, 1);
    private static readonly SemaphoreSlim TranslationSlots = new(4, 4);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static volatile bool _cacheLoaded;

    public static async Task TranslateOptionsAsync(
        IEnumerable<SophonContentOption> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await EnsureCacheLoadedAsync(cancellationToken).ConfigureAwait(false);

        List<IGrouping<string, SophonContentOption>> groups = options
            .Where(option => option is not null && ContainsChineseCharacters(option.OriginalName))
            .GroupBy(option => option.OriginalName, StringComparer.Ordinal)
            .ToList();

        if (groups.Count == 0)
            return;

        int translatedCount = 0;
        var tasks = groups.Select(async group =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = group.Key;

            if (!Cache.TryGetValue(source, out string? translated))
            {
                await TranslationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!Cache.TryGetValue(source, out translated))
                    {
                        translated = await TranslateTextAsync(source, cancellationToken)
                            .ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(translated) &&
                            !string.Equals(translated, source, StringComparison.Ordinal))
                        {
                            Cache[source] = translated;
                            PendingCacheEntries[source] = translated;
                        }
                    }
                }
                finally
                {
                    TranslationSlots.Release();
                }
            }

            if (!string.IsNullOrWhiteSpace(translated) &&
                !string.Equals(translated, source, StringComparison.Ordinal))
            {
                foreach (SophonContentOption option in group)
                    option.TranslatedName = translated;

                Interlocked.Add(ref translatedCount, group.Count());
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (translatedCount > 0)
        {
            await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
            Logger.Info($"Translated {translatedCount} Sophon category label(s) into English.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client;
        try
        {
            client = new HttpClient(NetworkClient.CreateHandler(AppSettingsStore.Load()));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to apply the configured proxy to the category translator. Using the system HTTP handler.");
            client = new HttpClient();
        }

        client.Timeout = TimeSpan.FromSeconds(6);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 KitsuneSophonDownloader/3.0.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cacheLoaded)
            return;

        await CacheLoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cacheLoaded)
                return;

            try
            {
                using var connection = new SqliteConnection($"Data Source={AppSettingsStore.DatabaseFilePath};Mode=ReadWriteCreate;Cache=Shared");
                connection.Open();

                using (var createCommand = connection.CreateCommand())
                {
                    createCommand.CommandText = """
                        CREATE TABLE IF NOT EXISTS sophon_category_translations
                        (
                            source_text TEXT PRIMARY KEY NOT NULL,
                            translated_text TEXT NOT NULL,
                            updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                        );
                        """;
                    createCommand.ExecuteNonQuery();
                }

                using var selectCommand = connection.CreateCommand();
                selectCommand.CommandText = "SELECT source_text, translated_text FROM sophon_category_translations;";
                using SqliteDataReader reader = selectCommand.ExecuteReader();

                while (reader.Read())
                {
                    string source = reader.GetString(0);
                    string translated = reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(translated))
                        Cache[source] = translated;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "The Sophon category translation table could not be loaded from sophon.db.");
            }

            _cacheLoaded = true;
        }
        finally
        {
            CacheLoadLock.Release();
        }
    }

    private static async Task<string?> TranslateTextAsync(string source, CancellationToken cancellationToken)
    {
        string url = "https://translate.googleapis.com/translate_a/single" + "?client=gtx&sl=zh-CN&tl=en&dt=t&q=" + Uri.EscapeDataString(source);

        try
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0 ||
                document.RootElement[0].ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (JsonElement segment in document.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array &&
                    segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                {
                    string? part = segment[0].GetString();
                    if (!string.IsNullOrEmpty(part))
                        parts.Add(part);
                }
            }

            string result = string.Concat(parts).Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Automatic English translation failed for Sophon category '{source}'. The original API name will be shown.");
            return null;
        }
    }

    private static async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        await CacheSaveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (PendingCacheEntries.IsEmpty)
                return;

            using var connection = new SqliteConnection($"Data Source={AppSettingsStore.DatabaseFilePath};Mode=ReadWriteCreate;Cache=Shared");
            connection.Open();
            using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText = """
                    CREATE TABLE IF NOT EXISTS sophon_category_translations
                    (
                        source_text TEXT PRIMARY KEY NOT NULL,
                        translated_text TEXT NOT NULL,
                        updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    """;
                createCommand.ExecuteNonQuery();
            }

            KeyValuePair<string, string>[] pendingSnapshot = PendingCacheEntries.ToArray();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sophon_category_translations (source_text, translated_text, updated_utc)
                VALUES ($source, $translated, CURRENT_TIMESTAMP)
                ON CONFLICT(source_text) DO UPDATE SET
                    translated_text = excluded.translated_text,
                    updated_utc = CURRENT_TIMESTAMP;
                """;

            foreach (KeyValuePair<string, string> entry in pendingSnapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$source", entry.Key);
                command.Parameters.AddWithValue("$translated", entry.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();

            ICollection<KeyValuePair<string, string>> pendingEntries = PendingCacheEntries;
            foreach (KeyValuePair<string, string> entry in pendingSnapshot)
                pendingEntries.Remove(entry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "The Sophon category translation cache could not be saved to sophon.db.");
        }
        finally
        {
            CacheSaveLock.Release();
        }
    }

    private static bool ContainsChineseCharacters(string value)
    {
        foreach (char character in value)
        {
            int codePoint = character;
            if ((codePoint >= 0x3400 && codePoint <= 0x4DBF) ||
                (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||
                (codePoint >= 0xF900 && codePoint <= 0xFAFF))
            {
                return true;
            }
        }

        return false;
    }
}
