using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>What the server cards need from a peer's speed history.</summary>
/// <param name="Mbps">The latest measured throughput.</param>
/// <param name="RttMs">The latest median round trip.</param>
/// <param name="JitterMs">The latest jitter.</param>
/// <param name="LossPct">The latest loss.</param>
/// <param name="Path">The path word of the latest sample.</param>
/// <param name="Verdicts">The one-line summary (1080p, 4K, remux).</param>
public sealed record SpeedSummary(double? Mbps, double? RttMs, double? JitterMs, double? LossPct, string Path, IReadOnlyList<SummaryRow> Verdicts);

/// <summary>
/// The speed numbers of each peer for the Overview, computed from the history and kept until the next sample (or a change of the
/// auto-fit setting) so the page's status polling does not read the file every time.
/// </summary>
public sealed class SpeedSummaryProvider
{
    private readonly SpeedHistoryStore _history;
    private readonly ISettingsAccessor _settings;
    private readonly ConcurrentDictionary<string, (bool AutoFit, SpeedSummary? Summary)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedSummaryProvider"/> class.
    /// </summary>
    /// <param name="history">The speed history.</param>
    /// <param name="settings">The plugin toggles (auto-fit).</param>
    public SpeedSummaryProvider(SpeedHistoryStore history, ISettingsAccessor settings)
    {
        _history = history;
        _settings = settings;
        _history.Appended += peerKey => _cache.TryRemove(peerKey, out _);
    }

    /// <summary>Gets the summary of a peer.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The summary, or null when the peer was never measured.</returns>
    public SpeedSummary? For(string peerKey)
    {
        var autoFit = _settings.Current.AutoFitQualityToLink;
        if (_cache.TryGetValue(peerKey, out var cached) && cached.AutoFit == autoFit)
        {
            return cached.Summary;
        }

        var summary = Compute(_history.Read(peerKey), autoFit);
        _cache[peerKey] = (autoFit, summary);
        return summary;
    }

    private static SpeedSummary? Compute(IReadOnlyList<SpeedSample> history, bool autoFit)
    {
        var tests = history.Where(s => s.Kind != SpeedKinds.Playback).ToList();
        if (tests.Count == 0)
        {
            return null;
        }

        var latest = tests.Where(s => s.HasSpeed).OrderBy(s => s.T).LastOrDefault();
        var lastAny = tests.OrderBy(s => s.T).Last();
        var t = VerdictEngine.Throughput(history);
        return new SpeedSummary(latest?.Mbps, latest?.RttMedianMs ?? lastAny.RttMedianMs, latest?.JitterMs, latest?.LossPct, (latest ?? lastAny).Path, VerdictEngine.Summary(t, autoFit));
    }
}

/// <summary>Builds the answer of <c>GET Peers/{h}/Speed</c> (the shape <c>shoal.js</c> reads: samples, offline, routeChanges, kpis, ladder, bottleneck, cooldowns, route, hints).</summary>
public static class SpeedReport
{
    /// <summary>The ranges the page asks for.</summary>
    /// <param name="range">24h, 7d or 30d.</param>
    /// <returns>The span, or null for anything else.</returns>
    public static TimeSpan? SpanOf(string? range) => range switch
    {
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => null
    };

    /// <summary>Builds the report.</summary>
    /// <param name="history">The whole history, oldest first.</param>
    /// <param name="span">The range.</param>
    /// <param name="now">The time.</param>
    /// <param name="peer">The peer.</param>
    /// <param name="autoFit">Whether auto-fit is on.</param>
    /// <param name="testUntilUtc">When "Test now" is available again, or null.</param>
    /// <param name="deepUntilUtc">When the deep test is available again, or null.</param>
    /// <param name="testRunning">True while a test is running (the page asks again soon).</param>
    /// <param name="others">The histories of the other peers (for the "your server or theirs" hints).</param>
    /// <returns>An object that serializes to the answer.</returns>
    public static object Build(IReadOnlyList<SpeedSample> history, TimeSpan span, DateTimeOffset now, PeerState peer, bool autoFit, DateTimeOffset? testUntilUtc, DateTimeOffset? deepUntilUtc, bool testRunning = false, IReadOnlyDictionary<string, IReadOnlyList<SpeedSample>>? others = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(peer);
        var inRange = history.Where(s => s.T >= now - span && s.T <= now).OrderBy(s => s.T).ToList();
        var tests = history.Where(s => s.Kind != SpeedKinds.Playback).ToList();
        var latest = history.Where(s => s.HasSpeed && s.Kind != SpeedKinds.Playback).OrderBy(s => s.T).LastOrDefault();
        var measured = inRange.Where(s => s.HasSpeed).Select(s => s.Mbps!.Value).ToList();
        var t = VerdictEngine.Throughput(history);
        var analysis = LinkAnalyzer.Analyze(peer, history, others ?? new Dictionary<string, IReadOnlyList<SpeedSample>>());
        var region = tests.LastOrDefault(static s => s.Path == "derp")?.DerpRegion;
        var deep = history.Where(static s => s.Kind == SpeedKinds.Deep && s.TranscodeSpeed is > 0).OrderBy(static s => s.T).LastOrDefault();
        return new
        {
            samples = inRange.Select(Sample).ToList(),
            offline = Array.Empty<object>(),
            routeChanges = RouteChanges(inRange),
            kpis = new
            {
                nowMbps = latest?.Mbps,
                latencyMs = latest?.RttMedianMs,
                jitterMs = latest?.JitterMs,
                lossPct = latest?.LossPct,
                lowestMbps = measured.Count == 0 ? (double?)null : measured.Min()
            },
            ladder = t is { } throughput ? VerdictEngine.Ladder(throughput, autoFit).Select(r => new { cls = r.Cls, verdict = r.Verdict, neededMbps = r.NeededMbps }).ToList() : [],
            bottleneck = new { kind = analysis.Bottleneck, region = analysis.Bottleneck == "relay" ? region : null },
            localApiAvailable = latest is { Path: "direct" or "derp" },
            cooldowns = new { testUntilUtc = testUntilUtc?.UtcDateTime, deepUntilUtc = deepUntilUtc?.UtcDateTime },
            route = new { kind = analysis.Route, region = analysis.Route == "derp" ? region : null },
            hints = analysis.Hints.Select(static h => new { kind = h.Kind, severity = h.Severity, n = h.N }).ToList(),
            testRunning,
            deep = deep is null ? null : new { t = deep.T.UtcDateTime, transcodeSpeed = deep.TranscodeSpeed, ttfbMs = deep.SegTtfbMs, suggestedConversions = TranscodeProbe.SuggestedConversions(deep.TranscodeSpeed!.Value) },
            lastMeasuredUtc = tests.Count == 0 ? (DateTime?)null : tests.Max(s => s.T).UtcDateTime
        };
    }

    private static object Sample(SpeedSample s) => new
    {
        t = s.T.UtcDateTime,
        kind = s.Kind == SpeedKinds.Light ? "scheduled" : s.Kind,
        mbps = s.Mbps,
        rttMedianMs = s.RttMedianMs,
        rttP90Ms = s.RttP90Ms,
        jitterMs = s.JitterMs,
        lossPct = s.LossPct,
        path = s.Path,
        error = s.Error,
        method = s.Kind == SpeedKinds.Playback ? "transcode" : null,
        segmentWaitMs = s.SegTtfbMs,
        pauses = s.Stalls
    };

    private static List<object> RouteChanges(IReadOnlyList<SpeedSample> samples)
    {
        var result = new List<object>();
        string? previous = null;
        foreach (var sample in samples.Where(s => s.Path != "unknown"))
        {
            if (previous is not null && previous != sample.Path)
            {
                result.Add(new { t = sample.T.UtcDateTime, from = previous, to = sample.Path });
            }

            previous = sample.Path;
        }

        return result;
    }
}
