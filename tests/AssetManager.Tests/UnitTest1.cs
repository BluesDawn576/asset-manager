using AssetManager.Application.Library;
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
    public async Task Synchronize_MarksDeletedFileMissing()
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

            Assert.Equal(1, syncResult.MissingCount);
            Assert.NotNull(asset);
            Assert.Equal(AssetStatus.Missing, asset.Status);
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
