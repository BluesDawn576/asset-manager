using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public interface IAssetContentStore
{
    Task PrepareLibraryAsync(LibraryLocation location, CancellationToken cancellationToken = default);

    Task<AssetCopyResult> CopyIntoLibraryAsync(
        LibraryLocation location,
        LibraryRelativePath targetFolder,
        IEnumerable<string> sourcePaths,
        AssetImportOptions? importOptions = null,
        CancellationToken cancellationToken = default);

    Task<PreparedAssetFile> WriteTextSnippetAsync(
        LibraryLocation location,
        LibraryRelativePath targetFolder,
        string fileName,
        string content,
        CancellationToken cancellationToken = default);

    Task RollbackCopiedAssetsAsync(
        LibraryLocation location,
        IEnumerable<PreparedAssetFile> copiedFiles,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryFolder>> ListFoldersAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default);

    Task CreateFolderAsync(
        LibraryLocation location,
        LibraryRelativePath folder,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredContentFile>> ScanContentFilesAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default);

    Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken = default);

    Task<string> ReadTextPreviewAsync(
        string fullPath,
        int maxCharacters,
        CancellationToken cancellationToken = default);
}
