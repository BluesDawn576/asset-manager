namespace AssetManager.Desktop;

public sealed record ThumbnailDisplaySettings(
    bool ShowType,
    bool ShowStatus,
    bool ShowTags,
    bool ShowPath,
    bool ShowSize)
{
    public static ThumbnailDisplaySettings Default { get; } = new(
        ShowType: true,
        ShowStatus: true,
        ShowTags: true,
        ShowPath: false,
        ShowSize: true);
}
