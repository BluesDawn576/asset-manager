using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

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
        imagePreview.Source = null;
        imagePreview.Visibility = Visibility.Collapsed;
        mediaPreview.Stop();
        mediaPreview.Source = null;
        mediaPreviewPanel.Visibility = Visibility.Collapsed;
        textPreview.Text = string.Empty;
        textPreview.Visibility = Visibility.Collapsed;
        unsupportedPreviewText.Visibility = Visibility.Collapsed;
    }

    public void ShowImage(BitmapImage image)
    {
        imagePreview.Source = image;
        imagePreview.Visibility = Visibility.Visible;
    }

    public void ShowMedia(Uri source)
    {
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
