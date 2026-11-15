namespace BleHid.Core;

/// <summary>
/// Watches a settings file and reports changes, so an edit takes effect on a session that is
/// already running. The file is the single source of truth rather than an in-process setting
/// because background mode runs in a separate process: the UI and a second CLI instance have
/// no other way to reach it.
/// </summary>
public class SettingsFileWatcher<T> : IDisposable where T : class
{
    private readonly string _path;
    private readonly Func<string, T> _load;
    private readonly Func<T, T, bool> _sameSettings;
    private readonly Func<T, bool> _hasWarning;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private T _current;

    /// <param name="hasWarning">True when a load may have read a half-written file.</param>
    protected SettingsFileWatcher(string path, Func<string, T> load, Func<T, T, bool> sameSettings,
        Func<T, bool> hasWarning)
    {
        _path = path;
        _load = load;
        _sameSettings = sameSettings;
        _hasWarning = hasWarning;
        _current = Reload();

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher the settings simply stay as they were at startup, which is no
            // worse than having no live toggle at all.
            _watcher = null;
        }
    }

    public T Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised on a watcher thread when the settings on disk actually differ.</summary>
    public event Action<T>? Changed;

    /// <summary>Rereads the file now, for a writer in this process that cannot wait for the watcher.</summary>
    public void Refresh() => OnFileEvent(this, new FileSystemEventArgs(WatcherChangeTypes.Changed,
        Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileName(_path)));

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var reloaded = Reload();

        // Writers raise several notifications for one save. Comparing values rather than
        // debouncing on time means a change is reported exactly once however many arrive.
        lock (_gate)
        {
            if (_sameSettings(_current, reloaded)) return;
            _current = reloaded;
        }

        Changed?.Invoke(reloaded);
    }

    private T Reload()
    {
        // Saving is not atomic, so a notification can arrive while the file is half written and
        // parse as garbage. A couple of quick retries turn that into the intended read instead
        // of a spurious default.
        for (var attempt = 0; ; attempt++)
        {
            var settings = _load(_path);
            if (!_hasWarning(settings) || attempt == 3) return settings;
            Thread.Sleep(30);
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Dispose();
    }
}
