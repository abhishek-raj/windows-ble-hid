using BleHid.Localization;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BleHid.Core;

/// <summary>
/// Translated wording for the app and CLI. Logs keep their English equivalents
/// (<see cref="BleHidPeripheral.SelectedHostDisplay"/>, <see cref="BleHidPeripheral.DescribeLockAlert"/>)
/// so bug reports stay readable to maintainers.
/// </summary>
public static class DisplayText
{
    /// <summary>Where input is going, in the user's language. Host names are shown as reported.</summary>
    public static string Target(BleHidPeripheral peripheral)
    {
        if (peripheral.IsLocalTarget) return Loc.T("target.local");
        if (peripheral.SelectedHostId is not { } id) return Loc.T("target.allHosts");
        return peripheral.Hosts().FirstOrDefault(host =>
                   string.Equals(host.DeviceId, id, StringComparison.OrdinalIgnoreCase))?.Display
               ?? Loc.T("target.disconnected");
    }

    /// <summary>Null when nothing about the lock keys is worth saying.</summary>
    public static string? LockAlert(LockAlert alert) => alert switch
    {
        Core.LockAlert.CapsLockOn | Core.LockAlert.NumLockOff => Loc.T("lock.capsOnNumOff"),
        Core.LockAlert.CapsLockOn => Loc.T("lock.capsOn"),
        Core.LockAlert.NumLockOff => Loc.T("lock.numOff"),
        _ => null
    };

    public static string Advertisement(GattServiceProviderAdvertisementStatus status) => status switch
    {
        GattServiceProviderAdvertisementStatus.Created => Loc.T("advertisement.created"),
        GattServiceProviderAdvertisementStatus.Stopped => Loc.T("advertisement.stopped"),
        GattServiceProviderAdvertisementStatus.Started => Loc.T("advertisement.started"),
        GattServiceProviderAdvertisementStatus.Aborted => Loc.T("advertisement.aborted"),
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData => Loc.T("advertisement.partial"),
        _ => status.ToString()
    };
}

/// <summary>
/// Capture shortcuts, built from translated key names so every string shows the same keys and
/// translators cannot change which keys they are. Only the names that differ between keyboard
/// languages are translatable; Alt and the letter keys read the same on Windows keyboards.
/// </summary>
public static class Shortcuts
{
    private static string Ctrl => Loc.T("key.ctrl");
    private static string CapsLock => Loc.T("key.capsLock");

    public static string Stop => Compact(Ctrl, "Alt", "Q");
    public static string SwitchTarget => Compact(Ctrl, "Alt", CapsLock);
    public static string SwitchTargetAlternative => Compact(Ctrl, "D", "C");

    /// <summary>Spaced forms for the Capture page, where each shortcut stands on its own.</summary>
    public static string StopSpaced => Spaced(Ctrl, "Alt", "Q");
    public static string SwitchTargetSpaced => Spaced(Ctrl, "Alt", CapsLock);
    public static string SwitchTargetAlternativeSpaced => Spaced(Ctrl, "D", "C");

    /// <summary>Lets any string use {stopShortcut}, {switchShortcut} and {alternativeShortcut}.</summary>
    public static void ShareWithTranslations()
    {
        Loc.SetSharedValue("stopShortcut", () => Stop);
        Loc.SetSharedValue("switchShortcut", () => SwitchTarget);
        Loc.SetSharedValue("alternativeShortcut", () => SwitchTargetAlternative);
    }

    private static string Compact(params string[] keys) => string.Join('+', keys);

    private static string Spaced(params string[] keys) => string.Join(" + ", keys);
}
