using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// The highest quality class a connection may play (PLAN v2.9, owner decision 6). A source fits a class when its width AND height are at or under
/// the class box: 854x480, 1280x720, 1920x1080, 3840x2160.
/// </summary>
public enum QualityClass
{
    /// <summary>No limit.</summary>
    Off,

    /// <summary>At most 854x480.</summary>
    [JsonStringEnumMemberName("480")]
    Sd480,

    /// <summary>At most 1280x720.</summary>
    [JsonStringEnumMemberName("720")]
    Hd720,

    /// <summary>At most 1920x1080.</summary>
    [JsonStringEnumMemberName("1080")]
    Hd1080,

    /// <summary>At most 3840x2160.</summary>
    [JsonStringEnumMemberName("2160")]
    Uhd2160,
}

/// <summary>
/// Who set a quality limit.
/// </summary>
public enum QualityLimitOrigin
{
    /// <summary>The admin chose it on the server detail page.</summary>
    Manual,

    /// <summary>The admin applied the speed-test recommendation.</summary>
    Recommendation,
}

/// <summary>
/// The quality limit of one connection (PLAN v2.9 K50). Stored on the peer record in <c>state.json</c>; enforcement is K51/K52.
/// </summary>
public sealed class QualityLimit
{
    /// <summary>The lowest <see cref="MaxMbps"/> accepted.</summary>
    public const int MinMbps = 1;

    /// <summary>The highest <see cref="MaxMbps"/> accepted.</summary>
    public const int MaxMbpsCap = 1000;

    /// <summary>Gets or sets the highest class; <see cref="QualityClass.Off"/> (the default) is no limit.</summary>
    public QualityClass MaxClass { get; set; } = QualityClass.Off;

    /// <summary>Gets or sets the optional bitrate limit in Mbps (clamped to 1..1000), or null for none.</summary>
    public int? MaxMbps { get; set; }

    /// <summary>Gets or sets when the limit was last set (UTC), or null when it never was.</summary>
    public DateTime? SetUtc { get; set; }

    /// <summary>Gets or sets the local admin who set it, or null.</summary>
    public Guid? SetByUserId { get; set; }

    /// <summary>Gets or sets how it was set.</summary>
    public QualityLimitOrigin Origin { get; set; } = QualityLimitOrigin.Manual;

    /// <summary>
    /// The box of a class: the largest width and height that fit it.
    /// </summary>
    /// <param name="cls">The class.</param>
    /// <returns>The box, or null for <see cref="QualityClass.Off"/>.</returns>
    public static (int Width, int Height)? BoxOf(QualityClass cls) => cls switch
    {
        QualityClass.Sd480 => (854, 480),
        QualityClass.Hd720 => (1280, 720),
        QualityClass.Hd1080 => (1920, 1080),
        QualityClass.Uhd2160 => (3840, 2160),
        _ => null,
    };

    /// <summary>
    /// Whether a source of the given size fits a class (width AND height at or under the box). Off fits everything; an unknown size (0) is treated
    /// as fitting only when the class is Off, so an unknown source is never assumed small.
    /// </summary>
    /// <param name="cls">The class.</param>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <returns>True when it fits.</returns>
    public static bool Fits(QualityClass cls, int width, int height)
    {
        if (BoxOf(cls) is not { } box)
        {
            return true;
        }

        return width > 0 && height > 0 && width <= box.Width && height <= box.Height;
    }
}

/// <summary>
/// A speed-test recommendation the admin dismissed (PLAN v2.9 K53 reads these). It is shown again only when the suggested class drops, the
/// measured throughput falls more than 30% below <see cref="ThroughputMbps"/>, or 30 days pass.
/// </summary>
public sealed class DismissedRecommendation
{
    /// <summary>Gets or sets the class that was suggested.</summary>
    public QualityClass SuggestedClass { get; set; } = QualityClass.Off;

    /// <summary>Gets or sets the measured throughput (median of the last samples) at dismissal, in Mbps.</summary>
    public double ThroughputMbps { get; set; }

    /// <summary>Gets or sets when it was dismissed (UTC).</summary>
    public DateTime DismissedUtc { get; set; }

    /// <summary>Gets or sets the local admin who dismissed it.</summary>
    public Guid DismissedByUserId { get; set; }
}
