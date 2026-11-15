using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BleHid.App.Services;
using BleHid.Core;
using BleHid.Localization;

namespace BleHid.App.Views;

public partial class SettingsPage : Page
{
    private SwitchGestureWatcher? _gestureWatcher;
    private PeripheralToggleShortcut? _toggleShortcut;
    private bool _recordingShortcut;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = AppSettings.Instance;

        var available = StartupService.Command is not null;
        DevBar.IsOpen = !available;
        AutoStartToggle.IsEnabled = available;
        AutoStartToggle.IsChecked = available && StartupService.IsEnabled();

        SwitchGestureToggle.IsChecked =
            SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig).Enabled;

        // Native names, so someone who cannot read the current language can still find their own.
        var languages = new List<LanguageChoice> { new(string.Empty, Loc.T("app.settings.language.system")) };
        languages.AddRange(Loc.AvailableLanguages.Select(code => new LanguageChoice(code, Loc.NativeName(code))));
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedValue = languages.Any(choice => choice.Code == AppSettings.Instance.Language)
            ? AppSettings.Instance.Language
            : string.Empty;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_toggleShortcut is null && PeripheralService.Instance.ToggleShortcut is { } shortcut)
        {
            _toggleShortcut = shortcut;
            shortcut.Changed += OnToggleShortcutChanged;
        }
        RefreshToggleShortcut();

        if (_gestureWatcher is not null) return;
        _gestureWatcher = new SwitchGestureWatcher(AppPaths.SwitchGestureConfig);
        _gestureWatcher.Changed += OnGestureSettingsChanged;
        RefreshGestureToggle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EndShortcutRecording();
        if (_toggleShortcut is not null) _toggleShortcut.Changed -= OnToggleShortcutChanged;
        _toggleShortcut = null;

        var watcher = _gestureWatcher;
        _gestureWatcher = null;
        if (watcher is null) return;
        watcher.Changed -= OnGestureSettingsChanged;
        watcher.Dispose();
    }

    private void OnGestureSettingsChanged(SwitchGestureOptions options) =>
        Dispatcher.BeginInvoke(RefreshGestureToggle);

    private void RefreshGestureToggle()
    {
        if (_gestureWatcher is { } watcher)
            SwitchGestureToggle.IsChecked = watcher.Current.Enabled;
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e) =>
        StartupService.Set(AutoStartToggle.IsChecked == true);

    private void OnSwitchGestureChanged(object sender, RoutedEventArgs e)
    {
        var enabled = SwitchGestureToggle.IsChecked == true;

        // Reload before writing so tuning values edited by hand are not flattened by a toggle,
        // and so a change made from the CLI meanwhile is not silently reverted.
        var updated = SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig).WithEnabled(enabled);
        if (updated.TrySave(AppPaths.SwitchGestureConfig, out _)) return;

        // The setting did not persist, so the switch must not claim that it did.
        SwitchGestureToggle.IsChecked = !enabled;
    }

    private void OnToggleShortcutChanged() => Dispatcher.BeginInvoke(RefreshToggleShortcut);

    private void RefreshToggleShortcut()
    {
        if (_recordingShortcut) return;
        var hotkey = _toggleShortcut?.Hotkey;
        ToggleShortcutBox.Text = hotkey is { } key ? Shortcuts.Format(key) : Loc.T("app.settings.toggleShortcut.none");
        ClearShortcutButton.IsEnabled = hotkey is not null;
        ShowShortcutProblem(hotkey is { } active && _toggleShortcut is { } shortcut
            ? Shortcuts.RegistrationProblem(active, shortcut.Registration)
            : null);
    }

    private void ShowShortcutProblem(string? problem)
    {
        ToggleShortcutProblem.Text = problem ?? string.Empty;
        ToggleShortcutProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Recording lasts exactly as long as the box has keyboard focus, so leaving the page, the
    // window or the box always gives the shortcut back.
    private void OnShortcutRecordingStarted(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingShortcut) return;
        _recordingShortcut = true;
        _toggleShortcut?.SetPaused(true);
        ToggleShortcutBox.Text = Loc.T("app.settings.toggleShortcut.prompt");
        ShowShortcutProblem(null);
    }

    private void OnShortcutRecordingEnded(object sender, KeyboardFocusChangedEventArgs e) => EndShortcutRecording();

    private void EndShortcutRecording()
    {
        if (!_recordingShortcut) return;
        _recordingShortcut = false;
        _toggleShortcut?.SetPaused(false);
        RefreshToggleShortcut();
    }

    private void FinishShortcutRecording()
    {
        EndShortcutRecording();
        ToggleShortcutBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        var modifiers = CurrentModifiers();

        // Plain Tab and Shift+Tab keep moving focus, so keyboard users are not trapped in the box.
        if (key == Key.Tab && modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift) return;
        e.Handled = true;

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            FinishShortcutRecording();
            return;
        }

        if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete)
        {
            SaveToggleShortcut(null);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            ToggleShortcutBox.Text = Shortcuts.FormatPartial(modifiers);
            return;
        }

        var hotkey = new Hotkey(modifiers, KeyInterop.VirtualKeyFromKey(key));
        if (Shortcuts.Problem(hotkey) is { } problem)
        {
            ToggleShortcutBox.Text = Loc.T("app.settings.toggleShortcut.prompt");
            ShowShortcutProblem(problem);
            return;
        }

        SaveToggleShortcut(hotkey);
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var keyboard = Keyboard.Modifiers;
        var modifiers = HotkeyModifiers.None;
        if (keyboard.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (keyboard.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (keyboard.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        // Keyboard.Modifiers never includes ModifierKeys.Windows, so ask for the keys themselves.
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private void OnClearShortcut(object sender, RoutedEventArgs e) => SaveToggleShortcut(null);

    private void SaveToggleShortcut(Hotkey? hotkey)
    {
        // Reload first so a change made from the CLI meanwhile is not silently reverted.
        var updated = ShortcutSettings.Load(AppPaths.ShortcutsConfig).WithTogglePeripheral(hotkey);
        if (!updated.TrySave(AppPaths.ShortcutsConfig, out var error))
        {
            ShowShortcutProblem(Loc.T("app.settings.toggleShortcut.saveFailed", ("error", error)));
            return;
        }

        _toggleShortcut?.Reload();
        if (_recordingShortcut) FinishShortcutRecording();
        else RefreshToggleShortcut();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is string code) AppSettings.Instance.Language = code;
    }

    private void OnExit(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    // ToString is what screen readers announce for a ComboBox item, so it must be the visible name.
    private sealed record LanguageChoice(string Code, string Name)
    {
        public override string ToString() => Name;
    }
}
