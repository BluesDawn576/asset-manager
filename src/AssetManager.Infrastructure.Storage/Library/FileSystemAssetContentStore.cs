using System.Security.Cryptography;
using System.Text;
using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Infrastructure.Storage.Library;

public sealed class FileSystemAssetContentStore : IAssetContentStore
{
    private readonly IAssetTypeResolver _assetTypeResolver;

    public FileSystemAssetContentStore(IAssetTypeResolver assetTypeResolver)
    {
        _assetTypeResolver = assetTypeResolver;
    }

    public Task PrepareLibraryAsync(LibraryLocation location, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(location.RootPath);
        Directory.CreateDirectory(location.ManagementPath);
        Directory.CreateDirectory(location.LogsPath);
        Directory.CreateDirectory(location.TempPath);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PreparedAssetFile>> CopyIntoLibraryAsync(
        LibraryLocation location,
        LibraryRelativePath targetFolder,
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var targetFolderPath = targetFolder.ToFullPath(location.RootPath);
        if (!Directory.Exists(targetFolderPath))
        {
            throw new DirectoryNotFoundException($"Target library folder does not exist: {targetFolder}");
        }

        var copiedFiles = new List<PreparedAssetFile>();
        try
        {
            foreach (var rawPath in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = Path.GetFullPath(rawPath);
                if (location.IsManagementPath(sourcePath))
                {
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    copiedFiles.Add(await CopyFileAsync(
                        location,
                        sourcePath,
                        targetFolderPath,
                        [],
                        cancellationToken));
                    continue;
                }

                if (Directory.Exists(sourcePath))
                {
                    copiedFiles.AddRange(await CopyDirectoryAsync(
                        location,
                        sourcePath,
                        targetFolderPath,
                        cancellationToken));
                    continue;
                }

                throw new FileNotFoundException("Source path does not exist.", sourcePath);
            }
        }
        catch
        {
            await RollbackCopiedAssetsAsync(location, copiedFiles, cancellationToken);
            throw;
        }

        return copiedFiles;
    }

    public async Task<PreparedAssetFile> WriteTextSnippetAsync(
        LibraryLocation location,
        LibraryRelativePath targetFolder,
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var targetFolderPath = targetFolder.ToFullPath(location.RootPath);
        if (!Directory.Exists(targetFolderPath))
        {
            throw new DirectoryNotFoundException($"Target library folder does not exist: {targetFolder}");
        }

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new ArgumentException("Text snippet file name is required.", nameof(fileName));
        }

        var destinationPath = GetUniqueFilePath(Path.Combine(targetFolderPath, safeFileName));
        await File.WriteAllTextAsync(destinationPath, content, Encoding.UTF8, cancellationToken);
        try
        {
            return await BuildPreparedAssetFileAsync(location, destinationPath, null, [], cancellationToken);
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    public Task RollbackCopiedAssetsAsync(
        LibraryLocation location,
        IEnumerable<PreparedAssetFile> copiedFiles,
        CancellationToken cancellationToken = default)
    {
        DeleteCopiedFilesAndCreatedDirectories(location, copiedFiles, cancellationToken);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LibraryFolder>> ListFoldersAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<LibraryFolder> { new(LibraryRelativePath.Root) };

        if (Directory.Exists(location.RootPath))
        {
            foreach (var directory in Directory.EnumerateDirectories(location.RootPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (location.IsManagementPath(directory))
                {
                    continue;
                }

                folders.Add(new LibraryFolder(ToLibraryRelativePath(location, directory)));
            }
        }

        return Task.FromResult<IReadOnlyList<LibraryFolder>>(
            folders.OrderBy(folder => folder.RelativePath.Value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task CreateFolderAsync(
        LibraryLocation location,
        LibraryRelativePath folder,
        CancellationToken cancellationToken = default)
    {
        if (folder.IsRoot)
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(folder.ToFullPath(location.RootPath));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StoredContentFile>> ScanContentFilesAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(location.RootPath))
        {
            return [];
        }

        var files = new List<StoredContentFile>();
        foreach (var filePath in Directory.EnumerateFiles(location.RootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (location.IsManagementPath(filePath))
            {
                continue;
            }

            files.Add(await BuildStoredContentFileAsync(location, filePath, cancellationToken));
        }

        return files;
    }

    public Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task<string> ReadTextPreviewAsync(
        string fullPath,
        int maxCharacters,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[maxCharacters];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(0, maxCharacters), cancellationToken);
        return new string(buffer, 0, count);
    }

    private async Task<IReadOnlyList<PreparedAssetFile>> CopyDirectoryAsync(
        LibraryLocation location,
        string sourceDirectory,
        string targetFolderPath,
        CancellationToken cancellationToken)
    {
        if (IsSameOrDescendantPath(targetFolderPath, sourceDirectory))
        {
            throw new InvalidOperationException("Cannot import a folder into itself or one of its child folders.");
        }

        var sourceDirectoryInfo = new DirectoryInfo(sourceDirectory);
        var destinationRoot = GetUniqueDirectoryPath(Path.Combine(targetFolderPath, sourceDirectoryInfo.Name));
        Directory.CreateDirectory(destinationRoot);

        var createdDirectories = new List<LibraryRelativePath>
        {
            ToLibraryRelativePath(location, destinationRoot)
        };
        var copiedFiles = new List<PreparedAssetFile>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
                var destinationDirectory = Path.Combine(destinationRoot, relativeDirectory);
                Directory.CreateDirectory(destinationDirectory);
                createdDirectories.Add(ToLibraryRelativePath(location, destinationDirectory));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativeFile = Path.GetRelativePath(sourceDirectory, file);
                var destinationFile = Path.Combine(destinationRoot, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.Copy(file, destinationFile, overwrite: false);
                try
                {
                    copiedFiles.Add(await BuildPreparedAssetFileAsync(
                        location,
                        destinationFile,
                        file,
                        createdDirectories,
                        cancellationToken));
                }
                catch
                {
                    if (File.Exists(destinationFile))
                    {
                        File.Delete(destinationFile);
                    }

                    throw;
                }
            }

            return copiedFiles;
        }
        catch
        {
            DeleteCopiedFilesAndCreatedDirectories(location, copiedFiles, cancellationToken);
            DeleteCreatedDirectories(location, createdDirectories, cancellationToken);
            throw;
        }
    }

    private async Task<PreparedAssetFile> CopyFileAsync(
        LibraryLocation location,
        string sourcePath,
        string targetFolderPath,
        IReadOnlyList<LibraryRelativePath> createdDirectories,
        CancellationToken cancellationToken)
    {
        var destinationPath = GetUniqueFilePath(Path.Combine(targetFolderPath, Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, destinationPath, overwrite: false);
        try
        {
            return await BuildPreparedAssetFileAsync(location, destinationPath, sourcePath, createdDirectories, cancellationToken);
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static void DeleteCopiedFilesAndCreatedDirectories(
        LibraryLocation location,
        IEnumerable<PreparedAssetFile> copiedFiles,
        CancellationToken cancellationToken)
    {
        var files = copiedFiles.ToArray();
        foreach (var file in files.OrderByDescending(file => file.LibraryRelativePath.Value.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = file.LibraryRelativePath.ToFullPath(location.RootPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        DeleteCreatedDirectories(
            location,
            files.SelectMany(file => file.CreatedDirectories).Distinct(),
            cancellationToken);
    }

    private static void DeleteCreatedDirectories(
        LibraryLocation location,
        IEnumerable<LibraryRelativePath> createdDirectories,
        CancellationToken cancellationToken)
    {
        foreach (var directory in createdDirectories.OrderByDescending(directory => directory.Value.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryPath = directory.ToFullPath(location.RootPath);
            if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath);
            }
        }
    }

    private async Task<PreparedAssetFile> BuildPreparedAssetFileAsync(
        LibraryLocation location,
        string destinationPath,
        string? sourcePath,
        IReadOnlyList<LibraryRelativePath> createdDirectories,
        CancellationToken cancellationToken)
    {
        var storedFile = await BuildStoredContentFileAsync(location, destinationPath, cancellationToken);
        return storedFile.ToPreparedAssetFile(sourcePath, DateTimeOffset.UtcNow) with
        {
            CreatedDirectories = createdDirectories
        };
    }

    private async Task<StoredContentFile> BuildStoredContentFileAsync(
        LibraryLocation location,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        var extension = fileInfo.Extension;
        return new StoredContentFile(
            fileInfo.Name,
            ToLibraryRelativePath(location, fullPath),
            _assetTypeResolver.Resolve(extension),
            extension,
            fileInfo.Length,
            fileInfo.CreationTimeUtc,
            fileInfo.LastWriteTimeUtc,
            await HashFileAsync(fullPath, cancellationToken));
    }

    private static async Task<string> HashFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(fullPath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static LibraryRelativePath ToLibraryRelativePath(LibraryLocation location, string fullPath)
    {
        var relativePath = Path.GetRelativePath(location.RootPath, fullPath);
        return LibraryRelativePath.Create(relativePath);
    }

    private static string GetUniqueFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            index++;
        } while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static string GetUniqueDirectoryPath(string desiredPath)
    {
        if (!Directory.Exists(desiredPath) && !File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var parent = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileName(desiredPath);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(parent, $"{name} ({index})");
            index++;
        } while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
    }

    private static bool IsSameOrDescendantPath(string candidatePath, string ancestorPath)
    {
        var candidate = NormalizeFullPath(candidatePath);
        var ancestor = NormalizeFullPath(ancestorPath);

        return candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFullPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
