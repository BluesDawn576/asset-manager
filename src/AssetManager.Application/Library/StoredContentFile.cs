using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record StoredContentFile(
    string DisplayName,
    LibraryRelativePath LibraryRelativePath,
    AssetTypeId TypeId,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    string ContentHash)
{
    public PreparedAssetFile ToPreparedAssetFile(string? sourcePath, DateTimeOffset importedAt)
    {
        return new PreparedAssetFile(
            DisplayName,
            LibraryRelativePath,
            sourcePath,
            TypeId,
            Extension,
            SizeBytes,
            CreatedAt,
            ModifiedAt,
            importedAt,
            ContentHash,
            []);
    }
}
