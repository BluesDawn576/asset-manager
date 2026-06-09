using System.Globalization;
using System.Windows.Threading;
using AssetManager.Application.BackgroundTasks;
using AssetManager.Desktop.Localization;
using AssetManager.Domain.Library;
using AssetManager.Infrastructure.Windows;

namespace AssetManager.Desktop;

public sealed class AssetThumbnailLoadCoordinator : IDisposable
{
    private readonly WindowsThumbnailCacheService _thumbnailCacheService;
    private readonly IBackgroundTaskCenter _backgroundTaskCenter;
    private readonly Dispatcher _dispatcher;

    private IBackgroundTaskSession? _session;

    public AssetThumbnailLoadCoordinator(
        WindowsThumbnailCacheService thumbnailCacheService,
        IBackgroundTaskCenter backgroundTaskCenter,
        Dispatcher dispatcher)
    {
        _thumbnailCacheService = thumbnailCacheService;
        _backgroundTaskCenter = backgroundTaskCenter;
        _dispatcher = dispatcher;
    }

    public void Reload(LibraryLocation location, IEnumerable<AssetRow> rows)
    {
        Cancel();

        var candidates = rows
            .Where(row => row.CanRequestThumbnail)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var progressFormat = LocalizationManager.Get("BackgroundTaskThumbnailProgressFormat");
        var unitLabel = LocalizationManager.Get("BackgroundTaskProgressUnitItems");
        var completedText = LocalizationManager.Get("BackgroundTaskThumbnailCompleted");
        var canceledText = LocalizationManager.Get("BackgroundTaskThumbnailCanceled");
        var failedAtFormat = LocalizationManager.Get("BackgroundTaskThumbnailFailedAtFormat");
        var session = _backgroundTaskCenter.StartTask(
            new BackgroundTaskStartRequest(
                BackgroundTaskKind.GenerateThumbnails,
                LocalizationManager.Get("BackgroundTaskThumbnailTitle"),
                string.Format(CultureInfo.CurrentCulture, progressFormat, 0, candidates.Length),
                IsCancelable: true,
                InitialProgress: new BackgroundTaskProgress(
                    0,
                    candidates.Length,
                    unitLabel)));

        _session = session;
        var taskText = new ThumbnailTaskText(
            progressFormat,
            unitLabel,
            completedText,
            canceledText,
            failedAtFormat);
        _ = Task.Run(() => LoadAsync(location, candidates, session, taskText));
    }

    public void Cancel()
    {
        if (_session is null)
        {
            return;
        }

        _session.Cancel(LocalizationManager.Get("BackgroundTaskThumbnailCanceled"));
        _session.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        Cancel();
    }

    private async Task LoadAsync(
        LibraryLocation location,
        IReadOnlyList<AssetRow> rows,
        IBackgroundTaskSession session,
        ThumbnailTaskText text)
    {
        var completedCount = 0;
        var latestStatusText = string.Format(CultureInfo.CurrentCulture, text.ProgressFormat, 0, rows.Count);

        try
        {
            foreach (var row in rows)
            {
                session.CancellationToken.ThrowIfCancellationRequested();

                var thumbnailPath = await _thumbnailCacheService.GetOrCreateAsync(
                    location,
                    row.Asset,
                    session.CancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    await _dispatcher.InvokeAsync(
                        () => row.SetThumbnailPath(thumbnailPath),
                        DispatcherPriority.Background,
                        session.CancellationToken);
                }

                completedCount++;
                latestStatusText = string.Format(CultureInfo.CurrentCulture, text.ProgressFormat, completedCount, rows.Count);
                session.Update(
                    latestStatusText,
                    new BackgroundTaskProgress(
                        completedCount,
                        rows.Count,
                        text.UnitLabel));
            }

            session.Complete(text.CompletedText);
        }
        catch (OperationCanceledException)
        {
            session.Cancel(text.CanceledText);
        }
        catch (Exception ex)
        {
            session.Fail(
                ex,
                string.Format(
                    CultureInfo.CurrentCulture,
                    text.FailedAtFormat,
                    latestStatusText,
                    ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            session.Dispose();
        }
    }

    private sealed record ThumbnailTaskText(
        string ProgressFormat,
        string UnitLabel,
        string CompletedText,
        string CanceledText,
        string FailedAtFormat);
}
