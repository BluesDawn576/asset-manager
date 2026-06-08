namespace AssetManager.Application.Library;

public sealed record AssetImportOptions(long? MaxCopyBytesPerSecond)
{
    public const long LowImpactMaxCopyBytesPerSecond = 32L * 1024L * 1024L;

    public static AssetImportOptions Default { get; } = new((long?)null);

    public static AssetImportOptions LowImpact { get; } = new(LowImpactMaxCopyBytesPerSecond);

    public bool IsThrottleEnabled => MaxCopyBytesPerSecond is > 0;
}
