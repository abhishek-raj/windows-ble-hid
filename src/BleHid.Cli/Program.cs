using System.Threading.Channels;
using BleHid.Cli;
using BleHid.Core;
using BleHid.Localization;

ConsoleText.Initialize(args);

var requireEncryption = !args.Contains("--plain", StringComparer.OrdinalIgnoreCase);

bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

if (Flag("--background")) return await BackgroundMode.RunAsync(requireEncryption);
if (Flag("--diagnose")) return await DiagnoseMode.RunAsync(requireEncryption);
if (Flag("--stop")) return BackgroundMode.Stop();
if (Flag("--install-autostart")) return BackgroundMode.InstallAutoStart(true);
if (Flag("--remove-autostart")) return BackgroundMode.InstallAutoStart(false);

var peripheral = new BleHidPeripheral(requireEncryption);
peripheral.Log += message => Console.WriteLine(message);

// Hook callbacks must not block, so reports are queued and sent sequentially.
var sendQueue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
_ = Task.Run(async () =>
{
    await foreach (var work in sendQueue.Reader.ReadAllAsync())
    {
        try { await work(); }
        catch (Exception ex) { Console.WriteLine("  " + Loc.T("cli.sendError", ("message", ex.Message))); }
    }
});

var appearance = new AppearanceAdvertiser();
appearance.Log += message => Console.WriteLine(message);

Console.WriteLine(Loc.T("cli.starting") + "\n");

try
{
    await peripheral.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine("\n" + Loc.T("cli.startupFailed", ("message", ex.Message)));
    return 1;
}

Console.WriteLine();
Console.WriteLine(Loc.T("cli.ready.status", ("status", DisplayText.Advertisement(peripheral.AdvertisementStatus))));
Console.WriteLine(Loc.T("cli.ready.pair"));
Console.WriteLine();
foreach (var (command, key) in new[]
{
    ("type <text>", "cli.help.type"),
    ("key <name>", "cli.help.key"),
    ("move <dx> <dy>", "cli.help.move"),
    ("click <l|r|m>", "cli.help.click"),
    ("scroll <n>", "cli.help.scroll"),
    ("capture", "cli.help.capture"),
    ("capture verbose", "cli.help.captureVerbose"),
    ("capture <ms>", "cli.help.captureInterval"),
    ("host", "cli.help.host"),
    ("host <n|next|local|all>", "cli.help.hostSelect"),
    ("gesture", "cli.help.gesture"),
    ("gesture <on|off>", "cli.help.gestureToggle"),
    ("status", "cli.help.status"),
    ("peers", "cli.help.peers"),
    ("appearance", "cli.help.appearance"),
    ("burst <n>", "cli.help.burst"),
    ("watch <secs>", "cli.help.watch"),
    ("probe <uuid16>", "cli.help.probe"),
    ("l2cap", "cli.help.l2cap"),
    ("classic <on|off>", "cli.help.classic"),
    ("quit", "cli.help.quit")
})
    Console.WriteLine(ConsoleText.Help(command, key, width: 25));

Console.WriteLine();
Console.WriteLine(Loc.T("cli.help.whileCapturing"));
Console.WriteLine(ConsoleText.Help(Shortcuts.SwitchTarget, "cli.help.switchTarget", width: 25));
Console.WriteLine();
Console.WriteLine(Loc.T("cli.help.backgroundTitle"));
foreach (var (option, key) in new[]
{
    ("--background", "cli.help.background"),
    ("--stop", "cli.help.stop"),
    ("--install-autostart", "cli.help.installAutostart"),
    ("--remove-autostart", "cli.help.removeAutostart"),
    ("--lang <code>", "cli.help.lang")
})
    Console.WriteLine(ConsoleText.Help(option, key, width: 25));
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;

    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;

    var command = parts[0].ToLowerInvariant();
    var argument = parts.Length > 1 ? parts[1] : string.Empty;

    try
    {
        switch (command)
        {
            case "quit" or "exit":
                appearance.Dispose();
                await peripheral.DisposeAsync();
                return 0;

            case "appearance":
                appearance.Start();
                break;

            case "capture":
            {
                if (peripheral.SubscribedKeyboardClients == 0)
                {
                    Console.WriteLine("  " + Loc.T("cli.noSubscriber"));
                    break;
                }

                var captureArgs = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var verbose = captureArgs.Any(a => a.Equals("verbose", StringComparison.OrdinalIgnoreCase));
                // A BLE link carries one packet per connection interval, so reports are paced
                // to that rate; sending faster just queues in the controller and adds latency.
                var mouseIntervalMs = captureArgs.Select(a => int.TryParse(a, out var v) ? v : 0)
                                                 .FirstOrDefault(v => v > 0);
                if (mouseIntervalMs <= 0) mouseIntervalMs = 10;

                await CaptureSession.RunAsync(peripheral, Console.WriteLine, verbose, () => mouseIntervalMs,
                    stopEndsSession: true, CancellationToken.None);
                break;
            }

            case "classic":
            {
                if (argument is "off" or "on")
                {
                    var enable = argument == "on";
                    // Order matters: a non-connectable radio must already be non-discoverable.
                    if (enable)
                    {
                        Console.WriteLine($"  incoming connections -> on  (result {ClassicRadio.SetIncomingConnections(true)})");
                        Console.WriteLine($"  discoverable         -> on  (result {ClassicRadio.SetDiscoverable(true)})");
                    }
                    else
                    {
                        Console.WriteLine($"  discoverable         -> off (result {ClassicRadio.SetDiscoverable(false)})");
                        Console.WriteLine($"  incoming connections -> off (result {ClassicRadio.SetIncomingConnections(false)})");
                    }
                }
                Console.WriteLine($"  connectable  : {ClassicRadio.IsConnectable}");
                Console.WriteLine($"  discoverable : {ClassicRadio.IsDiscoverable}");
                break;
            }

            case "l2cap":
            {
                foreach (var result in L2capProbe.Run()) Console.WriteLine($"  {result}");
                break;
            }

            case "probe":
            {
                if (!ushort.TryParse(argument.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var uuid16))
                {
                    Console.WriteLine("  " + Loc.T("cli.usage.probe"));
                    break;
                }
                var (service, characteristic) = await ServiceProbe.TryCreateAsync(uuid16);
                Console.WriteLine($"  service 0x{uuid16:X4}: {service}");
                if (characteristic is not null) Console.WriteLine($"  service changed 0x2A05: {characteristic}");
                break;
            }

            case "watch":
            {
                var seconds = int.TryParse(argument, out var requested) && requested > 0 ? requested : 120;
                Console.WriteLine($"  watching for {seconds}s - reporting only changes");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var previous = string.Empty;
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    var connectedLe = await BluetoothDiagnostics.ListConnectedLeDevicesAsync();
                    var connectedClassic = await BluetoothDiagnostics.ListConnectedClassicDevicesAsync();
                    var snapshot = $"k={peripheral.SubscribedKeyboardClients} m={peripheral.SubscribedMouseClients}"
                                 + $" | LE: {string.Join(", ", connectedLe)} | classic: {string.Join(", ", connectedClassic)}";
                    if (snapshot != previous)
                    {
                        Console.WriteLine($"  [{clock.Elapsed:mm\\:ss}] {snapshot}");
                        previous = snapshot;
                    }
                    await Task.Delay(1000);
                }
                Console.WriteLine("  watch finished");
                break;
            }

            case "burst":
            {
                var count = int.TryParse(argument, out var parsed) ? parsed : 20;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < count; i++)
                {
                    var started = clock.ElapsedMilliseconds;
                    await peripheral.SendMouseAsync(MouseButtons.None, 5, 0, 0);
                    Console.WriteLine($"  notify {i}: {clock.ElapsedMilliseconds - started} ms");
                }
                Console.WriteLine($"  {count} notifies in {clock.ElapsedMilliseconds} ms");
                break;
            }

            case "host":
            {
                await peripheral.RefreshHostNamesAsync();
                var trimmed = argument.Trim();

                if (trimmed.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    peripheral.SelectNextHost();
                    Console.WriteLine($"  -> {DisplayText.Target(peripheral)}");
                }
                else if (trimmed.Equals("local", StringComparison.OrdinalIgnoreCase))
                {
                    peripheral.SelectLocal();
                    Console.WriteLine($"  -> {DisplayText.Target(peripheral)}");
                }
                else if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    peripheral.SelectAllHosts();
                    Console.WriteLine($"  -> {DisplayText.Target(peripheral)}");
                }
                else if (int.TryParse(trimmed, out var index) && !peripheral.SelectHost(index - 1))
                    Console.WriteLine("  " + Loc.T("cli.host.notFound"));

                var hosts = peripheral.Hosts();
                Console.WriteLine("  " + Loc.T("cli.host.sendingTo", ("target", DisplayText.Target(peripheral))));
                for (var i = 0; i < hosts.Count; i++)
                {
                    var marker = hosts[i].DeviceId == peripheral.SelectedHostId ? "*" : " ";
                    var locks = DisplayText.LockAlert(BleHidPeripheral.LockAlertFor(
                        peripheral.LockKeysFor(hosts[i].DeviceId), peripheral.NumpadUsed(hosts[i].DeviceId)));
                    var note = locks is null
                        ? string.Empty
                        : "   " + (peripheral.LockKeysMeasured(hosts[i].DeviceId) ? locks : Loc.T("cli.host.inferred", ("locks", locks)));
                    Console.WriteLine($"  {marker} {i + 1}. {hosts[i].Display}{note}");
                }
                if (hosts.Count == 0) Console.WriteLine("    " + Loc.T("cli.host.none"));
                break;
            }

            case "gesture":
            {
                var trimmed = argument.Trim();
                // Reload rather than cache, so tuning edited in the file by hand survives a toggle.
                var current = SwitchGestureOptions.Load(AppPaths.SwitchGestureConfig);

                if (trimmed is "on" or "off")
                {
                    var updated = current.WithEnabled(trimmed == "on");
                    if (!updated.TrySave(AppPaths.SwitchGestureConfig, out var error))
                    {
                        Console.WriteLine("  " + Loc.T("cli.gesture.saveFailed", ("error", error)));
                        break;
                    }
                    current = updated;
                }
                else if (trimmed.Length > 0)
                {
                    Console.WriteLine("  " + Loc.T("cli.usage.gesture"));
                    break;
                }

                Console.WriteLine("  " + Loc.T("cli.gesture.state", ("state", Loc.T(current.Enabled ? "common.on" : "common.off"))));
                Console.WriteLine("  " + Loc.T("cli.gesture.tuning", ("reversals", current.RequiredReversals), ("pixels", current.MinStrokePx), ("milliseconds", current.WindowMs)));
                if (current.Warning is { } warning) Console.WriteLine($"  {warning}");
                break;
            }

            case "status":
            {
                var lockKeys = peripheral.TargetLockKeys;
                var lockSource = Loc.T(peripheral.TargetLockKeysMeasured ? "cli.status.measured" : "cli.status.inferred");
                // Upper case marks the state that catches people out on a host they cannot see.
                var caps = Loc.T(lockKeys.HasFlag(LockKeys.CapsLock) ? "cli.status.onAlert" : "common.off");
                var num = Loc.T(lockKeys.HasFlag(LockKeys.NumLock) ? "common.on" : "cli.status.offAlert");
                foreach (var row in ConsoleText.Rows(
                             (Loc.T("cli.status.advertisement"), DisplayText.Advertisement(peripheral.AdvertisementStatus)),
                             (Loc.T("cli.status.appearance"), appearance.Status.ToString()),
                             (Loc.T("cli.status.keyboardSubscribers"), peripheral.SubscribedKeyboardClients.ToString()),
                             (Loc.T("cli.status.mouseSubscribers"), peripheral.SubscribedMouseClients.ToString()),
                             (Loc.T("cli.status.sendingTo"), DisplayText.Target(peripheral)),
                             (Loc.T("cli.status.capsLock"), $"{caps} ({lockSource})"),
                             (Loc.T("cli.status.numLock"), $"{num} ({lockSource})")))
                    Console.WriteLine(row);

                // BR/EDR links are usually incidental (CDP/Phone Link, audio) and unrelated to HID.
                var brEdrPeers = await BluetoothDiagnostics.ListConnectedClassicDevicesAsync();
                if (brEdrPeers.Count > 0)
                {
                    Console.WriteLine("  " + Loc.T("cli.status.brEdrPeers", ("count", brEdrPeers.Count)));
                    foreach (var peer in brEdrPeers) Console.WriteLine($"    {peer}");
                }
                break;
            }

            case "peers":
                var le = await BluetoothDiagnostics.ListConnectedLeDevicesAsync();
                var classic = await BluetoothDiagnostics.ListConnectedClassicDevicesAsync();
                Console.WriteLine($"  connected LE      ({le.Count}):");
                foreach (var device in le) Console.WriteLine($"    {device}");
                Console.WriteLine($"  connected classic ({classic.Count}):");
                foreach (var device in classic) Console.WriteLine($"    {device}");
                break;

            case "type":
                await TypeTextAsync(peripheral, argument);
                break;

            case "key":
                if (HidReports.NamedKeys.TryGetValue(argument.Trim(), out var usage))
                {
                    await peripheral.SendKeyboardAsync(KeyModifiers.None, usage);
                    await peripheral.ReleaseKeysAsync();
                }
                else Console.WriteLine("  " + Loc.T("cli.unknownKey", ("key", argument)));
                break;

            case "move":
                var deltas = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (deltas.Length == 2 && int.TryParse(deltas[0], out var dx) && int.TryParse(deltas[1], out var dy))
                    await peripheral.SendMouseAsync(MouseButtons.None, dx, dy, 0);
                else Console.WriteLine("  " + Loc.T("cli.usage.move"));
                break;

            case "click":
                var button = argument.Trim().ToLowerInvariant() switch
                {
                    "r" or "right" => MouseButtons.Right,
                    "m" or "middle" => MouseButtons.Middle,
                    _ => MouseButtons.Left
                };
                await peripheral.SendMouseAsync(button, 0, 0, 0);
                await peripheral.SendMouseAsync(MouseButtons.None, 0, 0, 0);
                break;

            case "scroll":
                if (int.TryParse(argument.Trim(), out var wheel))
                    await peripheral.SendMouseAsync(MouseButtons.None, 0, 0, wheel);
                else Console.WriteLine("  " + Loc.T("cli.usage.scroll"));
                break;

            default:
                Console.WriteLine("  " + Loc.T("cli.unknownCommand", ("command", command)));
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  " + Loc.T("cli.error", ("message", ex.Message)));
    }
}

await peripheral.DisposeAsync();
appearance.Dispose();
return 0;

static async Task TypeTextAsync(BleHidPeripheral peripheral, string text)
{
    foreach (var character in text)
    {
        if (!HidReports.TryMapChar(character, out var usage, out var modifiers))
        {
            Console.WriteLine("  " + Loc.T("cli.type.skipped", ("character", character)));
            continue;
        }

        await peripheral.SendKeyboardAsync(modifiers, usage);
        await Task.Delay(8);
        await peripheral.ReleaseKeysAsync();
        await Task.Delay(8);
    }
}
