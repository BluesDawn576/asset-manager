using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public interface IAssetLibraryRepository
{
    Task InitializeAsync(LibraryLocation location, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetRecord>> AddAssetsAsync(
        LibraryLocation location,
        IEnumerable<PreparedAssetFile> assets,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetRecord>> SearchAsync(
        LibraryLocation location,
        AssetSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetRecord>> GetAllAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default);

    Task<AssetRecord?> GetByIdAsync(
        LibraryLocation location,
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(
        LibraryLocation location,
        Guid assetId,
        string notes,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default);

    Task UpdateAssetFileStateAsync(
        LibraryLocation location,
        Guid assetId,
        StoredContentFile file,
        AssetStatus status,
        CancellationToken cancellationToken = default);

    Task MarkAssetStatusAsync(
        LibraryLocation location,
        Guid assetId,
        AssetStatus status,
        CancellationToken cancellationToken = default);
}
