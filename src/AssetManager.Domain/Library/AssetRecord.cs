namespace AssetManager.Domain.Library;

public sealed record AssetRecord(
    Guid Id,
    string DisplayName,
    LibraryRelativePath LibraryRelativePath,
    string? SourcePath,
    AssetTypeId TypeId,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset ImportedAt,
    string ContentHash,
    string Notes,
    AssetStatus Status,
    IReadOnlyList<string> Tags)
{
    public string FullPath(LibraryLocation location)
    {
        return LibraryRelativePath.ToFullPath(location.RootPath);
    }
}
