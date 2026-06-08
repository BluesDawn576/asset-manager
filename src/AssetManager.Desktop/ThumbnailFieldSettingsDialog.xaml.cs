using System.ComponentModel;
using System.Windows;

namespace AssetManager.Desktop;

public partial class ThumbnailFieldSettingsDialog : Window, INotifyPropertyChanged
{
    private bool _showPath;
    private bool _showSize;
    private bool _showStatus;
    private bool _showTags;
    private bool _showType;

    public ThumbnailFieldSettingsDialog(ThumbnailDisplaySettings currentSettings)
    {
        InitializeComponent();
        DataContext = this;

        _showType = currentSettings.ShowType;
        _showStatus = currentSettings.ShowStatus;
        _showTags = currentSettings.ShowTags;
        _showPath = currentSettings.ShowPath;
        _showSize = currentSettings.ShowSize;
    }

    public bool ShowType
    {
        get => _showType;
        set
        {
            if (_showType == value)
            {
                return;
            }

            _showType = value;
            OnPropertyChanged(nameof(ShowType));
        }
    }

    public bool ShowStatus
    {
        get => _showStatus;
        set
        {
            if (_showStatus == value)
            {
                return;
            }

            _showStatus = value;
            OnPropertyChanged(nameof(ShowStatus));
        }
    }

    public bool ShowTags
    {
        get => _showTags;
        set
        {
            if (_showTags == value)
            {
                return;
            }

            _showTags = value;
            OnPropertyChanged(nameof(ShowTags));
        }
    }

    public bool ShowPath
    {
        get => _showPath;
        set
        {
            if (_showPath == value)
            {
                return;
            }

            _showPath = value;
            OnPropertyChanged(nameof(ShowPath));
        }
    }

    public bool ShowSize
    {
        get => _showSize;
        set
        {
            if (_showSize == value)
            {
                return;
            }

            _showSize = value;
            OnPropertyChanged(nameof(ShowSize));
        }
    }

    public ThumbnailDisplaySettings Settings => new(
        ShowType,
        ShowStatus,
        ShowTags,
        ShowPath,
        ShowSize);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
