using BleHid.Core;
using Xunit;

namespace BleHid.Core.Tests;

/// <summary>
/// Capture translates Windows virtual-key codes into HID usages, so an error here sends the
/// wrong character to the host with no local symptom at all.
/// </summary>
public class VirtualKeyMapTests
{
    [Theory]
    [InlineData(0x5A, 0x15, 0x1C)] // German Z, physical US Y
    [InlineData(0x59, 0x2C, 0x1D)] // German Y, physical US Z
    [InlineData(0xBA, 0x1A, 0x2F)] // German U umlaut
    [InlineData(0xC0, 0x27, 0x33)] // German O umlaut
    [InlineData(0xDE, 0x28, 0x34)] // German A umlaut
    [InlineData(0xDB, 0x0C, 0x2D)] // German sharp S
    [InlineData(0xBB, 0x1B, 0x30)] // German +
    [InlineData(0xBF, 0x2B, 0x31)] // German #
    [InlineData(0xBD, 0x35, 0x38)] // German -
    [InlineData(0xE2, 0x56, 0x64)] // ISO < > | (also UK extra key)
    [InlineData(0x41, 0x10, 0x14)] // French A, physical US Q
    [InlineData(0x51, 0x1E, 0x04)] // French Q, physical US A
    [InlineData(0x5A, 0x11, 0x1A)] // French Z, physical US W
    [InlineData(0x57, 0x2C, 0x1D)] // French W, physical US Z
    [InlineData(0x4D, 0x27, 0x33)] // French M, physical US semicolon
    public void Physical_positions_override_layout_translated_virtual_keys(int vk, uint scan, byte expected)
    {
        Assert.True(VirtualKeyMap.TryGetUsage(vk, scan, false, out var usage));
        Assert.Equal(expected, usage);
    }

    [Theory]
    [InlineData(0x0D, 0x1C, false, 0x28)]
    [InlineData(0x0D, 0x1C, true, 0x58)]
    [InlineData(0x24, 0x47, false, 0x5F)]
    [InlineData(0x24, 0x47, true, 0x4A)]
    [InlineData(0x6A, 0x37, false, 0x55)]
    [InlineData(0x2C, 0x37, true, 0x46)]
    [InlineData(0x6F, 0x35, true, 0x54)]
    [InlineData(0x13, 0x45, false, 0x48)]
    [InlineData(0x90, 0x45, true, 0x53)]
    public void Special_keys_preserve_physical_identity(int vk, uint scan, bool extended, byte expected)
    {
        Assert.True(VirtualKeyMap.TryGetUsage(vk, scan, extended, out var usage));
        Assert.Equal(expected, usage);
    }

    [Fact]
    public void Missing_or_unknown_scan_code_preserves_virtual_key_fallback()
    {
        Assert.True(VirtualKeyMap.TryGetUsage(0x41, 0, false, out var missing));
        Assert.True(VirtualKeyMap.TryGetUsage(0x41, 0xFF, false, out var unknown));
        Assert.Equal((byte)0x04, missing);
        Assert.Equal(missing, unknown);
    }

    [Fact]
    public void Physical_usages_fit_the_existing_report_descriptor()
    {
        foreach (var extended in new[] { false, true })
        {
            var seen = new HashSet<byte>();
            for (uint scan = 1; scan < 256; scan++)
            {
                if (!VirtualKeyMap.TryGetUsage(0, scan, extended, out var usage)) continue;
                Assert.InRange(usage, (byte)1, (byte)101);
                Assert.True(seen.Add(usage), $"Duplicate physical usage {usage:X2} at scan {scan:X2}");
            }
        }
    }

    [Theory]
    [InlineData(0x41, 0x04)] // A
    [InlineData(0x5A, 0x1D)] // Z
    [InlineData(0x31, 0x1E)] // 1
    [InlineData(0x39, 0x26)] // 9
    [InlineData(0x30, 0x27)] // 0
    [InlineData(0x0D, 0x28)] // Enter
    [InlineData(0x1B, 0x29)] // Escape
    [InlineData(0x08, 0x2A)] // Backspace
    [InlineData(0x09, 0x2B)] // Tab
    [InlineData(0x20, 0x2C)] // Space
    [InlineData(0x70, 0x3A)] // F1
    [InlineData(0x7B, 0x45)] // F12
    public void Maps_known_virtual_keys_to_usage_ids(int virtualKey, byte expected)
    {
        Assert.True(VirtualKeyMap.TryGetUsage(virtualKey, out var usage));
        Assert.Equal(expected, usage);
    }

    [Fact]
    public void Function_keys_are_contiguous_from_f1_to_f12()
    {
        for (var i = 0; i < 12; i++)
        {
            Assert.True(VirtualKeyMap.TryGetUsage(0x70 + i, out var usage));
            Assert.Equal(0x3A + i, usage);
        }
    }

    [Fact]
    public void Digits_one_through_nine_are_contiguous()
    {
        for (var i = 0; i < 9; i++)
        {
            Assert.True(VirtualKeyMap.TryGetUsage(0x31 + i, out var usage));
            Assert.Equal(0x1E + i, usage);
        }
    }

    [Fact]
    public void Zero_follows_nine_rather_than_preceding_one()
    {
        VirtualKeyMap.TryGetUsage(0x39, out var nine);
        VirtualKeyMap.TryGetUsage(0x30, out var zero);

        Assert.Equal(nine + 1, zero);
    }

    [Fact]
    public void Distinct_virtual_keys_never_share_a_usage()
    {
        var seen = new Dictionary<byte, int>();

        for (var vk = 0; vk <= 0xFF; vk++)
        {
            if (!VirtualKeyMap.TryGetUsage(vk, out var usage) || usage == 0) continue;
            Assert.False(seen.ContainsKey(usage),
                $"usage 0x{usage:X2} is produced by both VK 0x{seen.GetValueOrDefault(usage):X2} and VK 0x{vk:X2}");
            seen[usage] = vk;
        }
    }

    [Fact]
    public void Unmapped_virtual_keys_are_rejected()
    {
        Assert.False(VirtualKeyMap.TryGetUsage(0x07, out _));
    }

    /// <summary>Anything the map emits must fit the 6-slot key array's logical maximum of 101.</summary>
    [Fact]
    public void Every_mapped_usage_is_within_the_declared_logical_range()
    {
        for (var vk = 0; vk <= 0xFF; vk++)
            if (VirtualKeyMap.TryGetUsage(vk, out var usage))
                Assert.InRange(usage, (byte)0, (byte)101);
    }
}
