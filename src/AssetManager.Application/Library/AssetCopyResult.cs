using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record AssetCopyResult(
    IReadOnlyList<PreparedAssetFile> CopiedFiles,
    IReadOnlyList<LibraryRelativePath> CreatedFolders)
{
    public static AssetCopyResult Empty { get; } = new([], []);
}
