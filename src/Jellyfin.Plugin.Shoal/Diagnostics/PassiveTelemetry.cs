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

    /// <summary>A segment was read ahead (no client waited for it): only what it says about the link is kept.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="grantId">The play the segment belongs to.</param>
    /// <param name="segmentSeconds">The length of the segment.</param>
    /// <param name="bytes">The bytes copied.</param>
    /// <param name="copy">How long the reads from the peer took (the client is not in it).</param>
    void ObserveCopy(string peerKey, string grantId, double segmentSeconds, long bytes, TimeSpan copy);
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
    /// <param name="upstream">How long the reads from the peer took, the waits on the client left out (the speed of the client says nothing about the link); null for the time from the headers to the end.</param>
    public void Done(long bytes, TimeSpan? upstream = null)
    {
        if (_headers == 0)
        {
            return;
        }

        var end = Stopwatch.GetTimestamp();
        _owner.Record(_grant, _segmentSeconds, _index, Stopwatch.GetElapsedTime(_started, _headers).TotalMilliseconds, bytes, upstream ?? Stopwatch.GetElapsedTime(_headers, end));
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

    /// <summary>How far back the live evidence of a slow link reaches.</summary>
    public static readonly TimeSpan EvidenceWindow = TimeSpan.FromSeconds(30);

    /// <summary>How long a lowered limit that live evidence set stays at most when no fresh test ever shows the link recovered.</summary>
    public static readonly TimeSpan HoldMax = TimeSpan.FromHours(6);

    private const int MinSegments = 3;
    private const int FirstMeasuredIndex = 3;

    private readonly SpeedHistoryStore _history;
    private readonly StreamSlots _slots;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Play> _plays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Observation>> _recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _remeasure = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (double Mbps, DateTimeOffset Since)> _held = new(StringComparer.Ordinal);

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

    /// <summary>
    /// What the segments of the last 30 seconds say about the link, when they say it is too slow for the stream: the reads from the peer took about as long as the
    /// segments take to play (a segment is made before it is served, so the reads are the link; the client is not timed). Advice only, and careful with who gives it:
    /// one play counts for at most 30 seconds of playing time; with several streams through the peer at least two plays (half of them, rounded up) must show it, so one
    /// slow play of four changes nothing; one segment, or no recent segment, says nothing.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The measured rate of the reads in Mbps, or null.</returns>
    public double? SlowLinkMbps(string peerKey)
    {
        if (!_recent.TryGetValue(peerKey, out var list))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var since = now - EvidenceWindow;
        var perGrant = new Dictionary<string, (double Bytes, double Copy, double Played, int Count)>(StringComparer.Ordinal);
        lock (list)
        {
            list.RemoveAll(o => o.T < since);
            foreach (var o in list.OrderByDescending(static o => o.T))
            {
                var have = perGrant.GetValueOrDefault(o.GrantId);
                if (have.Played >= EvidenceWindow.TotalSeconds)
                {
                    continue; // this play already gave 30 seconds of playing time: a burst of read-ahead does not drown the others
                }

                perGrant[o.GrantId] = (have.Bytes + o.Bytes, have.Copy + o.CopySeconds, have.Played + o.SegmentSeconds, have.Count + 1);
            }
        }

        var slow = perGrant.Values.Where(static g => g.Count >= 2 && g.Copy > 0 && g.Played > 0 && g.Copy >= 0.9 * g.Played).ToList();
        var active = _slots.ActiveSlotsOnPeer(peerKey);
        var needed = active <= 1 ? 1 : Math.Max(2, (active + 1) / 2);
        if (slow.Count < needed)
        {
            return null;
        }

        var bytes = slow.Sum(static g => g.Bytes);
        var copy = slow.Sum(static g => g.Copy);
        return copy > 0 ? Math.Round(bytes * 8 / 1e6 / copy, 2) : null;
    }

    /// <summary>
    /// The limit live evidence puts on a link, and keeps: once the segments showed the link too slow, the limit stays after the evidence window has passed (a player that
    /// stopped, a pause) until two fresh speed tests in a row, taken after that evidence, both show the link at least 1.5 times as fast as the evidence did. One good test
    /// is not enough, and old tests do not count. At most <see cref="HoldMax"/>.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The rate in Mbps the link is held to, or null.</returns>
    public double? LinkLimitMbps(string peerKey)
    {
        var now = _time.GetUtcNow();
        if (SlowLinkMbps(peerKey) is { } live)
        {
            _held[peerKey] = (live, now);
            return live;
        }

        if (!_held.TryGetValue(peerKey, out var held))
        {
            return null;
        }

        var fresh = _history.Read(peerKey).Where(s => s.HasSpeed && s.Kind != SpeedKinds.Playback && s.SharedWithStreams != true && s.T > held.Since).OrderBy(static s => s.T).TakeLast(2).ToList();
        var recovered = fresh.Count == 2 && fresh.All(s => s.Mbps!.Value >= held.Mbps * 1.5);
        if (recovered || now - held.Since > HoldMax)
        {
            _held.TryRemove(peerKey, out _);
            return null;
        }

        return held.Mbps;
    }

    /// <summary>Says, once, that a peer link was seen too slow while it streamed, so a light test runs when the stream is over.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>True the first time after the evidence.</returns>
    public bool TakeRemeasure(string peerKey) => _remeasure.TryRemove(peerKey, out _);

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

    /// <inheritdoc />
    public void ObserveCopy(string peerKey, string grantId, double segmentSeconds, long bytes, TimeSpan copy)
    {
        var list = _recent.GetOrAdd(peerKey, static _ => []);
        lock (list)
        {
            list.Add(new Observation(_time.GetUtcNow(), grantId, bytes, copy.TotalSeconds, segmentSeconds));
            if (list.Count > 40)
            {
                list.RemoveAt(0);
            }
        }

        if (SlowLinkMbps(peerKey) is not null)
        {
            _remeasure[peerKey] = true;
        }
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

        ObserveCopy(grant.PeerKey, grant.GrantId, segmentSeconds, bytes, copy);
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

    private sealed record Observation(DateTimeOffset T, string GrantId, long Bytes, double CopySeconds, double SegmentSeconds);

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
