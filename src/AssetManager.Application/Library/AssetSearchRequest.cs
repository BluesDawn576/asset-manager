using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed record AssetSearchRequest(
    LibraryRelativePath CurrentFolder,
    string Query,
    IReadOnlyList<string> RequiredTags);
