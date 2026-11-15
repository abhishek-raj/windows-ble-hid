using System.Text.Encodings.Web;
using System.Text.Json;

namespace BleHid.Core;

/// <summary>
/// User-configured global shortcuts, shared by the app and CLI background mode through
/// <see cref="AppPaths.ShortcutsConfig"/>. Nothing is bound unless the file sets it.
/// </summary>
public sealed class ShortcutSettings
{
    private ShortcutSettings(Hotkey? togglePeripheral, string? warning)
    {
        TogglePeripheral = togglePeripheral;
        Warning = warning;
    }

    /// <summary>Stops a running peripheral, or starts a stopped one.</summary>
    public Hotkey? TogglePeripheral { get; }

    /// <summary>Set when the file could not be read or held a shortcut that is not allowed.</summary>
    public string? Warning { get; }

    public ShortcutSettings WithTogglePeripheral(Hotkey? hotkey) => new(hotkey, null);

    /// <summary>True when both describe the same settings, ignoring any load warning.</summary>
    internal bool SameSettingsAs(ShortcutSettings other) => TogglePeripheral == other.TogglePeripheral;

    public bool TrySave(string path, out string? error)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(new FileModel
            {
                TogglePeripheral = TogglePeripheral?.ToString()
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                // People edit this file by hand; the default encoder writes + as \u002B.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ShortcutSettings Load(string path)
    {
        if (!File.Exists(path)) return new(null, null);

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, $"could not read {path}: {ex.Message}");
        }
    }

    internal static ShortcutSettings Parse(string json)
    {
        var model = JsonSerializer.Deserialize<FileModel>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new FileModel();

        var text = model.TogglePeripheral;
        if (string.IsNullOrWhiteSpace(text)) return new(null, null);
        if (!Hotkey.TryParse(text, out var hotkey))
            return new(null, $"TogglePeripheral '{text}' is not a shortcut, e.g. Ctrl+Shift+F12");
        // A hand-edited file must not bind what the settings page would refuse.
        if (hotkey.Problem is not HotkeyProblem.None and var problem)
            return new(null, $"TogglePeripheral {hotkey} is not allowed ({problem})");
        return new(hotkey, null);
    }

    private sealed class FileModel
    {
        public string? TogglePeripheral { get; init; }
    }
}

/// <summary>Watches <see cref="AppPaths.ShortcutsConfig"/> so edits reach running sessions.</summary>
public sealed class ShortcutSettingsWatcher(string path) : SettingsFileWatcher<ShortcutSettings>(
    path, ShortcutSettings.Load, (current, reloaded) => current.SameSettingsAs(reloaded),
    settings => settings.Warning is not null);
