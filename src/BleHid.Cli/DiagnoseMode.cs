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

        await peripheral.DisposeAsync();

        if (advertising)
        {
            Emit(Verdict.Advertising);
        }
        else
        {
            var facts = await BluetoothDiagnostics.GetRadioFactsAsync();
            if (!facts.PeripheralRole)
            {
                Emit(Verdict.NoPeripheralRole);
            }
            else
            {
                Emit("");
                Emit("Bluetooth policy:");
                Emit(AdvertisingProbe.DescribePolicy());
                Emit("");
                Emit("Advertisement budget:");
                Emit(AdvertisingProbe.DescribeAdvertisementBudget());

                Emit("");
                Emit("Narrowing down the failure, one variable at a time:");
                var steps = await AdvertisingProbe.RunAsync();
                foreach (var step in steps)
                    Emit($"  [{(step.Ok ? " ok " : "FAIL")}] {step.Label,-34}: {step.Detail}");

                Emit("");
                if (facts.RadiosOn > 1)
                    Emit($"  ! {facts.RadiosOn} Bluetooth radios are on; the advertisement can land on the wrong one\n");

                Emit(AdvertisingProbe.Interpret(steps));
                Emit("");
                Emit("Please attach this report to a GitHub issue.");
            }
        }

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

        public const string NoPeripheralRole = """

            Advertising did not start.

              This radio does not support the LE peripheral role, so it cannot be a
              keyboard. No fix in software; a different adapter is the only option.
            """;
    }
}
