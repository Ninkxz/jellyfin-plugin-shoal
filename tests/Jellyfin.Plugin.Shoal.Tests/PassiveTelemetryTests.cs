using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U9 (DESIGN 7.1 "Passive", 7.3): one playback sample per play, with the conversions Home held on the peer at the time.</summary>
public sealed class PassiveTelemetryTests : IDisposable
{
    private const string Peer = "4b0c1d2e3f405162738495a6b7c8d9e0";

    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly StreamSlots _slots;
    private readonly SpeedHistoryStore _history;
    private readonly PassiveTelemetry _telemetry;

    public PassiveTelemetryTests()
    {
        _slots = new StreamSlots(_time, () => 6);
        _history = new SpeedHistoryStore(new ShoalPaths(_dir.Path), NullLogger.Instance);
        _telemetry = new PassiveTelemetry(_history, _slots, _time);
    }

    public void Dispose() => _dir.Dispose();

    private static Grant NewGrant(string id) => new()
    {
        GrantId = id.PadRight(22, 'A'),
        LocalUserId = Guid.NewGuid(),
        LocalItemId = Guid.NewGuid(),
        CallerDeviceId = "d",
        LocalPlaySessionId = Guid.NewGuid().ToString("N"),
        PeerKey = Peer,
        PeerItemId = Guid.NewGuid(),
        PeerMediaSourceId = Guid.NewGuid()
    };

    private void Segment(Grant grant, int index, double ttfbMs = 2000, long bytes = 6_000_000, double copySeconds = 1)
    {
        _ = _telemetry.Begin(grant, 6, index);
        _telemetry.Record(grant, 6, index, ttfbMs, bytes, TimeSpan.FromSeconds(copySeconds));
        _time.Advance(TimeSpan.FromSeconds(5));
    }

    private LinkBudgetLimits Limits(double testMbps)
    {
        _history.Append(Peer, new SpeedSample { T = _time.GetUtcNow(), Kind = SpeedKinds.Light, Mbps = testMbps });
        return new LinkBudgetLimits(new FixedLimits(), _history, new OnSettings(), _time, _telemetry);
    }

    private sealed class FixedLimits : ISystemLimits
    {
        public BitrateLimits Current() => new(0, null);
    }

    private sealed class OnSettings : ISettingsAccessor
    {
        public Jellyfin.Plugin.Shoal.Configuration.PluginConfiguration Current => new() { AutoFitQualityToLink = true };

        public void Save(Jellyfin.Plugin.Shoal.Configuration.PluginConfiguration configuration)
        {
        }
    }

    [Fact]
    public void ALinkThatDropsDuringPlayback_ShrinksTheBudgetAtOnce_WithoutANewTest_AndTheEvidenceExpires()
    {
        var limits = Limits(12); // the last test said 12 Mbps: a budget of 8
        var play = NewGrant("drop");
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);

        // The link fell to about 3 Mbps while a 6.1 Mbps stream plays: a 6 s segment is 4.6 MB and takes 12 s to copy.
        for (var i = 0; i < 3; i++)
        {
            _ = _telemetry.Begin(play, 6, i);
            _telemetry.Record(play, 6, i, 1000, 4_575_000, TimeSpan.FromSeconds(12.2));
            _time.Advance(TimeSpan.FromSeconds(6));
        }

        var live = _telemetry.SlowLinkMbps(Peer);
        Assert.InRange(live!.Value, 2.9, 3.1);
        Assert.InRange(limits.ForPeer(Peer).LinkBudget!.Value, 1_900_000, 2_100_000); // 3 / 1.5, not the 8 of the old test
        Assert.True(_telemetry.TakeRemeasure(Peer)); // and a new test is wanted once nothing streams
        Assert.False(_telemetry.TakeRemeasure(Peer));

        _time.Advance(PassiveTelemetry.EvidenceWindow + TimeSpan.FromSeconds(1)); // the evidence is 30 seconds old
        Assert.Null(_telemetry.SlowLinkMbps(Peer));
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);
    }

    private void SlowSegments(Grant grant, int count = 3, double copySeconds = 12.2)
    {
        for (var i = 0; i < count; i++)
        {
            _ = _telemetry.Begin(grant, 6, i);
            _telemetry.Record(grant, 6, i, 1000, 4_575_000, TimeSpan.FromSeconds(copySeconds));
            _time.Advance(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void OneSlowPlayOfFour_CannotLowerTheBudget_TwoCan()
    {
        var limits = Limits(12);
        var plays = Enumerable.Range(0, 4).Select(i => NewGrant("four" + i)).ToList();
        foreach (var play in plays)
        {
            Assert.True(_slots.TryTakeHls(play, 6, 6, out _)); // four streams run through the peer
        }

        SlowSegments(plays[0]);
        Assert.Null(_telemetry.SlowLinkMbps(Peer)); // one of four
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);

        SlowSegments(plays[1]);
        Assert.NotNull(_telemetry.SlowLinkMbps(Peer)); // two of four see the same link
        Assert.True(limits.ForPeer(Peer).LinkBudget < 8_000_000);
    }

    [Fact]
    public void LiveEvidenceAlone_NeverSetsACapBelowTheLowestRateWorthAConversion()
    {
        var limits = Limits(12);
        SlowSegments(NewGrant("crawl"), copySeconds: 40); // 4.6 MB in 40 s: about 0.9 Mbps

        Assert.InRange(_telemetry.SlowLinkMbps(Peer)!.Value, 0.8, 1.0);
        Assert.Equal(1_500_000, limits.ForPeer(Peer).LinkBudget); // advice, floored at 1.5 Mbps; a new test settles the rest
    }

    [Fact]
    public void AHealthyLink_OrASingleSlowSegment_ChangesNothing()
    {
        var limits = Limits(12);
        var play = NewGrant("fine");

        // Segments that copy in a fraction of their playing time: the link has room.
        for (var i = 0; i < 4; i++)
        {
            _ = _telemetry.Begin(play, 6, i);
            _telemetry.Record(play, 6, i, 1000, 4_575_000, TimeSpan.FromSeconds(2));
            _time.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.Null(_telemetry.SlowLinkMbps(Peer));
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);

        _time.Advance(PassiveTelemetry.EvidenceWindow + TimeSpan.FromSeconds(1));
        var other = NewGrant("one");
        _ = _telemetry.Begin(other, 6, 0);
        _telemetry.Record(other, 6, 0, 1000, 4_575_000, TimeSpan.FromSeconds(20)); // one slow segment is not a trend
        Assert.Null(_telemetry.SlowLinkMbps(Peer)); // nor does it say anything alone
        Assert.Equal(8_000_000, limits.ForPeer(Peer).LinkBudget);
    }

    [Fact]
    public void WithNoTestYet_TheEvidenceAloneSetsTheBudget_AndAutoFitOffIgnoresIt()
    {
        var play = NewGrant("fresh");
        for (var i = 0; i < 3; i++)
        {
            _ = _telemetry.Begin(play, 6, i);
            _telemetry.Record(play, 6, i, 1000, 4_575_000, TimeSpan.FromSeconds(12.2));
            _time.Advance(TimeSpan.FromSeconds(6));
        }

        var on = new LinkBudgetLimits(new FixedLimits(), _history, new OnSettings(), _time, _telemetry);

        Assert.InRange(on.ForPeer(Peer).LinkBudget!.Value, 1_900_000, 2_100_000);
    }

    [Fact]
    public void APlay_BecomesOnePlaybackSample_AfterTwoQuietMinutes_WithTheRatesAndTheConversionsAtTheTime()
    {
        var play = NewGrant("play-one");
        var other = NewGrant("play-two");
        Assert.True(_slots.TryTakeHls(play, 4, 4, out _));
        Assert.True(_slots.TryTakeHls(other, 4, 4, out _));

        for (var i = 0; i < 8; i++)
        {
            Segment(play, i, ttfbMs: i < 3 ? 9000 : 2000); // the start of the Peer's job is slow; the rest is steady
        }

        Assert.Equal(0, _telemetry.FlushIdle()); // still playing
        _time.Advance(PassiveTelemetry.Quiet);
        Assert.Equal(1, _telemetry.FlushIdle());
        Assert.Equal(0, _telemetry.FlushIdle()); // saved once

        var sample = Assert.Single(_history.Read(Peer));
        Assert.Equal(SpeedKinds.Playback, sample.Kind);
        Assert.Equal(2, sample.ConversionsActive); // two conversions from Home were running on the peer
        Assert.Equal(2000, sample.SegTtfbMs); // segments 3 and later only
        Assert.Equal(3.0, sample.TranscodeSpeed); // 6 s of video per 2 s to the first byte
        Assert.Equal(48.0, sample.Mbps); // 6 MB in 1 s
        Assert.Equal(8.0, sample.StreamMbps); // 6 MB per 6 s of video
        Assert.Equal(0, sample.Stalls);
        Assert.Null(sample.Error);
    }

    [Fact]
    public void AGapOverOneAndAHalfSegmentLengths_BetweenRequests_IsAStall()
    {
        var play = NewGrant("stall");
        for (var i = 0; i < 3; i++)
        {
            Segment(play, i);
        }

        _time.Advance(TimeSpan.FromSeconds(20)); // the player ran dry
        Segment(play, 3);
        _time.Advance(PassiveTelemetry.Quiet);
        _telemetry.FlushIdle();

        Assert.Equal(1, _history.Read(Peer).Single().Stalls);
    }

    [Fact]
    public void ATooShortPlay_IsNotWorthASample()
    {
        var play = NewGrant("short");
        Segment(play, 0);
        Segment(play, 1);
        _time.Advance(PassiveTelemetry.Quiet);

        Assert.Equal(0, _telemetry.FlushIdle());
        Assert.Empty(_history.Read(Peer));
    }

    [Fact]
    public void ASlowConversion_ShowsAsTheHint_AndNamesTheSettingWhenTwoRan()
    {
        var play = NewGrant("slow");
        var other = NewGrant("slow-two");
        Assert.True(_slots.TryTakeHls(play, 4, 4, out _));
        Assert.True(_slots.TryTakeHls(other, 4, 4, out _));
        for (var i = 0; i < 6; i++)
        {
            Segment(play, i, ttfbMs: 5500); // 6 s of video took 5.5 s to start
        }

        _time.Advance(PassiveTelemetry.Quiet);
        _telemetry.FlushIdle();
        var peer = new Jellyfin.Plugin.Shoal.State.PeerState { PeerKey = Peer, DisplayName = "Fern" };

        var analysis = LinkAnalyzer.Analyze(peer, _history.Read(Peer), new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<SpeedSample>>());

        var hint = Assert.Single(analysis.Hints);
        Assert.Equal("peerTranscodeSlowShared", hint.Kind);
        Assert.Equal(2, hint.N);
    }
}
