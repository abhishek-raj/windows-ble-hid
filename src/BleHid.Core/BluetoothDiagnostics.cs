using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BleHid.Core;

public static class BluetoothDiagnostics
{
    /// <summary>Lists LE peers currently holding a connection to this radio.</summary>
    public static async Task<IReadOnlyList<string>> ListConnectedLeDevicesAsync()
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.Select(d => $"{d.Name} ({d.Id})").ToList();
    }

    /// <summary>Lists Classic Bluetooth peers currently connected, to spot pairing with the wrong transport.</summary>
    public static async Task<IReadOnlyList<string>> ListConnectedClassicDevicesAsync()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.Select(d => $"{d.Name} ({d.Id})").ToList();
    }
}
