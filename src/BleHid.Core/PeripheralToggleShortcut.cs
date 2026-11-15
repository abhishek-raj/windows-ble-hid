namespace BleHid.Core;

/// <summary>
/// The configurable shortcut that stops and starts the peripheral. While input stays on this
/// PC, Windows delivers it as a registered hotkey. While input is redirected, the capture hook
/// swallows keys before Windows sees them, so <see cref="InputCapture"/> matches it instead;
/// both paths raise the same <see cref="Pressed"/>.
/// </summary>
public sealed class PeripheralToggleShortcut : IDisposable
{
    private readonly ShortcutSettingsWatcher _watcher;
    private readonly GlobalHotkey _global = new();
    private readonly object _gate = new();
    private ShortcutSettings _settings;
    private HotkeyRegistration _registration;
    private bool _paused;

    public PeripheralToggleShortcut(string path)
    {
        _watcher = new ShortcutSettingsWatcher(path);
        _global.Pressed += RequestToggle;
        _settings = _watcher.Current;
        Register();
        _watcher.Changed += OnSettingsChanged;
    }

    public Hotkey? Hotkey
    {
        get { lock (_gate) return _settings.TogglePeripheral; }
    }

    public HotkeyRegistration Registration
    {
        get { lock (_gate) return _registration; }
    }

    /// <summary>Why the file's shortcut was ignored, in English for logs.</summary>
    public string? Warning
    {
        get { lock (_gate) return _settings.Warning; }
    }

    /// <summary>Current state in English for logs; null while paused for recording.</summary>
    public string? LogLine
    {
        get
        {
            lock (_gate)
            {
                return (_settings.TogglePeripheral, _registration) switch
                {
                    (null, _) => _settings.Warning is { } warning
                        ? $"[shortcut] {warning}"
                        : "[shortcut] no stop/start shortcut set",
                    ({ } key, HotkeyRegistration.Registered) => $"[shortcut] {key} stops and starts the peripheral",
                    ({ } key, HotkeyRegistration.InUse) => $"[shortcut] {key} is already registered by another app",
                    ({ } key, HotkeyRegistration.Failed) => $"[shortcut] Windows refused {key}",
                    _ => null
                };
            }
        }
    }

    /// <summary>Raised on a background thread. Handlers must not block: the hook thread can be the caller.</summary>
    public event Action? Pressed;

    /// <summary>Raised on a background thread after the shortcut or its registration changed.</summary>
    public event Action? Changed;

    internal void RequestToggle() => Pressed?.Invoke();

    /// <summary>
    /// Releases the combination while the settings page records a new one. Windows turns a
    /// registered combination into WM_HOTKEY, so the recorder would never see it otherwise.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused) return;
            _paused = paused;
            Register();
        }

        Changed?.Invoke();
    }

    /// <summary>Applies a save from this process without waiting for the file watcher.</summary>
    public void Reload() => _watcher.Refresh();

    private void OnSettingsChanged(ShortcutSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            Register();
        }

        Changed?.Invoke();
    }

    private void Register() =>
        _registration = _global.Set(_paused ? null : _settings.TogglePeripheral);

    public void Dispose()
    {
        _watcher.Changed -= OnSettingsChanged;
        _watcher.Dispose();
        _global.Dispose();
    }
}
