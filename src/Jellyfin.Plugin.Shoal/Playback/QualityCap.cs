using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The quality limit of a connection as playback enforces it (PLAN v2.9 K51): a picture box and a bitrate. It is computed from the stored limit at
/// PlaybackInfo time, held by the grant, and applied to everything Home asks the Peer for. Nothing the client sends can raise it.
/// </summary>
/// <param name="MaxWidth">The widest picture allowed, or 0 for no box.</param>
/// <param name="MaxHeight">The tallest picture allowed, or 0 for no box.</param>
/// <param name="MaxBitrate">The highest bitrate in bits per second, or 0 for none.</param>
public sealed record QualityCap(int MaxWidth, int MaxHeight, int MaxBitrate)
{
    /// <summary>The words a user reads when nothing is left that plays inside the limit.</summary>
    public const string RefusalText = "This title can't play at the quality limit you set.";

    private static readonly string[] SizeKeys = ["Width", "Height"];

    /// <summary>
    /// The cap of a stored limit.
    /// </summary>
    /// <param name="limit">The limit, or null.</param>
    /// <returns>The cap, or null when the limit is Off and has no bitrate.</returns>
    public static QualityCap? From(QualityLimit? limit)
    {
        if (limit is null)
        {
            return null;
        }

        var box = QualityLimit.BoxOf(limit.MaxClass);
        var bitrate = limit.MaxMbps is { } mbps ? Math.Clamp(mbps, QualityLimit.MinMbps, QualityLimit.MaxMbpsCap) * 1_000_000 : 0;
        return box is null && bitrate == 0 ? null : new QualityCap(box?.Width ?? 0, box?.Height ?? 0, bitrate);
    }

    /// <summary>
    /// Whether this cap (the limit stored now) is stricter than the one a grant holds (Warden 11 L1): a smaller box, a box where it had none, a lower bitrate, or a
    /// bitrate where it had none.
    /// </summary>
    /// <param name="held">The grant's cap, or null for none.</param>
    /// <returns>True when a running stream must stop.</returns>
    public bool IsStricterThan(QualityCap? held)
    {
        var box = MaxWidth > 0 && (held is not { MaxWidth: > 0 } || MaxWidth < held.MaxWidth || MaxHeight < held.MaxHeight);
        var rate = MaxBitrate > 0 && (held is not { MaxBitrate: > 0 } || MaxBitrate < held.MaxBitrate);
        return box || rate;
    }

    /// <summary>
    /// Whether a source plays inside the cap as it is. An unknown size (0) never fits a box, and an unknown bitrate never fits a bitrate cap: an unknown
    /// source is converted, never assumed small.
    /// </summary>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <param name="bitrate">The source bitrate in bits per second.</param>
    /// <returns>True when it fits.</returns>
    public bool Allows(int width, int height, long bitrate)
    {
        if (MaxWidth > 0 && (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight))
        {
            return false;
        }

        return MaxBitrate <= 0 || (bitrate > 0 && bitrate <= MaxBitrate);
    }

    /// <summary>
    /// The size and bitrate of a Peer's source: its first video stream's picture, and the source bitrate.
    /// </summary>
    /// <param name="source">The Peer's source (hostile input; only numbers are read).</param>
    /// <returns>Width, height and bitrate, each 0 when not known or out of range.</returns>
    public static (int Width, int Height, int Bitrate) SizeOf(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var video = source.MediaStreams?.FirstOrDefault(static s => s is not null && s.Type == MediaStreamType.Video);
        var width = video?.Width is >= 1 and <= 16384 ? video.Width.Value : 0;
        var height = video?.Height is >= 1 and <= 16384 ? video.Height.Value : 0;
        var bitrate = source.Bitrate is >= 1 and <= 1_000_000_000 ? source.Bitrate.Value : 0;
        return (width, height, bitrate);
    }

    /// <summary>
    /// Adds the cap to a forwarded profile: a video codec profile for every codec with required Width and Height conditions, so the Peer neither plays the
    /// file as it is nor converts it to a bigger picture.
    /// </summary>
    /// <returns>The codec profile, or null when the cap has no box.</returns>
    public CodecProfile? ProfileRule()
    {
        if (MaxWidth <= 0)
        {
            return null;
        }

        return new CodecProfile
        {
            Type = CodecType.Video,
            Codec = string.Empty,
            Container = string.Empty,
            Conditions =
            [
                new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.Width, MaxWidth.ToString(CultureInfo.InvariantCulture), true),
                new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.Height, MaxHeight.ToString(CultureInfo.InvariantCulture), true),
            ],
            ApplyConditions = [],
        };
    }

    /// <summary>
    /// Holds a Peer query (the master query of a grant or the variant query learned from the master playlist) to the cap: <c>MaxWidth</c> and
    /// <c>MaxHeight</c> at most the box (added when missing), a fixed <c>Width</c>/<c>Height</c> removed, and <c>VideoBitrate</c> at most the bitrate cap
    /// (added when missing). A value that is not a plain number is replaced by the cap. The order of the other keys is kept.
    /// </summary>
    /// <param name="query">The query, decoded.</param>
    /// <returns>The held query.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> Hold(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pairs = query.ToList();
        if (MaxWidth > 0)
        {
            pairs.RemoveAll(static p => SizeKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
            Clamp(pairs, "MaxWidth", MaxWidth);
            Clamp(pairs, "MaxHeight", MaxHeight);
        }

        if (MaxBitrate > 0)
        {
            Clamp(pairs, "VideoBitrate", MaxBitrate);
        }

        return pairs;
    }

    /// <summary>The text of the cap for the admin page: "Up to 1080p, 20 Mbps", "Up to 720p" or "Up to 8 Mbps".</summary>
    /// <param name="limit">The stored limit.</param>
    /// <returns>The text, or "No limit".</returns>
    public static string Describe(QualityLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        var cls = limit.MaxClass == QualityClass.Off ? null : ClassLabel(limit.MaxClass);
        var mbps = limit.MaxMbps is { } m ? m.ToString(CultureInfo.InvariantCulture) + " Mbps" : null;
        return (cls, mbps) switch
        {
            (null, null) => "No limit",
            (not null, null) => "Up to " + cls,
            (null, not null) => "Up to " + mbps,
            _ => "Up to " + cls + ", " + mbps,
        };
    }

    /// <summary>The label of a class as people read it.</summary>
    /// <param name="cls">The class.</param>
    /// <returns>480p, 720p, 1080p, 4K or Off.</returns>
    public static string ClassLabel(QualityClass cls) => cls switch
    {
        QualityClass.Sd480 => "480p",
        QualityClass.Hd720 => "720p",
        QualityClass.Hd1080 => "1080p",
        QualityClass.Uhd2160 => "4K",
        _ => "Off",
    };

    private static void Clamp(List<KeyValuePair<string, string>> pairs, string key, int cap)
    {
        var index = pairs.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var capText = cap.ToString(CultureInfo.InvariantCulture);
        if (index < 0)
        {
            pairs.Add(new KeyValuePair<string, string>(key, capText));
            return;
        }

        var value = pairs[index].Value;
        var keep = value.Length is >= 1 and <= 10 && value.All(static c => c is >= '0' and <= '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= cap;
        pairs[index] = new KeyValuePair<string, string>(pairs[index].Key, keep ? value : capText);

        // A repeated key would let the Peer read the other copy.
        for (var i = pairs.Count - 1; i > index; i--)
        {
            if (pairs[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                pairs.RemoveAt(i);
            }
        }
    }
}
