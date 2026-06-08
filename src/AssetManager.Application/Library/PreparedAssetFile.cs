using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record PreparedAssetFile(
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
    IReadOnlyList<LibraryRelativePath> CreatedDirectories);
