using System.Windows.Media.Imaging;
using XamlAnimatedGif;
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
        if (AnimatedImageDetector.IsAnimated(preview.FullPath))
        {
            // XamlAnimatedGif drives GIF frame compositing, disposal methods,
            // and infinite loop via AnimationBehavior attached property.
            surface.ShowAnimatedGif(new Uri(preview.FullPath));
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(preview.FullPath);
        image.EndInit();
        surface.ShowImage(image);
    }
}