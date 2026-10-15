using System;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U9 (DESIGN 6.2.1, 7.2): the link budget that auto-fit adds to the bitrate limits.</summary>
public sealed class LinkBudgetTests
{
    private const string Peer = "4b0c1d2e3f405162738495a6b7c8d9e0";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static SpeedSample Test(double hoursAgo, double mbps) => new() { T = Now.AddHours(-hoursAgo), Kind = SpeedKinds.Light, Mbps = mbps };

    [Fact]
    public void AtEightMbit_TheBudgetIsAboutFiveMbps_SoARemuxIsAskedForNoMoreThan5Point3()
    {
        var budget = LinkBudgetLimits.BudgetOf([Test(3, 7.4), Test(2, 7.5), Test(1, 7.6)], autoFit: true, Now);

        Assert.InRange(budget!.Value, 4_900_000, 5_300_000); // the handler passes this as MaxStreamingBitrate (PlaybackInfoTests.U9_...)
    }

    [Fact]
    public void NoBudget_WhenAutoFitIsOff_NothingWasMeasured_OrTheLastTestIsOld()
    {
        Assert.Null(LinkBudgetLimits.BudgetOf([Test(1, 8)], autoFit: false, Now));
        Assert.Null(LinkBudgetLimits.BudgetOf([], autoFit: true, Now));
        Assert.Null(LinkBudgetLimits.BudgetOf([new SpeedSample { T = Now, Kind = SpeedKinds.Light, Error = "timeout" }], autoFit: true, Now));
        Assert.Null(LinkBudgetLimits.BudgetOf([Test(49, 8)], autoFit: true, Now));
        Assert.NotNull(LinkBudgetLimits.BudgetOf([Test(47, 8)], autoFit: true, Now));
    }

    [Fact]
    public void ATinyLink_StillGetsAFloor()
    {
        Assert.Equal(4_000_000, LinkBudgetLimits.BudgetOf([Test(1, 0.05)], autoFit: true, Now)); // never below a 720p stream
        Assert.Equal(4_000_000, LinkBudgetLimits.BudgetOf([Test(1, 5)], autoFit: true, Now)); // 3.3 Mbps of budget is raised to the floor
    }

    [Fact]
    public void ATestThatSharedTheLinkWithAStream_DoesNotSetTheBudget_OrT()
    {
        var shared = new SpeedSample { T = Now.AddHours(-1), Kind = SpeedKinds.Manual, Mbps = 3, SharedWithStreams = true };

        Assert.Null(LinkBudgetLimits.BudgetOf([shared], autoFit: true, Now));
        Assert.Equal(60, VerdictEngine.Throughput([Test(3, 60), Test(2, 60), shared]) is { } t ? Math.Round(t) : -1); // the median of the two clean ones
        Assert.Equal(30_000_000, LinkBudgetLimits.BudgetOf([Test(3, 45), shared], autoFit: true, Now));
    }

    [Fact]
    public void ForPeer_AddsTheBudgetOfThatPeerOnly_AndCurrentStaysThePlainServerLimits()
    {
        using var dir = new TempDirectory();
        var history = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        history.Append(Peer, new SpeedSample { T = DateTimeOffset.UtcNow, Kind = SpeedKinds.Manual, Mbps = 12 });
        var limits = new LinkBudgetLimits(new Fixed(), history, new Settings(true), TimeProvider.System);

        Assert.Equal(new BitrateLimits(25_000_000, null), limits.Current());
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);
        Assert.Equal(25_000_000, limits.ForPeer(Peer).ServerRemoteLimit);
        Assert.Null(limits.ForPeer("00000000000000000000000000000000").LinkBudget);
        Assert.Null(new LinkBudgetLimits(new Fixed(), history, new Settings(false), TimeProvider.System).ForPeer(Peer).LinkBudget);
    }

    private sealed class Fixed : ISystemLimits
    {
        public BitrateLimits Current() => new(25_000_000, null);
    }

    private sealed class Settings(bool autoFit) : ISettingsAccessor
    {
        public PluginConfiguration Current => new() { AutoFitQualityToLink = autoFit };

        public void Save(PluginConfiguration configuration)
        {
        }
    }
}
