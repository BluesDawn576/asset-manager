using AssetManager.Application.BackgroundTasks;

namespace AssetManager.Desktop;

public static class BackgroundTaskPresentation
{
    public static IReadOnlyList<BackgroundTaskSnapshot> OrderForDisplay(IEnumerable<BackgroundTaskSnapshot> snapshots)
    {
        return snapshots
            .OrderBy(snapshot => snapshot.State == BackgroundTaskState.Running ? 0 : 1)
            .ThenBy(snapshot => snapshot.State == BackgroundTaskState.Running
                ? GetSummaryPriority(snapshot.Kind)
                : int.MaxValue)
            .ThenByDescending(snapshot => snapshot.State == BackgroundTaskState.Running
                ? snapshot.StartedAt
                : snapshot.FinishedAt ?? snapshot.StartedAt)
            .ToArray();
    }

    public static BackgroundTaskSnapshot? SelectSummaryTask(IEnumerable<BackgroundTaskSnapshot> snapshots)
    {
        return OrderForDisplay(snapshots).FirstOrDefault();
    }

    public static int GetSummaryPriority(BackgroundTaskKind kind)
    {
        return kind switch
        {
            BackgroundTaskKind.ImportAssets => 0,
            BackgroundTaskKind.SynchronizeLibrary => 1,
            BackgroundTaskKind.PluginOperation => 2,
            BackgroundTaskKind.GenerateThumbnails => 3,
            _ => int.MaxValue
        };
    }
}
