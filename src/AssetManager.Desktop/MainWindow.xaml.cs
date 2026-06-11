using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AssetManager.Application.BackgroundTasks;
using AssetManager.Application.Library;
using AssetManager.Desktop.Localization;
using AssetManager.Desktop.Preview;
using AssetManager.Domain.Library;
using AssetManager.Infrastructure.Windows;
using Microsoft.Win32;

namespace AssetManager.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string AssetListDragDataFormat = "AssetManager.Desktop.AssetListDrag";

    private readonly LibraryApplicationService _libraryService;
    private readonly KnownLibraryApplicationService _knownLibraryService;
    private readonly AssetPreviewPresenter _previewPresenter;
    private readonly UiSettingsStore _uiSettingsStore;
    private readonly IBackgroundTaskCenter _backgroundTaskCenter;
    private readonly AssetThumbnailLoadCoordinator _thumbnailLoadCoordinator;
    private readonly LibraryFileSystemChangeMonitor _libraryChangeMonitor;

    private LibraryLocation? _libraryLocation;
    private LibraryRelativePath _currentFolder = LibraryRelativePath.Root;
    private Point _dragStartPoint;
    private bool _canStartAssetDrag;
    private string _statusMessage = string.Empty;
    private string _libraryRootMessage = string.Empty;
    private string _currentFolderMessage = string.Empty;
    private bool _isSelectingFolder;
    private bool _isRefreshingKnownLibraries;
    private bool _isChangingLanguage;
    private bool _isBusy;
    private bool _isLowImpactImportEnabled;
    private ThumbnailDisplaySettings _thumbnailDisplaySettings = ThumbnailDisplaySettings.Default;
    private IReadOnlyList<BackgroundTaskSnapshot> _backgroundTaskSnapshots = [];
    private BackgroundTasksWindow? _backgroundTasksWindow;
    private int _backgroundTaskRefreshQueued;
    private long _backgroundTaskSnapshotVersion;
    private long _lastAppliedBackgroundTaskSnapshotVersion;
    private bool _autoSyncQueued;
    private readonly HashSet<string> _pendingAutoSyncPaths = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(
        LibraryApplicationService libraryService,
        KnownLibraryApplicationService knownLibraryService,
        IReadOnlyList<IAssetPreviewRenderer> previewRenderers,
        UiSettingsStore uiSettingsStore,
        WindowsThumbnailCacheService thumbnailCacheService,
        IBackgroundTaskCenter backgroundTaskCenter)
    {
        _libraryService = libraryService;
        _knownLibraryService = knownLibraryService;
        _uiSettingsStore = uiSettingsStore;
        _backgroundTaskCenter = backgroundTaskCenter;

        InitializeComponent();
        _thumbnailLoadCoordinator = new AssetThumbnailLoadCoordinator(
            thumbnailCacheService,
            backgroundTaskCenter,
            Dispatcher);
        _libraryChangeMonitor = new LibraryFileSystemChangeMonitor(
            Dispatcher,
            QueueAutomaticSynchronization);
        _backgroundTaskCenter.SnapshotsChanged += OnBackgroundTaskSnapshotsChanged;
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
        _backgroundTaskSnapshots = _backgroundTaskCenter.GetSnapshots();
        SyncBackgroundTaskRows(_backgroundTaskSnapshots);
    }

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    public ObservableCollection<KnownLibraryRow> KnownLibraries { get; } = new();

    public ObservableCollection<AssetRow> Assets { get; } = new();

    public ObservableCollection<LibraryFolderNode> FolderRoots { get; } = new();

    public ObservableCollection<BackgroundTaskRow> BackgroundTasks { get; } = new();

    public bool CanInteract => !_isBusy;

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
        _backgroundTaskCenter.SnapshotsChanged -= OnBackgroundTaskSnapshotsChanged;
        if (_backgroundTasksWindow is not null)
        {
            _backgroundTasksWindow.Close();
            _backgroundTasksWindow = null;
        }

        _libraryChangeMonitor.Dispose();
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

            RefreshBackgroundTaskPresentation();
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
        await RunUiAsync(() => RunSynchronizeAsync(allowQueueWhenBusy: false));
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

    private void BackgroundTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_backgroundTasksWindow is null)
        {
            _backgroundTasksWindow = new BackgroundTasksWindow(
                BackgroundTasks,
                taskId => _backgroundTaskCenter.RequestCancel(taskId))
            {
                Owner = this
            };
            _backgroundTasksWindow.Closed += BackgroundTasksWindow_Closed;
            _backgroundTasksWindow.Show();
            return;
        }

        if (!_backgroundTasksWindow.IsVisible)
        {
            _backgroundTasksWindow.Show();
        }

        _backgroundTasksWindow.Activate();
    }

    private void AssetGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(AssetListDragDataFormat)
                    && e.Data.GetDataPresent(DataFormats.FileDrop)
                    && _libraryLocation is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void AssetGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(AssetListDragDataFormat))
        {
            e.Handled = true;
            return;
        }

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
        _canStartAssetDrag = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null;
    }

    private void AssetList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_canStartAssetDrag
            || e.LeftButton != MouseButtonState.Pressed
            || !ShouldStartDrag(e.GetPosition(null)))
        {
            return;
        }

        var selectedPaths = GetSelectedAvailablePaths();
        if (selectedPaths.Length == 0)
        {
            return;
        }

        var dataObject = WindowsFileTransferService.CreateFileDropDataObject(selectedPaths);
        dataObject.SetData(AssetListDragDataFormat, true);
        DragDrop.DoDragDrop(AssetList, dataObject, DragDropEffects.Copy);
        _canStartAssetDrag = false;
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

        if (_isBusy)
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
        var currentImportStatus = LocalizationManager.Format(
            "BackgroundTaskImportPreparingFormat",
            FormatFolderName(targetFolder));
        var session = _backgroundTaskCenter.StartTask(
            new BackgroundTaskStartRequest(
                BackgroundTaskKind.ImportAssets,
                LocalizationManager.Get("BackgroundTaskImportTitle"),
                currentImportStatus,
                IsCancelable: true,
                InitialProgress: BackgroundTaskProgress.None));
        var progress = new Progress<AssetImportProgress>(value =>
        {
            currentImportStatus = BuildImportTaskStatus(value);
            session.Update(
                currentImportStatus,
                BuildImportTaskProgress(value));
        });
        var importOptions = IsLowImpactImportEnabled
            ? AssetImportOptions.LowImpact with { Progress = progress }
            : AssetImportOptions.Default with { Progress = progress };
        using var changeSuppression = _libraryChangeMonitor.SuppressChanges();

        SetBusy(true);
        StatusMessage = LocalizationManager.Format(
            "StatusImportingIntoFormat",
            FormatFolderName(targetFolder));

        try
        {
            var result = await Task.Run(
                () => _libraryService.ImportPathsAsync(
                    location,
                    targetFolder,
                    normalizedPaths,
                    importOptions,
                    session.CancellationToken),
                session.CancellationToken);

            session.Complete(LocalizationManager.Format(
                "BackgroundTaskImportCompletedFormat",
                result.ImportedCount,
                FormatFolderName(targetFolder)));

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
        catch (OperationCanceledException)
        {
            session.Cancel(LocalizationManager.Get("BackgroundTaskImportCanceled"));
            StatusMessage = LocalizationManager.Get("BackgroundTaskImportCanceled");
        }
        catch (Exception ex)
        {
            session.Fail(
                ex,
                LocalizationManager.Format(
                    "BackgroundTaskImportFailedAtFormat",
                    currentImportStatus,
                    ex.Message));
            StatusMessage = ex.Message;
        }
        finally
        {
            session.Dispose();
            SetBusy(false);
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

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
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
        _autoSyncQueued = false;
        _libraryChangeMonitor.Start(session.Location);
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

        var insertedRows = new List<AssetRow>();
        for (var index = visibleAssets.Length - 1; index >= 0; index--)
        {
            var row = new AssetRow(
                visibleAssets[index],
                visibleAssets[index].FullPath(_libraryLocation),
                _thumbnailDisplaySettings);
            Assets.Insert(0, row);
            insertedRows.Add(row);
        }

        AssetList.SelectedItem = Assets.FirstOrDefault(asset => asset.Id == visibleAssets[0].Id);
        ScheduleThumbnailLoading(insertedRows);
    }

    private void ApplySynchronizedAssetsIncrementally(LibrarySyncResult result)
    {
        if (_libraryLocation is null)
        {
            return;
        }

        var selectedAssetId = (AssetList.SelectedItem as AssetRow)?.Id;
        var removedAssetIds = (result.RemovedAssetIds ?? [])
            .ToHashSet();
        for (var index = Assets.Count - 1; index >= 0; index--)
        {
            if (removedAssetIds.Contains(Assets[index].Id))
            {
                Assets.RemoveAt(index);
            }
        }

        var changedRows = new List<AssetRow>();
        foreach (var asset in result.AffectedAssets ?? [])
        {
            var existingIndex = FindAssetRowIndex(asset.Id);
            if (!IsVisibleInCurrentFolder(asset, _currentFolder))
            {
                if (existingIndex >= 0)
                {
                    Assets.RemoveAt(existingIndex);
                }

                continue;
            }

            var row = new AssetRow(asset, asset.FullPath(_libraryLocation), _thumbnailDisplaySettings);
            if (existingIndex >= 0)
            {
                Assets[existingIndex] = row;
            }
            else
            {
                InsertAssetRow(row);
            }

            changedRows.Add(row);
        }

        if (selectedAssetId is not null)
        {
            AssetList.SelectedItem = Assets.FirstOrDefault(asset => asset.Id == selectedAssetId.Value);
        }

        if (AssetList.SelectedItem is null)
        {
            ClearDetails();
        }

        if (changedRows.Count > 0)
        {
            ScheduleThumbnailLoading(changedRows);
        }
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

    private void ScheduleThumbnailLoading(IEnumerable<AssetRow>? rows = null)
    {
        if (_libraryLocation is null)
        {
            _thumbnailLoadCoordinator.Cancel();
            return;
        }

        _thumbnailLoadCoordinator.Reload(_libraryLocation, rows ?? Assets);
    }

    private void SetBusy(bool isBusy)
    {
        if (_isBusy == isBusy)
        {
            return;
        }

        _isBusy = isBusy;
        OnPropertyChanged(nameof(CanInteract));
        if (!_isBusy && _autoSyncQueued)
        {
            var changedPaths = ConsumePendingAutoSyncPaths();
            _ = Dispatcher.InvokeAsync(
                () => _ = RunUiAsync(() => RunSynchronizeAsync(allowQueueWhenBusy: true, changedPaths)),
                DispatcherPriority.Background);
        }
    }

    private void QueueAutomaticSynchronization(IReadOnlyList<string> changedPaths)
    {
        if (_libraryLocation is null)
        {
            return;
        }

        AddPendingAutoSyncPaths(changedPaths);
        _autoSyncQueued = true;
        StatusMessage = LocalizationManager.Get("StatusAutoSyncQueued");
        if (_isBusy)
        {
            return;
        }

        var queuedPaths = ConsumePendingAutoSyncPaths();
        _ = RunUiAsync(() => RunSynchronizeAsync(allowQueueWhenBusy: true, queuedPaths));
    }

    private async Task RunSynchronizeAsync(
        bool allowQueueWhenBusy,
        IReadOnlyList<string>? changedPaths = null)
    {
        if (!TryGetLibrary(out var location))
        {
            return;
        }

        if (changedPaths is { Count: 0 })
        {
            return;
        }

        if (_isBusy)
        {
            _autoSyncQueued = allowQueueWhenBusy || _autoSyncQueued;
            if (allowQueueWhenBusy && changedPaths is not null)
            {
                AddPendingAutoSyncPaths(changedPaths);
            }

            return;
        }

        var isIncremental = changedPaths is { Count: > 0 };
        if (!isIncremental)
        {
            _autoSyncQueued = false;
        }

        var currentSyncStatus = LocalizationManager.Get("BackgroundTaskSyncScanning");
        var session = _backgroundTaskCenter.StartTask(
            new BackgroundTaskStartRequest(
                BackgroundTaskKind.SynchronizeLibrary,
                LocalizationManager.Get("BackgroundTaskSyncTitle"),
                currentSyncStatus,
                IsCancelable: true,
                InitialProgress: BackgroundTaskProgress.None));
        SetBusy(true);

        try
        {
            var syncProgress = new Progress<LibrarySyncProgress>(progress =>
            {
                currentSyncStatus = BuildSyncTaskStatus(progress);
                session.Update(
                    currentSyncStatus,
                    BuildSyncTaskProgress(progress));
            });

            var result = await Task.Run(
                () => isIncremental
                    ? _libraryService.SynchronizePathsAsync(
                        location,
                        changedPaths!,
                        syncProgress,
                        session.CancellationToken)
                    : _libraryService.SynchronizeAsync(
                        location,
                        syncProgress,
                        session.CancellationToken),
                session.CancellationToken);

            session.Complete(LocalizationManager.Format(
                "BackgroundTaskSyncCompletedFormat",
                result.UpdatedCount,
                    result.MovedCount,
                    result.MissingCount,
                    result.NewAssetCount));
            await RefreshFoldersAsync(_currentFolder);
            if (isIncremental && !HasActiveAssetFilter())
            {
                ApplySynchronizedAssetsIncrementally(result);
            }
            else
            {
                await RefreshAssetsAsync();
            }

            StatusMessage = LocalizationManager.Format(
                "StatusSyncCompleteFormat",
                result.UpdatedCount,
                result.MovedCount,
                result.MissingCount,
                result.NewAssetCount);
        }
        catch (OperationCanceledException)
        {
            session.Cancel(LocalizationManager.Get("BackgroundTaskSyncCanceled"));
            StatusMessage = LocalizationManager.Get("BackgroundTaskSyncCanceled");
        }
        catch (Exception ex)
        {
            session.Fail(
                ex,
                LocalizationManager.Format(
                    "BackgroundTaskSyncFailedAtFormat",
                    currentSyncStatus,
                    ex.Message));
            StatusMessage = ex.Message;
        }
        finally
        {
            session.Dispose();
            SetBusy(false);
        }
    }

    private void AddPendingAutoSyncPaths(IEnumerable<string> changedPaths)
    {
        foreach (var path in changedPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _pendingAutoSyncPaths.Add(path);
            }
        }
    }

    private IReadOnlyList<string> ConsumePendingAutoSyncPaths()
    {
        var paths = _pendingAutoSyncPaths.ToArray();
        _pendingAutoSyncPaths.Clear();
        _autoSyncQueued = false;
        return paths;
    }

    private void BackgroundTasksWindow_Closed(object? sender, EventArgs e)
    {
        if (_backgroundTasksWindow is not null)
        {
            _backgroundTasksWindow.Closed -= BackgroundTasksWindow_Closed;
        }

        _backgroundTasksWindow = null;
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

    private int FindAssetRowIndex(Guid assetId)
    {
        for (var index = 0; index < Assets.Count; index++)
        {
            if (Assets[index].Id == assetId)
            {
                return index;
            }
        }

        return -1;
    }

    private void InsertAssetRow(AssetRow row)
    {
        var insertAt = 0;
        while (insertAt < Assets.Count)
        {
            var importedAtComparison = DateTimeOffset.Compare(
                row.ImportedAt,
                Assets[insertAt].ImportedAt);
            if (importedAtComparison > 0)
            {
                break;
            }

            if (importedAtComparison == 0
                && StringComparer.OrdinalIgnoreCase.Compare(row.DisplayName, Assets[insertAt].DisplayName) < 0)
            {
                break;
            }

            insertAt++;
        }

        Assets.Insert(insertAt, row);
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

    private void OnBackgroundTaskSnapshotsChanged(IReadOnlyList<BackgroundTaskSnapshot> snapshots)
    {
        _backgroundTaskSnapshots = snapshots.ToArray();
        Interlocked.Increment(ref _backgroundTaskSnapshotVersion);
        QueueBackgroundTaskRefresh();
    }

    private void QueueBackgroundTaskRefresh()
    {
        if (Interlocked.Exchange(ref _backgroundTaskRefreshQueued, 1) == 1)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(ApplyBackgroundTaskSnapshots, DispatcherPriority.Background);
    }

    private void ApplyBackgroundTaskSnapshots()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _backgroundTaskSnapshotVersion);
                if (version == _lastAppliedBackgroundTaskSnapshotVersion)
                {
                    break;
                }

                _lastAppliedBackgroundTaskSnapshotVersion = version;
                SyncBackgroundTaskRows(_backgroundTaskSnapshots);
                OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundTaskRefreshQueued, 0);
            if (Interlocked.Read(ref _backgroundTaskSnapshotVersion) != _lastAppliedBackgroundTaskSnapshotVersion)
            {
                QueueBackgroundTaskRefresh();
            }
        }
    }

    private void RefreshBackgroundTaskPresentation()
    {
        SyncBackgroundTaskRows(_backgroundTaskSnapshots);
        OnPropertyChanged(nameof(BackgroundTaskStatusMessage));
    }

    private void SyncBackgroundTaskRows(IReadOnlyList<BackgroundTaskSnapshot> snapshots)
    {
        var orderedSnapshots = BackgroundTaskPresentation.OrderForDisplay(snapshots);
        var snapshotIds = orderedSnapshots
            .Select(snapshot => snapshot.Id)
            .ToHashSet();
        for (var index = BackgroundTasks.Count - 1; index >= 0; index--)
        {
            if (!snapshotIds.Contains(BackgroundTasks[index].Id))
            {
                BackgroundTasks.RemoveAt(index);
            }
        }

        for (var index = 0; index < orderedSnapshots.Count; index++)
        {
            var snapshot = orderedSnapshots[index];
            var existingIndex = FindBackgroundTaskRowIndex(snapshot.Id);
            if (existingIndex < 0)
            {
                BackgroundTasks.Insert(index, new BackgroundTaskRow(snapshot));
                continue;
            }

            var row = BackgroundTasks[existingIndex];
            row.Apply(snapshot);
            if (existingIndex != index)
            {
                BackgroundTasks.Move(existingIndex, index);
            }
        }
    }

    private int FindBackgroundTaskRowIndex(Guid taskId)
    {
        for (var index = 0; index < BackgroundTasks.Count; index++)
        {
            if (BackgroundTasks[index].Id == taskId)
            {
                return index;
            }
        }

        return -1;
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

    private static BackgroundTaskProgress BuildImportTaskProgress(AssetImportProgress progress)
    {
        var totalFiles = Math.Max(progress.DiscoveredFiles, progress.CopiedFiles);
        return new BackgroundTaskProgress(
            progress.CopiedFiles,
            totalFiles,
            LocalizationManager.Get("BackgroundTaskProgressUnitFiles"));
    }

    private static string BuildImportTaskStatus(AssetImportProgress progress)
    {
        var totalFiles = Math.Max(progress.DiscoveredFiles, progress.CopiedFiles);
        var currentItemName = string.IsNullOrWhiteSpace(progress.CurrentItemName)
            ? "-"
            : progress.CurrentItemName;

        return LocalizationManager.Format(
            "BackgroundTaskImportCopyingFormat",
            progress.CopiedFiles,
            totalFiles,
            currentItemName);
    }

    private static BackgroundTaskProgress BuildSyncTaskProgress(LibrarySyncProgress progress)
    {
        return progress.Stage switch
        {
            LibrarySyncStage.ReconcilingAssets when progress.TotalAssets is > 0 => new BackgroundTaskProgress(
                progress.ProcessedAssets,
                progress.TotalAssets,
                LocalizationManager.Get("BackgroundTaskProgressUnitAssets")),
            LibrarySyncStage.Completed when progress.TotalAssets is > 0 => new BackgroundTaskProgress(
                progress.ProcessedAssets,
                progress.TotalAssets,
                LocalizationManager.Get("BackgroundTaskProgressUnitAssets")),
            _ => BackgroundTaskProgress.None
        };
    }

    private static string BuildSyncTaskStatus(LibrarySyncProgress progress)
    {
        return progress.Stage switch
        {
            LibrarySyncStage.ScanningFiles => LocalizationManager.Get("BackgroundTaskSyncScanning"),
            LibrarySyncStage.ReconcilingAssets when progress.TotalAssets is > 0 => LocalizationManager.Format(
                "BackgroundTaskSyncReconcilingFormat",
                progress.ProcessedAssets,
                progress.TotalAssets.Value),
            LibrarySyncStage.ReconcilingAssets => LocalizationManager.Get("BackgroundTaskSyncReconciling"),
            LibrarySyncStage.RegisteringNewAssets => LocalizationManager.Format(
                "BackgroundTaskSyncRegisteringFormat",
                progress.NewAssetCount),
            LibrarySyncStage.Completed => LocalizationManager.Format(
                "BackgroundTaskSyncCompletedFormat",
                progress.UpdatedCount,
                progress.MovedCount,
                progress.MissingCount,
                progress.NewAssetCount),
            _ => LocalizationManager.Get("BackgroundTaskSyncScanning")
        };
    }

    private string BuildBackgroundTaskStatusMessage()
    {
        var primaryTask = BackgroundTaskPresentation.SelectSummaryTask(_backgroundTaskSnapshots);
        if (primaryTask is null)
        {
            return LocalizationManager.Get("BackgroundTaskIdle");
        }

        var runningCount = _backgroundTaskSnapshots.Count(snapshot => snapshot.State == BackgroundTaskState.Running);
        if (runningCount > 1 && primaryTask.State == BackgroundTaskState.Running)
        {
            return LocalizationManager.Format(
                "BackgroundTaskRunningMoreFormat",
                primaryTask.StatusText,
                runningCount - 1);
        }

        return primaryTask.StatusText;
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

    public DateTimeOffset ImportedAt => _asset.ImportedAt;

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
