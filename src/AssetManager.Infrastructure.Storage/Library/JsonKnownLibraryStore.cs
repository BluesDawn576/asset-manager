using System.Text.Json;
using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Infrastructure.Storage.Library;

public sealed class JsonKnownLibraryStore : IKnownLibraryStore
{
    private const int RegistryVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _registryPath;

    public JsonKnownLibraryStore()
        : this(GetDefaultRegistryPath())
    {
    }

    public JsonKnownLibraryStore(string registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            throw new ArgumentException("Known library registry path is required.", nameof(registryPath));
        }

        _registryPath = Path.GetFullPath(registryPath);
    }

    public async Task<IReadOnlyList<KnownLibrary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var registry = await ReadRegistryAsync(cancellationToken);
        return registry.Libraries
            .Select(ToKnownLibrary)
            .OrderByDescending(library => library.LastOpenedAt)
            .ThenBy(library => library.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<KnownLibrary?> GetAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        var registry = await ReadRegistryAsync(cancellationToken);
        var entry = registry.Libraries.FirstOrDefault(library => library.Id == libraryId);
        return entry is null ? null : ToKnownLibrary(entry);
    }

    public async Task<KnownLibrary?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var registry = await ReadRegistryAsync(cancellationToken);
        if (registry.ActiveLibraryId is null)
        {
            return null;
        }

        var entry = registry.Libraries.FirstOrDefault(library => library.Id == registry.ActiveLibraryId.Value);
        return entry is null ? null : ToKnownLibrary(entry);
    }

    public async Task<KnownLibrary> AddOrUpdateAsync(
        string rootPath,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var registry = await ReadRegistryAsync(cancellationToken);
        var normalizedRootPath = Path.GetFullPath(rootPath);
        var now = DateTimeOffset.UtcNow;
        var resolvedDisplayName = ResolveDisplayName(normalizedRootPath, displayName);

        var entry = registry.Libraries.FirstOrDefault(library =>
            string.Equals(library.RootPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            entry = new KnownLibraryRegistryEntry
            {
                Id = Guid.NewGuid(),
                DisplayName = resolvedDisplayName,
                RootPath = normalizedRootPath,
                RegisteredAt = now,
                LastOpenedAt = now
            };
            registry.Libraries.Add(entry);
        }
        else
        {
            entry.DisplayName = resolvedDisplayName;
            entry.RootPath = normalizedRootPath;
            entry.LastOpenedAt = now;
        }

        registry.ActiveLibraryId = entry.Id;
        await WriteRegistryAsync(registry, cancellationToken);
        return ToKnownLibrary(entry);
    }

    public async Task MarkOpenedAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        var registry = await ReadRegistryAsync(cancellationToken);
        var entry = registry.Libraries.FirstOrDefault(library => library.Id == libraryId);
        if (entry is null)
        {
            throw new InvalidOperationException("The selected asset library is not registered.");
        }

        entry.LastOpenedAt = DateTimeOffset.UtcNow;
        registry.ActiveLibraryId = entry.Id;
        await WriteRegistryAsync(registry, cancellationToken);
    }

    private async Task<KnownLibraryRegistryFile> ReadRegistryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_registryPath))
        {
            return new KnownLibraryRegistryFile();
        }

        await using var stream = File.OpenRead(_registryPath);
        var registry = await JsonSerializer.DeserializeAsync<KnownLibraryRegistryFile>(
            stream,
            JsonOptions,
            cancellationToken);

        return registry ?? new KnownLibraryRegistryFile();
    }

    private async Task WriteRegistryAsync(
        KnownLibraryRegistryFile registry,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        registry.Version = RegistryVersion;

        var tempPath = _registryPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, registry, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _registryPath, overwrite: true);
    }

    private static KnownLibrary ToKnownLibrary(KnownLibraryRegistryEntry entry)
    {
        var location = LibraryLocation.Create(entry.RootPath);
        var isAvailable = Directory.Exists(location.RootPath)
                          && Directory.Exists(location.ManagementPath)
                          && File.Exists(location.DatabasePath);

        return new KnownLibrary(
            entry.Id,
            entry.DisplayName,
            location.RootPath,
            entry.RegisteredAt,
            entry.LastOpenedAt,
            isAvailable);
    }

    private static string ResolveDisplayName(string rootPath, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        var directoryName = new DirectoryInfo(rootPath).Name;
        return string.IsNullOrWhiteSpace(directoryName) ? rootPath : directoryName;
    }

    private static string GetDefaultRegistryPath()
    {
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataRoot, "AssetManager", "known-libraries.json");
    }

    private sealed class KnownLibraryRegistryFile
    {
        public int Version { get; set; } = RegistryVersion;

        public Guid? ActiveLibraryId { get; set; }

        public List<KnownLibraryRegistryEntry> Libraries { get; set; } = [];
    }

    private sealed class KnownLibraryRegistryEntry
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string RootPath { get; set; } = string.Empty;

        public DateTimeOffset RegisteredAt { get; set; }

        public DateTimeOffset LastOpenedAt { get; set; }
    }
}
