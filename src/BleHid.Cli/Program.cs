using System.Threading.Channels;
using BleHid.Core;

var requireEncryption = !args.Contains("--plain", StringComparer.OrdinalIgnoreCase);

var peripheral = new BleHidPeripheral(requireEncryption);
peripheral.Log += message => Console.WriteLine(message);

// Hook callbacks must not block, so reports are queued and sent sequentially.
var sendQueue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
_ = Task.Run(async () =>
{
    await foreach (var work in sendQueue.Reader.ReadAllAsync())
    {
        try { await work(); }
        catch (Exception ex) { Console.WriteLine($"  send error: {ex.Message}"); }
    }
});

var appearance = new AppearanceAdvertiser();
appearance.Log += message => Console.WriteLine(message);

Console.WriteLine("Starting BLE HID peripheral...\n");

try
{
    await peripheral.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"\nStartup failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"""

    Advertisement status: {peripheral.AdvertisementStatus}
    Pair from your phone's Bluetooth settings, then use the commands below.

      type <text>          send keystrokes
      key <name>           press a named key (enter, esc, up, f5, ...)
      move <dx> <dy>       move the pointer
      click <l|r|m>        click a mouse button
      scroll <n>           scroll wheel
      capture              redirect local keyboard+mouse (Ctrl+Alt+Q to stop)
      capture verbose      same, with per-report timing diagnostics
      capture <ms>         same, with a custom pointer report interval
      status               show subscriber counts
      peers                list connected Bluetooth peers
      appearance           advertise GAP appearance = keyboard
      burst <n>            time <n> raw mouse notifies (link diagnostic)
      quit                 exit

    """);

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
                    Console.WriteLine("  no subscriber yet - connect a host first");
                    break;
                }

                var captureArgs = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var verbose = captureArgs.Any(a => a.Equals("verbose", StringComparison.OrdinalIgnoreCase));
                // A BLE link carries one packet per connection interval, so reports are paced
                // to that rate; sending faster just queues in the controller and adds latency.
                var mouseIntervalMs = captureArgs.Select(a => int.TryParse(a, out var v) ? v : 0)
                                                 .FirstOrDefault(v => v > 0);
                if (mouseIntervalMs <= 0) mouseIntervalMs = 10;
                Console.WriteLine($"  pointer report interval: {mouseIntervalMs} ms");

                using var capture = new InputCapture { Verbose = verbose };
                var stopped = new TaskCompletionSource();

                // Keystrokes must all be delivered, but pointer motion is coalesced:
                // the hook produces far more events than the BLE link can carry.
                var keyQueue = new System.Collections.Concurrent.ConcurrentQueue<(KeyModifiers Modifiers, byte[] Usages)>();
                var mouseLock = new object();
                int pendingDx = 0, pendingDy = 0, pendingWheel = 0;
                var pendingButtons = MouseButtons.None;
                var mouseDirty = false;
                var sent = 0;

                // Signal-driven rather than polled: Task.Delay has ~15 ms granularity on
                // Windows, which alone made pointer motion feel sluggish.
                var signal = new SemaphoreSlim(0, 1);
                void Wake()
                {
                    try { signal.Release(); }
                    catch (SemaphoreFullException) { }
                }

                using var pumpCancellation = new CancellationTokenSource();
                var pump = Task.Run(async () =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var iterations = 0;
                    long lastMouseSend = -1000;
                    if (verbose) Console.WriteLine("  [pump] started");
                    while (!pumpCancellation.IsCancellationRequested)
                    {
                        iterations++;
                        try
                        {
                            await signal.WaitAsync(pumpCancellation.Token);
                        }
                        catch (OperationCanceledException) { break; }

                        try
                        {
                            while (keyQueue.TryDequeue(out var key))
                            {
                                var started = clock.ElapsedMilliseconds;
                                await peripheral.SendKeyboardAsync(key.Modifiers, key.Usages);
                                var elapsed = clock.ElapsedMilliseconds - started;
                                if (verbose && sent < 40) Console.WriteLine($"  [pump] key notify #{sent} took {elapsed} ms");
                                Interlocked.Increment(ref sent);
                            }

                            bool hasMotion;
                            lock (mouseLock) hasMotion = mouseDirty;
                            if (!hasMotion) continue;

                            var sinceLast = clock.ElapsedMilliseconds - lastMouseSend;
                            if (sinceLast < mouseIntervalMs)
                                await Task.Delay((int)(mouseIntervalMs - sinceLast), pumpCancellation.Token);

                            int dx, dy, wheel;
                            MouseButtons buttons;
                            lock (mouseLock)
                            {
                                dx = pendingDx; dy = pendingDy; wheel = pendingWheel;
                                buttons = pendingButtons;
                                pendingDx = pendingDy = pendingWheel = 0;
                                mouseDirty = false;
                            }

                            {
                                var started = clock.ElapsedMilliseconds;
                                await peripheral.SendMouseAsync(buttons, dx, dy, wheel);
                                lastMouseSend = clock.ElapsedMilliseconds;
                                if (verbose && sent < 40) Console.WriteLine($"  [pump] mouse notify #{sent} ({dx},{dy}) took {lastMouseSend - started} ms");
                                Interlocked.Increment(ref sent);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [pump] send error: {ex}");
                        }
                    }

                    if (verbose)
                        Console.WriteLine($"  [pump] exited after {iterations} iterations, {clock.ElapsedMilliseconds} ms");
                });

                capture.Log += message => Console.WriteLine(message);
                capture.KeyboardReport += (modifiers, usages) =>
                {
                    keyQueue.Enqueue((modifiers, usages));
                    Wake();
                };
                capture.MouseReport += (buttons, dx, dy, wheel) =>
                {
                    lock (mouseLock)
                    {
                        pendingDx += dx;
                        pendingDy += dy;
                        pendingWheel += wheel;
                        pendingButtons = buttons;
                        mouseDirty = true;
                    }
                    Wake();
                };
                capture.StopRequested += () => stopped.TrySetResult();

                Console.WriteLine("  capturing - local input is redirected. Press Ctrl+Alt+Q to stop.");
                capture.Start();
                await stopped.Task;
                capture.Stop();
                pumpCancellation.Cancel();
                try { await pump; } catch (OperationCanceledException) { }
                await peripheral.ReleaseKeysAsync();
                Console.WriteLine($"  capture stopped. keyboard events={capture.KeyboardEvents}, mouse events={capture.MouseEvents}, reports sent={sent}");
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

            case "status":
                Console.WriteLine($"  advertisement : {peripheral.AdvertisementStatus}");
                Console.WriteLine($"  appearance adv: {appearance.Status}");
                Console.WriteLine($"  keyboard subs : {peripheral.SubscribedKeyboardClients}");
                Console.WriteLine($"  mouse subs    : {peripheral.SubscribedMouseClients}");
                break;

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
                else Console.WriteLine($"  unknown key '{argument}'");
                break;

            case "move":
                var deltas = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (deltas.Length == 2 && int.TryParse(deltas[0], out var dx) && int.TryParse(deltas[1], out var dy))
                    await peripheral.SendMouseAsync(MouseButtons.None, dx, dy, 0);
                else Console.WriteLine("  usage: move <dx> <dy>");
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
                else Console.WriteLine("  usage: scroll <n>");
                break;

            default:
                Console.WriteLine($"  unknown command '{command}'");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  error: {ex.Message}");
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
            Console.WriteLine($"  skipped unmappable character '{character}'");
            continue;
        }

        await peripheral.SendKeyboardAsync(modifiers, usage);
        await Task.Delay(8);
        await peripheral.ReleaseKeysAsync();
        await Task.Delay(8);
    }
}
