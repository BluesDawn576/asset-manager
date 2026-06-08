namespace AssetManager.Application.Library;

public sealed record KnownLibrary(
    Guid Id,
    string DisplayName,
    string RootPath,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastOpenedAt,
    bool IsAvailable);
