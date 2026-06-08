namespace AssetManager.Application.Library;

public sealed record LibrarySyncResult(
    int UpdatedCount,
    int MovedCount,
    int MissingCount,
    int NewAssetCount);
