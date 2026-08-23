using System.IO;
using System.Windows;
using System.Windows.Threading;
using BleHid.App.Services;
using Wpf.Ui.Appearance;

namespace BleHid.App;

public partial class App : Application
{
    // Shared with the CLI's background mode: only one process may own the radio.
    private const string InstanceMutex = @"Local\BleHid.Peripheral";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "blehid-app.log");

    private Mutex? _instance;
    private bool _exiting;

    public new static App Current => (App)Application.Current;

    public TrayIconService? Tray { get; private set; }

    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, InstanceMutex, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "BLE HID is already running. Look for it in the notification area.",
                "BLE HID", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplicationThemeManager.ApplySystemTheme();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        base.OnStartup(e);

        Tray = new TrayIconService(ShowMainWindow, ExitApplication);

        var startHidden = e.Args.Contains(StartupService.TrayArgument, StringComparer.OrdinalIgnoreCase);
        MainWindow = new MainWindow();
        if (!startHidden) MainWindow.Show();

        // Auto-start matters most for the hidden launch, where there is no window to press Start in.
        if (startHidden) _ = StartResidentAsync();
        else if (AppSettings.Instance.StartPeripheralOnLaunch) _ = PeripheralService.Instance.StartAsync();
    }

    // A tray-only launch has no UI to arm capture from, so the hotkeys have to work on their own.
    private static async Task StartResidentAsync()
    {
        await PeripheralService.Instance.StartAsync();
        await PeripheralService.Instance.StartCaptureAsync(resident: true);
    }

    public void ShowMainWindow()
    {
        MainWindow ??= new MainWindow();
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        await PeripheralService.Instance.StopAsync();
        Tray?.Dispose();
        // Shutdown closes windows, so it must not run inside a close that is still unwinding.
        await Dispatcher.InvokeAsync(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
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
        try
        {
            // The tray build can run for weeks, so the log cannot grow without a bound.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:s}  {ex}\n\n");
        }
        catch (IOException) { }
    }
}
