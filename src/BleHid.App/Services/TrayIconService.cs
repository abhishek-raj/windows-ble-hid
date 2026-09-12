using System.Windows;
using Forms = System.Windows.Forms;

namespace BleHid.App.Services;

/// <summary>
/// Notification area presence. WPF-UI 4.3 ships no tray control, so this wraps the WinForms one.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _current;

    public TrayIconService(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open BLE HID", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

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
