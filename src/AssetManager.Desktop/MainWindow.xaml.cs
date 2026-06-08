using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AssetManager.Domain;
using AssetManager.Infrastructure.Windows;

namespace AssetManager.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private Point _dragStartPoint;
    private string _statusMessage = "Drop files or folders from Explorer.";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Assets.CollectionChanged += Assets_CollectionChanged;
    }

    public ObservableCollection<AssetTransferItem> Assets { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void RootGrid_Drop(object sender, DragEventArgs e)
    {
        var droppedPaths = WindowsFileTransferService.ExtractFilePaths(e.Data);
        if (droppedPaths.Count == 0)
        {
            StatusMessage = "No file paths were found in the drop data.";
            return;
        }

        AddPaths(droppedPaths, "Explorer");
    }

    private void AssetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void AssetList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!ShouldStartDrag(e.GetPosition(null)))
        {
            return;
        }

        var selectedPaths = AssetList.SelectedItems
            .OfType<AssetTransferItem>()
            .Select(item => item.SourcePath)
            .ToArray();

        if (selectedPaths.Length == 0)
        {
            return;
        }

        var dataObject = WindowsFileTransferService.CreateFileDropDataObject(selectedPaths);
        DragDrop.DoDragDrop(AssetList, dataObject, DragDropEffects.Copy);
        StatusMessage = $"Dragged {selectedPaths.Length} item(s).";
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        var selectedPaths = GetSelectedPaths();
        if (selectedPaths.Length == 0)
        {
            StatusMessage = "Select one or more items first.";
            return;
        }

        try
        {
            WindowsFileTransferService.CopyToClipboard(selectedPaths);
            StatusMessage = $"Copied {selectedPaths.Length} item(s) to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        Assets.Clear();
        StatusMessage = "List cleared.";
    }

    private void AddPaths(IEnumerable<string> paths, string sourceName)
    {
        var addedCount = 0;
        var skippedCount = 0;

        foreach (var path in paths)
        {
            if (Assets.Any(asset => string.Equals(asset.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skippedCount++;
                continue;
            }

            Assets.Add(new AssetTransferItem(path));
            addedCount++;
        }

        if (addedCount > 0)
        {
            StatusMessage = skippedCount == 0
                ? $"Added {addedCount} item(s) from {sourceName}."
                : $"Added {addedCount} item(s) from {sourceName}; skipped {skippedCount} duplicate(s).";
        }
        else
        {
            StatusMessage = skippedCount > 0
                ? $"No new items from {sourceName}; skipped {skippedCount} duplicate(s)."
                : $"No new items from {sourceName}.";
        }
    }

    private string[] GetSelectedPaths()
    {
        var selectedPaths = AssetList.SelectedItems
            .OfType<AssetTransferItem>()
            .Select(item => item.SourcePath)
            .ToArray();

        return selectedPaths.Length > 0
            ? selectedPaths
            : Assets.Select(item => item.SourcePath).ToArray();
    }

    private bool ShouldStartDrag(Point currentPosition)
    {
        var horizontalChange = Math.Abs(currentPosition.X - _dragStartPoint.X);
        var verticalChange = Math.Abs(currentPosition.Y - _dragStartPoint.Y);

        return horizontalChange > SystemParameters.MinimumHorizontalDragDistance
               || verticalChange > SystemParameters.MinimumVerticalDragDistance;
    }

    private void Assets_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
