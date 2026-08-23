using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace BleHid.App;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "blehid-app.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplicationThemeManager.ApplySystemTheme();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    // A XAML error on one page used to terminate the process with no output at all.
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true;
        MessageBox.Show($"{e.Exception.Message}\n\nLogged to {LogPath}",
            "BLE HID", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Report(Exception? ex)
    {
        if (ex is null) return;
        try { File.AppendAllText(LogPath, $"{DateTime.Now:s}  {ex}\n\n"); }
        catch (IOException) { }
    }
}
