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
    public void OtherDevice_StillLimited()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 1);
        var user = Guid.NewGuid();
        slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out _, Origin)!.Dispose(); // device-a has nothing flowing

        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var otherDevice, new SlotOrigin(Series, "device-b")));
        Assert.Equal(SlotDenial.User, otherDevice);
        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 2, out var noDevice, new SlotOrigin(Series, string.Empty)));
        Assert.Equal(SlotDenial.User, noDevice);
        Assert.False(slots.CanStart(user, Guid.NewGuid(), Peer, 2, new SlotOrigin(string.Empty, "device-b")));

        // A quiet HLS stream of device-a is not device-b's to replace either, for the stream limits or for the conversions.
        var hlsSlots = new StreamSlots(time, () => 1);
        var item = Guid.NewGuid();
        Assert.True(hlsSlots.TryTakeHls(HlsGrant("a", user, item, "device-a"), 2, 1, out _));
        time.Advance(StreamSlots.HlsSupersedeAfter + TimeSpan.FromSeconds(1));
        Assert.True(hlsSlots.ConversionsFull(Peer, 1, user, item, new SlotOrigin(string.Empty, "device-b")));
        Assert.False(hlsSlots.TryTakeHls(HlsGrant("b", user, item, "device-b"), 2, 1, out var hlsDenial));
        Assert.Equal(SlotDenial.User, hlsDenial);
    }

    [Fact]
    public void FailedPlayThenOtherTitle_SameDevice_Allowed()
    {
        // A user report: two failed plays of different movies held 2 of the 2 places a user gets on a peer of 4, so a third title was refused for 2 minutes.
        var slots = new StreamSlots(new ManualTimeProvider(), () => 4);
        var user = Guid.NewGuid();
        var movie = new SlotOrigin(string.Empty, Device);
        slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 4, out _, movie)!.Dispose();
        slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 4, out _, movie)!.Dispose();

        var third = Guid.NewGuid();
        Assert.True(slots.CanStart(user, third, Peer, 4, movie));
        using var lease = slots.TryOpenStatic(user, third, Peer, 4, out var denial, movie);
        Assert.NotNull(lease);
        Assert.Equal(SlotDenial.None, denial);
        Assert.Equal(1, slots.ActiveSlots(user)); // the two failed plays are gone

        // An episode after a movie on the same device moves on as well; the binge +1 of the half rule stays for episodes of one series only.
        lease.Dispose();
        using var episode = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 4, out _, Origin);
        Assert.NotNull(episode);
        Assert.Equal(1, slots.ActiveSlots(user));
    }

    [Fact]
    public void RetrySameItem_StaleHls_Superseded()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 1);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var killed = new List<string>();
        slots.HlsSuperseded += ids => killed.AddRange(ids);
        var failed = HlsGrant("failed", user, item, Device);
        var retry = HlsGrant("retry", user, item, Device);
        var movie = new SlotOrigin(string.Empty, Device);

        Assert.True(slots.TryTakeHls(failed, 2, 1, out _));
        time.Advance(StreamSlots.HlsSupersedeAfter + TimeSpan.FromSeconds(1)); // the player quit: no segment request since

        Assert.True(slots.ConversionsFull(Peer, 1)); // the old pre-check refused the retry
        Assert.False(slots.ConversionsFull(Peer, 1, user, item, movie));
        Assert.True(slots.CanStart(user, item, Peer, 2, movie));
        Assert.True(slots.TryTakeHls(retry, 2, 1, out var denial), "refused: " + denial);
        Assert.Equal(["grant-failed"], killed); // the peer's job of the failed play gets the scoped kill
        Assert.True(slots.HoldsOwnSlot(retry));
        Assert.False(slots.HoldsOwnSlot(failed));
        Assert.Equal(1, slots.ActiveSlots(user));
    }

    [Fact]
    public void StreamingSlot_NeverSuperseded()
    {
        // Warden 16 M1: one device id cannot run more streams than the limits by starting new ones while the old ones still send.
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 1);
        var user = Guid.NewGuid();
        var movie = new SlotOrigin(string.Empty, Device);
        using var sending = slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 4, out _, movie); // an open connection: bytes flow
        Assert.NotNull(sending);
        time.Advance(StreamSlots.Grace - TimeSpan.FromSeconds(1));

        Assert.Null(slots.TryOpenStatic(user, Guid.NewGuid(), Peer, 4, out var denial, movie));
        Assert.Equal(SlotDenial.User, denial);
        Assert.False(slots.CanStart(user, Guid.NewGuid(), Peer, 4, Origin));

        // An HLS stream that still requests segments is not replaced, the same title under another grant included.
        var hls = new StreamSlots(time, () => 1);
        var item = Guid.NewGuid();
        var killed = new List<string>();
        hls.HlsSuperseded += ids => killed.AddRange(ids);
        var playing = HlsGrant("playing", user, item, Device);
        Assert.True(hls.TryTakeHls(playing, 2, 1, out _));
        time.Advance(StreamSlots.HlsSupersedeAfter - TimeSpan.FromSeconds(1));
        Assert.True(hls.ConversionsFull(Peer, 1, user, item, movie));
        Assert.False(hls.TryTakeHls(HlsGrant("second", user, item, Device), 2, 1, out var hlsDenial));
        Assert.Equal(SlotDenial.User, hlsDenial);
        Assert.False(hls.TryTakeHls(HlsGrant("third", user, Guid.NewGuid(), Device), 2, 1, out _));
        Assert.Empty(killed);
        Assert.True(hls.HoldsOwnSlot(playing));
    }

    private static Grant HlsGrant(string id, Guid user, Guid item, string device) => new()
    {
        GrantId = "grant-" + id,
        LocalUserId = user,
        LocalItemId = item,
        PeerKey = Peer,
        CallerDeviceId = device,
        Method = PlayMethod.Transcode,
        LocalPlaySessionId = "play-" + id,
        PeerItemId = Guid.NewGuid(),
        PeerMediaSourceId = Guid.NewGuid(),
    };

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
