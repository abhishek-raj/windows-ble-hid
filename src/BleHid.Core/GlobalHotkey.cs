using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BleHid.Core;

public enum HotkeyRegistration
{
    /// <summary>No shortcut is set, or it is paused.</summary>
    Off,
    Registered,
    /// <summary>Another app already owns the combination.</summary>
    InUse,
    Failed
}

/// <summary>
/// One system-wide hotkey. RegisterHotKey delivers WM_HOTKEY to the registering thread's
/// queue, so registration lives on a dedicated thread that does nothing but pump messages;
/// the UI and background mode both work without a window.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APPLY = 0x8001; // WM_APP + 1
    private const uint PM_NOREMOVE = 0x0000;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly ConcurrentQueue<(Hotkey? Hotkey, TaskCompletionSource<HotkeyRegistration> Done)> _requests = new();
    private readonly Thread _thread;
    private readonly uint _threadId;
    private bool _registered;
    private volatile bool _disposed;

    public GlobalHotkey()
    {
        using var ready = new ManualResetEventSlim();
        uint threadId = 0;
        _thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            // Creates the message queue, so requests posted from now on are not lost.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            ready.Set();
            Pump();
        }) { IsBackground = true, Name = "BleHid global hotkey" };
        _thread.Start();
        ready.Wait();
        _threadId = threadId;
    }

    /// <summary>
    /// Raised on the hotkey thread. Handlers must hand work off rather than block, or later
    /// presses and registration changes queue up behind them.
    /// </summary>
    public event Action? Pressed;

    /// <summary>Replaces the registered combination; null unregisters it.</summary>
    public HotkeyRegistration Set(Hotkey? hotkey)
    {
        if (_disposed) return HotkeyRegistration.Off;
        var done = new TaskCompletionSource<HotkeyRegistration>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue((hotkey, done));
        if (!PostThreadMessage(_threadId, WM_APPLY, IntPtr.Zero, IntPtr.Zero)) return HotkeyRegistration.Failed;
        return done.Task.Wait(TimeSpan.FromSeconds(5)) ? done.Task.Result : HotkeyRegistration.Failed;
    }

    private void Pump()
    {
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WM_HOTKEY && message.wParam == HotkeyId)
            {
                try { Pressed?.Invoke(); }
                catch (Exception) { /* a handler fault must not stop the pump */ }
            }
            else if (message.message == WM_APPLY)
            {
                while (_requests.TryDequeue(out var request)) request.Done.TrySetResult(Apply(request.Hotkey));
            }
        }

        if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
        while (_requests.TryDequeue(out var request)) request.Done.TrySetResult(HotkeyRegistration.Off);
    }

    private HotkeyRegistration Apply(Hotkey? hotkey)
    {
        if (_registered)
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
        }

        if (hotkey is not { } key) return HotkeyRegistration.Off;

        // Without MOD_NOREPEAT a held shortcut would stop and start the peripheral repeatedly.
        if (RegisterHotKey(IntPtr.Zero, HotkeyId, (uint)key.Modifiers | MOD_NOREPEAT, (uint)key.VirtualKey))
        {
            _registered = true;
            return HotkeyRegistration.Registered;
        }

        return Marshal.GetLastWin32Error() == ERROR_HOTKEY_ALREADY_REGISTERED
            ? HotkeyRegistration.InUse
            : HotkeyRegistration.Failed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
