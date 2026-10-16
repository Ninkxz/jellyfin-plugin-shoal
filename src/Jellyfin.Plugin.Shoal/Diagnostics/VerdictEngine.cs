using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>One quality class of the ladder with its verdict.</summary>
/// <param name="Cls">The class word (720p, 1080pTranscode, 1080p, 4k, remux1080, remux4k).</param>
/// <param name="Verdict">smooth, mayBuffer, needsTranscode or willBuffer.</param>
/// <param name="NeededMbps">The throughput at which the class plays smoothly (1.5 times its reference bitrate).</param>
public sealed record LadderRow(string Cls, string Verdict, double NeededMbps);

/// <summary>One word of the one-line summary on a server card.</summary>
/// <param name="Cls">1080p, 4k or remux.</param>
/// <param name="Verdict">smooth, mayBuffer, needsTranscode or willBuffer.</param>
public sealed record SummaryRow(string Cls, string Verdict);

/// <summary>
/// Turns measured throughput into verdicts (DESIGN 7.2). Pure: no clock, no I/O.
/// </summary>
public static class VerdictEngine
{
    /// <summary>Headroom over a class's reference bitrate (VBR peaks and short-burst variance).</summary>
    public const double Headroom = 1.5;

    /// <summary>The lowest rate worth transcoding down to: 1.5 Mbps, so a slow link gets a low-bitrate conversion instead of a stall (owner, 2026-10-16; it was 4, the 720p preset).</summary>
    public const double MinUsableMbps = 1.5;

    private static readonly (string Cls, double Reference, bool CanTranscodeDown)[] Classes =
    [
        ("remux4k", 80, false),
        ("remux1080", 35, false),
        ("4k", 20, true),
        ("1080p", 10, true),
        ("1080pTranscode", 8, false),
        ("720p", 4, false)
    ];

    /// <summary>
    /// The throughput T of a link: the median of the last three samples that carry a speed (scheduled, manual and deep tests; passive playback
    /// samples measure something else and a test that shared the link with a stream measured only what was left: neither is used).
    /// </summary>
    /// <param name="history">The samples, oldest first.</param>
    /// <returns>T in Mbps, or null when nothing has been measured.</returns>
    public static double? Throughput(IEnumerable<SpeedSample> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var last = history.Where(s => s.HasSpeed && s.Kind != SpeedKinds.Playback && s.SharedWithStreams != true).OrderBy(s => s.T).TakeLast(3).Select(s => s.Mbps!.Value).Order().ToList();
        return last.Count == 0 ? null : last.Count % 2 == 1 ? last[last.Count / 2] : (last[(last.Count / 2) - 1] + last[last.Count / 2]) / 2;
    }

    /// <summary>The bitrate auto-fit would cap a stream at: T divided by the headroom.</summary>
    /// <param name="throughputMbps">T.</param>
    /// <returns>The cap in Mbps.</returns>
    public static double AutoFitMbps(double throughputMbps) => throughputMbps / Headroom;

    /// <summary>The ladder: one row per quality class.</summary>
    /// <param name="throughputMbps">T.</param>
    /// <param name="autoFit">Whether auto-fit is on (the peer then transcodes what does not fit).</param>
    /// <returns>The rows, biggest class first.</returns>
    public static IReadOnlyList<LadderRow> Ladder(double throughputMbps, bool autoFit) =>
        [.. Classes.Select(c => new LadderRow(c.Cls, ClassVerdict(throughputMbps, c.Reference, c.CanTranscodeDown, autoFit), Math.Round(c.Reference * Headroom, 1)))];

    /// <summary>The one-line summary of a server card: 1080p, 4K and remux.</summary>
    /// <param name="throughputMbps">T, or null when nothing has been measured.</param>
    /// <param name="autoFit">Whether auto-fit is on.</param>
    /// <returns>The three words, or none when nothing has been measured.</returns>
    public static IReadOnlyList<SummaryRow> Summary(double? throughputMbps, bool autoFit)
    {
        if (throughputMbps is not { } t)
        {
            return [];
        }

        var by = Ladder(t, autoFit).ToDictionary(r => r.Cls, r => r.Verdict, StringComparer.Ordinal);
        return [new SummaryRow("1080p", by["1080p"]), new SummaryRow("4k", by["4k"]), new SummaryRow("remux", by["remux4k"])];
    }

    /// <summary>The verdict for one title by its actual bitrate (DESIGN 7.2).</summary>
    /// <param name="throughputMbps">T.</param>
    /// <param name="titleMbps">The title's bitrate b.</param>
    /// <param name="autoFit">Whether auto-fit is on.</param>
    /// <returns>smooth, mayBuffer, needsTranscode or willBuffer.</returns>
    public static string TitleVerdict(double throughputMbps, double titleMbps, bool autoFit)
    {
        if (throughputMbps >= Headroom * titleMbps)
        {
            return "smooth";
        }

        if (throughputMbps >= titleMbps)
        {
            return "mayBuffer";
        }

        return autoFit && AutoFitMbps(throughputMbps) >= MinUsableMbps ? "needsTranscode" : "willBuffer";
    }

    /// <summary>The latency word for a median round trip.</summary>
    /// <param name="rttMedianMs">The median in ms.</param>
    /// <returns>good, ok, slow or poor.</returns>
    public static string LatencyVerdict(double rttMedianMs) => rttMedianMs switch
    {
        <= 50 => "good",
        <= 150 => "ok",
        <= 300 => "slow",
        _ => "poor"
    };

    /// <summary>Whether a link is unstable: jitter over 30 ms or loss over 2 percent.</summary>
    /// <param name="jitterMs">The jitter.</param>
    /// <param name="lossPct">The loss in percent.</param>
    /// <returns>True when unstable.</returns>
    public static bool Unstable(double? jitterMs, double? lossPct) => jitterMs > 30 || lossPct > 2;

    private static string ClassVerdict(double t, double reference, bool canTranscodeDown, bool autoFit)
    {
        if (t >= Headroom * reference)
        {
            return "smooth";
        }

        if (t >= reference)
        {
            return "mayBuffer";
        }

        // A file class the peer can transcode down plays when auto-fit has a usable rate to give it; a remux, or a class the client already
        // asks the peer to transcode at a fixed rate, does not.
        return canTranscodeDown && autoFit && AutoFitMbps(t) >= MinUsableMbps ? "needsTranscode" : "willBuffer";
    }
}
