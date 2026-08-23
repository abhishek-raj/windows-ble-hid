using System.ComponentModel;
using BleHid.App.Services;
using BleHid.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BleHid.App;

public partial class MainWindow : FluentWindow
{
    private bool _stopping;

    public MainWindow()
    {
        InitializeComponent();
        // Applies the Mica backdrop and keeps tracking light/dark changes made while running.
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(StatusPage));
    }

    // Shutting the radio down is async, so the close is deferred rather than blocking the dispatcher.
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_stopping)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _stopping = true;
        await PeripheralService.Instance.StopAsync();
        // StopAsync can finish synchronously, and Close() throws if it runs inside the close it cancelled.
        await Dispatcher.InvokeAsync(Close);
    }
}
