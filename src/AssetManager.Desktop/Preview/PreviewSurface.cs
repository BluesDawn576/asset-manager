using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XamlAnimatedGif;

namespace AssetManager.Desktop.Preview;

public sealed class PreviewSurface(
    Image imagePreview,
    MediaElement mediaPreview,
    FrameworkElement mediaPreviewPanel,
    TextBox textPreview,
    TextBlock unsupportedPreviewText)
{
    public void HideAll()
    {
        AnimationBehavior.SetSourceUri(imagePreview, null);
        imagePreview.Source = null;
        imagePreview.Visibility = Visibility.Collapsed;
        mediaPreview.Stop();
        mediaPreview.Source = null;
        mediaPreviewPanel.Visibility = Visibility.Collapsed;
        textPreview.Text = string.Empty;
        textPreview.Visibility = Visibility.Collapsed;
        unsupportedPreviewText.Visibility = Visibility.Collapsed;
    }

    public void ShowImage(BitmapImage? image)
    {
        AnimationBehavior.SetSourceUri(imagePreview, null);
        imagePreview.Source = image;
        imagePreview.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Plays an animated GIF via XamlAnimatedGif's AnimationBehavior.
    /// The library handles frame compositing, disposal methods, and infinite loop.
    /// </summary>
    public void ShowAnimatedGif(Uri source)
    {
        imagePreview.Source = null;
        AnimationBehavior.SetSourceUri(imagePreview, source);
        imagePreview.Visibility = Visibility.Visible;
    }

    public void ShowMedia(Uri source)
    {
        mediaPreview.LoadedBehavior = MediaState.Manual;
        mediaPreview.UnloadedBehavior = MediaState.Stop;
        mediaPreview.Source = source;
        mediaPreviewPanel.Visibility = Visibility.Visible;
    }

    public void ShowText(string text)
    {
        textPreview.Text = text;
        textPreview.Visibility = Visibility.Visible;
    }

    public void ShowUnsupported(string text)
    {
        unsupportedPreviewText.Text = text;
        unsupportedPreviewText.Visibility = Visibility.Visible;
    }
}