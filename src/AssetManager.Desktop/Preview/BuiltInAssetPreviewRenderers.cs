namespace AssetManager.Desktop.Preview;

public static class BuiltInAssetPreviewRenderers
{
    public static IReadOnlyList<IAssetPreviewRenderer> Create()
    {
        return
        [
            new ImageAssetPreviewRenderer(),
            new MediaAssetPreviewRenderer(),
            new TextAssetPreviewRenderer()
        ];
    }
}
