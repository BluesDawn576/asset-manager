using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record AssetImportResult(IReadOnlyList<AssetRecord> ImportedAssets)
{
    public int ImportedCount => ImportedAssets.Count;
}
