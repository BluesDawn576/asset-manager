using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record AssetImportResult(
    IReadOnlyList<AssetRecord> ImportedAssets,
    IReadOnlyList<LibraryRelativePath> ImportedFolders)
{
    public int ImportedCount => ImportedAssets.Count;
}
