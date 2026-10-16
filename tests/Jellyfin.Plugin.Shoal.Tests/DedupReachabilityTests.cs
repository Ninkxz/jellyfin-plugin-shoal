using System;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Warden 16 M2: a peer nobody has checked yet holds its titles, an offline one lets go only after it stayed offline, a paused one at once.</summary>
public sealed class DedupReachabilityTests
{
    private const string Peer = "ffffffffffffffffffffffffffffffff";

    [Fact]
    public void APeerNobodyHasCheckedYet_StillHoldsItsTitles()
    {
        var time = new ManualTimeProvider();
        var reach = new DedupReachability(new PeerRegistry(time), time);

        Assert.True(reach.IsUp(Peer)); // a Home that has just started has not run its first health pass
    }

    [Fact]
    public void AnOfflinePeer_LetsGoOnlyAfterTheGrace_AndOnlineAgainResetsIt()
    {
        var time = new ManualTimeProvider();
        var registry = new PeerRegistry(time);
        var reach = new DedupReachability(registry, time);
        registry.SetStatus(Peer, PeerStatus.Offline);

        Assert.True(reach.IsUp(Peer));
        time.Advance(DedupReachability.Grace - TimeSpan.FromSeconds(1));
        Assert.True(reach.IsUp(Peer));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(reach.IsUp(Peer));

        registry.SetStatus(Peer, PeerStatus.Online); // it flaps back
        Assert.True(reach.IsUp(Peer));
        registry.SetStatus(Peer, PeerStatus.Offline);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(reach.IsUp(Peer)); // the grace starts over
    }

    [Fact]
    public void APausedPeer_LetsGoAtOnce_AndAStoppedOne_AfterTheSameHoldAsAnOfflineOne()
    {
        var time = new ManualTimeProvider();
        var registry = new PeerRegistry(time);
        var reach = new DedupReachability(registry, time);

        registry.SetStatus(Peer, PeerStatus.Disabled);
        Assert.False(reach.IsUp(Peer));

        foreach (var status in new[] { PeerStatus.AuthFailed, PeerStatus.IdentityMismatch, PeerStatus.TlsKeyChanged })
        {
            registry.SetStatus(Peer, status);
            Assert.True(reach.IsUp(Peer), status + " still holds at first");
            time.Advance(DedupReachability.Grace + TimeSpan.FromSeconds(1));
            Assert.False(reach.IsUp(Peer), status + " lets go after the hold");
        }

        registry.SetStatus(Peer, PeerStatus.Degraded);
        Assert.True(reach.IsUp(Peer));
    }
}
