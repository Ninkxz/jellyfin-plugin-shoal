using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>One hint about a link (DESIGN 7.3), shown under the speed chart.</summary>
/// <param name="Kind">relayed, maybeRelayed, peerTranscodeSlow, peerTranscodeSlowShared, homeDownload, peerUpload, perConnection or proxyCpu.</param>
/// <param name="Severity">warn or bad.</param>
/// <param name="N">For peerTranscodeSlowShared: how many conversions from this server were running.</param>
public sealed record LinkHint(string Kind, string Severity, int? N = null);

/// <summary>What the analysis found for one peer.</summary>
/// <param name="Route">The route word: direct, derp, maybe or unknown.</param>
/// <param name="DerpRegion">The DERP region, when the Tailscale LocalAPI said.</param>
/// <param name="Hints">Every hint that applies.</param>
/// <param name="Bottleneck">The first match (relay, peerTranscode, homeDownload, peerUpload, perConnection, proxyCpu) or none.</param>
public sealed record LinkAnalysis(string Route, string? DerpRegion, IReadOnlyList<LinkHint> Hints, string Bottleneck);

/// <summary>
/// Reads a peer's speed history (and the other peers') and says where the limit probably is (DESIGN 7.3). Pure. Nothing here is stated as
/// fact unless the Tailscale LocalAPI said it: the relay heuristic only ever says "possibly".
/// </summary>
public static class LinkAnalyzer
{
    /// <summary>A link is slow when its throughput is under this share of its best.</summary>
    public const double SlowShare = 0.5;

    /// <summary>The conversions from Home at which the slow-conversion hint names the setting.</summary>
    public const int SharedConversions = 2;

    /// <summary>
    /// The relay heuristic for a Tailnet peer (DESIGN 7.3 item 2): the round trip is over twice the best ever seen and more than 20 ms above it
    /// while the throughput is under half the best, or the round trip fell by more than 40% within one ping burst.
    /// </summary>
    /// <param name="before">The samples taken before this one.</param>
    /// <param name="now">The sample being judged (its round trip and throughput).</param>
    /// <returns>True when the link is possibly relayed.</returns>
    public static bool PossiblyRelayed(IEnumerable<SpeedSample> before, SpeedSample now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(now);
        if (now.RttDropPct > 40)
        {
            return true;
        }

        var past = before.Where(static s => s.Error is null && s.Kind != SpeedKinds.Playback).ToList();
        var bestRtt = past.Where(static s => s.RttMedianMs is not null).Select(static s => s.RttMedianMs!.Value).DefaultIfEmpty(double.NaN).Min();
        var bestMbps = past.Where(static s => s.Mbps is not null).Select(static s => s.Mbps!.Value).DefaultIfEmpty(double.NaN).Max();
        return now.RttMedianMs is { } rtt && now.Mbps is { } mbps
            && !double.IsNaN(bestRtt) && !double.IsNaN(bestMbps)
            && rtt > 2 * bestRtt && rtt - bestRtt > 20 && mbps < SlowShare * bestMbps;
    }

    /// <summary>Analyzes one peer.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="history">Its history, oldest first.</param>
    /// <param name="others">The histories of the other peers (by key), used for the "my server or theirs" hints.</param>
    /// <returns>The analysis.</returns>
    public static LinkAnalysis Analyze(PeerState peer, IReadOnlyList<SpeedSample> history, IReadOnlyDictionary<string, IReadOnlyList<SpeedSample>> others)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(others);
        var tests = history.Where(static s => s.Error is null && s.Kind != SpeedKinds.Playback).OrderBy(static s => s.T).ToList();
        var latest = tests.LastOrDefault();
        var route = latest?.Path switch { "direct" => "direct", "derp" => "derp", "derp?" => "maybe", _ => "unknown" };
        var hints = new List<LinkHint>();
        var bottleneck = "none";

        void Add(LinkHint hint, string? kind = null)
        {
            hints.Add(hint);
            if (bottleneck == "none" && kind is not null)
            {
                bottleneck = kind;
            }
        }

        if (route == "derp")
        {
            Add(new LinkHint("relayed", "warn"), "relay");
        }
        else if (route == "maybe")
        {
            Add(new LinkHint("maybeRelayed", "warn"));
        }

        // Passive: the peer's transcoding is slow while the link is fast enough for the stream.
        if (history.Where(static s => s.Kind == SpeedKinds.Playback && s.TranscodeSpeed is not null).OrderBy(static s => s.T).LastOrDefault() is { } play
            && play.TranscodeSpeed < 1 / 0.7 && play.Mbps >= 1.5 * (play.StreamMbps ?? double.MaxValue))
        {
            Add(play.ConversionsActive >= SharedConversions ? new LinkHint("peerTranscodeSlowShared", "warn", play.ConversionsActive) : new LinkHint("peerTranscodeSlow", "warn"), "peerTranscode");
        }

        var mine = Slow(tests);
        var theirs = others.Values.Select(h => (Known: Throughput(h) is not null, Slow: Slow(h.Where(static s => s.Error is null && s.Kind != SpeedKinds.Playback).ToList()))).Where(static o => o.Known).ToList();
        if (mine && theirs.Count >= 1 && theirs.All(static o => o.Slow))
        {
            Add(new LinkHint("homeDownload", "warn"), "homeDownload");
        }
        else if (mine && theirs.Count >= 1 && theirs.Any(static o => !o.Slow) && RttNormal(tests))
        {
            Add(new LinkHint("peerUpload", "warn"), "peerUpload");
        }

        if (tests.LastOrDefault(static s => s.MbpsParallel is not null && s.Mbps is > 0) is { } parallel && parallel.MbpsParallel >= 1.6 * parallel.Mbps)
        {
            Add(new LinkHint("perConnection", "warn"), "perConnection");
        }

        if (!string.IsNullOrEmpty(peer.ProxyUrl))
        {
            Add(new LinkHint("proxyCpu", "warn"), "proxyCpu");
        }

        return new LinkAnalysis(route, null, hints, bottleneck);
    }

    private static double? Throughput(IReadOnlyList<SpeedSample> history) => VerdictEngine.Throughput(history);

    // The link now is under half of what it has done: the median of the last three tests against the best of all of them.
    private static bool Slow(IReadOnlyList<SpeedSample> tests)
    {
        var withSpeed = tests.Where(static s => s.Mbps is not null).ToList();
        return withSpeed.Count >= 2 && VerdictEngine.Throughput(withSpeed) is { } now && now < SlowShare * withSpeed.Max(static s => s.Mbps!.Value);
    }

    // The round trip is within the usual range: not over twice the best median and not 20 ms above it.
    private static bool RttNormal(IReadOnlyList<SpeedSample> tests)
    {
        var rtts = tests.Where(static s => s.RttMedianMs is not null).Select(static s => s.RttMedianMs!.Value).ToList();
        return rtts.Count == 0 || rtts[^1] <= 2 * rtts.Min() || rtts[^1] - rtts.Min() <= 20;
    }
}
