using System;
using System.Linq;
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
