namespace BleHid.Core;

/// <summary>Maps physical Windows keys to HID usages, with a virtual-key fallback.</summary>
public static class VirtualKeyMap
{
    /// <summary>
    /// Resolves a key from its physical identity rather than the virtual-key code Windows
    /// derived from it. The receiving host applies its keyboard layout and lock state, just as
    /// for a hardware keyboard. Applying the Windows layout here would translate keys twice.
    /// </summary>
    public static bool TryGetUsage(int virtualKey, uint scanCode, bool extended, out byte usage)
    {
        // Pause and Num Lock can both arrive with scan code 0x45. Pause's E1 prefix is not
        // represented by the hook's extended flag, so use VK_PAUSE to disambiguate it.
        if (virtualKey == 0x13)
        {
            usage = 0x48;
            return true;
        }

        if (TryGetScanCodeUsage(scanCode, extended, out usage)) return true;

        // Keypad Enter shares VK_RETURN with the main Enter; only the extended flag tells them
        // apart, and some hosts bind them separately.
        if (virtualKey == 0x0D && extended)
        {
            usage = 0x58; // Keypad Enter
            return true;
        }

        return TryGetUsage(virtualKey, out usage);
    }

    /// <summary>
    /// Windows set-1 scan codes identify positions, independent of the active input language.
    /// Extended navigation keys share scan codes with the non-extended numeric keypad.
    /// </summary>
    private static bool TryGetScanCodeUsage(uint scanCode, bool extended, out byte usage)
    {
        if (extended)
        {
            usage = scanCode switch
            {
                0x1C => 0x58, // Keypad Enter
                0x35 => 0x54, // Keypad /
                0x37 => 0x46, // Print Screen
                0x45 => 0x53, // Num Lock
                0x47 => 0x4A, // Home
                0x48 => 0x52, // Up
                0x49 => 0x4B, // Page Up
                0x4B => 0x50, // Left
                0x4D => 0x4F, // Right
                0x4F => 0x4D, // End
                0x50 => 0x51, // Down
                0x51 => 0x4E, // Page Down
                0x52 => 0x49, // Insert
                0x53 => 0x4C, // Delete
                0x5D => 0x65, // Application
                _ => 0
            };
            return usage != 0;
        }

        usage = scanCode switch
        {
            0x01 => 0x29, // Escape
            >= 0x02 and <= 0x0A => (byte)(0x1E + scanCode - 0x02), // 1-9 positions
            0x0B => 0x27, // 0
            0x0C => 0x2D, // US -
            0x0D => 0x2E, // US =
            0x0E => 0x2A, // Backspace
            0x0F => 0x2B, // Tab
            0x10 => 0x14, // Q
            0x11 => 0x1A, // W
            0x12 => 0x08, // E
            0x13 => 0x15, // R
            0x14 => 0x17, // T
            0x15 => 0x1C, // Y (German Z)
            0x16 => 0x18, // U
            0x17 => 0x0C, // I
            0x18 => 0x12, // O
            0x19 => 0x13, // P
            0x1A => 0x2F, // US [
            0x1B => 0x30, // US ]
            0x1C => 0x28, // Enter
            0x1E => 0x04, // A
            0x1F => 0x16, // S
            0x20 => 0x07, // D
            0x21 => 0x09, // F
            0x22 => 0x0A, // G
            0x23 => 0x0B, // H
            0x24 => 0x0D, // J
            0x25 => 0x0E, // K
            0x26 => 0x0F, // L
            0x27 => 0x33, // US ;
            0x28 => 0x34, // US '
            0x29 => 0x35, // US `
            0x2B => 0x31, // US backslash / ISO key beside Enter
            0x2C => 0x1D, // Z (German Y)
            0x2D => 0x1B, // X
            0x2E => 0x06, // C
            0x2F => 0x19, // V
            0x30 => 0x05, // B
            0x31 => 0x11, // N
            0x32 => 0x10, // M
            0x33 => 0x36, // US ,
            0x34 => 0x37, // US .
            0x35 => 0x38, // US /
            0x37 => 0x55, // Keypad *
            0x39 => 0x2C, // Space
            0x3A => 0x39, // Caps Lock
            >= 0x3B and <= 0x44 => (byte)(0x3A + scanCode - 0x3B), // F1-F10
            0x45 => 0x53, // Num Lock
            0x46 => 0x47, // Scroll Lock
            0x47 => (byte)0x5F, // Keypad 7
            0x48 => (byte)0x60, // Keypad 8
            0x49 => (byte)0x61, // Keypad 9
            0x4A => 0x56, // Keypad -
            0x4B => (byte)0x5C, // Keypad 4
            0x4C => (byte)0x5D, // Keypad 5
            0x4D => (byte)0x5E, // Keypad 6
            0x4E => 0x57, // Keypad +
            0x4F => (byte)0x59, // Keypad 1
            0x50 => (byte)0x5A, // Keypad 2
            0x51 => (byte)0x5B, // Keypad 3
            0x52 => (byte)0x62, // Keypad 0
            0x53 => (byte)0x63, // Keypad .
            0x56 => 0x64, // ISO extra key beside left Shift (German < > |)
            0x57 => 0x44, // F11
            0x58 => 0x45, // F12
            _ => (byte)0
        };
        return usage != 0;
    }

    public static bool TryGetUsage(int virtualKey, out byte usage)
    {
        switch (virtualKey)
        {
            case >= 0x41 and <= 0x5A: // A-Z
                usage = (byte)(0x04 + (virtualKey - 0x41));
                return true;
            case >= 0x31 and <= 0x39: // 1-9
                usage = (byte)(0x1E + (virtualKey - 0x31));
                return true;
            case 0x30: usage = 0x27; return true; // 0
            case >= 0x70 and <= 0x7B: // F1-F12
                usage = (byte)(0x3A + (virtualKey - 0x70));
                return true;
            case >= 0x61 and <= 0x69: // Numpad 1-9 (Windows sends these only with Num Lock on)
                usage = (byte)(0x59 + (virtualKey - 0x61));
                return true;
        }

        usage = virtualKey switch
        {
            0x0D => 0x28, // Enter
            0x1B => 0x29, // Escape
            0x08 => 0x2A, // Backspace
            0x09 => 0x2B, // Tab
            0x20 => 0x2C, // Space
            0xBD => 0x2D, // -
            0xBB => 0x2E, // =
            0xDB => 0x2F, // [
            0xDD => 0x30, // ]
            0xDC => 0x31, // \
            0xBA => 0x33, // ;
            0xDE => 0x34, // '
            0xC0 => 0x35, // `
            0xBC => 0x36, // ,
            0xBE => 0x37, // .
            0xBF => 0x38, // /
            0x14 => 0x39, // Caps Lock
            0x2C => 0x46, // Print Screen
            0x91 => 0x47, // Scroll Lock
            0x13 => 0x48, // Pause
            0x2D => 0x49, // Insert
            0x24 => 0x4A, // Home
            0x21 => 0x4B, // Page Up
            0x2E => 0x4C, // Delete
            0x23 => 0x4D, // End
            0x22 => 0x4E, // Page Down
            0x27 => 0x4F, // Right
            0x25 => 0x50, // Left
            0x28 => 0x51, // Down
            0x26 => 0x52, // Up
            0x90 => 0x53, // Num Lock
            0x6F => 0x54, // Numpad /
            0x6A => 0x55, // Numpad *
            0x6D => 0x56, // Numpad -
            0x6B => 0x57, // Numpad +
            0x60 => 0x62, // Numpad 0
            0x6E => 0x63, // Numpad .
            _ => 0
        };

        return usage != 0;
    }
}
