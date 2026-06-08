using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop;

public sealed class LibraryFolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public LibraryFolderNode(LibraryRelativePath relativePath, LibraryFolderNode? parent = null)
    {
        RelativePath = relativePath;
        Parent = parent;
    }

    public LibraryRelativePath RelativePath { get; }

    public LibraryFolderNode? Parent { get; }

    public ObservableCollection<LibraryFolderNode> Children { get; } = new();

    public string DisplayName => RelativePath.IsRoot
        ? Localization.LocalizationManager.Get("LibraryRootFolder")
        : Path.GetFileName(RelativePath.Value.Replace('/', Path.DirectorySeparatorChar));

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ExpandAncestors()
    {
        var current = Parent;
        while (current is not null)
        {
            current.IsExpanded = true;
            current = current.Parent;
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
