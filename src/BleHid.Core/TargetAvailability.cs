namespace BleHid.Core;

internal static class TargetAvailability
{
    public static bool IsUnavailable(bool localOnly, string? selectedHostId, IEnumerable<string> hostIds)
    {
        if (localOnly) return false;

        return selectedHostId is null
            ? !hostIds.Any()
            : !hostIds.Contains(selectedHostId, StringComparer.OrdinalIgnoreCase);
    }
}
