using AssetManager.Application.BackgroundTasks;
using AssetManager.Application.Library;
using AssetManager.Desktop;
using AssetManager.Domain.Library;
using AssetManager.Infrastructure.Storage.Library;
using AssetManager.Plugin.Abstractions;
using AssetManager.Plugin.Host;
using AssetManager.Plugin.Sdk;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AssetManager.Tests;

public class UnitTest1
{
    [Fact]
    public async Task OpenOrCreate_CreatesOnlyManagementDirectoryInEmptyRoot()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            await CreateLibraryService().OpenOrCreateAsync(tempRoot);

            var rootEntries = Directory.EnumerateFileSystemEntries(tempRoot)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal([LibraryLocation.ManagementDirectoryName], rootEntries);
            Assert.True(File.Exists(Path.Combine(
                tempRoot,
                LibraryLocation.ManagementDirectoryName,
                LibraryLocation.DatabaseFileName)));
            Assert.True(Directory.Exists(Path.Combine(
                tempRoot,
                LibraryLocation.ManagementDirectoryName,
                LibraryLocation.LogsDirectoryName)));
            Assert.True(Directory.Exists(Path.Combine(
                tempRoot,
                LibraryLocation.ManagementDirectoryName,
                LibraryLocation.ThumbnailsDirectoryName)));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ImportPaths_CopiesIntoCurrentFolderWithoutAssetsDirectory()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "photo.png");
            File.WriteAllText(sourceFile, "fake image");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var targetFolder = LibraryRelativePath.Create("images");
            await service.CreateFolderAsync(session.Location, targetFolder);

            var result = await service.ImportPathsAsync(session.Location, targetFolder, [sourceFile]);

            var imported = Assert.Single(result.ImportedAssets);
            Assert.Equal("images/photo.png", imported.LibraryRelativePath.Value);
            Assert.Equal(Path.GetFullPath(sourceFile), imported.SourcePath);
            Assert.True(File.Exists(Path.Combine(libraryRoot, "images", "photo.png")));
            Assert.False(Directory.Exists(Path.Combine(libraryRoot, "assets")));
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task ImportPaths_AutoRenamesFileNameConflicts()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var firstDirectory = Path.Combine(sourceRoot, "first");
            var secondDirectory = Path.Combine(sourceRoot, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);

            var firstFile = Path.Combine(firstDirectory, "demo.txt");
            var secondFile = Path.Combine(secondDirectory, "demo.txt");
            File.WriteAllText(firstFile, "first");
            File.WriteAllText(secondFile, "second");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);

            var result = await service.ImportPathsAsync(
                session.Location,
                LibraryRelativePath.Root,
                [firstFile, secondFile]);

            var relativePaths = result.ImportedAssets
                .Select(asset => asset.LibraryRelativePath.Value)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(["demo (1).txt", "demo.txt"], relativePaths);
            Assert.True(File.Exists(Path.Combine(libraryRoot, "demo.txt")));
            Assert.True(File.Exists(Path.Combine(libraryRoot, "demo (1).txt")));
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task ImportPaths_PreservesContentHashForImportedFile()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "clip.txt");
            File.WriteAllText(sourceFile, "content hash regression");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();

            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(libraryRoot, "clip.txt"))));

            Assert.Equal(expectedHash, imported.ContentHash);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task ImportPaths_ReturnsCreatedFoldersForEmptyDirectoryImport()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFolder = Path.Combine(sourceRoot, "package");
            Directory.CreateDirectory(Path.Combine(sourceFolder, "nested"));

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var result = await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFolder]);

            Assert.Empty(result.ImportedAssets);
            Assert.Equal(
                ["package", "package/nested"],
                result.ImportedFolders
                    .Select(folder => folder.Value)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Assert.True(Directory.Exists(Path.Combine(libraryRoot, "package", "nested")));
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task ImportPaths_RejectsDirectoryImportIntoItself()
    {
        var libraryRoot = CreateTempRoot();

        try
        {
            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var targetFolder = LibraryRelativePath.Create("collection");
            await service.CreateFolderAsync(session.Location, targetFolder);

            var sourceFolder = Path.Combine(libraryRoot, "collection");
            File.WriteAllText(Path.Combine(sourceFolder, "source.txt"), "content");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ImportPathsAsync(session.Location, targetFolder, [sourceFolder]));

            Assert.False(Directory.Exists(Path.Combine(sourceFolder, "collection")));
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
        }
    }

    [Fact]
    public async Task Search_MatchesFileNameTagsNotesAndRequiredTags()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "note.txt");
            File.WriteAllText(sourceFile, "content");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();

            await service.UpdateMetadataAsync(
                session.Location,
                imported.Id,
                "warm texture reference",
                ["portrait"]);

            Assert.Contains(
                await service.SearchAsync(session.Location, LibraryRelativePath.Root, "note", []),
                asset => asset.Id == imported.Id);
            Assert.Contains(
                await service.SearchAsync(session.Location, LibraryRelativePath.Root, "portrait", []),
                asset => asset.Id == imported.Id);
            Assert.Contains(
                await service.SearchAsync(session.Location, LibraryRelativePath.Root, "warm", []),
                asset => asset.Id == imported.Id);
            Assert.Contains(
                await service.SearchAsync(session.Location, LibraryRelativePath.Root, string.Empty, ["portrait"]),
                asset => asset.Id == imported.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task Search_TreatsFtsOperatorsAsPlainText()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "operator.txt");
            File.WriteAllText(sourceFile, "content");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();

            await service.UpdateMetadataAsync(session.Location, imported.Id, "AND OR NOT", []);

            Assert.Contains(
                await service.SearchAsync(session.Location, LibraryRelativePath.Root, "AND", []),
                asset => asset.Id == imported.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task Preview_ReturnsExpectedKindsForFirstBatchTypes()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var image = WriteSourceFile(sourceRoot, "image.png", "fake image");
            var video = WriteSourceFile(sourceRoot, "video.mp4", "fake video");
            var audio = WriteSourceFile(sourceRoot, "audio.mp3", "fake audio");
            var text = WriteSourceFile(sourceRoot, "snippet.txt", "hello text snippet");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = await service.ImportPathsAsync(
                session.Location,
                LibraryRelativePath.Root,
                [image, video, audio, text]);

            var previews = new Dictionary<string, AssetPreview>();
            foreach (var asset in imported.ImportedAssets)
            {
                previews[asset.DisplayName] = await service.GetPreviewAsync(session.Location, asset.Id);
            }

            Assert.Equal(AssetTypeId.Image, previews["image.png"].TypeId);
            Assert.Equal(AssetTypeId.Video, previews["video.mp4"].TypeId);
            Assert.Equal(AssetTypeId.Audio, previews["audio.mp3"].TypeId);
            Assert.Equal(AssetTypeId.Text, previews["snippet.txt"].TypeId);
            Assert.Contains("hello text snippet", previews["snippet.txt"].TextContent);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task Synchronize_ReattachesRenamedFileByContentHash()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "original.txt");
            File.WriteAllText(sourceFile, "stable content");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();

            File.Move(Path.Combine(libraryRoot, "original.txt"), Path.Combine(libraryRoot, "renamed.txt"));

            var syncResult = await service.SynchronizeAsync(session.Location);
            var synchronizedAsset = (await service.SearchAsync(
                    session.Location,
                    LibraryRelativePath.Root,
                    "renamed",
                    []))
                .Single(asset => asset.Id == imported.Id);

            Assert.Equal(1, syncResult.MovedCount);
            Assert.Equal("renamed.txt", synchronizedAsset.LibraryRelativePath.Value);
            Assert.Equal(AssetStatus.Available, synchronizedAsset.Status);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task Synchronize_DeletesAssetRecordWhenFileIsMissing()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "deleted.txt");
            File.WriteAllText(sourceFile, "delete me");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();

            File.Delete(Path.Combine(libraryRoot, "deleted.txt"));

            var syncResult = await service.SynchronizeAsync(session.Location);
            var asset = await new SqliteAssetLibraryRepository().GetByIdAsync(session.Location, imported.Id);
            var assets = await service.SearchAsync(session.Location, LibraryRelativePath.Root, string.Empty, []);

            Assert.Equal(1, syncResult.MissingCount);
            Assert.Null(asset);
            Assert.DoesNotContain(assets, candidate => candidate.Id == imported.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task SynchronizePaths_RegistersOnlyChangedNewFile()
    {
        var libraryRoot = CreateTempRoot();

        try
        {
            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var newFile = Path.Combine(libraryRoot, "external.txt");
            File.WriteAllText(newFile, "external content");

            var syncResult = await service.SynchronizePathsAsync(session.Location, [newFile]);
            var assets = await service.SearchAsync(session.Location, LibraryRelativePath.Root, "external", []);

            var asset = Assert.Single(assets);
            Assert.Equal("external.txt", asset.LibraryRelativePath.Value);
            Assert.Equal(0, syncResult.UpdatedCount);
            Assert.Equal(0, syncResult.MovedCount);
            Assert.Equal(0, syncResult.MissingCount);
            Assert.Equal(1, syncResult.NewAssetCount);
            Assert.Contains(syncResult.AffectedAssets ?? [], changed => changed.Id == asset.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
        }
    }

    [Fact]
    public async Task SynchronizePaths_DeletesOnlyChangedMissingFile()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "removed.txt");
            File.WriteAllText(sourceFile, "remove me");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();
            var removedFile = Path.Combine(libraryRoot, "removed.txt");
            File.Delete(removedFile);

            var syncResult = await service.SynchronizePathsAsync(session.Location, [removedFile]);
            var asset = await new SqliteAssetLibraryRepository().GetByIdAsync(session.Location, imported.Id);

            Assert.Equal(0, syncResult.UpdatedCount);
            Assert.Equal(0, syncResult.MovedCount);
            Assert.Equal(1, syncResult.MissingCount);
            Assert.Equal(0, syncResult.NewAssetCount);
            Assert.Null(asset);
            Assert.Contains(syncResult.RemovedAssetIds ?? [], id => id == imported.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task SynchronizePaths_ReattachesChangedRenameByContentHash()
    {
        var libraryRoot = CreateTempRoot();
        var sourceRoot = CreateTempRoot();

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "before.txt");
            File.WriteAllText(sourceFile, "rename me");

            var service = CreateLibraryService();
            var session = await service.OpenOrCreateAsync(libraryRoot);
            var imported = (await service.ImportPathsAsync(session.Location, LibraryRelativePath.Root, [sourceFile]))
                .ImportedAssets
                .Single();
            await service.UpdateMetadataAsync(session.Location, imported.Id, "keep metadata", ["renamed"]);

            var oldPath = Path.Combine(libraryRoot, "before.txt");
            var newPath = Path.Combine(libraryRoot, "after.txt");
            File.Move(oldPath, newPath);

            var syncResult = await service.SynchronizePathsAsync(session.Location, [oldPath, newPath]);
            var asset = await new SqliteAssetLibraryRepository().GetByIdAsync(session.Location, imported.Id);

            Assert.Equal(0, syncResult.UpdatedCount);
            Assert.Equal(1, syncResult.MovedCount);
            Assert.Equal(0, syncResult.MissingCount);
            Assert.Equal(0, syncResult.NewAssetCount);
            Assert.NotNull(asset);
            Assert.Equal("after.txt", asset.LibraryRelativePath.Value);
            Assert.Contains("renamed", asset.Tags);
            Assert.Contains(syncResult.AffectedAssets ?? [], changed => changed.Id == imported.Id);
        }
        finally
        {
            DeleteTempRoot(libraryRoot);
            DeleteTempRoot(sourceRoot);
        }
    }

    [Fact]
    public async Task KnownLibraryRegistry_RegisterAndOpenPersistsLibraryForReload()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var registryPath = Path.Combine(tempRoot, "settings", "known-libraries.json");
            var libraryRoot = Path.Combine(tempRoot, "libraries", "primary");
            Directory.CreateDirectory(libraryRoot);

            var service = CreateKnownLibraryService(registryPath);
            var result = await service.RegisterAndOpenAsync(libraryRoot);

            var reloadedStore = new JsonKnownLibraryStore(registryPath);
            var libraries = await reloadedStore.ListAsync();
            var activeLibrary = await reloadedStore.GetActiveAsync();

            var knownLibrary = Assert.Single(libraries);
            Assert.Equal(result.KnownLibrary.Id, knownLibrary.Id);
            Assert.Equal(Path.GetFullPath(libraryRoot), knownLibrary.RootPath);
            Assert.True(knownLibrary.IsAvailable);
            Assert.NotNull(activeLibrary);
            Assert.Equal(knownLibrary.Id, activeLibrary.Id);
            Assert.True(File.Exists(Path.Combine(
                libraryRoot,
                LibraryLocation.ManagementDirectoryName,
                LibraryLocation.DatabaseFileName)));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task KnownLibraryRegistry_DuplicateRegisterKeepsSingleEntry()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var registryPath = Path.Combine(tempRoot, "known-libraries.json");
            var libraryRoot = Path.Combine(tempRoot, "library");
            Directory.CreateDirectory(libraryRoot);

            var service = CreateKnownLibraryService(registryPath);
            var first = await service.RegisterAndOpenAsync(libraryRoot);
            var second = await service.RegisterAndOpenAsync(libraryRoot);

            var libraries = await service.ListAsync();
            var knownLibrary = Assert.Single(libraries);

            Assert.Equal(first.KnownLibrary.Id, second.KnownLibrary.Id);
            Assert.Equal(first.KnownLibrary.Id, knownLibrary.Id);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task KnownLibraryRegistry_OpenRegisteredSwitchesActiveLibrary()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var registryPath = Path.Combine(tempRoot, "known-libraries.json");
            var firstRoot = Path.Combine(tempRoot, "first");
            var secondRoot = Path.Combine(tempRoot, "second");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);

            var service = CreateKnownLibraryService(registryPath);
            var first = await service.RegisterAndOpenAsync(firstRoot);
            await service.RegisterAndOpenAsync(secondRoot);

            var opened = await service.OpenRegisteredAsync(first.KnownLibrary.Id);
            var active = await service.GetActiveAsync();

            Assert.Equal(first.KnownLibrary.Id, opened.KnownLibrary.Id);
            Assert.NotNull(active);
            Assert.Equal(first.KnownLibrary.Id, active.Id);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task KnownLibraryRegistry_DoesNotOpenUnavailableRegisteredLibrary()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var registryPath = Path.Combine(tempRoot, "known-libraries.json");
            var libraryRoot = Path.Combine(tempRoot, "library");
            Directory.CreateDirectory(libraryRoot);

            var service = CreateKnownLibraryService(registryPath);
            var registered = await service.RegisterAndOpenAsync(libraryRoot);
            Directory.Delete(libraryRoot, recursive: true);

            var libraries = await service.ListAsync();
            var unavailable = Assert.Single(libraries);

            Assert.False(unavailable.IsAvailable);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                service.OpenRegisteredAsync(registered.KnownLibrary.Id));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void LocalizationResourceDictionaries_HaveMatchingKeys()
    {
        var repositoryRoot = FindRepositoryRoot();
        var localizationRoot = Path.Combine(
            repositoryRoot,
            "src",
            "AssetManager.Desktop",
            "Localization");

        var enUsKeys = ReadResourceKeys(Path.Combine(localizationRoot, "Strings.en-US.xaml"));
        var zhCnKeys = ReadResourceKeys(Path.Combine(localizationRoot, "Strings.zh-CN.xaml"));

        Assert.Equal(enUsKeys, zhCnKeys);
    }

    [Fact]
    public void LocalizationResourceDictionaries_ContainReferencedKeys()
    {
        var repositoryRoot = FindRepositoryRoot();
        var desktopRoot = Path.Combine(repositoryRoot, "src", "AssetManager.Desktop");
        var localizationRoot = Path.Combine(desktopRoot, "Localization");

        var resourceKeys = ReadResourceKeys(Path.Combine(localizationRoot, "Strings.en-US.xaml"))
            .ToHashSet(StringComparer.Ordinal);
        var referencedKeys = ReadReferencedLocalizationKeys(desktopRoot);

        var missingKeys = referencedKeys
            .Where(key => !resourceKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missingKeys);
    }

    [Fact]
    public void Architecture_DomainDoesNotReferenceFrameworkOrOuterLayers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var domainProject = Path.Combine(repositoryRoot, "src", "AssetManager.Domain", "AssetManager.Domain.csproj");
        var domainRoot = Path.GetDirectoryName(domainProject)!;

        AssertNoProjectReferences(domainProject);
        Assert.DoesNotContain(ReadUsingNamespaces(domainRoot), @namespace =>
            @namespace.StartsWith("System.Windows", StringComparison.Ordinal)
            || @namespace.StartsWith("Microsoft.", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Application", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Infrastructure", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Desktop", StringComparison.Ordinal));
    }

    [Fact]
    public void Architecture_ApplicationDoesNotReferenceInfrastructureDesktopOrPlugins()
    {
        var repositoryRoot = FindRepositoryRoot();
        var applicationProject = Path.Combine(repositoryRoot, "src", "AssetManager.Application", "AssetManager.Application.csproj");
        var applicationRoot = Path.GetDirectoryName(applicationProject)!;

        var projectReferences = ReadProjectReferences(applicationProject);
        Assert.Contains(projectReferences, reference => reference.Contains("AssetManager.Domain.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(projectReferences, reference =>
            reference.Contains("Infrastructure", StringComparison.Ordinal)
            || reference.Contains("Desktop", StringComparison.Ordinal)
            || reference.Contains("Plugin", StringComparison.Ordinal));

        Assert.DoesNotContain(ReadUsingNamespaces(applicationRoot), @namespace =>
            @namespace.StartsWith("System.Windows", StringComparison.Ordinal)
            || @namespace.StartsWith("Microsoft.Data", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Infrastructure", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Desktop", StringComparison.Ordinal)
            || @namespace.StartsWith("AssetManager.Plugin", StringComparison.Ordinal));
    }

    [Fact]
    public void BackgroundTaskCenter_StartUpdateAndCompletePublishesSnapshots()
    {
        var taskCenter = new InMemoryBackgroundTaskCenter();
        IReadOnlyList<BackgroundTaskSnapshot>? lastSnapshots = null;
        taskCenter.SnapshotsChanged += snapshots => lastSnapshots = snapshots;

        using var session = taskCenter.StartTask(new BackgroundTaskStartRequest(
            BackgroundTaskKind.ImportAssets,
            "Import Assets",
            "Preparing",
            IsCancelable: true));

        session.Update("Copying", new BackgroundTaskProgress(2, 5, "files"));
        session.Complete("Completed");

        var snapshot = Assert.Single(taskCenter.GetSnapshots());
        Assert.NotNull(lastSnapshots);
        Assert.Equal(snapshot.Id, lastSnapshots![0].Id);
        Assert.Equal(BackgroundTaskState.Completed, snapshot.State);
        Assert.Equal("Completed", snapshot.StatusText);
        Assert.Equal(2, snapshot.Progress.CompletedUnits);
        Assert.Equal(5, snapshot.Progress.TotalUnits);
    }

    [Fact]
    public void BackgroundTaskCenter_CompletePartiallyUsesPartialSuccessState()
    {
        var taskCenter = new InMemoryBackgroundTaskCenter();

        using var session = taskCenter.StartTask(new BackgroundTaskStartRequest(
            BackgroundTaskKind.PluginOperation,
            "Plugin Task",
            "Running"));

        session.CompletePartially("Completed with warnings", "1 item skipped");

        var snapshot = Assert.Single(taskCenter.GetSnapshots());
        Assert.Equal(BackgroundTaskState.PartialSuccess, snapshot.State);
        Assert.Equal("Completed with warnings", snapshot.StatusText);
        Assert.Equal("1 item skipped", snapshot.ErrorMessage);
        Assert.Null(snapshot.ErrorType);
    }

    [Fact]
    public void BackgroundTaskCenter_RequestCancelSignalsCancellationToken()
    {
        var taskCenter = new InMemoryBackgroundTaskCenter();
        using var session = taskCenter.StartTask(new BackgroundTaskStartRequest(
            BackgroundTaskKind.ImportAssets,
            "Import Assets",
            "Preparing",
            IsCancelable: true));

        Assert.True(taskCenter.RequestCancel(session.TaskId));
        Assert.True(session.CancellationToken.IsCancellationRequested);

        session.Cancel("Canceled");

        var snapshot = Assert.Single(taskCenter.GetSnapshots());
        Assert.Equal(BackgroundTaskState.Canceled, snapshot.State);
        Assert.False(snapshot.IsCancelable);
    }

    [Fact]
    public void BackgroundTaskCenter_FailCapturesExceptionType()
    {
        var taskCenter = new InMemoryBackgroundTaskCenter();

        using var session = taskCenter.StartTask(new BackgroundTaskStartRequest(
            BackgroundTaskKind.ImportAssets,
            "Import Assets",
            "Preparing"));

        session.Fail(new IOException("disk error"), "Import failed");

        var snapshot = Assert.Single(taskCenter.GetSnapshots());
        Assert.Equal(BackgroundTaskState.Failed, snapshot.State);
        Assert.Equal("disk error", snapshot.ErrorMessage);
        Assert.Equal(nameof(IOException), snapshot.ErrorType);
    }

    [Fact]
    public void BackgroundTaskCenter_TrimsFinishedHistory()
    {
        var taskCenter = new InMemoryBackgroundTaskCenter(historyLimit: 2);

        for (var index = 0; index < 3; index++)
        {
            using var session = taskCenter.StartTask(new BackgroundTaskStartRequest(
                BackgroundTaskKind.PluginOperation,
                $"Task {index}",
                "Running"));
            session.Complete($"Completed {index}");
        }

        var snapshots = taskCenter.GetSnapshots();

        Assert.Equal(2, snapshots.Count);
        Assert.DoesNotContain(snapshots, snapshot => snapshot.Title == "Task 0");
    }

    [Fact]
    public void BackgroundTaskPresentation_SelectSummaryTaskPrefersImportOverNewerThumbnailTask()
    {
        var baseTime = DateTimeOffset.Parse("2026-06-08T12:00:00+08:00");
        var snapshots = new[]
        {
            CreateSnapshot(
                Guid.NewGuid(),
                BackgroundTaskKind.GenerateThumbnails,
                BackgroundTaskState.Running,
                "Generating thumbnails",
                baseTime.AddMinutes(2)),
            CreateSnapshot(
                Guid.NewGuid(),
                BackgroundTaskKind.ImportAssets,
                BackgroundTaskState.Running,
                "Importing assets",
                baseTime)
        };

        var summary = BackgroundTaskPresentation.SelectSummaryTask(snapshots);

        Assert.NotNull(summary);
        Assert.Equal(BackgroundTaskKind.ImportAssets, summary!.Kind);
    }

    [Fact]
    public void BackgroundTaskPresentation_OrderForDisplayPlacesRunningTasksByPriorityThenRecentHistory()
    {
        var baseTime = DateTimeOffset.Parse("2026-06-08T12:00:00+08:00");
        var pluginTask = CreateSnapshot(
            Guid.NewGuid(),
            BackgroundTaskKind.PluginOperation,
            BackgroundTaskState.Running,
            "Plugin task",
            baseTime.AddMinutes(1));
        var syncTask = CreateSnapshot(
            Guid.NewGuid(),
            BackgroundTaskKind.SynchronizeLibrary,
            BackgroundTaskState.Running,
            "Sync task",
            baseTime.AddMinutes(3));
        var completedTask = CreateSnapshot(
            Guid.NewGuid(),
            BackgroundTaskKind.ImportAssets,
            BackgroundTaskState.Completed,
            "Completed task",
            baseTime.AddMinutes(-2),
            baseTime.AddMinutes(4));

        var ordered = BackgroundTaskPresentation.OrderForDisplay([pluginTask, completedTask, syncTask]);

        Assert.Collection(
            ordered,
            snapshot => Assert.Equal(syncTask.Id, snapshot.Id),
            snapshot => Assert.Equal(pluginTask.Id, snapshot.Id),
            snapshot => Assert.Equal(completedTask.Id, snapshot.Id));
    }

    [Fact]
    public void LibraryFileSystemChangeMonitor_IgnoresAssetManagerManagementDirectory()
    {
        var root = Path.Combine("D:", "library");

        Assert.True(LibraryFileSystemChangeMonitor.IsManagementPath(
            root,
            Path.Combine(root, LibraryLocation.ManagementDirectoryName, LibraryLocation.DatabaseFileName)));
        Assert.False(LibraryFileSystemChangeMonitor.IsManagementPath(
            root,
            Path.Combine(root, "images", "photo.png")));
    }

    [Fact]
    public void PluginRegistry_AggregatesContributionsAndRejectsDuplicateIds()
    {
        var registry = new PluginRegistry();
        var plugin = new TestPlugin(
            "demo.preview",
            new PluginContribution(
                [new AssetTypeContribution("font", [".ttf", ".otf"])],
                [new PreviewContribution("font", "font-preview")],
                [new UiContribution("asset.details", "font-panel")]));

        registry.Register(plugin);
        var contribution = registry.DescribeAll();

        Assert.Single(registry.Plugins);
        Assert.Single(contribution.AssetTypes);
        Assert.Single(contribution.Previews);
        Assert.Single(contribution.UiElements);
        Assert.Equal("font", contribution.AssetTypes[0].TypeId);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new TestPlugin("demo.preview")));
    }

    [Fact]
    public async Task GetByRelativePathsAsync_Over1000PathsReturnsResults()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var repo = new SqliteAssetLibraryRepository();
            var location = LibraryLocation.Create(tempRoot);
            await repo.InitializeAsync(location);

            var assets = Enumerable.Range(0, 1100).Select(i => new PreparedAssetFile(
                $"Asset{i}.txt",
                LibraryRelativePath.Create($"asset{i}.txt"),
                null,
                AssetTypeId.Text,
                ".txt",
                100,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                $"hash{i}",
                [])).ToArray();

            await repo.AddAssetsAsync(location, assets);

            var allPaths = assets.Select(a => a.LibraryRelativePath).ToArray();
            var results = await repo.GetByRelativePathsAsync(location, allPaths);

            Assert.Equal(1100, results.Count);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task GetByRelativePathPrefixesAsync_Over1000PrefixesReturnsResults()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var repo = new SqliteAssetLibraryRepository();
            var location = LibraryLocation.Create(tempRoot);
            await repo.InitializeAsync(location);

            var assets = Enumerable.Range(0, 1100).Select(i => new PreparedAssetFile(
                $"Asset{i}.txt",
                LibraryRelativePath.Create($"folder{i}/asset.txt"),
                null,
                AssetTypeId.Text,
                ".txt",
                100,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                $"hash{i}",
                [LibraryRelativePath.Create($"folder{i}")])).ToArray();

            await repo.AddAssetsAsync(location, assets);

            var prefixPaths = assets.Select(a => a.LibraryRelativePath).ToArray();
            var results = await repo.GetByRelativePathPrefixesAsync(location, prefixPaths);

            Assert.Equal(1100, results.Count);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static LibraryApplicationService CreateLibraryService()
    {
        var assetTypeResolver = new BuiltInAssetTypeResolver();
        return new LibraryApplicationService(
            new SqliteAssetLibraryRepository(),
            new FileSystemAssetContentStore(assetTypeResolver),
            assetTypeResolver,
            new FileAssetActivityLog());
    }

    private static KnownLibraryApplicationService CreateKnownLibraryService(string registryPath)
    {
        return new KnownLibraryApplicationService(
            CreateLibraryService(),
            new JsonKnownLibraryStore(registryPath));
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string WriteSourceFile(string sourceRoot, string name, string content)
    {
        var path = Path.Combine(sourceRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string[] ReadResourceKeys(string resourcePath)
    {
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(resourcePath)
            .Root!
            .Elements()
            .Select(element => element.Attribute(xNamespace + "Key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    private static string[] ReadReferencedLocalizationKeys(string desktopRoot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xamlFile in Directory.EnumerateFiles(desktopRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(xamlFile);
            foreach (Match match in Regex.Matches(content, @"\{DynamicResource\s+([A-Za-z0-9_.-]+)\}"))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        foreach (var csharpFile in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csharpFile);
            foreach (Match match in Regex.Matches(content, @"LocalizationManager\.(?:Get|Format)\(""([^""]+)"""))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] ReadUsingNamespaces(string sourceRoot)
    {
        return Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path)
                .Select(line => Regex.Match(line, @"^\s*using\s+([^;]+);"))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value.Trim()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ReadProjectReferences(string projectPath)
    {
        return XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    private static void AssertNoProjectReferences(string projectPath)
    {
        Assert.Empty(ReadProjectReferences(projectPath));
    }

    private static BackgroundTaskSnapshot CreateSnapshot(
        Guid id,
        BackgroundTaskKind kind,
        BackgroundTaskState state,
        string statusText,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt = null)
    {
        return new BackgroundTaskSnapshot(
            id,
            kind,
            kind.ToString(),
            statusText,
            state,
            BackgroundTaskProgress.None,
            false,
            startedAt,
            finishedAt);
    }

    private sealed class TestPlugin : AssetManagerPluginBase
    {
        private readonly PluginContribution _contribution;

        public TestPlugin(string id, PluginContribution? contribution = null)
            : base(new PluginManifest(id, "Test Plugin", new Version(1, 0, 0)))
        {
            _contribution = contribution ?? PluginContribution.Empty;
        }

        public override PluginContribution Describe()
        {
            return _contribution;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AssetManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
