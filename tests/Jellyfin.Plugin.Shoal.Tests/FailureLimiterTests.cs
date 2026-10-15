using System;
using System.Net;
using Jellyfin.Plugin.Shoal.Sharing;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Warden 08 H1: failure counting is constant work with bounded memory.</summary>
public class FailureLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void H1_AFloodOfDistinctCallers_KeepsMemoryBounded_AndStillCounts()
    {
        var limiter = new FailureLimiter(10, 100);
        for (var i = 0; i < 50_000; i++)
        {
            limiter.Record("198.51." + (i / 256) + "." + (i % 256), T0);
        }

        Assert.True(limiter.TrackedKeys <= 1025);
        Assert.Equal(50_000, limiter.LastHour(T0));
        Assert.True(limiter.Throttled("203.0.113.1", T0), "the global bucket is spent");
    }

    [Fact]
    public void H1_PerCaller_ThrottleRollsEveryMinute_AndTheHourWindowDropsOldMinutes()
    {
        var limiter = new FailureLimiter(10, 100);
        for (var i = 0; i < 10; i++)
        {
            limiter.Record("203.0.113.9", T0);
        }

        Assert.True(limiter.Throttled("203.0.113.9", T0));
        Assert.False(limiter.Throttled("203.0.113.10", T0));
        Assert.False(limiter.Throttled("203.0.113.9", T0.AddMinutes(1)));
        Assert.Equal(10, limiter.LastHour(T0.AddMinutes(59)));
        Assert.Equal(0, limiter.LastHour(T0.AddMinutes(61)));
    }

    [Fact]
    public void H1_AddressKey_GroupsAnIpv6Network_AndMappedIpv4()
    {
        Assert.Equal(AddressKey.Of(IPAddress.Parse("2001:db8:1:2::1")), AddressKey.Of(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(AddressKey.Of(IPAddress.Parse("2001:db8:1:2::1")), AddressKey.Of(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("192.0.2.5", AddressKey.Of(IPAddress.Parse("::ffff:192.0.2.5")));
        Assert.Equal("unknown", AddressKey.Of("not an address"));
    }
}
