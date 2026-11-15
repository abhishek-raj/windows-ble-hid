namespace BleHid.Core;

/// <summary>
/// Watches the switch-gesture config file, so toggling the gesture takes effect on a session
/// that is already running, including background mode in another process.
/// </summary>
public sealed class SwitchGestureWatcher(string path) : SettingsFileWatcher<SwitchGestureOptions>(
    path, SwitchGestureOptions.Load, (current, reloaded) => current.SameSettingsAs(reloaded),
    options => options.Warning is not null);
