using System.Windows.Media.Imaging;
using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop.Preview;

public sealed class ImageAssetPreviewRenderer : IAssetPreviewRenderer
{
    public bool CanRender(AssetPreview preview)
    {
        return preview.TypeId == AssetTypeId.Image;
    }

    public void Render(AssetPreview preview, PreviewSurface surface)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(preview.FullPath);
        image.EndInit();
        surface.ShowImage(image);
    }
}
