using AssetManager.Application.Library;
using AssetManager.Desktop.Preview;
using AssetManager.Infrastructure.Storage.Library;

namespace AssetManager.Desktop;

public static class DesktopBootstrapper
{
    public static MainWindow CreateMainWindow()
    {
        var assetTypeResolver = new BuiltInAssetTypeResolver();
        var libraryService = new LibraryApplicationService(
            new SqliteAssetLibraryRepository(),
            new FileSystemAssetContentStore(assetTypeResolver),
            assetTypeResolver,
            new FileAssetActivityLog());
        var knownLibraryService = new KnownLibraryApplicationService(
            libraryService,
            new JsonKnownLibraryStore());

        return new MainWindow(
            libraryService,
            knownLibraryService,
            BuiltInAssetPreviewRenderers.Create());
    }
}
