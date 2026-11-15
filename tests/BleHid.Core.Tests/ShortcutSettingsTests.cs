using BleHid.Core;
using Xunit;

namespace BleHid.Core.Tests;

public sealed class ShortcutSettingsTests
{
    [Theory]
    [InlineData("ctrl+shift+f12", "Ctrl+Shift+F12")]
    [InlineData(" Win + Alt + 7 ", "Alt+Win+7")]
    [InlineData("Control+Windows+b", "Ctrl+Win+B")]
    [InlineData("F13", "F13")]
    public void Shortcuts_parse_to_one_canonical_form(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(expected, hotkey.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Shift+F25")]
    [InlineData("Ctrl+Shift+Tab")]
    public void Malformed_shortcuts_do_not_parse(string text) =>
        Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Shift+B", HotkeyProblem.None)]
    [InlineData("Ctrl+Alt+F5", HotkeyProblem.None)]
    [InlineData("Ctrl+Alt+Win+E", HotkeyProblem.None)]
    [InlineData("F13", HotkeyProblem.None)]
    [InlineData("Ctrl+F12", HotkeyProblem.NeedsModifiers)]
    [InlineData("Shift+A", HotkeyProblem.NeedsModifiers)]
    [InlineData("Ctrl+Alt+E", HotkeyProblem.AltGr)]
    [InlineData("Ctrl+Alt+7", HotkeyProblem.AltGr)]
    [InlineData("Ctrl+Alt+Shift+E", HotkeyProblem.AltGr)]
    [InlineData("Ctrl+Alt+Q", HotkeyProblem.Reserved)]
    [InlineData("Ctrl+Alt+Shift+Q", HotkeyProblem.Reserved)]
    public void Shortcuts_that_would_steal_typing_or_existing_hotkeys_are_refused(string text, HotkeyProblem expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(expected, hotkey.Problem);
    }

    [Fact]
    public void A_refused_shortcut_in_a_hand_edited_file_is_ignored_with_a_warning()
    {
        var settings = ShortcutSettings.Parse("""{ "TogglePeripheral": "Ctrl+Q" }""");

        Assert.Null(settings.TogglePeripheral);
        Assert.Contains("NeedsModifiers", settings.Warning);
    }

    [Fact]
    public void A_saved_shortcut_loads_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-shortcuts-{Guid.NewGuid():N}.json");
        try
        {
            var hotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B);
            Assert.True(ShortcutSettings.Load(path).WithTogglePeripheral(hotkey).TrySave(path, out _));

            var loaded = ShortcutSettings.Load(path);

            Assert.Equal(hotkey, loaded.TogglePeripheral);
            Assert.Null(loaded.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
