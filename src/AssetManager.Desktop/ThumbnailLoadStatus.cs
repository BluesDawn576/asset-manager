namespace AssetManager.Desktop;

public sealed record ThumbnailLoadStatus(
    bool IsRunning,
    int CompletedCount,
    int TotalCount)
{
    public static ThumbnailLoadStatus Idle { get; } = new(false, 0, 0);
}
