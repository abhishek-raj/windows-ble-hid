using System.ComponentModel;
using System.Windows;
using BleHid.App.Services;
using BleHid.App.Views;
using BleHid.Localization;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BleHid.App;

public partial class MainWindow : FluentWindow
{
    private bool _warnedAboutTray;

    public MainWindow()
    {
        InitializeComponent();
        FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        // Applies the Mica backdrop and keeps tracking light/dark changes made while running.
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(StatusPage));
    }

    // Staying resident keeps the GATT attribute table alive, which is what hosts reconnect to.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Current.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (!AppSettings.Instance.CloseToTray)
        {
            App.Current.ExitApplication();
            return;
        }

        Hide();
        if (_warnedAboutTray) return;
        _warnedAboutTray = true;
        App.Current.Tray?.ShowMessage(Loc.T("app.tray.stillRunning"));
    }
}
