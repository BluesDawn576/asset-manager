using System.Buffers;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Infrastructure.Storage.Library;

public sealed class FileSystemAssetContentStore : IAssetContentStore
{
    private const int CopyBufferSize = 128 * 1024;

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
        Directory.CreateDirectory(location.ThumbnailsPath);
        return Task.CompletedTask;
    }

    public async Task<AssetCopyResult> CopyIntoLibraryAsync(
        LibraryLocation location,
        LibraryRelativePath targetFolder,
        IEnumerable<string> sourcePaths,
        AssetImportOptions? importOptions = null,
        CancellationToken cancellationToken = default)
    {
        var targetFolderPath = targetFolder.ToFullPath(location.RootPath);
        if (!Directory.Exists(targetFolderPath))
        {
            throw new DirectoryNotFoundException($"Target library folder does not exist: {targetFolder}");
        }

        var copiedFiles = new List<PreparedAssetFile>();
        var createdFolders = new HashSet<LibraryRelativePath>();
        var copyThrottle = CopyThrottle.Create(importOptions);
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
                        copyThrottle,
                        cancellationToken));
                    continue;
                }

                if (Directory.Exists(sourcePath))
                {
                    var copyResult = await CopyDirectoryAsync(
                        location,
                        sourcePath,
                        targetFolderPath,
                        copyThrottle,
                        cancellationToken);
                    copiedFiles.AddRange(copyResult.CopiedFiles);
                    foreach (var createdFolder in copyResult.CreatedFolders)
                    {
                        createdFolders.Add(createdFolder);
                    }

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

        return new AssetCopyResult(
            copiedFiles,
            createdFolders
                .OrderBy(folder => folder.Value.Count(ch => ch == '/'))
                .ThenBy(folder => folder.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray());
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

    private async Task<AssetCopyResult> CopyDirectoryAsync(
        LibraryLocation location,
        string sourceDirectory,
        string targetFolderPath,
        CopyThrottle? copyThrottle,
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
                try
                {
                    copiedFiles.Add(await CopyImportedFileAsync(
                        location,
                        file,
                        destinationFile,
                        createdDirectories,
                        copyThrottle,
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

            return new AssetCopyResult(copiedFiles, createdDirectories);
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
        CopyThrottle? copyThrottle,
        CancellationToken cancellationToken)
    {
        var destinationPath = GetUniqueFilePath(Path.Combine(targetFolderPath, Path.GetFileName(sourcePath)));
        try
        {
            return await CopyImportedFileAsync(
                location,
                sourcePath,
                destinationPath,
                createdDirectories,
                copyThrottle,
                cancellationToken);
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

    private async Task<PreparedAssetFile> CopyImportedFileAsync(
        LibraryLocation location,
        string sourcePath,
        string destinationPath,
        IReadOnlyList<LibraryRelativePath> createdDirectories,
        CopyThrottle? copyThrottle,
        CancellationToken cancellationToken)
    {
        var sourceFileInfo = new FileInfo(sourcePath);
        var contentHash = await CopyFileAndComputeHashAsync(
            sourcePath,
            destinationPath,
            copyThrottle,
            cancellationToken);
        ApplySourceFileMetadata(sourceFileInfo, destinationPath);

        return CreatePreparedAssetFile(
            location,
            destinationPath,
            sourcePath,
            createdDirectories,
            contentHash);
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
        return ToPreparedAssetFile(storedFile, sourcePath, createdDirectories);
    }

    private async Task<StoredContentFile> BuildStoredContentFileAsync(
        LibraryLocation location,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        return CreateStoredContentFile(
            location,
            fullPath,
            fileInfo,
            await HashFileAsync(fullPath, cancellationToken));
    }

    private static async Task<string> HashFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(fullPath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> CopyFileAndComputeHashAsync(
        string sourcePath,
        string destinationPath,
        CopyThrottle? copyThrottle,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            await using var sourceStream = new FileStream(
                sourcePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = CopyBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            await using var destinationStream = new FileStream(
                destinationPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = CopyBufferSize,
                    Options = FileOptions.Asynchronous
                });

            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while (true)
            {
                var bytesRead = await sourceStream.ReadAsync(
                    buffer.AsMemory(0, CopyBufferSize),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, bytesRead);
                await destinationStream.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);

                if (copyThrottle is not null)
                {
                    await copyThrottle.ThrottleAsync(bytesRead, cancellationToken);
                }
            }

            await destinationStream.FlushAsync(cancellationToken);
            return Convert.ToHexString(incrementalHash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class CopyThrottle
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly long _maxBytesPerSecond;
        private long _copiedBytes;

        private CopyThrottle(long maxBytesPerSecond)
        {
            _maxBytesPerSecond = maxBytesPerSecond;
        }

        public static CopyThrottle? Create(AssetImportOptions? importOptions)
        {
            return importOptions?.MaxCopyBytesPerSecond is > 0
                ? new CopyThrottle(importOptions.MaxCopyBytesPerSecond.Value)
                : null;
        }

        public async Task ThrottleAsync(int copiedBytes, CancellationToken cancellationToken)
        {
            _copiedBytes += copiedBytes;

            var expectedElapsed = TimeSpan.FromSeconds((double)_copiedBytes / _maxBytesPerSecond);
            var delay = expectedElapsed - _elapsed.Elapsed;
            if (delay > TimeSpan.FromMilliseconds(10))
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static void ApplySourceFileMetadata(FileInfo sourceFileInfo, string destinationPath)
    {
        File.SetAttributes(destinationPath, sourceFileInfo.Attributes);
        File.SetCreationTimeUtc(destinationPath, sourceFileInfo.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destinationPath, sourceFileInfo.LastWriteTimeUtc);
    }

    private PreparedAssetFile CreatePreparedAssetFile(
        LibraryLocation location,
        string destinationPath,
        string? sourcePath,
        IReadOnlyList<LibraryRelativePath> createdDirectories,
        string contentHash)
    {
        var storedFile = CreateStoredContentFile(
            location,
            destinationPath,
            new FileInfo(destinationPath),
            contentHash);

        return ToPreparedAssetFile(storedFile, sourcePath, createdDirectories);
    }

    private PreparedAssetFile ToPreparedAssetFile(
        StoredContentFile storedFile,
        string? sourcePath,
        IReadOnlyList<LibraryRelativePath> createdDirectories)
    {
        return storedFile.ToPreparedAssetFile(sourcePath, DateTimeOffset.UtcNow) with
        {
            CreatedDirectories = createdDirectories
        };
    }

    private StoredContentFile CreateStoredContentFile(
        LibraryLocation location,
        string fullPath,
        FileInfo fileInfo,
        string contentHash)
    {
        var extension = fileInfo.Extension;

        return new StoredContentFile(
            fileInfo.Name,
            ToLibraryRelativePath(location, fullPath),
            _assetTypeResolver.Resolve(extension),
            extension,
            fileInfo.Length,
            fileInfo.CreationTimeUtc,
            fileInfo.LastWriteTimeUtc,
            contentHash);
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
