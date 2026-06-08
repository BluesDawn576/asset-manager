using System.Windows;

namespace AssetManager.Desktop;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Focus();
    }

    public string Value => ValueBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            return;
        }

        DialogResult = true;
    }
}
