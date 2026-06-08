using System.Windows.Threading;
using AssetManager.Domain.Library;
using AssetManager.Infrastructure.Windows;

namespace AssetManager.Desktop;

public sealed class AssetThumbnailLoadCoordinator : IDisposable
{
    private readonly WindowsThumbnailCacheService _thumbnailCacheService;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _loadCts;
    private long _statusGeneration;

    public event Action<ThumbnailLoadStatus>? StatusChanged;

    public AssetThumbnailLoadCoordinator(
        WindowsThumbnailCacheService thumbnailCacheService,
        Dispatcher dispatcher)
    {
        _thumbnailCacheService = thumbnailCacheService;
        _dispatcher = dispatcher;
    }

    public void Reload(LibraryLocation location, IEnumerable<AssetRow> rows)
    {
        Cancel();
        var generation = Interlocked.Increment(ref _statusGeneration);

        var candidates = rows
            .Where(row => row.CanRequestThumbnail)
            .ToArray();
        if (candidates.Length == 0)
        {
            PublishStatus(ThumbnailLoadStatus.Idle, generation);
            return;
        }

        PublishStatus(new ThumbnailLoadStatus(true, 0, candidates.Length), generation);
        var loadCts = new CancellationTokenSource();
        _loadCts = loadCts;
        _ = Task.Run(() => LoadAsync(location, candidates, generation, loadCts.Token));
    }

    public void Cancel()
    {
        var generation = Interlocked.Increment(ref _statusGeneration);

        if (_loadCts is null)
        {
            PublishStatus(ThumbnailLoadStatus.Idle, generation);
            return;
        }

        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = null;
        PublishStatus(ThumbnailLoadStatus.Idle, generation);
    }

    public void Dispose()
    {
        Cancel();
    }

    private async Task LoadAsync(
        LibraryLocation location,
        IReadOnlyList<AssetRow> rows,
        long generation,
        CancellationToken cancellationToken)
    {
        var completedCount = 0;

        try
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var thumbnailPath = await _thumbnailCacheService.GetOrCreateAsync(
                    location,
                    row.Asset,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    completedCount++;
                    await PublishStatusAsync(
                        new ThumbnailLoadStatus(true, completedCount, rows.Count),
                        generation,
                        cancellationToken);
                    continue;
                }

                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        row.SetThumbnailPath(thumbnailPath);
                        if (generation == Interlocked.Read(ref _statusGeneration))
                        {
                            StatusChanged?.Invoke(new ThumbnailLoadStatus(true, completedCount + 1, rows.Count));
                        }
                    },
                    DispatcherPriority.Background,
                    cancellationToken);
                completedCount++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            PublishStatus(ThumbnailLoadStatus.Idle, generation);
        }
    }

    private void PublishStatus(ThumbnailLoadStatus status, long generation)
    {
        if (generation != Interlocked.Read(ref _statusGeneration))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            StatusChanged?.Invoke(status);
            return;
        }

        _ = _dispatcher.InvokeAsync(
            () => StatusChanged?.Invoke(status),
            DispatcherPriority.Background);
    }

    private Task PublishStatusAsync(
        ThumbnailLoadStatus status,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation != Interlocked.Read(ref _statusGeneration))
        {
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(
            () =>
            {
                if (generation == Interlocked.Read(ref _statusGeneration))
                {
                    StatusChanged?.Invoke(status);
                }
            },
            DispatcherPriority.Background,
            cancellationToken).Task;
    }
}
