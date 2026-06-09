using System.IO;
using System.Windows.Threading;
using AssetManager.Domain.Library;

namespace AssetManager.Desktop;

public sealed class LibraryFileSystemChangeMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<IReadOnlyList<string>> _onChanged;
    private readonly DispatcherTimer _debounceTimer;
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private LibraryLocation? _location;
    private bool _isDisposed;
    private int _suppressionDepth;
    private DateTimeOffset _ignoreChangesUntil;

    public LibraryFileSystemChangeMonitor(
        Dispatcher dispatcher,
        Action<IReadOnlyList<string>> onChanged,
        TimeSpan? debounceDelay = null)
    {
        _dispatcher = dispatcher;
        _onChanged = onChanged;
        _debounceTimer = new DispatcherTimer(
            debounceDelay ?? TimeSpan.FromMilliseconds(900),
            DispatcherPriority.Background,
            (_, _) => FlushPendingChange(),
            dispatcher);
        _debounceTimer.Stop();
    }

    public void Start(LibraryLocation location)
    {
        Stop();
        _location = location;

        _watcher = new FileSystemWatcher(location.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
                           | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += FileSystemChanged;
        _watcher.Changed += FileSystemChanged;
        _watcher.Deleted += FileSystemChanged;
        _watcher.Renamed += FileSystemRenamed;
        _watcher.Error += FileSystemWatcherError;
    }

    public void Stop()
    {
        _debounceTimer.Stop();
        _changedPaths.Clear();
        _suppressionDepth = 0;
        _ignoreChangesUntil = default;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= FileSystemChanged;
            _watcher.Changed -= FileSystemChanged;
            _watcher.Deleted -= FileSystemChanged;
            _watcher.Renamed -= FileSystemRenamed;
            _watcher.Error -= FileSystemWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _location = null;
    }

    public IDisposable SuppressChanges(TimeSpan? trailingQuietPeriod = null)
    {
        _suppressionDepth++;
        _debounceTimer.Stop();
        _changedPaths.Clear();
        return new ChangeSuppression(this, trailingQuietPeriod ?? TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
    }

    public static bool IsManagementPath(string rootPath, string changedPath)
    {
        var managementPath = Path.GetFullPath(Path.Combine(rootPath, LibraryLocation.ManagementDirectoryName));
        var fullChangedPath = Path.GetFullPath(changedPath);

        return string.Equals(fullChangedPath, managementPath, StringComparison.OrdinalIgnoreCase)
               || fullChangedPath.StartsWith(
                   managementPath + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || fullChangedPath.StartsWith(
                   managementPath + Path.AltDirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void FileSystemChanged(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath);
    }

    private void FileSystemRenamed(object sender, RenamedEventArgs e)
    {
        QueueChange(e.FullPath);
        QueueChange(e.OldFullPath);
    }

    private void FileSystemWatcherError(object sender, ErrorEventArgs e)
    {
        QueueChange(null);
    }

    private void QueueChange(string? fullPath)
    {
        if (_isDisposed
            || _location is null
            || _suppressionDepth > 0
            || DateTimeOffset.UtcNow < _ignoreChangesUntil)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(fullPath)
            && IsManagementPath(_location.RootPath, fullPath))
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed
                || _location is null
                || _suppressionDepth > 0
                || DateTimeOffset.UtcNow < _ignoreChangesUntil)
            {
                return;
            }

            _changedPaths.Add(fullPath ?? _location.RootPath);
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }, DispatcherPriority.Background);
    }

    private void FlushPendingChange()
    {
        _debounceTimer.Stop();
        if (_isDisposed || _location is null)
        {
            return;
        }

        var paths = _changedPaths.ToArray();
        _changedPaths.Clear();
        if (paths.Length == 0)
        {
            return;
        }

        _onChanged(paths);
    }

    private void EndSuppression(TimeSpan trailingQuietPeriod)
    {
        if (_suppressionDepth > 0)
        {
            _suppressionDepth--;
        }

        _debounceTimer.Stop();
        _changedPaths.Clear();
        _ignoreChangesUntil = DateTimeOffset.UtcNow.Add(trailingQuietPeriod);
    }

    private sealed class ChangeSuppression(
        LibraryFileSystemChangeMonitor owner,
        TimeSpan trailingQuietPeriod) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            owner.EndSuppression(trailingQuietPeriod);
        }
    }
}
