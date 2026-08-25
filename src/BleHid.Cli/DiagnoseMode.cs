using System.Text;
using BleHid.Core;

namespace BleHid.Cli;

/// <summary>
/// Produces a paste-ready report for bug reports. Advertising failures are almost always
/// environmental, so the radio's capabilities matter more than anything this process does.
/// </summary>
internal static class DiagnoseMode
{
    public static async Task<int> RunAsync(bool requireEncryption)
    {
        var report = new StringBuilder();
        void Emit(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        Emit($"BLE HID diagnostics  {DateTime.Now:s}");
        Emit(new string('-', 60));

        try
        {
            Emit(await BluetoothDiagnostics.DescribeEnvironmentAsync());
        }
        catch (Exception ex)
        {
            Emit($"environment probe failed: {ex.Message}");
        }

        Emit(new string('-', 60));
        Emit("Starting peripheral...");

        var peripheral = new BleHidPeripheral(requireEncryption);
        peripheral.Log += Emit;

        var advertising = false;
        try
        {
            await peripheral.StartAsync();
            advertising = peripheral.AdvertisementStatus
                == Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisementStatus.Started;
        }
        catch (Exception ex)
        {
            Emit($"startup threw: {ex}");
        }

        Emit(new string('-', 60));
        Emit($"Advertisement status: {peripheral.AdvertisementStatus}");
        Emit(advertising ? Verdict.Advertising : Verdict.NotAdvertising);

        await peripheral.DisposeAsync();

        try
        {
            var path = AppPaths.InLogs("diagnostics.txt");
            await File.WriteAllTextAsync(path, report.ToString());
            Console.WriteLine($"\nSaved to {path}\nAttach this file to a bug report.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"\nCould not save the report: {ex.Message}");
        }

        return advertising ? 0 : 1;
    }

    private static class Verdict
    {
        public const string Advertising = """

            Advertising is running. If a phone still cannot see "BLE HID":
              - remove any stale pairing for this PC from the phone, then rescan
              - some phones only list HID peripherals in the "pair new device" screen
            """;

        public const string NotAdvertising = """

            Advertising did not start. In order of likelihood:
              - Peripheral role is False above: this radio cannot be a keyboard. No fix in software.
              - More than one Bluetooth radio is on: disable the others in Device Manager.
              - A dongle is present but Windows is still using the built-in radio, or vice versa.
              - Another app already holds the advertisement slots; reboot and retry before anything else.
            """;
    }
}
