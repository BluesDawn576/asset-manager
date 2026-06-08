using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop.Preview;

public sealed class TextAssetPreviewRenderer : IAssetPreviewRenderer
{
    public bool CanRender(AssetPreview preview)
    {
        return preview.TypeId == AssetTypeId.Text;
    }

    public void Render(AssetPreview preview, PreviewSurface surface)
    {
        surface.ShowText(preview.TextContent ?? string.Empty);
    }
}
