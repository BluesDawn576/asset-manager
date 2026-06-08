using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop.Preview;

public sealed class MediaAssetPreviewRenderer : IAssetPreviewRenderer
{
    public bool CanRender(AssetPreview preview)
    {
        return preview.TypeId == AssetTypeId.Video || preview.TypeId == AssetTypeId.Audio;
    }

    public void Render(AssetPreview preview, PreviewSurface surface)
    {
        surface.ShowMedia(new Uri(preview.FullPath));
    }
}
