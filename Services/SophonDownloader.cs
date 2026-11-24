using KitsuneSophonDownloader;
using KitsuneSophonDownloader.Core;
using KitsuneSophonDownloader.Models;

namespace KitsuneSophonDownloader.Services;

public sealed class SophonCoreDownloader : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private ChunkDownloader? _chunkDownloader;
    private bool _disposed;

    public Action<ChunkDownloadProgress>? ChunkProgressUpdateCallback { get; set; }
    public Action<string>? StatusTextCallback { get; set; }
    public Action? DownloadCancelledCallback { get; set; }

    public bool IsPaused => _chunkDownloader?.IsPaused ?? false;

    public async Task StartDownload(
        List<SophonChunkFile> allFiles,
        Dictionary<string, string> fileManifest,
        string saveDirectory,
        int maxConcurrency = 16)
    {
        ThrowIfDisposed();
        DisposeChunkDownloader();

        _chunkDownloader = new ChunkDownloader
        {
            ProgressUpdateCallback = progress => ChunkProgressUpdateCallback?.Invoke(progress),
            StatusTextCallback = text => StatusTextCallback?.Invoke(text),
            DownloadCancelledCallback = () => DownloadCancelledCallback?.Invoke()
        };

        try
        {
            await _chunkDownloader.StartDownload(allFiles, fileManifest, saveDirectory, maxConcurrency);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Chunk download failed.");
            throw;
        }
    }

    public void TogglePause()
    {
        ThrowIfDisposed();
        _chunkDownloader?.TogglePause();
    }

    public void CancelDownload()
    {
        if (_disposed) return;
        _chunkDownloader?.CancelDownload();
    }

    private void DisposeChunkDownloader()
    {
        _chunkDownloader?.Dispose();
        _chunkDownloader = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SophonCoreDownloader));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeChunkDownloader();
    }
}
