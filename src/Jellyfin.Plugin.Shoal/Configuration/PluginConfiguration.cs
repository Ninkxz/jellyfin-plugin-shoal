using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Shoal.Configuration;

/// <summary>
/// The quality of the posters and backdrops that are stored with the stubs (DESIGN 5.4).
/// </summary>
public enum ArtQuality
{
    /// <summary>Poster 600 px, backdrop 1280 px.</summary>
    Standard = 0,

    /// <summary>Poster 1000 px, backdrop 1920 px.</summary>
    High = 1
}

/// <summary>
/// Non-critical toggles, persisted as XML by Jellyfin (DESIGN 3.2, schema 2). Nothing in here is a secret, topology or a security
/// choice whose default is less safe than the admin's setting: core silently replaces this file with defaults whenever it cannot
/// read it, and then only these toggles return to their (safe) defaults. Peers, access modes, the hardening mode and every
/// token live in Shoal's own stores (<c>state.json</c>, <c>secrets.enc</c>).
/// <para>
/// Out-of-range values are clamped by <see cref="SettingsValidator"/> on load and on every write; it never throws, because a
/// throwing validator would trigger core's reset-to-defaults path.
/// </para>
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The schema of this class.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Gets or sets the schema of this file.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets a value indicating whether the quality is fitted to the measured link (phase 4).</summary>
    public bool AutoFitQualityToLink { get; set; } = true;

    /// <summary>Gets or sets the most streams one local user may run at once across all friends' servers (1 to 4, owner D13).</summary>
    public int MaxStreamsPerUser { get; set; } = 4;

    /// <summary>Gets or sets how many segments Home reads ahead of an HLS stream from a friend's server (2 to 20): the client then reads from Home's fast side and a short loss on the slow link does not stall playback.</summary>
    public int HlsBufferAhead { get; set; } = 6;

    /// <summary>Gets or sets how often the catalogs are synced, in hours.</summary>
    public int SyncIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets how often the link is measured, in hours (phase 4).</summary>
    public int SpeedTestIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets the smallest number of removals that the mass-deletion guard holds back.</summary>
    public int MassDeletionMinCount { get; set; } = 25;

    /// <summary>Gets or sets the percentage of active titles whose removal the mass-deletion guard holds back.</summary>
    public int MassDeletionPercent { get; set; } = 10;

    /// <summary>Gets or sets the smallest number of additions that the new-items guard holds back.</summary>
    public int NewItemsMinCount { get; set; } = 500;

    /// <summary>Gets or sets the percentage of active titles whose addition the new-items guard holds back.</summary>
    public int NewItemsPercent { get; set; } = 20;

    /// <summary>Gets or sets the disk quota for the stored images of one peer, in GB.</summary>
    public int DiskQuotaGbPerPeer { get; set; } = 2;

    /// <summary>Gets or sets the image quality.</summary>
    public ArtQuality ArtQuality { get; set; } = ArtQuality.Standard;

    /// <summary>Gets or sets a value indicating whether titles of an offline peer are hidden after <see cref="ParkAfterHours"/> (owner D5: off).</summary>
    public bool ParkOfflinePeers { get; set; }

    /// <summary>Gets or sets after how many hours an offline peer is parked.</summary>
    public double ParkAfterHours { get; set; } = 0.5;

    /// <summary>Gets or sets a value indicating whether clients are told (a display message) when a peer is offline.</summary>
    public bool NotifyClientsWhenOffline { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether HLS subtitles are offered (a kill switch).</summary>
    public bool HlsSubtitlesEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether friends' servers may detect that Shoal is installed here (DESIGN pairing v2; the anonymous ping; default on). Off makes the ping answer like any unknown address.</summary>
    public bool DetectableByFriends { get; set; } = true;

    /// <summary>Gets or sets the Tailscale LocalAPI socket: <c>auto</c> or an absolute path (phase 4).</summary>
    public string TailscaleSocket { get; set; } = "auto";

    /// <summary>Gets or sets a value indicating whether a title may have several versions, one stub per peer media source (DESIGN v2.6.4; default off until the lab gate T-ALT-0 has passed).</summary>
    public bool AlternateVersionsEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether trickplay tiles are served (DESIGN v2.6.5; default on).</summary>
    public bool TrickplayEnabled { get; set; } = true;

    /// <summary>Gets or sets the size cap of the trickplay tile cache in GB, 1..20 (DESIGN v2.6.5).</summary>
    public int TrickplayCacheGb { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether embedded fonts are offered and served (DESIGN v2.6.6; default on, a kill switch).</summary>
    public bool AttachmentsEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether images are re-encoded in a child process when it is available (DESIGN v2.6.7; default on).</summary>
    public bool ReencodeImages { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether TV libraries of a friend can be added (DESIGN v2.7; default off until the lab gate T-TV-0 has passed). Off keeps v1 behavior: the picker lists tvshows as coming later.</summary>
    public bool TvShowsEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether episode thumbnails are fetched and stored (DESIGN v2.7.3; default on). Off writes none.</summary>
    public bool TvEpisodeThumbs { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a friend's offline server gets the offline card (a short video that says so) instead of a bare "playback error" (default on).</summary>
    public bool OfflineCardEnabled { get; set; } = true;

    /// <summary>Gets or sets the name of the request service ("Seerr") used in the offline card and the offline message.</summary>
    public string RequestServiceName { get; set; } = "Seerr";

    /// <summary>Gets or sets the address of the request service, or empty. Shown in the admin page and in the offline message; never in the video.</summary>
    public string RequestServiceUrl { get; set; } = string.Empty;
}
