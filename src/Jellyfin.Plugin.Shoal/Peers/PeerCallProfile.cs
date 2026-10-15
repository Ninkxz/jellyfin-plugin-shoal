using System;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The deadlines and the byte cap of one kind of peer call (the table in DESIGN 6.10).
/// </summary>
/// <param name="Name">A short name for logs and tests.</param>
/// <param name="Connect">The longest the connection may take to open.</param>
/// <param name="Headers">The longest the peer may take to send the response headers (counted from the request).</param>
/// <param name="Idle">The longest a single body read may wait.</param>
/// <param name="Total">The deadline for the whole call including the body, or null for none (static streams; bounded by the declared length).</param>
/// <param name="MaxBytes">The most body bytes that are accepted.</param>
/// <param name="RequireDeclaredLength">True when the body must declare its length (static streams); a chunked answer is then refused.</param>
/// <param name="CountsTowardBreaker">False for calls whose timeouts, 5xx answers and successes must not touch the circuit breaker (subtitle extraction, DESIGN 6.10, W4-1); connect failures still count.</param>
public sealed record PeerCallProfile(string Name, TimeSpan Connect, TimeSpan Headers, TimeSpan Idle, TimeSpan? Total, long MaxBytes, bool RequireDeclaredLength = false, bool CountsTowardBreaker = true)
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;

    /// <summary>Gets the profile for <c>System/Info[/Public]</c>, <c>System/Ping</c>, <c>Users/Me</c>, the session anchor, <c>Shoal/Pair</c>.</summary>
    public static PeerCallProfile Info { get; } = new("info", Seconds(3), Seconds(5), Seconds(5), Seconds(8), 64 * KiB);

    /// <summary>Gets the profile for Quick Connect: the 1 s poll and the redeem use a 3 s total (DESIGN 4.2a).</summary>
    public static PeerCallProfile QuickConnect { get; } = new("quick-connect", Seconds(3), Seconds(3), Seconds(3), Seconds(3), 64 * KiB);

    /// <summary>Gets the profile for Quick Connect <c>Enabled</c>, <c>Initiate</c> and <c>Logout</c> (3 s each, DESIGN 4.2a).</summary>
    public static PeerCallProfile QuickConnectStep { get; } = QuickConnect with { Name = "quick-connect-step" };

    /// <summary>Gets the profile for <c>GET /Sessions</c> (the foreign-device watch).</summary>
    public static PeerCallProfile Sessions { get; } = new("sessions", Seconds(3), Seconds(5), Seconds(5), Seconds(8), 256 * KiB);

    /// <summary>Gets the profile for An items listing page or a details batch.</summary>
    public static PeerCallProfile Items { get; } = new("items", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 8 * MiB);

    /// <summary>Gets the profile for A poster image.</summary>
    public static PeerCallProfile PosterImage { get; } = new("poster", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 2 * MiB);

    /// <summary>Gets the profile for A backdrop image.</summary>
    public static PeerCallProfile BackdropImage { get; } = new("backdrop", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 4 * MiB);

    /// <summary>Gets the profile for <c>POST Items/{id}/PlaybackInfo</c>.</summary>
    public static PeerCallProfile PlaybackInfo { get; } = new("playback-info", Seconds(3), Seconds(10), Seconds(5), Seconds(12), 2 * MiB);

    /// <summary>Gets the profile for A static (direct play) stream: no total deadline, bounded by the declared length and the client's abort.</summary>
    public static PeerCallProfile StaticStream { get; } = new("static", Seconds(5), Seconds(15), Seconds(30), null, long.MaxValue, RequireDeclaredLength: true);

    /// <summary>Gets the profile for An HLS master playlist.</summary>
    public static PeerCallProfile HlsMaster { get; } = new("hls-master", Seconds(5), Seconds(30), Seconds(10), Seconds(40), 256 * KiB);

    /// <summary>Gets the profile for An HLS main (variant) playlist.</summary>
    public static PeerCallProfile HlsMain { get; } = new("hls-main", Seconds(5), Seconds(30), Seconds(10), Seconds(40), 8 * MiB);

    /// <summary>Gets the profile for An HLS segment (the peer blocks until the segment exists, so headers may take a minute).</summary>
    public static PeerCallProfile HlsSegment { get; } = new("hls-segment", Seconds(5), Seconds(60), Seconds(30), Seconds(180), 256 * MiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile for An HLS subtitle playlist.</summary>
    public static PeerCallProfile SubtitlePlaylist { get; } = new("subtitle-playlist", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 2 * MiB);

    /// <summary>Gets the profile for An HLS subtitle segment (WebVTT).</summary>
    public static PeerCallProfile SubtitleSegment { get; } = new("subtitle-segment", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 2 * MiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile of a subtitle file (<c>Stream.{fmt}</c>, external or embedded): the Peer extracts every track first, so the header wait is 120 s and the call never counts toward the breaker (6.10, W4-1).</summary>
    public static PeerCallProfile SubtitleFile { get; } = new("subtitle-file", Seconds(5), Seconds(120), Seconds(10), Seconds(150), 10 * MiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile for <c>POST Shoal/Share/Rotate</c> (never retried: a retry would carry a dead token; DESIGN v2.6.1).</summary>
    public static PeerCallProfile Rotate { get; } = new("rotate", Seconds(3), Seconds(5), Seconds(5), Seconds(8), 4 * KiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile for a trickplay tile (<c>Trickplay/{width}/{index}.jpg</c>; connect failures count toward the breaker, timeouts and 5xx do not; DESIGN v2.6.5).</summary>
    public static PeerCallProfile Trickplay { get; } = new("trickplay", Seconds(5), Seconds(15), Seconds(10), Seconds(30), 2 * MiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile for an attachment (a font): the Peer extracts every attachment first, so it waits like a subtitle file (DESIGN v2.6.6).</summary>
    public static PeerCallProfile AttachmentFile { get; } = new("attachment-file", Seconds(5), Seconds(120), Seconds(10), Seconds(150), 16 * MiB, CountsTowardBreaker: false);

    /// <summary>Gets the profile for An external subtitle file.</summary>
    public static PeerCallProfile ExternalSubtitle { get; } = new("external-subtitle", Seconds(5), Seconds(20), Seconds(10), Seconds(30), 10 * MiB);

    /// <summary>Gets the profile for Session reports and <c>ActiveEncodings</c> delete.</summary>
    public static PeerCallProfile SessionReport { get; } = new("session-report", Seconds(3), Seconds(5), Seconds(5), Seconds(8), 64 * KiB);

    /// <summary>Gets the profile for The bitrate test (phase 4).</summary>
    public static PeerCallProfile BitrateTest { get; } = new("bitrate-test", Seconds(5), Seconds(15), Seconds(5), Seconds(30), 64 * MiB);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
}
