using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.Shoal.Playback;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>Watches the HLS segments Home fetches for a play (DESIGN 7.1 "Passive").</summary>
public interface ISegmentTelemetry
{
    /// <summary>A segment is about to be requested from the peer.</summary>
    /// <param name="grant">The play.</param>
    /// <param name="segmentSeconds">The length of the segment.</param>
    /// <param name="segmentIndex">The segment's number in the playlist.</param>
    /// <returns>The measurement to complete.</returns>
    SegmentMeasure Begin(Grant grant, double segmentSeconds, int segmentIndex);
}

/// <summary>One segment fetch: the time to the first byte and the copy.</summary>
public sealed class SegmentMeasure
{
    private readonly PassiveTelemetry _owner;
    private readonly Grant _grant;
    private readonly double _segmentSeconds;
    private readonly int _index;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _headers;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentMeasure"/> class.
    /// </summary>
    /// <param name="owner">The collector.</param>
    /// <param name="grant">The play.</param>
    /// <param name="segmentSeconds">The segment length.</param>
    /// <param name="index">The segment number.</param>
    internal SegmentMeasure(PassiveTelemetry owner, Grant grant, double segmentSeconds, int index)
    {
        _owner = owner;
        _grant = grant;
        _segmentSeconds = segmentSeconds;
        _index = index;
    }

    /// <summary>The peer's headers arrived.</summary>
    public void GotHeaders() => _headers = Stopwatch.GetTimestamp();

    /// <summary>The whole segment was copied.</summary>
    /// <param name="bytes">The bytes of the segment.</param>
    public void Done(long bytes)
    {
        if (_headers == 0)
        {
            return;
        }

        var end = Stopwatch.GetTimestamp();
        _owner.Record(_grant, _segmentSeconds, _index, Stopwatch.GetElapsedTime(_started, _headers).TotalMilliseconds, bytes, Stopwatch.GetElapsedTime(_headers, end));
    }
}

/// <summary>
/// Collects what the segments of each play showed (time to first byte, rate, gaps between requests, how many conversions Home held on the peer)
/// and turns it into one <c>playback</c> sample when the play has been quiet for two minutes (the stream slot's own grace, DESIGN 6.9). A play
/// of fewer than three segments is not worth a sample.
/// </summary>
public sealed class PassiveTelemetry : ISegmentTelemetry
{
    /// <summary>How long a play may be quiet before it counts as ended.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    private const int MinSegments = 3;
    private const int FirstMeasuredIndex = 3;

    private readonly SpeedHistoryStore _history;
    private readonly StreamSlots _slots;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Play> _plays = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PassiveTelemetry"/> class.
    /// </summary>
    /// <param name="history">The speed history.</param>
    /// <param name="slots">The stream slots (the conversions Home holds on a peer).</param>
    /// <param name="time">The clock.</param>
    public PassiveTelemetry(SpeedHistoryStore history, StreamSlots slots, TimeProvider time)
    {
        _history = history;
        _slots = slots;
        _time = time;
    }

    /// <inheritdoc />
    public SegmentMeasure Begin(Grant grant, double segmentSeconds, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var play = _plays.GetOrAdd(grant.GrantId, _ => new Play(grant.PeerKey));
        lock (play)
        {
            var now = _time.GetUtcNow();
            if (play.LastRequest is { } last && play.LastSegmentSeconds > 0 && (now - last).TotalSeconds > 1.5 * play.LastSegmentSeconds)
            {
                play.Stalls++;
            }

            play.LastRequest = now;
            play.LastActivity = now;
            play.LastSegmentSeconds = segmentSeconds;
            play.MaxConversions = Math.Max(play.MaxConversions, _slots.ConversionsOnPeer(grant.PeerKey));
        }

        return new SegmentMeasure(this, grant, segmentSeconds, segmentIndex);
    }

    /// <summary>Turns every play that has been quiet for two minutes into a sample.</summary>
    /// <returns>The number of samples saved.</returns>
    public int FlushIdle()
    {
        var now = _time.GetUtcNow();
        var saved = 0;
        foreach (var (grantId, play) in _plays.ToList())
        {
            SpeedSample? sample = null;
            lock (play)
            {
                if (now - play.LastActivity < Quiet)
                {
                    continue;
                }

                _plays.TryRemove(grantId, out _);
                sample = SampleOf(play);
            }

            if (sample is not null)
            {
                _history.Append(play.PeerKey, sample);
                saved++;
            }
        }

        return saved;
    }

    /// <summary>Records one finished segment.</summary>
    /// <param name="grant">The play.</param>
    /// <param name="segmentSeconds">The segment's length.</param>
    /// <param name="index">The segment's number.</param>
    /// <param name="ttfbMs">Milliseconds to the first byte.</param>
    /// <param name="bytes">The bytes copied.</param>
    /// <param name="copy">How long the copy took.</param>
    internal void Record(Grant grant, double segmentSeconds, int index, double ttfbMs, long bytes, TimeSpan copy)
    {
        if (!_plays.TryGetValue(grant.GrantId, out var play))
        {
            return;
        }

        lock (play)
        {
            play.LastActivity = _time.GetUtcNow();
            play.Segments++;
            play.Bytes += bytes;
            play.CopySeconds += copy.TotalSeconds;
            play.SegmentSeconds += segmentSeconds;
            if (index >= FirstMeasuredIndex)
            {
                // The first segments carry the start of the Peer's job; the steady state is what tells a slow transcode.
                play.Measured++;
                play.TtfbMs += ttfbMs;
                play.MeasuredSegmentSeconds += segmentSeconds;
            }
        }
    }

    private SpeedSample? SampleOf(Play play)
    {
        if (play.Segments < MinSegments || play.CopySeconds <= 0 || play.SegmentSeconds <= 0)
        {
            return null;
        }

        var latest = _history.Read(play.PeerKey).Where(static s => s.Kind != SpeedKinds.Playback).OrderBy(static s => s.T).LastOrDefault();
        double? ttfb = play.Measured > 0 ? Math.Round(play.TtfbMs / play.Measured, 1) : null;
        double? speed = ttfb is > 0 ? Math.Round(play.MeasuredSegmentSeconds / play.Measured / (ttfb.Value / 1000), 2) : null;
        return new SpeedSample
        {
            T = play.LastActivity,
            Kind = SpeedKinds.Playback,
            Mbps = Math.Round(play.Bytes * 8 / 1e6 / play.CopySeconds, 1),
            StreamMbps = Math.Round(play.Bytes * 8 / 1e6 / play.SegmentSeconds, 1),
            SegTtfbMs = ttfb,
            TranscodeSpeed = speed,
            Stalls = play.Stalls,
            ConversionsActive = play.MaxConversions,
            Path = latest?.Path ?? "unknown"
        };
    }

    private sealed class Play
    {
        public Play(string peerKey) => PeerKey = peerKey;

        public string PeerKey { get; }

        public DateTimeOffset? LastRequest { get; set; }

        public DateTimeOffset LastActivity { get; set; }

        public double LastSegmentSeconds { get; set; }

        public int Segments { get; set; }

        public int Measured { get; set; }

        public long Bytes { get; set; }

        public double CopySeconds { get; set; }

        public double SegmentSeconds { get; set; }

        public double MeasuredSegmentSeconds { get; set; }

        public double TtfbMs { get; set; }

        public int Stalls { get; set; }

        public int MaxConversions { get; set; }
    }
}
