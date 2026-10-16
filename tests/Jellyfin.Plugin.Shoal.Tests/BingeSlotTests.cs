using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot 08, N9: playing a series from episode 1, episodes 1 and 2 played and the third was refused ("rate limits") while the first still held a slot. A stream that has
/// finished sending (no open connection; for HLS no segment request for a while) is not a stream any more: when the next episode of the same series starts on the same
/// device it takes the place. A stream that still sends is never replaced, so a device id cannot run several streams on one slot (Warden 16 M1).
/// </summary>
public sealed class BingeSlotTests
{
    private const string Peer = "peer";
    private const string Series = "55555555555555555555555555555555";
    private const string Device = "device-a";

    private static SlotOrigin Origin => new(Series, Device);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    public void FiveEpisodesInARow_EachStartedAfterTheLastOneEnded_AllPlay(int maxPerUser, int peerMax)
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => maxPerUser);
        var user = Guid.NewGuid();

        for (var episode = 1; episode <= 5; episode++)
        {
            var item = Guid.NewGuid();
            Assert.True(slots.CanStart(user, item, Peer, peerMax, Origin), "episode " + episode + " would be refused at PlaybackInfo");
            var lease = slots.TryOpenStatic(user, item, Peer, peerMax, out var denial, Origin);
            Assert.True(lease is not null, "episode " + episode + " was refused: " + denial);
            Assert.Equal(1, slots.ActiveSlots(user));
            Assert.Equal(1, slots.ActiveSlotsOnPeer(Peer));
            lease.Dispose(); // the stream ends; its stop report never arrives
        }
    }

    [Fact]
    public void TheThirdEpisode_IsNotRefused_WhileTheSecondPlaysAndTheFirstEndedWithoutAStopReport()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 2);
        var user = Guid.NewGuid();
        slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin)!.Dispose();
        using var second = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin);
        Assert.NotNull(second);

        using var third = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var denial, Origin);

        Assert.NotNull(third);
        Assert.Equal(SlotDenial.None, denial);
        Assert.Equal(2, slots.ActiveSlots(user));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    public void ConcurrentStreamsFromOneDeviceId_AreRefusedAtTheLimit_ThePlayingOnesAreNeverReplaced(int maxPerUser, int peerMax)
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => maxPerUser);
        var user = Guid.NewGuid();
        var open = new List<StaticLease>();

        for (var i = 0; i < maxPerUser; i++)
        {
            var lease = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, peerMax, out var denial, Origin);
            Assert.True(lease is not null, "stream " + i + " was refused: " + denial);
            open.Add(lease);
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, peerMax, out var denial, Origin));
            Assert.Equal(SlotDenial.User, denial);
            Assert.False(slots.CanStart(user, Guid.NewGuid(), Peer, peerMax, Origin));
        }

        Assert.Equal(maxPerUser, slots.ActiveSlots(user));
        Assert.Equal(maxPerUser, slots.OpenConnections(user));
        open.ForEach(static l => l.Dispose());
    }

    [Fact]
    public void ThePeerTotal_StaysHard_AndTheHandoverIsOnlyThePlusOneOfTheHalfRule()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 4);
        var user = Guid.NewGuid();

        using var first = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin); // half of 2 is 1
        using var second = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin); // the handover: one more than the half
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var denial, Origin)); // two of two, both sending
        Assert.Equal(SlotDenial.Peer, denial);
        Assert.Equal(2, slots.ActiveSlotsOnPeer(Peer));
    }

    [Fact]
    public void ADifferentDevice_ASeries_OrAMovie_IsNeverMovedOn_EvenWhenNothingIsSending()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 1);
        var user = Guid.NewGuid();
        slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin)!.Dispose();

        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var otherDevice, new SlotOrigin(Series, "device-b")));
        Assert.Equal(SlotDenial.User, otherDevice);
        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var otherSeries, new SlotOrigin("99999999999999999999999999999999", Device)));
        Assert.Equal(SlotDenial.User, otherSeries);
        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var movie, default));
        Assert.Equal(SlotDenial.User, movie);
    }

    [Fact]
    public void AnotherUsersStreams_AreNeverMovedOn()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 4);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        using var bobs = slots.TryOpenStatic(bob, Guid.NewGuid(), Peer, 2, out _, Origin);
        slots.TryOpenStatic(alice, Guid.NewGuid(), Peer, 2, out _, Origin)!.Dispose();

        using var next = slots.TryOpenStatic(alice, Guid.NewGuid(), Peer, 2, out _, Origin); // alice moves on: her own ended slot is replaced, bob's stays

        Assert.NotNull(next);
        Assert.Equal(2, slots.ActiveSlotsOnPeer(Peer));
        Assert.Equal(1, slots.ActiveSlots(bob));
    }

    [Fact]
    public void AnHlsStream_IsReplacedOnlyAfterItWentQuiet_AndTheTwoTranscodesCannotAlternate()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 1);
        var user = Guid.NewGuid();
        Grant Of(int episode) => new()
        {
            GrantId = "grant-" + episode,
            LocalUserId = user,
            LocalItemId = Guid.NewGuid(),
            PeerKey = Peer,
            SeriesKey = Series,
            CallerDeviceId = Device,
            Method = PlayMethod.Transcode,
            LocalPlaySessionId = "play-" + episode,
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
        };

        var one = Of(1);
        var two = Of(2);
        Assert.True(slots.TryTakeHls(one, 2, 2, out _));
        Assert.False(slots.TryTakeHls(two, 2, 2, out var busy)); // episode 1 still requests segments: it is not replaced
        Assert.Equal(SlotDenial.User, busy);

        time.Advance(StreamSlots.HlsSupersedeAfter + TimeSpan.FromSeconds(1)); // episode 1 went quiet
        Assert.True(slots.TryTakeHls(two, 2, 2, out _));
        Assert.Equal(1, slots.ActiveSlots(user));

        Assert.False(slots.TryTakeHls(one, 2, 2, out var back)); // a late segment request of episode 1 cannot push episode 2 out again
        Assert.Equal(SlotDenial.User, back);
        Assert.True(slots.HoldsOwnSlot(two));
        Assert.False(slots.HoldsOwnSlot(one));
    }
}
