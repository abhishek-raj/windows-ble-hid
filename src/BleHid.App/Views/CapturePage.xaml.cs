using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using BleHid.App.Services;

namespace BleHid.App.Views;

public partial class CapturePage : Page
{
    private readonly PeripheralService _service = PeripheralService.Instance;

    public CapturePage()
    {
        InitializeComponent();
        DataContext = _service;
        _service.Hosts.CollectionChanged += OnHostsChanged;
        Unloaded += (_, _) => _service.Hosts.CollectionChanged -= OnHostsChanged;
        UpdateEmptyState();
    }

    private void OnHostsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() => NoHostsBar.IsOpen = _service.Hosts.Count == 0;

    private void OnTargetChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TargetOption option) _service.Select(option);
    }

    private async void OnToggleCapture(object sender, RoutedEventArgs e)
    {
        if (_service.IsCapturing) await _service.StopCaptureAsync();
        else await _service.StartCaptureAsync();

        // Assigning IsChecked would replace the one-way binding and leave the switch deaf to
        // Ctrl+Alt+Q from then on; SetCurrentValue snaps it back without detaching.
        CaptureToggle.SetCurrentValue(Wpf.Ui.Controls.ToggleSwitch.IsCheckedProperty, _service.IsCapturing);
    }
}
