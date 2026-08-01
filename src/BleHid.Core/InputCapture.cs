using System.Runtime.InteropServices;

namespace BleHid.Core;

/// <summary>
/// Captures local keyboard and mouse input via low-level hooks and translates it to HID reports.
/// Input is swallowed while capturing; Ctrl+Alt+Q releases it.
/// </summary>
public sealed class InputCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const uint LLMHF_INJECTED = 0x01;

    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;

    private readonly HashSet<byte> _pressedUsages = [];
    private readonly HashSet<int> _pressedVirtualKeys = [];
    private LowLevelProc? _keyboardProc;
    private LowLevelProc? _mouseProc;
    private IntPtr _keyboardHook, _mouseHook;
    private Thread? _thread;
    private uint _threadId;
    private MouseButtons _buttons;
    private int _centerX, _centerY;
    private int _keyboardEvents, _mouseEvents;
    private bool _switchLatched;
    private volatile bool _running;

    public event Action<KeyModifiers, byte[]>? KeyboardReport;
    public event Action<MouseButtons, int, int, int>? MouseReport;
    public event Action? SwitchHostRequested;
    public event Action? StopRequested;
    public event Action<string>? Log;

    public bool IsRunning => _running;

    /// <summary>Logs every hook event and report; useful only for diagnosing delivery problems.</summary>
    public bool Verbose { get; init; }

    public int KeyboardEvents => _keyboardEvents;
    public int MouseEvents => _mouseEvents;

    public void Start()
    {
        if (_running) return;
        _running = true;

        _thread = new Thread(HookThread) { IsBackground = true, Name = "BleHid input capture" };
        // Must not be STA: an STA hook thread dispatches WinRT completions through the same
        // message pump the input callbacks saturate, which stalls GATT notifications for seconds.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();

        // Default timer granularity is ~15.6 ms, too coarse to pace HID reports.
        timeBeginPeriod(1);

        _centerX = GetSystemMetrics(0) / 2;
        _centerY = GetSystemMetrics(1) / 2;
        SetCursorPos(_centerX, _centerY);

        _keyboardProc = KeyboardHookProc;
        _mouseProc = MouseHookProc;

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        var keyboardError = Marshal.GetLastWin32Error();
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        var mouseError = Marshal.GetLastWin32Error();

        Log?.Invoke($"  [hook] keyboard=0x{_keyboardHook:x} (err {keyboardError}), mouse=0x{_mouseHook:x} (err {mouseError}), center={_centerX},{_centerY}");

        int result;
        while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (Verbose)
            Log?.Invoke($"  [hook] message loop exited ({result}), keyboard events={_keyboardEvents}, mouse events={_mouseEvents}");

        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
        timeEndPeriod(1);
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var virtualKey = (int)data.vkCode;
        _keyboardEvents++;

        if (isDown) _pressedVirtualKeys.Add(virtualKey);
        else _pressedVirtualKeys.Remove(virtualKey);

        if (isDown && virtualKey == 0x51 && IsDown(0x11) && IsDown(0x12)) // Ctrl+Alt+Q
        {
            _pressedUsages.Clear();
            _pressedVirtualKeys.Clear();
            KeyboardReport?.Invoke(KeyModifiers.None, []);
            StopRequested?.Invoke();
            return 1;
        }

        // Ctrl+D+C switches host. Latched so key auto-repeat cannot cycle past the target.
        if (!isDown && virtualKey is 0x44 or 0x43) _switchLatched = false;
        if (isDown && IsDown(0x11) && _pressedVirtualKeys.Contains(0x44) && _pressedVirtualKeys.Contains(0x43))
        {
            if (!_switchLatched)
            {
                _switchLatched = true;
                _pressedUsages.Clear();
                KeyboardReport?.Invoke(KeyModifiers.None, []);
                SwitchHostRequested?.Invoke();
            }
            return 1;
        }

        if (VirtualKeyMap.TryGetUsage(virtualKey, out var usage))
        {
            if (isDown) _pressedUsages.Add(usage);
            else _pressedUsages.Remove(usage);
        }

        var modifiers = CurrentModifiers();
        var usages = _pressedUsages.Take(6).ToArray();
        if (Verbose && _keyboardEvents <= 20)
            Log?.Invoke($"  [key] vk=0x{virtualKey:x2} {(isDown ? "down" : "up")} -> mod=0x{(byte)modifiers:x2} usages=[{string.Join(" ", usages.Select(u => u.ToString("x2")))}]");

        KeyboardReport?.Invoke(modifiers, usages);
        return 1; // swallow locally
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLMHF_INJECTED) != 0)
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        _mouseEvents++;

        switch ((int)wParam)
        {
            case WM_MOUSEMOVE:
                var dx = data.pt.x - _centerX;
                var dy = data.pt.y - _centerY;
                if (dx != 0 || dy != 0)
                {
                    if (Verbose && _mouseEvents <= 10) Log?.Invoke($"  [mouse] move {dx},{dy}");
                    MouseReport?.Invoke(_buttons, dx, dy, 0);
                    SetCursorPos(_centerX, _centerY);
                }
                break;

            case WM_LBUTTONDOWN: _buttons |= MouseButtons.Left; MouseReport?.Invoke(_buttons, 0, 0, 0); break;
            case WM_LBUTTONUP:   _buttons &= ~MouseButtons.Left; MouseReport?.Invoke(_buttons, 0, 0, 0); break;
            case WM_RBUTTONDOWN: _buttons |= MouseButtons.Right; MouseReport?.Invoke(_buttons, 0, 0, 0); break;
            case WM_RBUTTONUP:   _buttons &= ~MouseButtons.Right; MouseReport?.Invoke(_buttons, 0, 0, 0); break;
            case WM_MBUTTONDOWN: _buttons |= MouseButtons.Middle; MouseReport?.Invoke(_buttons, 0, 0, 0); break;
            case WM_MBUTTONUP:   _buttons &= ~MouseButtons.Middle; MouseReport?.Invoke(_buttons, 0, 0, 0); break;

            case WM_MOUSEWHEEL:
                var notches = (short)((data.mouseData >> 16) & 0xFFFF) / 120;
                if (notches != 0) MouseReport?.Invoke(_buttons, 0, 0, notches);
                break;
        }

        return 1; // swallow locally
    }

    private bool IsDown(int virtualKey) => virtualKey switch
    {
        0x11 => _pressedVirtualKeys.Contains(0xA2) || _pressedVirtualKeys.Contains(0xA3),
        0x12 => _pressedVirtualKeys.Contains(0xA4) || _pressedVirtualKeys.Contains(0xA5),
        _ => _pressedVirtualKeys.Contains(virtualKey)
    };

    private KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (_pressedVirtualKeys.Contains(0xA0)) modifiers |= KeyModifiers.LeftShift;
        if (_pressedVirtualKeys.Contains(0xA1)) modifiers |= KeyModifiers.RightShift;
        if (_pressedVirtualKeys.Contains(0xA2)) modifiers |= KeyModifiers.LeftControl;
        if (_pressedVirtualKeys.Contains(0xA3)) modifiers |= KeyModifiers.RightControl;
        if (_pressedVirtualKeys.Contains(0xA4)) modifiers |= KeyModifiers.LeftAlt;
        if (_pressedVirtualKeys.Contains(0xA5)) modifiers |= KeyModifiers.RightAlt;
        if (_pressedVirtualKeys.Contains(0x5B)) modifiers |= KeyModifiers.LeftGui;
        if (_pressedVirtualKeys.Contains(0x5C)) modifiers |= KeyModifiers.RightGui;
        return modifiers;
    }

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
