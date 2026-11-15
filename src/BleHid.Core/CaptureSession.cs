using System.Collections.Concurrent;
using System.Diagnostics;

namespace BleHid.Core;

/// <summary>
/// Runs a capture session: local hooks in, paced HID reports out. Shared by the interactive
/// `capture` command, background mode, and the desktop UI.
/// </summary>
public static class CaptureSession
{
    internal enum Queued { Report, MouseReport, SwitchHost, GoLocal }

    internal readonly record struct InputReport(Queued Kind,
        KeyModifiers Modifiers = KeyModifiers.None, byte[]? Usages = null,
        MouseButtons Buttons = MouseButtons.None, int Dx = 0, int Dy = 0, int Wheel = 0);

    internal sealed class ReportQueue
    {
        private readonly ConcurrentQueue<InputReport> _ordered = new();
        private readonly object _mouseLock = new();
        private InputReport? _pendingMouse;

        public bool HasMouseReport
        {
            get { lock (_mouseLock) return _pendingMouse.HasValue; }
        }

        public void EnqueueKeyboard(KeyModifiers modifiers, byte[] usages) =>
            _ordered.Enqueue(new(Queued.Report, modifiers, usages));

        public void EnqueueMouse(MouseButtons buttons, int dx, int dy, int wheel)
        {
            lock (_mouseLock)
            {
                var pending = _pendingMouse.GetValueOrDefault();
                _pendingMouse = new(Queued.MouseReport, Buttons: buttons,
                    Dx: pending.Dx + dx, Dy: pending.Dy + dy, Wheel: pending.Wheel + wheel);
            }
        }

        public void EnqueueTargetChange(Queued kind)
        {
            lock (_mouseLock)
            {
                if (_pendingMouse is { } mouse)
                {
                    _ordered.Enqueue(mouse);
                    _pendingMouse = null;
                }
                _ordered.Enqueue(new(kind));
            }
        }

        public bool TryDequeue(out InputReport report) => _ordered.TryDequeue(out report);

        public bool TryTakeMouseReport(out InputReport report)
        {
            lock (_mouseLock)
            {
                if (!_ordered.IsEmpty)
                {
                    report = default;
                    return false;
                }

                report = _pendingMouse.GetValueOrDefault();
                var available = _pendingMouse.HasValue;
                _pendingMouse = null;
                return available;
            }
        }
    }

    /// <param name="mouseIntervalMs">
    /// Read per report rather than captured, so the UI can retune pacing without restarting.
    /// </param>
    /// <param name="stopEndsSession">
    /// Console mode ends on Ctrl+Alt+Q. Background mode has no console to return to, so the
    /// same hotkey drops to the local target instead and capture keeps running.
    /// </param>
    /// <param name="toggleShortcut">
    /// The owner's stop/start shortcut. Capture only has to catch it while input is redirected,
    /// and reports it through the shortcut's own event; stopping is the owner's job.
    /// </param>
    public static async Task<int> RunAsync(
        BleHidPeripheral peripheral,
        Action<string> log,
        bool verbose,
        Func<int> mouseIntervalMs,
        bool stopEndsSession,
        CancellationToken cancellationToken,
        PeripheralToggleShortcut? toggleShortcut = null)
    {
        using var capture = new InputCapture { Verbose = verbose };
        var stopped = new TaskCompletionSource();
        using var registration = cancellationToken.Register(() => stopped.TrySetResult());

        // The pointer switch gesture is opt-in via a JSON file, and that file is watched so a
        // toggle from the UI or another CLI instance reaches this session without a restart.
        using var gestureWatcher = new SwitchGestureWatcher(AppPaths.SwitchGestureConfig);
        ApplyGesture(gestureWatcher.Current);
        gestureWatcher.Changed += ApplyGesture;

        void ApplyGesture(SwitchGestureOptions options)
        {
            capture.SetSwitchGesture(options);
            if (options.Warning is { } warning) log($"  [gesture] {warning}");
            log(options.Enabled
                ? $"  [gesture] shake to switch enabled ({options.RequiredReversals} reversals of {options.MinStrokePx}px within {options.WindowMs}ms)"
                : "  [gesture] shake to switch disabled");
        }

        // Keystrokes must all be delivered, but pointer motion is coalesced: the hook produces
        // far more events than the BLE link can carry. Target changes travel through the same
        // queue so they take effect only after the key-release report has gone to the old host.
        var reports = new ReportQueue();
        var sent = 0;

        // The radio interleaves connection events across every subscribed link, so a second host
        // starves the one we are notifying even when it receives nothing. Broadcast pays again on
        // top of that: measured, 2 hosts needed 40 ms rather than 20 ms.
        int PointerIntervalMs()
        {
            var hostIntervalMs = peripheral.MouseReportIntervalMs(mouseIntervalMs());
            var links = Math.Max(1, peripheral.SubscribedMouseClients);
            var broadcasting = peripheral.SelectedHostId is null && !peripheral.IsLocalTarget;
            return broadcasting ? hostIntervalMs * 2 * links : hostIntervalMs;
        }

        // Signal-driven rather than polled: Task.Delay has ~15 ms granularity on Windows,
        // which alone made pointer motion feel sluggish.
        var signal = new SemaphoreSlim(0, 1);
        void Wake()
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
        }

        using var pumpCancellation = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var iterations = 0;
            long lastMouseSend = -1000;
            if (verbose) log("  [pump] started");
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
                    while (reports.TryDequeue(out var key))
                    {
                        if (key.Kind == Queued.MouseReport)
                        {
                            await peripheral.SendMouseAsync(key.Buttons, key.Dx, key.Dy, key.Wheel);
                            lastMouseSend = clock.ElapsedMilliseconds;
                            Interlocked.Increment(ref sent);
                            continue;
                        }

                        if (key.Kind is Queued.SwitchHost or Queued.GoLocal)
                        {
                            // Read before the target moves: this is the host we are leaving, and
                            // a lock left on there is invisible once we stop sending to it.
                            var leftHost = peripheral.IsLocalTarget ? null : peripheral.SelectedHostDisplay;
                            var leftLocks = peripheral.TargetLockWarning;

                            await peripheral.RefreshHostNamesAsync();
                            var target = key.Kind == Queued.GoLocal
                                ? peripheral.SelectLocal()
                                : peripheral.SelectNextHost();
                            capture.SetPassThrough(peripheral.IsLocalTarget);

                            var locks = peripheral.TargetLockWarning;
                            var arriving = locks is null ? string.Empty : $" - {locks}";
                            log($"  [host] -> {target} (pointer interval {PointerIntervalMs()} ms){arriving}");
                            if (leftLocks is not null && leftHost is not null)
                                log($"  [host] left {leftHost} with {leftLocks}");
                            continue;
                        }

                        var started = clock.ElapsedMilliseconds;
                        await peripheral.SendKeyboardAsync(key.Modifiers, key.Usages!);
                        var elapsed = clock.ElapsedMilliseconds - started;
                        if (verbose && sent < 40) log($"  [pump] key notify #{sent} took {elapsed} ms");
                        Interlocked.Increment(ref sent);
                    }

                    if (!reports.HasMouseReport) continue;

                    // NotifyValueAsync returns on queueing, so overshoot is invisible here and
                    // shows up as pointer drift after the user stops moving.
                    var interval = PointerIntervalMs();

                    var sinceLast = clock.ElapsedMilliseconds - lastMouseSend;
                    if (sinceLast < interval)
                        await Task.Delay((int)(interval - sinceLast), pumpCancellation.Token);

                    if (!reports.TryTakeMouseReport(out var mouse)) continue;

                    {
                        var started = clock.ElapsedMilliseconds;
                        await peripheral.SendMouseAsync(mouse.Buttons, mouse.Dx, mouse.Dy, mouse.Wheel);
                        lastMouseSend = clock.ElapsedMilliseconds;
                        if (verbose && sent < 40) log($"  [pump] mouse notify #{sent} ({mouse.Dx},{mouse.Dy}) took {lastMouseSend - started} ms");
                        Interlocked.Increment(ref sent);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log($"  [pump] send error: {ex}");
                }
            }

            if (verbose) log($"  [pump] exited after {iterations} iterations, {clock.ElapsedMilliseconds} ms");
        });

        capture.Log += message => log(message);
        capture.KeyboardReport += (modifiers, usages) =>
        {
            reports.EnqueueKeyboard(modifiers, usages);
            Wake();
        };
        capture.MouseReport += (buttons, dx, dy, wheel) =>
        {
            reports.EnqueueMouse(buttons, dx, dy, wheel);
            Wake();
        };
        capture.SwitchHostRequested += () =>
        {
            reports.EnqueueTargetChange(Queued.SwitchHost);
            Wake();
        };
        capture.StopRequested += () =>
        {
            if (stopEndsSession)
            {
                stopped.TrySetResult();
                return;
            }

            // Local recovery must not wait behind a notification to a host that just vanished.
            // Keep GoLocal queued as well so a healthy host still receives key releases first.
            capture.SetPassThrough(true);
            reports.EnqueueTargetChange(Queued.GoLocal);
            Wake();
        };

        await peripheral.RefreshHostNamesAsync();
        capture.SetPassThrough(peripheral.IsLocalTarget);

        // The UI can retarget mid-session, and the hook has to stop swallowing input when it does.
        void OnTargetChanged() => capture.SetPassThrough(peripheral.IsLocalTarget);
        peripheral.TargetChanged += OnTargetChanged;

        void ApplyToggleShortcut() => capture.SetToggleHotkey(toggleShortcut?.Hotkey);
        if (toggleShortcut is not null)
        {
            ApplyToggleShortcut();
            toggleShortcut.Changed += ApplyToggleShortcut;
            capture.TogglePeripheralRequested += toggleShortcut.RequestToggle;
        }

        try
        {
            log($"  pointer report interval: {PointerIntervalMs()} ms");
            log($"  sending to: {peripheral.SelectedHostDisplay}");
            log("  Ctrl+Alt+CapsLock or Ctrl+D+C switches target.");
            log(stopEndsSession
                ? "  capturing - Ctrl+Alt+Q stops."
                : "  capturing - Ctrl+Alt+Q returns input to this PC.");
            if (toggleShortcut?.Hotkey is { } toggle) log($"  {toggle} stops the peripheral.");

            capture.Start();
            await stopped.Task;
        }
        finally
        {
            capture.Stop();
            peripheral.TargetChanged -= OnTargetChanged;
            if (toggleShortcut is not null) toggleShortcut.Changed -= ApplyToggleShortcut;
        }

        pumpCancellation.Cancel();
        try { await pump; } catch (OperationCanceledException) { }
        await peripheral.ReleaseKeysAsync();
        log($"  capture stopped. keyboard events={capture.KeyboardEvents}, mouse events={capture.MouseEvents}, reports sent={sent}");
        return sent;
    }
}
