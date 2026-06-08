using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record AssetPreview(
    Guid AssetId,
    AssetTypeId TypeId,
    AssetStatus Status,
    string FullPath,
    string? TextContent);
