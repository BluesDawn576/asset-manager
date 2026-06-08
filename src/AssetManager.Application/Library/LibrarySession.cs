using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record LibrarySession(
    LibraryLocation Location,
    LibraryRelativePath CurrentFolder,
    IReadOnlyList<LibraryFolder> Folders,
    IReadOnlyList<AssetRecord> Assets);
