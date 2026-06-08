using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private readonly UiSettingsStore _uiSettingsStore;
    private readonly AssetThumbnailLoadCoordinator _thumbnailLoadCoordinator;

    private LibraryLocation? _libraryLocation;
    private LibraryRelativePath _currentFolder = LibraryRelativePath.Root;
    private Point _dragStartPoint;
    private string _statusMessage = string.Empty;
    private string _libraryRootMessage = string.Empty;
    private string _currentFolderMessage = string.Empty;
    private string _currentImportTargetDisplayName = string.Empty;
    private bool _isSelectingFolder;
    private bool _isRefreshingKnownLibraries;
    private bool _isChangingLanguage;
    private bool _isImporting;
    private bool _isSynchronizing;
    private bool _isLowImpactImportEnabled;
    private int _currentImportSourceCount;
    private ThumbnailDisplaySettings _thumbnailDisplaySettings = ThumbnailDisplaySettings.Default;
    private ThumbnailLoadStatus _thumbnailLoadStatus = ThumbnailLoadStatus.Idle;

    public MainWindow(
        LibraryApplicationService libraryService,
        KnownLibraryApplicationService knownLibraryService,
        IReadOnlyList<IAssetPreviewRenderer> previewRenderers,
        UiSettingsStore uiSettingsStore,
        WindowsThumbnailCacheService thumbnailCacheService)
    {
        _libraryService = libraryService;
        _knownLibraryService = knownLibraryService;
        _uiSettingsStore = uiSettingsStore;

        InitializeComponent();
        _thumbnailLoadCoordinator = new AssetThumbnailLoadCoordinator(thumbnailCacheService, Dispatcher);
        _thumbnailLoadCoordinator.StatusChanged += OnThumbnailLoadStatusChanged;
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

    public ObservableCollection<LibraryFolderNode> FolderRoots { get; } = new();

    public bool CanInteract => !_isImporting;

    public string BackgroundTaskStatusMessage => BuildBackgroundTaskStatusMessage();

    public bool IsLowImpactImportEnabled
    {
        get => _isLowImpactImportEnabled;
        private set
        {
            if (_isLowImpactImportEnabled == value)
            {
                return;
            }

            _isLowImpactImportEnabled = value;
            OnPropertyChanged(nameof(IsLowImpactImportEnabled));
        }
    }

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

    protected override void OnClosed(EventArgs e)
    {
        _thumbnailLoadCoordinator.StatusChanged -= OnThumbnailLoadStatusChanged;
        _thumbnailLoadCoordinator.Dispose();
        base.OnClosed(e);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

            OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
            StatusMessage = LocalizationManager.Get("StatusLanguageChanged");
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            await LoadThumbnailDisplaySettingsAsync();
            await LoadImportBehaviorSettingsAsync();

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

    private async void KnownLibraryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            SetSynchronizing(true);
            try
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
            }
            finally
            {
                SetSynchronizing(false);
            }
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

    private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isSelectingFolder || e.NewValue is not LibraryFolderNode folder)
        {
            return;
        }

        _currentFolder = folder.RelativePath;
        UpdateCurrentFolderMessage();
        await RunUiAsync(() => RefreshAssetsAsync());
    }

    private async void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    private async void ThumbnailFieldsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ThumbnailFieldSettingsDialog(_thumbnailDisplaySettings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            ApplyThumbnailDisplaySettings(dialog.Settings);
            await _uiSettingsStore.SaveThumbnailDisplaySettingsAsync(dialog.Settings);
        });
    }

    private async void LowImpactImportCheckBox_Click(object sender, RoutedEventArgs e)
    {
        IsLowImpactImportEnabled = LowImpactImportCheckBox.IsChecked == true;

        await RunUiAsync(async () =>
        {
            await _uiSettingsStore.SaveLowImpactImportEnabledAsync(IsLowImpactImportEnabled);
        });
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

        if (_isImporting)
        {
            return;
        }

        var normalizedPaths = NormalizeImportPaths(paths);
        if (normalizedPaths.Length == 0)
        {
            StatusMessage = LocalizationManager.Get("StatusNoFilePathsInDropData");
            return;
        }

        var targetFolder = _currentFolder;
        var importOptions = IsLowImpactImportEnabled
            ? AssetImportOptions.LowImpact
            : AssetImportOptions.Default;
        SetImporting(true, normalizedPaths.Length, FormatFolderName(targetFolder));
        StatusMessage = LocalizationManager.Format(
            "StatusImportingIntoFormat",
            FormatFolderName(targetFolder));

        try
        {
            var result = await Task.Run(() =>
                _libraryService.ImportPathsAsync(location, targetFolder, normalizedPaths, importOptions));

            ApplyImportedFoldersIncrementally(result.ImportedFolders, targetFolder);

            if (HasActiveAssetFilter())
            {
                if (result.ImportedCount > 0)
                {
                    await RefreshAssetsAsync(result.ImportedAssets.FirstOrDefault()?.Id);
                }
            }
            else
            {
                ApplyImportedAssetsIncrementally(result.ImportedAssets);
            }

            StatusMessage = LocalizationManager.Format(
                "StatusImportedIntoFormat",
                result.ImportedCount,
                FormatFolderName(targetFolder));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            SetImporting(false);
        }
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
            FolderRoots.Clear();
            var folderList = await _libraryService.ListFoldersAsync(_libraryLocation);
            var rootNode = BuildFolderTree(folderList);
            rootNode.IsExpanded = true;
            FolderRoots.Add(rootNode);

            var selectedNode = FindFolderNode(rootNode, selectedFolder) ?? rootNode;
            selectedNode.ExpandAncestors();
            selectedNode.IsExpanded = true;
            selectedNode.IsSelected = true;

            _currentFolder = selectedNode.RelativePath;
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
            _thumbnailLoadCoordinator.Cancel();
            return;
        }

        var assets = await _libraryService.SearchAsync(
            _libraryLocation,
            _currentFolder,
            SearchBox.Text,
            ParseTagFilter());

        Assets.Clear();
        foreach (var asset in assets)
        {
            Assets.Add(new AssetRow(asset, asset.FullPath(_libraryLocation), _thumbnailDisplaySettings));
        }

        if (selectedAssetId is not null)
        {
            AssetList.SelectedItem = Assets.FirstOrDefault(asset => asset.Id == selectedAssetId.Value);
        }

        if (AssetList.SelectedItem is null)
        {
            ClearDetails();
        }

        ScheduleThumbnailLoading();
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

    private async Task LoadThumbnailDisplaySettingsAsync()
    {
        ApplyThumbnailDisplaySettings(await _uiSettingsStore.GetThumbnailDisplaySettingsAsync());
    }

    private async Task LoadImportBehaviorSettingsAsync()
    {
        IsLowImpactImportEnabled = await _uiSettingsStore.GetLowImpactImportEnabledAsync();
    }

    private void ApplyThumbnailDisplaySettings(ThumbnailDisplaySettings settings)
    {
        _thumbnailDisplaySettings = settings;
        foreach (var asset in Assets)
        {
            asset.ApplyThumbnailDisplaySettings(settings);
        }
    }

    private void ApplyImportedFoldersIncrementally(
        IReadOnlyList<LibraryRelativePath> importedFolders,
        LibraryRelativePath selectedFolder)
    {
        if (FolderRoots.Count == 0)
        {
            var rootNode = new LibraryFolderNode(LibraryRelativePath.Root)
            {
                IsExpanded = true
            };
            FolderRoots.Add(rootNode);
        }

        var root = FolderRoots[0];
        foreach (var folder in importedFolders
                     .Where(folder => !folder.IsRoot)
                     .Distinct()
                     .OrderBy(folder => folder.Value.Count(ch => ch == '/'))
                     .ThenBy(folder => folder.Value, StringComparer.OrdinalIgnoreCase))
        {
            EnsureFolderNode(root, folder);
        }

        var selectedNode = FindFolderNode(root, selectedFolder) ?? root;
        selectedNode.ExpandAncestors();
        selectedNode.IsExpanded = true;
        _currentFolder = selectedNode.RelativePath;
        UpdateCurrentFolderMessage();
    }

    private void ApplyImportedAssetsIncrementally(IReadOnlyList<AssetRecord> importedAssets)
    {
        if (_libraryLocation is null || importedAssets.Count == 0)
        {
            return;
        }

        var visibleAssets = importedAssets
            .Where(asset => IsVisibleInCurrentFolder(asset, _currentFolder))
            .OrderByDescending(asset => asset.ImportedAt)
            .ThenBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (visibleAssets.Length == 0)
        {
            return;
        }

        var importedIds = visibleAssets
            .Select(asset => asset.Id)
            .ToHashSet();

        for (var index = Assets.Count - 1; index >= 0; index--)
        {
            if (importedIds.Contains(Assets[index].Id))
            {
                Assets.RemoveAt(index);
            }
        }

        for (var index = visibleAssets.Length - 1; index >= 0; index--)
        {
            Assets.Insert(0, new AssetRow(
                visibleAssets[index],
                visibleAssets[index].FullPath(_libraryLocation),
                _thumbnailDisplaySettings));
        }

        AssetList.SelectedItem = Assets.FirstOrDefault(asset => asset.Id == visibleAssets[0].Id);
        ScheduleThumbnailLoading();
    }

    private IReadOnlyList<string> ParseTagFilter()
    {
        return TagFilterBox.Text.Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private bool HasActiveAssetFilter()
    {
        return !string.IsNullOrWhiteSpace(SearchBox.Text) || ParseTagFilter().Count > 0;
    }

    private void ScheduleThumbnailLoading()
    {
        if (_libraryLocation is null)
        {
            _thumbnailLoadCoordinator.Cancel();
            return;
        }

        _thumbnailLoadCoordinator.Reload(_libraryLocation, Assets);
    }

    private void SetImporting(
        bool isImporting,
        int sourceCount = 0,
        string? targetDisplayName = null)
    {
        if (_isImporting == isImporting)
        {
            if (isImporting)
            {
                _currentImportSourceCount = sourceCount;
                _currentImportTargetDisplayName = targetDisplayName ?? string.Empty;
                OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
            }

            return;
        }

        _isImporting = isImporting;
        if (isImporting)
        {
            _currentImportSourceCount = sourceCount;
            _currentImportTargetDisplayName = targetDisplayName ?? string.Empty;
        }
        else
        {
            _currentImportSourceCount = 0;
            _currentImportTargetDisplayName = string.Empty;
        }

        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
    }

    private void SetSynchronizing(bool isSynchronizing)
    {
        if (_isSynchronizing == isSynchronizing)
        {
            return;
        }

        _isSynchronizing = isSynchronizing;
        OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
    }

    private static string[] NormalizeImportPaths(IEnumerable<string> paths)
    {
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            uniquePaths.Add(Path.GetFullPath(path));
        }

        return uniquePaths.ToArray();
    }

    private static bool IsVisibleInCurrentFolder(AssetRecord asset, LibraryRelativePath currentFolder)
    {
        return currentFolder.IsRoot
               || asset.LibraryRelativePath.Value.StartsWith(
                   currentFolder.Value + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static LibraryFolderNode BuildFolderTree(IReadOnlyList<LibraryFolder> folders)
    {
        var rootNode = new LibraryFolderNode(LibraryRelativePath.Root);
        var nodesByPath = new Dictionary<string, LibraryFolderNode>(StringComparer.OrdinalIgnoreCase)
        {
            [LibraryRelativePath.Root.Value] = rootNode
        };

        foreach (var folder in folders
                     .Where(folder => !folder.RelativePath.IsRoot)
                     .OrderBy(folder => folder.RelativePath.Value.Count(ch => ch == '/'))
                     .ThenBy(folder => folder.RelativePath.Value, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = GetParentFolderPath(folder.RelativePath);
            if (!nodesByPath.TryGetValue(parentPath.Value, out var parentNode))
            {
                continue;
            }

            var node = new LibraryFolderNode(folder.RelativePath, parentNode);
            nodesByPath[folder.RelativePath.Value] = node;
            parentNode.Children.Add(node);
        }

        return rootNode;
    }

    private static LibraryFolderNode EnsureFolderNode(LibraryFolderNode root, LibraryRelativePath path)
    {
        if (path.IsRoot)
        {
            return root;
        }

        var current = root;
        foreach (var segment in path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var childPath = current.RelativePath.Combine(segment);
            var child = current.Children.FirstOrDefault(existing =>
                string.Equals(existing.RelativePath.Value, childPath.Value, StringComparison.OrdinalIgnoreCase));

            if (child is null)
            {
                child = new LibraryFolderNode(childPath, current);
                InsertChildNode(current.Children, child);
            }

            current = child;
        }

        return current;
    }

    private static LibraryFolderNode? FindFolderNode(LibraryFolderNode node, LibraryRelativePath target)
    {
        if (string.Equals(node.RelativePath.Value, target.Value, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindFolderNode(child, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static LibraryRelativePath GetParentFolderPath(LibraryRelativePath path)
    {
        if (path.IsRoot)
        {
            return LibraryRelativePath.Root;
        }

        var separatorIndex = path.Value.LastIndexOf('/');
        return separatorIndex < 0
            ? LibraryRelativePath.Root
            : LibraryRelativePath.Create(path.Value[..separatorIndex]);
    }

    private static void InsertChildNode(
        ObservableCollection<LibraryFolderNode> children,
        LibraryFolderNode node)
    {
        var insertAt = 0;
        while (insertAt < children.Count)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(
                children[insertAt].DisplayName,
                node.DisplayName);
            if (comparison > 0)
            {
                break;
            }

            insertAt++;
        }

        children.Insert(insertAt, node);
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

    private void OnThumbnailLoadStatusChanged(ThumbnailLoadStatus status)
    {
        _thumbnailLoadStatus = status;
        OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
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

    private string BuildBackgroundTaskStatusMessage()
    {
        if (_isImporting)
        {
            return LocalizationManager.Format(
                "BackgroundTaskImportingFormat",
                _currentImportSourceCount,
                _currentImportTargetDisplayName);
        }

        if (_isSynchronizing)
        {
            return LocalizationManager.Get("BackgroundTaskSynchronizing");
        }

        if (_thumbnailLoadStatus.IsRunning)
        {
            return LocalizationManager.Format(
                "BackgroundTaskLoadingThumbnailsFormat",
                _thumbnailLoadStatus.CompletedCount,
                _thumbnailLoadStatus.TotalCount);
        }

        return LocalizationManager.Get("BackgroundTaskIdle");
    }
}

public sealed class AssetRow : INotifyPropertyChanged
{
    private readonly AssetRecord _asset;
    private ThumbnailDisplaySettings _thumbnailDisplaySettings;
    private Uri? _thumbnailImageUri;

    public AssetRow(AssetRecord asset, string fullPath, ThumbnailDisplaySettings thumbnailDisplaySettings)
    {
        _asset = asset;
        FullPath = fullPath;
        _thumbnailDisplaySettings = thumbnailDisplaySettings;
        ThumbnailFields = new ObservableCollection<ThumbnailFieldDisplay>();
        RebuildThumbnailFields();
    }

    public Guid Id => _asset.Id;

    internal AssetRecord Asset => _asset;

    public string DisplayName => _asset.DisplayName;

    public AssetStatus Status => _asset.Status;

    public string StatusText => _asset.Status switch
    {
        AssetStatus.Available => LocalizationManager.Get("AssetStatusAvailable"),
        AssetStatus.Missing => LocalizationManager.Get("AssetStatusMissing"),
        _ => _asset.Status.ToString()
    };

    public string RelativePath => _asset.LibraryRelativePath.Value;

    public string FullPath { get; }

    public string Notes => _asset.Notes;

    public string TagsText => string.Join(", ", _asset.Tags);

    public string SizeText => _asset.SizeBytes switch
    {
        < 1024 => _asset.SizeBytes + " B",
        < 1024 * 1024 => (_asset.SizeBytes / 1024d).ToString("0.#") + " KB",
        < 1024 * 1024 * 1024 => (_asset.SizeBytes / 1024d / 1024d).ToString("0.#") + " MB",
        _ => (_asset.SizeBytes / 1024d / 1024d / 1024d).ToString("0.#") + " GB"
    };

    public string TypeText => FormatAssetType(_asset.TypeId);

    public bool CanRequestThumbnail => _asset.TypeId == AssetTypeId.Image && _asset.Status == AssetStatus.Available;

    public bool HasThumbnailImage => _thumbnailImageUri is not null;

    public Uri? ThumbnailImageUri => _thumbnailImageUri;

    public string PlaceholderText => _asset.Status == AssetStatus.Missing ? StatusText : TypeText;

    public ObservableCollection<ThumbnailFieldDisplay> ThumbnailFields { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyThumbnailDisplaySettings(ThumbnailDisplaySettings settings)
    {
        _thumbnailDisplaySettings = settings;
        RebuildThumbnailFields();
    }

    public void SetThumbnailPath(string thumbnailPath)
    {
        var nextThumbnailUri = string.IsNullOrWhiteSpace(thumbnailPath)
            ? null
            : new Uri(thumbnailPath);
        if (_thumbnailImageUri == nextThumbnailUri)
        {
            return;
        }

        _thumbnailImageUri = nextThumbnailUri;
        OnPropertyChanged(nameof(ThumbnailImageUri));
        OnPropertyChanged(nameof(HasThumbnailImage));
    }

    private void RebuildThumbnailFields()
    {
        ThumbnailFields.Clear();

        if (_thumbnailDisplaySettings.ShowType)
        {
            ThumbnailFields.Add(new ThumbnailFieldDisplay(
                LocalizationManager.Get("AssetListTypeHeader"),
                TypeText));
        }

        if (_thumbnailDisplaySettings.ShowStatus)
        {
            ThumbnailFields.Add(new ThumbnailFieldDisplay(
                LocalizationManager.Get("AssetListStatusHeader"),
                StatusText));
        }

        if (_thumbnailDisplaySettings.ShowTags && !string.IsNullOrWhiteSpace(TagsText))
        {
            ThumbnailFields.Add(new ThumbnailFieldDisplay(
                LocalizationManager.Get("TagsLabel"),
                TagsText));
        }

        if (_thumbnailDisplaySettings.ShowPath)
        {
            ThumbnailFields.Add(new ThumbnailFieldDisplay(
                LocalizationManager.Get("AssetListPathHeader"),
                RelativePath));
        }

        if (_thumbnailDisplaySettings.ShowSize)
        {
            ThumbnailFields.Add(new ThumbnailFieldDisplay(
                LocalizationManager.Get("AssetListSizeHeader"),
                SizeText));
        }

        OnPropertyChanged(nameof(ThumbnailFields));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlaceholderText));
    }

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

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class KnownLibraryRow(KnownLibrary library)
{
    public Guid Id => library.Id;

    public string DisplayText => library.IsAvailable
        ? LocalizationManager.Format("KnownLibraryAvailableFormat", library.DisplayName, library.RootPath)
        : LocalizationManager.Format("KnownLibraryMissingFormat", library.DisplayName, library.RootPath);
}
