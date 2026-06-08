using AssetManager.Application.Library;

namespace AssetManager.Desktop.Preview;

public interface IAssetPreviewRenderer
{
    bool CanRender(AssetPreview preview);

    void Render(AssetPreview preview, PreviewSurface surface);
}
