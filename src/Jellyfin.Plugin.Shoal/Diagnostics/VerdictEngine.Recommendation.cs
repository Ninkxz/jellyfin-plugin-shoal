using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>
/// One title of a friend's catalog as the recommendation reads it: numbers only.
/// </summary>
/// <param name="Width">The width, or 0 when not known.</param>
/// <param name="Height">The height, or 0 when not known.</param>
/// <param name="Bitrate">The bitrate in bits per second, or 0 when not known.</param>
public readonly record struct TitleSize(int Width, int Height, int Bitrate);

/// <summary>
/// The speed-test recommendation card of one connection (PLAN v2.9 K53).
/// </summary>
/// <param name="SuggestedClass">The highest class the link carries: the limit to apply.</param>
/// <param name="SlowForClass">The class just above it (the one the link is slow for).</param>
/// <param name="ThroughputMbps">T, the median of the last three tests.</param>
/// <param name="TitlesAbove">How many of the friend's titles are above the suggested class.</param>
/// <param name="TitlesKnown">How many of the friend's titles have a known size.</param>
public sealed record QualityRecommendation(QualityClass SuggestedClass, QualityClass SlowForClass, double ThroughputMbps, int TitlesAbove, int TitlesKnown)
{
    /// <summary>
    /// The words of the card: "This link is slow for 4K. Limit {Server} to 1080p?". The server name is the admin's own alias or the friend's name; the page sets it as
    /// text, never as markup.
    /// </summary>
    /// <param name="serverName">The name of the server as the admin sees it.</param>
    /// <returns>The text.</returns>
    public string Text(string serverName) =>
        $"This link is slow for {Playback.QualityCap.ClassLabel(SlowForClass)}. Limit {serverName} to {Playback.QualityCap.ClassLabel(SuggestedClass)}?";
}

/// <summary>
/// The quality limit recommendation (PLAN v2.9 K53). Pure: the clock is a parameter.
/// </summary>
public static partial class VerdictEngine
{
    /// <summary>A recommendation shows when at least this share of the friend's titles is above the suggested class.</summary>
    public const double RecommendShareAbove = 0.10;

    /// <summary>... or at least this many titles.</summary>
    public const int RecommendCountAbove = 20;

    /// <summary>A dismissed card shows again when T falls by more than this share below the T at dismissal.</summary>
    public const double DismissThroughputDrop = 0.30;

    /// <summary>A dismissed card shows again after this long.</summary>
    public static readonly TimeSpan DismissFor = TimeSpan.FromDays(30);

    private static readonly QualityClass[] LimitClasses = [QualityClass.Sd480, QualityClass.Hd720, QualityClass.Hd1080, QualityClass.Uhd2160];

    /// <summary>
    /// The reference bitrate of a class when the friend has no title of it with a known bitrate, in Mbps (480p 2, 720p 4, 1080p 10, 4K 20).
    /// </summary>
    /// <param name="cls">The class.</param>
    /// <returns>The reference in Mbps.</returns>
    public static double ReferenceMbps(QualityClass cls) => cls switch
    {
        QualityClass.Sd480 => 2,
        QualityClass.Hd720 => 4,
        QualityClass.Hd1080 => 10,
        _ => 20,
    };

    /// <summary>
    /// The class of a picture: the smallest box it fits; a picture larger than the 4K box counts as 4K.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>The class, or <see cref="QualityClass.Off"/> when the size is not known.</returns>
    public static QualityClass ClassOf(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return QualityClass.Off;
        }

        foreach (var cls in LimitClasses)
        {
            if (QualityLimit.Fits(cls, width, height))
            {
                return cls;
            }
        }

        return QualityClass.Uhd2160;
    }

    /// <summary>
    /// The bitrate a class needs on this friend: the 75th percentile of the bitrates of the friend's titles of that class, or the class's reference when
    /// none has a known bitrate.
    /// </summary>
    /// <param name="titles">The friend's titles.</param>
    /// <param name="cls">The class.</param>
    /// <returns>The bitrate in Mbps.</returns>
    public static double ClassBitrateMbps(IEnumerable<TitleSize> titles, QualityClass cls)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var rates = titles.Where(t => t.Bitrate > 0 && ClassOf(t.Width, t.Height) == cls).Select(static t => t.Bitrate / 1_000_000.0).Order().ToList();
        if (rates.Count == 0)
        {
            return ReferenceMbps(cls);
        }

        // Nearest rank: the smallest value with at least 75 percent of the values at or under it.
        var rank = (int)Math.Ceiling(0.75 * rates.Count) - 1;
        return rates[Math.Clamp(rank, 0, rates.Count - 1)];
    }

    /// <summary>
    /// The recommendation for one connection. A class holds when T is at least <see cref="Headroom"/> times its bitrate on this friend
    /// (<see cref="ClassBitrateMbps"/>). The card shows when the highest class that holds (480p when none does) is below the current limit, at least
    /// <see cref="RecommendShareAbove"/> of the titles with a known size or <see cref="RecommendCountAbove"/> titles are above it, the link is not unstable,
    /// and no dismissal still covers it. A dismissal stops covering when the suggested class drops below the dismissed one, T falls more than
    /// <see cref="DismissThroughputDrop"/> below the T at dismissal, or <see cref="DismissFor"/> passes.
    /// </summary>
    /// <param name="current">The connection's current limit.</param>
    /// <param name="throughputMbps">T, or null when never measured (no card).</param>
    /// <param name="unstable">Whether the last test found the link unstable (no card).</param>
    /// <param name="titles">The friend's titles that can play from it.</param>
    /// <param name="dismissed">The connection's dismissals.</param>
    /// <param name="now">The time (UTC).</param>
    /// <returns>The card, or null when none shows.</returns>
    public static QualityRecommendation? Recommend(QualityLimit current, double? throughputMbps, bool unstable, IReadOnlyCollection<TitleSize> titles, IEnumerable<DismissedRecommendation> dismissed, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(dismissed);
        if (throughputMbps is not { } t || !double.IsFinite(t) || t < 0 || unstable)
        {
            return null;
        }

        var suggested = QualityClass.Sd480;
        foreach (var cls in LimitClasses.Reverse())
        {
            if (t >= Headroom * ClassBitrateMbps(titles, cls))
            {
                suggested = cls;
                break;
            }
        }

        if (suggested == QualityClass.Uhd2160 || LimitRank(suggested) >= LimitRank(current.MaxClass))
        {
            // A link that carries 4K needs no limit (a picture larger than the 4K box is converted like 4K anyway).
            return null;
        }

        var known = titles.Count(static s => s.Width > 0 && s.Height > 0);
        var above = titles.Count(s => s.Width > 0 && s.Height > 0 && !QualityLimit.Fits(suggested, s.Width, s.Height));
        if (above == 0 || (above < RecommendCountAbove && above < RecommendShareAbove * known))
        {
            return null;
        }

        var last = dismissed.OrderByDescending(static d => d.DismissedUtc).FirstOrDefault();
        if (last is not null && LimitRank(suggested) >= LimitRank(last.SuggestedClass) && t >= (1 - DismissThroughputDrop) * last.ThroughputMbps && now - last.DismissedUtc < DismissFor)
        {
            return null;
        }

        return new QualityRecommendation(suggested, LimitClasses[Array.IndexOf(LimitClasses, suggested) + 1], Math.Round(t, 1), above, known);
    }

    // Off is above every class.
    private static int LimitRank(QualityClass cls) => cls == QualityClass.Off ? LimitClasses.Length : Array.IndexOf(LimitClasses, cls);
}
