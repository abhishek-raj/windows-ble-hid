using System.Numerics;

namespace BleHid.Core;

/// <summary>Modifier flags, valued as RegisterHotKey expects them.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8
}

/// <summary>Why a combination cannot be used as a shortcut.</summary>
public enum HotkeyProblem
{
    None,
    /// <summary>Only letters, digits and F1–F24 are supported.</summary>
    UnsupportedKey,
    /// <summary>A global shortcut with fewer than two modifiers steals ordinary typing or app shortcuts.</summary>
    NeedsModifiers,
    /// <summary>Windows treats AltGr as Ctrl+Alt, so this would block a character on many layouts.</summary>
    AltGr,
    /// <summary>Ctrl+Alt+Q already stops capture.</summary>
    Reserved
}

/// <summary>
/// A global shortcut: modifiers plus one key, identified by virtual-key code. Virtual keys
/// follow the local layout, which is what users expect: on AZERTY, Ctrl+Shift+A is the key
/// labelled A. The text form is always English so the settings file reads the same everywhere.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const HotkeyModifiers AllModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win;
    private const int VK_Q = 0x51;

    public HotkeyProblem Problem
    {
        get
        {
            if (!IsSupportedKey(VirtualKey) || (Modifiers & ~AllModifiers) != 0) return HotkeyProblem.UnsupportedKey;

            // Keyboards rarely have F13–F24, so macro keys mapped to them can stand alone.
            if (VirtualKey is >= 0x7C and <= 0x87) return HotkeyProblem.None;

            var ctrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
            // The stop check ignores Shift and Win, so any Ctrl+Alt+Q would trigger both.
            if (VirtualKey == VK_Q && (Modifiers & ctrlAlt) == ctrlAlt) return HotkeyProblem.Reserved;
            if (BitOperations.PopCount((uint)Modifiers) < 2) return HotkeyProblem.NeedsModifiers;
            // AltGr+Shift types characters too (É on US-International, Ą on Polish); only Win
            // takes a combination out of AltGr's reach.
            if ((Modifiers & ctrlAlt) == ctrlAlt && !Modifiers.HasFlag(HotkeyModifiers.Win) && VirtualKey is < 0x70)
                return HotkeyProblem.AltGr;
            return HotkeyProblem.None;
        }
    }

    /// <summary>The key alone, without modifiers: a letter, a digit or F1–F24.</summary>
    public string KeyName => VirtualKey is >= 0x70 and <= 0x87
        ? $"F{VirtualKey - 0x6F}"
        : ((char)VirtualKey).ToString();

    public static bool IsSupportedKey(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    /// <summary>Parses the English form, e.g. Ctrl+Shift+F12. Does not check <see cref="Problem"/>.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None
            };

            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryParseKey(part, out var virtualKey)) return false;
            key = virtualKey;
        }

        if (key is not { } found) return false;
        hotkey = new Hotkey(modifiers, found);
        return true;
    }

    private static bool TryParseKey(string text, out int virtualKey)
    {
        virtualKey = 0;
        if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
        {
            virtualKey = char.ToUpperInvariant(text[0]);
            return true;
        }

        if (text.Length is 2 or 3 && text[0] is 'F' or 'f' &&
            int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.None, null, out var number) &&
            number is >= 1 and <= 24)
        {
            virtualKey = 0x6F + number;
            return true;
        }

        return false;
    }
}
