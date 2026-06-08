using AssetManager.Application.Library;
using AssetManager.Desktop.Localization;
using AssetManager.Desktop.Preview;
using AssetManager.Infrastructure.Storage.Library;
using AssetManager.Infrastructure.Windows;

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
            BuiltInAssetPreviewRenderers.Create(),
            new UiSettingsStore(),
            new WindowsThumbnailCacheService());
    }
}
