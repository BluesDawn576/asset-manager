using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed class LibraryApplicationService(
    IAssetLibraryRepository repository,
    IAssetContentStore contentStore,
    IAssetTypeResolver assetTypeResolver,
    IAssetActivityLog activityLog)
{
    private const int TextPreviewCharacterLimit = 32_000;

    public async Task<LibrarySession> OpenOrCreateAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var location = LibraryLocation.Create(rootPath);

        await contentStore.PrepareLibraryAsync(location, cancellationToken);
        await repository.InitializeAsync(location, cancellationToken);
        await activityLog.AppendAsync(location, "Library opened.", cancellationToken);

        var folders = await contentStore.ListFoldersAsync(location, cancellationToken);
        var assets = await SearchAsync(location, LibraryRelativePath.Root, string.Empty, [], cancellationToken);
        return new LibrarySession(location, LibraryRelativePath.Root, folders, assets);
    }

    public async Task<AssetImportResult> ImportPathsAsync(
        LibraryLocation location,
        LibraryRelativePath currentFolder,
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (sources.Length == 0)
        {
            return new AssetImportResult([]);
        }

        var copiedFiles = await contentStore.CopyIntoLibraryAsync(location, currentFolder, sources, cancellationToken);
        if (copiedFiles.Count == 0)
        {
            return new AssetImportResult([]);
        }

        try
        {
            var importedAssets = await repository.AddAssetsAsync(location, copiedFiles, cancellationToken);
            await activityLog.AppendAsync(location, $"Imported {importedAssets.Count} asset(s).", cancellationToken);
            return new AssetImportResult(importedAssets);
        }
        catch
        {
            await contentStore.RollbackCopiedAssetsAsync(location, copiedFiles, cancellationToken);
            await activityLog.AppendAsync(location, "Import failed; copied files were rolled back.", cancellationToken);
            throw;
        }
    }

    public async Task<AssetRecord> CreateTextSnippetAsync(
        LibraryLocation location,
        LibraryRelativePath currentFolder,
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var preparedFile = await contentStore.WriteTextSnippetAsync(
            location,
            currentFolder,
            NormalizeTextSnippetFileName(fileName),
            content,
            cancellationToken);

        try
        {
            var created = await repository.AddAssetsAsync(location, [preparedFile], cancellationToken);
            await activityLog.AppendAsync(location, $"Created text snippet {created[0].LibraryRelativePath}.", cancellationToken);
            return created[0];
        }
        catch
        {
            await contentStore.RollbackCopiedAssetsAsync(location, [preparedFile], cancellationToken);
            await activityLog.AppendAsync(location, "Text snippet creation failed; copied file was rolled back.", cancellationToken);
            throw;
        }
    }

    public Task<IReadOnlyList<AssetRecord>> SearchAsync(
        LibraryLocation location,
        LibraryRelativePath currentFolder,
        string query,
        IEnumerable<string> requiredTags,
        CancellationToken cancellationToken = default)
    {
        var request = new AssetSearchRequest(
            currentFolder,
            query.Trim(),
            NormalizeTags(requiredTags));

        return repository.SearchAsync(location, request, cancellationToken);
    }

    public async Task<AssetPreview> GetPreviewAsync(
        LibraryLocation location,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var asset = await repository.GetByIdAsync(location, assetId, cancellationToken)
                    ?? throw new InvalidOperationException("Asset was not found.");

        var fullPath = asset.FullPath(location);
        var fileExists = await contentStore.FileExistsAsync(fullPath, cancellationToken);
        if (asset.Status == AssetStatus.Missing || !fileExists)
        {
            return new AssetPreview(asset.Id, asset.TypeId, AssetStatus.Missing, fullPath, null);
        }

        var textContent = asset.TypeId == AssetTypeId.Text
            ? await contentStore.ReadTextPreviewAsync(fullPath, TextPreviewCharacterLimit, cancellationToken)
            : null;

        return new AssetPreview(asset.Id, asset.TypeId, AssetStatus.Available, fullPath, textContent);
    }

    public async Task UpdateMetadataAsync(
        LibraryLocation location,
        Guid assetId,
        string notes,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        await repository.UpdateMetadataAsync(location, assetId, notes.Trim(), NormalizeTags(tags), cancellationToken);
        await activityLog.AppendAsync(location, $"Updated metadata for asset {assetId}.", cancellationToken);
    }

    public Task<IReadOnlyList<LibraryFolder>> ListFoldersAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        return contentStore.ListFoldersAsync(location, cancellationToken);
    }

    public async Task CreateFolderAsync(
        LibraryLocation location,
        LibraryRelativePath folder,
        CancellationToken cancellationToken = default)
    {
        await contentStore.CreateFolderAsync(location, folder, cancellationToken);
        await activityLog.AppendAsync(location, $"Created folder {folder}.", cancellationToken);
    }

    public async Task<LibrarySyncResult> SynchronizeAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        var existingAssets = await repository.GetAllAsync(location, cancellationToken);
        var scannedFiles = await contentStore.ScanContentFilesAsync(location, cancellationToken);

        var scannedByPath = scannedFiles.ToDictionary(
            file => file.LibraryRelativePath.Value,
            StringComparer.OrdinalIgnoreCase);

        var unusedScannedFiles = new List<StoredContentFile>(scannedFiles);
        var updatedCount = 0;
        var movedCount = 0;
        var missingCount = 0;

        foreach (var asset in existingAssets)
        {
            if (scannedByPath.TryGetValue(asset.LibraryRelativePath.Value, out var samePathFile))
            {
                await repository.UpdateAssetFileStateAsync(
                    location,
                    asset.Id,
                    samePathFile,
                    AssetStatus.Available,
                    cancellationToken);

                unusedScannedFiles.Remove(samePathFile);
                updatedCount++;
                continue;
            }

            var movedFile = unusedScannedFiles.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.ContentHash)
                && string.Equals(file.ContentHash, asset.ContentHash, StringComparison.OrdinalIgnoreCase));

            if (movedFile is not null)
            {
                await repository.UpdateAssetFileStateAsync(
                    location,
                    asset.Id,
                    movedFile,
                    AssetStatus.Available,
                    cancellationToken);

                unusedScannedFiles.Remove(movedFile);
                movedCount++;
                continue;
            }

            await repository.MarkAssetStatusAsync(location, asset.Id, AssetStatus.Missing, cancellationToken);
            missingCount++;
        }

        var newAssets = unusedScannedFiles.Select(file => file.ToPreparedAssetFile(null, DateTimeOffset.UtcNow)).ToArray();
        if (newAssets.Length > 0)
        {
            await repository.AddAssetsAsync(location, newAssets, cancellationToken);
        }

        var result = new LibrarySyncResult(updatedCount, movedCount, missingCount, newAssets.Length);
        await activityLog.AppendAsync(
            location,
            $"Synchronized library. Updated={result.UpdatedCount}; moved={result.MovedCount}; missing={result.MissingCount}; new={result.NewAssetCount}.",
            cancellationToken);

        return result;
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        return tags
            .SelectMany(tag => tag.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeTextSnippetFileName(string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(fileName)
            ? $"snippet-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt"
            : fileName.Trim();

        return Path.GetExtension(normalized).Length == 0 ? normalized + ".txt" : normalized;
    }

    public AssetTypeId ResolveAssetType(string? extension)
    {
        return assetTypeResolver.Resolve(extension);
    }
}
