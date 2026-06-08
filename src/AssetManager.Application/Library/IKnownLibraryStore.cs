namespace AssetManager.Application.Library;

public interface IKnownLibraryStore
{
    Task<IReadOnlyList<KnownLibrary>> ListAsync(CancellationToken cancellationToken = default);

    Task<KnownLibrary?> GetAsync(Guid libraryId, CancellationToken cancellationToken = default);

    Task<KnownLibrary?> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<KnownLibrary> AddOrUpdateAsync(
        string rootPath,
        string? displayName,
        CancellationToken cancellationToken = default);

    Task MarkOpenedAsync(Guid libraryId, CancellationToken cancellationToken = default);
}
