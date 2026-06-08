namespace AssetManager.Application.Library;

public sealed class KnownLibraryApplicationService(
    LibraryApplicationService libraryService,
    IKnownLibraryStore knownLibraryStore)
{
    public Task<IReadOnlyList<KnownLibrary>> ListAsync(CancellationToken cancellationToken = default)
    {
        return knownLibraryStore.ListAsync(cancellationToken);
    }

    public Task<KnownLibrary?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        return knownLibraryStore.GetActiveAsync(cancellationToken);
    }

    public async Task<KnownLibraryOpenResult> RegisterAndOpenAsync(
        string rootPath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var session = await libraryService.OpenOrCreateAsync(rootPath, cancellationToken);
        var knownLibrary = await knownLibraryStore.AddOrUpdateAsync(
            session.Location.RootPath,
            displayName,
            cancellationToken);

        await knownLibraryStore.MarkOpenedAsync(knownLibrary.Id, cancellationToken);
        var refreshedLibrary = await knownLibraryStore.GetAsync(knownLibrary.Id, cancellationToken) ?? knownLibrary;
        return new KnownLibraryOpenResult(refreshedLibrary, session);
    }

    public async Task<KnownLibraryOpenResult> OpenRegisteredAsync(
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        var knownLibrary = await knownLibraryStore.GetAsync(libraryId, cancellationToken)
                           ?? throw new InvalidOperationException("The selected asset library is not registered.");

        if (!knownLibrary.IsAvailable)
        {
            throw new DirectoryNotFoundException($"Registered asset library is not available: {knownLibrary.RootPath}");
        }

        var session = await libraryService.OpenOrCreateAsync(knownLibrary.RootPath, cancellationToken);
        await knownLibraryStore.MarkOpenedAsync(knownLibrary.Id, cancellationToken);

        var refreshedLibrary = await knownLibraryStore.GetAsync(knownLibrary.Id, cancellationToken) ?? knownLibrary;
        return new KnownLibraryOpenResult(refreshedLibrary, session);
    }
}
