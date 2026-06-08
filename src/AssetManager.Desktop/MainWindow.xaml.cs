using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AssetManager.Application.Library;
using AssetManager.Desktop.Localization;
using AssetManager.Desktop.Preview;
using AssetManager.Domain.Library;
using AssetManager.Infrastructure.Windows;
using Microsoft.Win32;

namespace AssetManager.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LibraryApplicationService _libraryService;
    private readonly KnownLibraryApplicationService _knownLibraryService;
    private readonly AssetPreviewPresenter _previewPresenter;

    private LibraryLocation? _libraryLocation;
    private LibraryRelativePath _currentFolder = LibraryRelativePath.Root;
    private Point _dragStartPoint;
    private string _statusMessage = string.Empty;
    private string _libraryRootMessage = string.Empty;
    private string _currentFolderMessage = string.Empty;
    private bool _isSelectingFolder;
    private bool _isRefreshingKnownLibraries;
    private bool _isChangingLanguage;

    public MainWindow(
        LibraryApplicationService libraryService,
        KnownLibraryApplicationService knownLibraryService,
        IReadOnlyList<IAssetPreviewRenderer> previewRenderers)
    {
        _libraryService = libraryService;
        _knownLibraryService = knownLibraryService;

        InitializeComponent();
        _previewPresenter = new AssetPreviewPresenter(
            previewRenderers,
            new PreviewSurface(
                ImagePreview,
                MediaPreview,
                MediaPreviewPanel,
                TextPreview,
                UnsupportedPreviewText));
        DataContext = this;

        foreach (var languageOption in LocalizationManager.SupportedLanguages)
        {
            LanguageOptions.Add(languageOption);
        }

        SelectCurrentLanguage();
        StatusMessage = LocalizationManager.Get("StatusRegisterOrSelectLibrary");
        UpdateLibraryRootMessage();
        UpdateCurrentFolderMessage();
    }

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    public ObservableCollection<KnownLibraryRow> KnownLibraries { get; } = new();

    public ObservableCollection<AssetRow> Assets { get; } = new();

    public ObservableCollection<LibraryFolderRow> Folders { get; } = new();

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

    public string LibraryRootMessage
    {
        get => _libraryRootMessage;
        private set
        {
            if (_libraryRootMessage == value)
            {
                return;
            }

            _libraryRootMessage = value;
            OnPropertyChanged(nameof(LibraryRootMessage));
        }
    }

    public string CurrentFolderMessage
    {
        get => _currentFolderMessage;
        private set
        {
            if (_currentFolderMessage == value)
            {
                return;
            }

            _currentFolderMessage = value;
            OnPropertyChanged(nameof(CurrentFolderMessage));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void LanguageBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isChangingLanguage || LanguageBox.SelectedItem is not LanguageOption languageOption)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var selectedAssetId = (AssetList.SelectedItem as AssetRow)?.Id;
            var selectedLibraryId = (KnownLibraryBox.SelectedItem as KnownLibraryRow)?.Id;

            await LocalizationManager.SetCultureAsync(languageOption.CultureName);
            await RefreshKnownLibrariesAsync(selectedLibraryId);

            if (_libraryLocation is not null)
            {
                await RefreshFoldersAsync(_currentFolder);
                await RefreshAssetsAsync(selectedAssetId);
            }
            else
            {
                ClearDetails();
                UpdateCurrentFolderMessage();
                UpdateLibraryRootMessage();
            }

            StatusMessage = LocalizationManager.Get("StatusLanguageChanged");
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var activeLibrary = await _knownLibraryService.GetActiveAsync();
            await RefreshKnownLibrariesAsync(activeLibrary?.Id);

            if (activeLibrary is null)
            {
                StatusMessage = LocalizationManager.Get("StatusRegisterLibraryFirst");
                return;
            }

            if (!activeLibrary.IsAvailable)
            {
                StatusMessage = LocalizationManager.Format(
                    "StatusRegisteredLibraryUnavailableFormat",
                    activeLibrary.RootPath);
                return;
            }

            await OpenKnownLibraryAsync(activeLibrary.Id);
        });
    }

    private async void RegisterLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Get("DialogRegisterLibraryTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await _knownLibraryService.RegisterAndOpenAsync(dialog.FolderName);
            await ApplyLibrarySessionAsync(result.Session);
            await RefreshKnownLibrariesAsync(result.KnownLibrary.Id);
            StatusMessage = LocalizationManager.Format(
                "StatusRegisteredLibraryFormat",
                result.KnownLibrary.DisplayName);
        });
    }

    private async void RefreshLibraries_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var selectedId = (KnownLibraryBox.SelectedItem as KnownLibraryRow)?.Id;
            await RefreshKnownLibrariesAsync(selectedId);
            StatusMessage = LocalizationManager.Get("StatusLibraryListRefreshed");
        });
    }

    private async void KnownLibraryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingKnownLibraries || KnownLibraryBox.SelectedItem is not KnownLibraryRow row)
        {
            return;
        }

        await RunUiAsync(() => OpenKnownLibraryAsync(row.Id));
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLibrary(out var location))
        {
            return;
        }

        var dialog = new TextInputDialog(
            LocalizationManager.Get("DialogNewFolderTitle"),
            LocalizationManager.Get("DialogNewFolderPrompt"));
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var folder = _currentFolder.Combine(dialog.Value);
            await _libraryService.CreateFolderAsync(location, folder);
            await RefreshFoldersAsync(folder);
            StatusMessage = LocalizationManager.Get("StatusFolderCreated");
        });
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = LocalizationManager.Get("DialogImportFilesTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportPathsAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Get("DialogImportFolderTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportPathsAsync([dialog.FolderName]);
    }

    private async void NewText_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLibrary(out var location))
        {
            return;
        }

        var dialog = new TextSnippetDialog
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var asset = await _libraryService.CreateTextSnippetAsync(
                location,
                _currentFolder,
                dialog.FileNameValue,
                dialog.ContentValue);

            await RefreshFoldersAsync(_currentFolder);
            await RefreshAssetsAsync(asset.Id);
            StatusMessage = LocalizationManager.Get("StatusTextSnippetCreated");
        });
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLibrary(out var location))
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await _libraryService.SynchronizeAsync(location);
            await RefreshFoldersAsync(_currentFolder);
            await RefreshAssetsAsync();
            StatusMessage = LocalizationManager.Format(
                "StatusSyncCompleteFormat",
                result.UpdatedCount,
                result.MovedCount,
                result.MissingCount,
                result.NewAssetCount);
        });
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(() => RefreshAssetsAsync());
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        await RunUiAsync(() => RefreshAssetsAsync());
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLibrary(out var location) || AssetList.SelectedItem is not AssetRow row)
        {
            StatusMessage = LocalizationManager.Get("StatusSelectAssetFirst");
            return;
        }

        await RunUiAsync(async () =>
        {
            await _libraryService.UpdateMetadataAsync(
                location,
                row.Id,
                NotesBox.Text,
                [TagsBox.Text]);

            await RefreshAssetsAsync(row.Id);
            StatusMessage = LocalizationManager.Get("StatusMetadataSaved");
        });
    }

    private async void FolderList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isSelectingFolder || FolderList.SelectedItem is not LibraryFolderRow folder)
        {
            return;
        }

        _currentFolder = folder.RelativePath;
        UpdateCurrentFolderMessage();
        await RunUiAsync(() => RefreshAssetsAsync());
    }

    private async void AssetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetRow row)
        {
            ClearDetails();
            return;
        }

        NotesBox.Text = row.Notes;
        TagsBox.Text = row.TagsText;
        SelectedNameText.Text = row.DisplayName;
        SelectedPathText.Text = row.RelativePath;
        await RunUiAsync(() => LoadPreviewAsync(row));
    }

    private void AssetGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _libraryLocation is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void AssetGrid_Drop(object sender, DragEventArgs e)
    {
        var droppedPaths = WindowsFileTransferService.ExtractFilePaths(e.Data);
        if (droppedPaths.Count == 0)
        {
            StatusMessage = LocalizationManager.Get("StatusNoFilePathsInDropData");
            return;
        }

        await ImportPathsAsync(droppedPaths);
    }

    private void AssetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void AssetList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !ShouldStartDrag(e.GetPosition(null)))
        {
            return;
        }

        var selectedPaths = GetSelectedAvailablePaths();
        if (selectedPaths.Length == 0)
        {
            return;
        }

        var dataObject = WindowsFileTransferService.CreateFileDropDataObject(selectedPaths);
        DragDrop.DoDragDrop(AssetList, dataObject, DragDropEffects.Copy);
        StatusMessage = LocalizationManager.Format("StatusDraggedItemsFormat", selectedPaths.Length);
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        var selectedPaths = GetSelectedAvailablePaths();
        if (selectedPaths.Length == 0)
        {
            StatusMessage = LocalizationManager.Get("StatusSelectAvailableAssetsFirst");
            return;
        }

        try
        {
            WindowsFileTransferService.CopyToClipboard(selectedPaths);
            StatusMessage = LocalizationManager.Format("StatusCopiedItemsFormat", selectedPaths.Length);
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationManager.Format("StatusClipboardCopyFailedFormat", ex.Message);
        }
    }

    private void PlayPreview_Click(object sender, RoutedEventArgs e)
    {
        MediaPreview.Play();
    }

    private void PausePreview_Click(object sender, RoutedEventArgs e)
    {
        MediaPreview.Pause();
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        MediaPreview.Stop();
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (!TryGetLibrary(out var location))
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await _libraryService.ImportPathsAsync(location, _currentFolder, paths);
            await RefreshFoldersAsync(_currentFolder);
            await RefreshAssetsAsync(result.ImportedAssets.FirstOrDefault()?.Id);
            StatusMessage = LocalizationManager.Format(
                "StatusImportedIntoFormat",
                result.ImportedCount,
                FormatFolderName(_currentFolder));
        });
    }

    private async Task RefreshFoldersAsync(LibraryRelativePath selectedFolder)
    {
        if (_libraryLocation is null)
        {
            return;
        }

        _isSelectingFolder = true;
        try
        {
            Folders.Clear();
            var folders = await _libraryService.ListFoldersAsync(_libraryLocation);
            foreach (var folder in folders)
            {
                Folders.Add(new LibraryFolderRow(folder.RelativePath));
            }

            var selected = Folders.FirstOrDefault(folder =>
                               string.Equals(folder.RelativePath.Value, selectedFolder.Value, StringComparison.OrdinalIgnoreCase))
                           ?? Folders.First(folder => folder.RelativePath.IsRoot);

            FolderList.SelectedItem = selected;
            _currentFolder = selected.RelativePath;
            UpdateCurrentFolderMessage();
        }
        finally
        {
            _isSelectingFolder = false;
        }
    }

    private async Task RefreshAssetsAsync(Guid? selectedAssetId = null)
    {
        if (_libraryLocation is null)
        {
            return;
        }

        var requiredTags = TagFilterBox.Text.Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var assets = await _libraryService.SearchAsync(
            _libraryLocation,
            _currentFolder,
            SearchBox.Text,
            requiredTags);

        Assets.Clear();
        foreach (var asset in assets)
        {
            Assets.Add(new AssetRow(asset, asset.FullPath(_libraryLocation)));
        }

        if (selectedAssetId is not null)
        {
            AssetList.SelectedItem = Assets.FirstOrDefault(asset => asset.Id == selectedAssetId.Value);
        }

        if (AssetList.SelectedItem is null)
        {
            ClearDetails();
        }
    }

    private async Task LoadPreviewAsync(AssetRow row)
    {
        HidePreviewElements();

        if (_libraryLocation is null)
        {
            return;
        }

        var preview = await _libraryService.GetPreviewAsync(_libraryLocation, row.Id);
        _previewPresenter.Show(
            preview,
            LocalizationManager.Get("StatusAssetFileMissing"),
            LocalizationManager.Get("StatusPreviewUnavailable"));
    }

    private void ClearDetails()
    {
        SelectedNameText.Text = LocalizationManager.Get("NoAssetSelected");
        SelectedPathText.Text = string.Empty;
        NotesBox.Text = string.Empty;
        TagsBox.Text = string.Empty;
        _previewPresenter.Clear(LocalizationManager.Get("SelectAssetToPreview"));
    }

    private void HidePreviewElements()
    {
        _previewPresenter.HideAll();
    }

    private string[] GetSelectedAvailablePaths()
    {
        return AssetList.SelectedItems
            .OfType<AssetRow>()
            .Where(asset => asset.Status == AssetStatus.Available && File.Exists(asset.FullPath))
            .Select(asset => asset.FullPath)
            .ToArray();
    }

    private bool ShouldStartDrag(Point currentPosition)
    {
        var horizontalChange = Math.Abs(currentPosition.X - _dragStartPoint.X);
        var verticalChange = Math.Abs(currentPosition.Y - _dragStartPoint.Y);

        return horizontalChange > SystemParameters.MinimumHorizontalDragDistance
               || verticalChange > SystemParameters.MinimumVerticalDragDistance;
    }

    private bool TryGetLibrary(out LibraryLocation location)
    {
        if (_libraryLocation is not null)
        {
            location = _libraryLocation;
            return true;
        }

        location = null!;
        StatusMessage = LocalizationManager.Get("StatusRegisterOrSelectLibrary");
        return false;
    }

    private async Task RefreshKnownLibrariesAsync(Guid? selectedLibraryId = null)
    {
        _isRefreshingKnownLibraries = true;
        try
        {
            KnownLibraries.Clear();
            var knownLibraries = await _knownLibraryService.ListAsync();
            foreach (var knownLibrary in knownLibraries)
            {
                KnownLibraries.Add(new KnownLibraryRow(knownLibrary));
            }

            var selected = selectedLibraryId is null
                ? null
                : KnownLibraries.FirstOrDefault(library => library.Id == selectedLibraryId.Value);

            KnownLibraryBox.SelectedItem = selected;
        }
        finally
        {
            _isRefreshingKnownLibraries = false;
        }
    }

    private async Task OpenKnownLibraryAsync(Guid libraryId)
    {
        var result = await _knownLibraryService.OpenRegisteredAsync(libraryId);
        await ApplyLibrarySessionAsync(result.Session);
        await RefreshKnownLibrariesAsync(result.KnownLibrary.Id);
        StatusMessage = LocalizationManager.Format(
            "StatusSwitchedLibraryFormat",
            result.KnownLibrary.DisplayName);
    }

    private async Task ApplyLibrarySessionAsync(LibrarySession session)
    {
        _libraryLocation = session.Location;
        _currentFolder = session.CurrentFolder;
        UpdateLibraryRootMessage();
        await RefreshFoldersAsync(_currentFolder);
        await RefreshAssetsAsync();
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SelectCurrentLanguage()
    {
        _isChangingLanguage = true;
        try
        {
            LanguageBox.SelectedItem = LanguageOptions.FirstOrDefault(language =>
                string.Equals(language.CultureName, LocalizationManager.CurrentCultureName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _isChangingLanguage = false;
        }
    }

    private void UpdateCurrentFolderMessage()
    {
        CurrentFolderMessage = LocalizationManager.Format(
            "StatusCurrentFolderFormat",
            FormatFolderName(_currentFolder));
    }

    private void UpdateLibraryRootMessage()
    {
        LibraryRootMessage = _libraryLocation is null
            ? LocalizationManager.Get("StatusNoLibraryOpened")
            : LocalizationManager.Format("StatusLibraryPathFormat", _libraryLocation.RootPath);
    }

    private static string FormatFolderName(LibraryRelativePath folder)
    {
        return folder.IsRoot ? LocalizationManager.Get("LibraryRootFolder") : folder.Value;
    }
}

public sealed class AssetRow(AssetRecord asset, string fullPath)
{
    public Guid Id => asset.Id;

    public string DisplayName => asset.DisplayName;

    public string TypeText => FormatAssetType(asset.TypeId);

    public AssetStatus Status => asset.Status;

    public string StatusText => asset.Status switch
    {
        AssetStatus.Available => LocalizationManager.Get("AssetStatusAvailable"),
        AssetStatus.Missing => LocalizationManager.Get("AssetStatusMissing"),
        _ => asset.Status.ToString()
    };

    public string RelativePath => asset.LibraryRelativePath.Value;

    public string FullPath { get; } = fullPath;

    public string Notes => asset.Notes;

    public string TagsText => string.Join(", ", asset.Tags);

    public string SizeText => asset.SizeBytes switch
    {
        < 1024 => asset.SizeBytes + " B",
        < 1024 * 1024 => (asset.SizeBytes / 1024d).ToString("0.#") + " KB",
        < 1024 * 1024 * 1024 => (asset.SizeBytes / 1024d / 1024d).ToString("0.#") + " MB",
        _ => (asset.SizeBytes / 1024d / 1024d / 1024d).ToString("0.#") + " GB"
    };

    private static string FormatAssetType(AssetTypeId typeId)
    {
        if (typeId == AssetTypeId.Image)
        {
            return LocalizationManager.Get("AssetTypeImage");
        }

        if (typeId == AssetTypeId.Video)
        {
            return LocalizationManager.Get("AssetTypeVideo");
        }

        if (typeId == AssetTypeId.Audio)
        {
            return LocalizationManager.Get("AssetTypeAudio");
        }

        if (typeId == AssetTypeId.Text)
        {
            return LocalizationManager.Get("AssetTypeText");
        }

        return typeId == AssetTypeId.Unknown
            ? LocalizationManager.Get("AssetTypeUnknown")
            : typeId.Value;
    }
}

public sealed class KnownLibraryRow(KnownLibrary library)
{
    public Guid Id => library.Id;

    public string DisplayText => library.IsAvailable
        ? LocalizationManager.Format("KnownLibraryAvailableFormat", library.DisplayName, library.RootPath)
        : LocalizationManager.Format("KnownLibraryMissingFormat", library.DisplayName, library.RootPath);
}

public sealed class LibraryFolderRow
{
    public LibraryFolderRow(LibraryRelativePath relativePath)
    {
        RelativePath = relativePath;
    }

    public LibraryRelativePath RelativePath { get; }

    public string DisplayName => RelativePath.IsRoot
        ? LocalizationManager.Get("LibraryRootFolder")
        : RelativePath.Value;
}
