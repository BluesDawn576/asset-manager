using System.Windows;

namespace AssetManager.Desktop;

public partial class TextSnippetDialog : Window
{
    public TextSnippetDialog()
    {
        InitializeComponent();
        FileNameBox.Focus();
        FileNameBox.SelectAll();
    }

    public string FileNameValue => FileNameBox.Text.Trim();

    public string ContentValue => ContentBox.Text;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FileNameValue))
        {
            return;
        }

        DialogResult = true;
    }
}
