using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop.Preview;

public sealed class AssetPreviewPresenter(
    IReadOnlyList<IAssetPreviewRenderer> renderers,
    PreviewSurface surface)
{
    public void Show(
        AssetPreview preview,
        string missingText,
        string unsupportedText)
    {
        surface.HideAll();
        if (preview.Status == AssetStatus.Missing)
        {
            surface.ShowUnsupported(missingText);
            return;
        }

        var renderer = renderers.FirstOrDefault(renderer => renderer.CanRender(preview));
        if (renderer is null)
        {
            surface.ShowUnsupported(unsupportedText);
            return;
        }

        renderer.Render(preview, surface);
    }

    public void Clear(string text)
    {
        surface.HideAll();
        surface.ShowUnsupported(text);
    }

    public void HideAll()
    {
        surface.HideAll();
    }
}
