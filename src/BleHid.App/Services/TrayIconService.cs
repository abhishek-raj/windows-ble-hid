using System.Windows;
using BleHid.Localization;
using Forms = System.Windows.Forms;

namespace BleHid.App.Services;

/// <summary>
/// Notification area presence. WPF-UI 4.3 ships no tray control, so this wraps the WinForms one.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _current;
    private string _target = Loc.T("target.none");
    private string? _lockWarning;

    public TrayIconService(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("app.tray.open"), null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("app.common.exit"), null, (_, _) => exit());

        _current = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "BLE HID",
            ContextMenuStrip = menu,
            Icon = _current,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void ShowMessage(string text) =>
        _icon.ShowBalloonTip(3000, "BLE HID", text, Forms.ToolTipIcon.Info);

    /// <summary>
    /// Keeps the notification area in step with where input is going and the state of the lock
    /// keys there. The physical keyboard lamp belongs to Windows and describes Windows, so the
    /// tray is the honest place to show a different machine's lock state.
    /// </summary>
    public void UpdateStatus(string target, string? lockWarning)
    {
        if (_target == target && _lockWarning == lockWarning) return;

        var previous = _lockWarning;
        _target = target;
        _lockWarning = lockWarning;

        // NotifyIcon.Text throws above 63 characters, and host names are user-controlled.
        var text = $"BLE HID - {target}" + (lockWarning is null ? string.Empty : $"\n{lockWarning}");
        _icon.Text = text.Length <= 63 ? text : text[..63];

        // Only on a rising edge: re-announcing an unchanged warning on every switch would nag.
        if (lockWarning is not null && lockWarning != previous)
            _icon.ShowBalloonTip(3000, Loc.T("app.tray.lockKeys"), $"{target}: {lockWarning}", Forms.ToolTipIcon.Warning);
    }

    // SmallIconSize is the DPI-scaled tray metric, so this selects the frame Windows is about to
    // ask for instead of letting it rescale a mismatched one.
    private static System.Drawing.Icon LoadIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/blehid.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current.Dispose();
    }
}
