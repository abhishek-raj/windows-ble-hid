using Xunit;

namespace BleHid.Core.Tests;

public sealed class TargetAvailabilityTests
{
    [Fact]
    public void Selected_host_is_unavailable_after_it_disconnects() =>
        Assert.True(TargetAvailability.IsUnavailable(false, "mac", ["phone"]));

    [Fact]
    public void Selected_host_remains_available_while_it_is_subscribed() =>
        Assert.False(TargetAvailability.IsUnavailable(false, "mac", ["MAC", "phone"]));

    [Fact]
    public void Broadcast_is_unavailable_after_the_last_host_disconnects() =>
        Assert.True(TargetAvailability.IsUnavailable(false, null, []));

    [Fact]
    public void Broadcast_remains_available_while_any_host_is_subscribed() =>
        Assert.False(TargetAvailability.IsUnavailable(false, null, ["mac"]));

    [Fact]
    public void Local_target_never_depends_on_subscribers() =>
        Assert.False(TargetAvailability.IsUnavailable(true, null, []));
}
