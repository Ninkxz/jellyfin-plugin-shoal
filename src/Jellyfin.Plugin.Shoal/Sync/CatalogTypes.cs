using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// What a catalog entry stands for (DESIGN 3.3).
/// </summary>
public enum EntryState
{
    /// <summary>The title has a stub on disk.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Home has its own copy: no stub (local always wins, DESIGN 5.3).</summary>
    [JsonStringEnumMemberName("duplicate-local")]
    DuplicateLocal,

    /// <summary>Another peer (or another library of this peer) with a better rank carries the title: no stub.</summary>
    [JsonStringEnumMemberName("duplicate-peer")]
    DuplicatePeer,

    /// <summary>The peer no longer lists the title, but the mass-deletion guard holds the removal back: the stub stays.</summary>
    [JsonStringEnumMemberName("held-removal")]
    HeldRemoval,

    /// <summary>The new-items guard holds the title back: no stub yet.</summary>
    [JsonStringEnumMemberName("held-addition")]
    HeldAddition,

    /// <summary>Removed; such entries are dropped from the file at the end of a sync and are never stored.</summary>
    [JsonStringEnumMemberName("removed")]
    Removed,
}

/// <summary>
/// The outcome word stored in <c>lastSync.result</c>.
/// </summary>
public enum SyncResultKind
{
    /// <summary>A complete sync.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Some pages failed or the count did not match: titles were added and updated, none removed.</summary>
    [JsonStringEnumMemberName("partial")]
    Partial,

    /// <summary>Nothing could be synced (peer unreachable, identity mismatch, entry cap, damaged catalog).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>A guard held changes back; the rest was applied.</summary>
    [JsonStringEnumMemberName("held")]
    Held,

    /// <summary>The sync did not run (another sync was running, too soon, a scan was running, peer disabled).</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>
/// What a catalog entry is (catalog schema 4, DESIGN v2.7.2). A file written before schema 4 has no kind and reads as <see cref="Movie"/>.
/// </summary>
public enum EntryKind
{
    /// <summary>A movie: one <c>.strm</c> per media source, two path segments.</summary>
    [JsonStringEnumMemberName("movie")]
    Movie,

    /// <summary>A series: a container only (a folder, no stub). It exists while at least one active episode does.</summary>
    [JsonStringEnumMemberName("series")]
    Series,

    /// <summary>A season: a container only (a folder inside the series folder).</summary>
    [JsonStringEnumMemberName("season")]
    Season,

    /// <summary>An episode: one <c>.strm</c>, three path segments (<c>series/Season NN/SnnEnn.strm</c>).</summary>
    [JsonStringEnumMemberName("episode")]
    Episode,
}

/// <summary>
/// One mapped peer library as the catalog knows it.
/// </summary>
public sealed class CatalogLibrary
{
    /// <summary>Gets or sets the local mapping key.</summary>
    public string MappingKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's library id (32 hex characters), resolved once from <c>/UserViews</c>.</summary>
    public string PeerLibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles the peer listed at the last complete sync.</summary>
    public int LastCount { get; set; }

    /// <summary>Gets or sets a value indicating whether a sync of this mapping has completed once; the first sync of each mapping is exempt from the new-items guard (DESIGN v2.7.2).</summary>
    public bool CompletedOnce { get; set; }

    /// <summary>Gets or sets how many episodes the last sync skipped for a missing or out-of-range season or episode number (TV libraries).</summary>
    public int SkippedNoNumber { get; set; }

    /// <summary>Gets or sets how many episodes the last sync skipped because their numbers overlapped an accepted one (<c>duplicate-peer</c>).</summary>
    public int SkippedDuplicate { get; set; }

    /// <summary>Gets or sets how many episodes the last sync dropped because their series was not in the listing.</summary>
    public int DroppedOrphans { get; set; }

    /// <summary>Gets or sets how many series, seasons or episodes the last sync dropped because a cap was reached.</summary>
    public int DroppedOverCap { get; set; }
}

/// <summary>
/// The record of the last sync.
/// </summary>
public sealed class LastSyncInfo
{
    /// <summary>Gets or sets when it started (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when it ended (UTC).</summary>
    public DateTime EndedUtc { get; set; }

    /// <summary>Gets or sets the outcome.</summary>
    public SyncResultKind Result { get; set; }

    /// <summary>Gets or sets how many stubs were added.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets how many stubs were updated.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many stubs were removed.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets how many removals are held back.</summary>
    public int HeldRemovals { get; set; }

    /// <summary>Gets or sets how many additions are held back.</summary>
    public int HeldAdditions { get; set; }

    /// <summary>Gets or sets a short error code (never peer text), or null.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the alert codes this sync raised (<see cref="SyncAlert"/> names), for the admin screens.</summary>
    public List<string> Alerts { get; set; } = [];
}

/// <summary>
/// How much of each budget the peer uses (DESIGN 5.10).
/// </summary>
public sealed class CatalogUsage
{
    /// <summary>Gets or sets the bytes Shoal wrote for this peer (stubs and images).</summary>
    public long DiskBytes { get; set; }

    /// <summary>Gets or sets the number of distinct people names kept.</summary>
    public int People { get; set; }

    /// <summary>Gets or sets the number of distinct genre names kept.</summary>
    public int Genres { get; set; }

    /// <summary>Gets or sets the number of distinct studio names kept.</summary>
    public int Studios { get; set; }
}

/// <summary>
/// A series whose new episodes are held by the new-items guard. The key is random and Home's own: the admin page names the group by it, never by a peer id.
/// </summary>
public sealed class HeldSeries
{
    /// <summary>Gets or sets the random key of the group (lower-case letters and digits).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the mapping key of the library.</summary>
    public string MappingKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's series id (32 hex characters).</summary>
    public string PeerSeriesId { get; set; } = string.Empty;

    /// <summary>Gets or sets the sanitized title, or empty.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year, or null.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets how many episodes are held.</summary>
    public int Episodes { get; set; }
}

/// <summary>
/// The overlap and storage report (DESIGN 5.8).
/// </summary>
public sealed class OverlapReport
{
    /// <summary>Gets or sets how many titles both have (<see cref="EntryState.DuplicateLocal"/>).</summary>
    public int OverlapCount { get; set; }

    /// <summary>Gets or sets the size of Home's own copies of those titles.</summary>
    public long OverlapLocalBytes { get; set; }

    /// <summary>Gets or sets the size of the peer's copies of those titles.</summary>
    public long OverlapPeerBytes { get; set; }

    /// <summary>Gets or sets how many titles Home gains without storing them.</summary>
    public int GainCount { get; set; }

    /// <summary>Gets or sets the size of the peer's copies of those titles.</summary>
    public long GainBytes { get; set; }

    /// <summary>Gets or sets how many of the friend's series Home already has (series-level dedup, DESIGN v2.7).</summary>
    public int SharedSeries { get; set; }

    /// <summary>Gets or sets how many episodes the friend has of those series (all hidden: the series is Home's).</summary>
    public int SharedSeriesEpisodes { get; set; }

    /// <summary>Gets or sets how many series Home gains.</summary>
    public int GainSeries { get; set; }

    /// <summary>Gets or sets how many episodes Home gains (part of <see cref="GainCount"/>).</summary>
    public int GainEpisodes { get; set; }

    /// <summary>Gets or sets when it was computed (UTC).</summary>
    public DateTime ComputedUtc { get; set; }
}

/// <summary>
/// The names kept to bound people, genres and studios per peer (DESIGN 5.10).
/// </summary>
public sealed class CatalogNames
{
    /// <summary>Gets or sets the people names.</summary>
    public List<string> People { get; set; } = [];

    /// <summary>Gets or sets the genre names.</summary>
    public List<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the studio names.</summary>
    public List<string> Studios { get; set; } = [];
}

/// <summary>
/// Tracks how long the same set of removals has been held, for the automatic release (3 complete syncs over at least 24 h).
/// </summary>
public sealed class HeldRemovalTracker
{
    /// <summary>Gets or sets when this set was first held (UTC).</summary>
    public DateTime FirstUtc { get; set; }

    /// <summary>Gets or sets how many consecutive complete syncs held this same set.</summary>
    public int CompleteSyncs { get; set; }

    /// <summary>Gets or sets a fingerprint of the held set (sorted stub keys, hashed).</summary>
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary>
/// One title the peer has, as Home stores it. Every string is a sanitized value (DESIGN 5.5), never raw peer text.
/// </summary>
public sealed class CatalogEntry
{
    /// <summary>Gets or sets what this entry is (schema 4); absent in an older file, which reads as a movie.</summary>
    public EntryKind Kind { get; set; } = EntryKind.Movie;

    /// <summary>Gets or sets the stub key of the series entry this season or episode belongs to (re-derived and checked on load), or null.</summary>
    public string? ParentKey { get; set; }

    /// <summary>Gets or sets the season number of a season or an episode, 0..2100, or null.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number, 0..9999, or null.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets the last episode number of a multi-episode file (at least <see cref="Episode"/>, at most 99 more), or null.</summary>
    public int? EpisodeEnd { get; set; }

    /// <summary>Gets or sets the peer's series id (32 hex characters) of an episode or a season, or empty.</summary>
    public string PeerSeriesId { get; set; } = string.Empty;

    /// <summary>Gets or sets how many episodes the peer lists for a series entry (0..10,000), or null; it is what a series Home already has hides.</summary>
    public int? PeerEpisodeCount { get; set; }

    /// <summary>Gets or sets the folder of a series or season entry relative to the library folder (<c>Show</c> or <c>Show/Season 01</c>), or null. Containers have no stub path.</summary>
    public string? FolderRel { get; set; }

    /// <summary>Gets or sets a hash of the sanitized fields the stub, NFO and images were written from, so a peer-side Etag storm rewrites nothing (DESIGN v2.7.2), or null.</summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="FriendLabel.Version"/> the entry's NFO was last written with (PLAN v2.9 K48); 0 in an older file. A movie, series or
    /// episode entry below the current version is rewritten once. Seasons carry no label and keep 0.
    /// </summary>
    public int LabelVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the extra versions of this stub were removed because its title group has two or more servers (PLAN v2.9 K42);
    /// false in an older file. When the group falls back to one server, the title is looked at again so its extras come back.
    /// </summary>
    public bool ExtrasHidden { get; set; }

    /// <summary>Gets or sets the random key of the stub (16 lower-case letters and digits).</summary>
    public string StubKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's item id (32 hex characters).</summary>
    public string PeerItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's media source id (32 hex characters).</summary>
    public string PeerMediaSourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the mapping key of the library.</summary>
    public string MappingKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's etag of the item, as listed.</summary>
    public string PeerEtag { get; set; } = string.Empty;

    /// <summary>Gets or sets the image tags the stored images came from (<c>Primary</c>, <c>Backdrop</c>).</summary>
    public Dictionary<string, string> ImageTags { get; set; } = [];

    /// <summary>Gets or sets the canonical provider ids (<c>Tmdb</c>, <c>Imdb</c>, <c>Tvdb</c>).</summary>
    public Dictionary<string, string> ProviderIds { get; set; } = [];

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year, or null.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the size of the file on the peer, or 0.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the bitrate, or 0.</summary>
    public int Bitrate { get; set; }

    /// <summary>Gets or sets the validated container list, or null.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video codec, or null.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the width, or 0.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the height, or 0.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the duration in ticks, or 0.</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public EntryState State { get; set; }

    /// <summary>Gets or sets the stub key of the entry this one duplicates, or null.</summary>
    public string? DuplicateOf { get; set; }

    /// <summary>Gets or sets the stub's <c>.strm</c> path relative to the library folder, or null while there is no stub.</summary>
    public string? StubRelPath { get; set; }

    /// <summary>Gets or sets when the title was first listed (UTC).</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when the title was last listed (UTC).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Gets or sets the local size of Home's own copy, for <see cref="EntryState.DuplicateLocal"/> entries (report only).</summary>
    public long LocalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the media sources of the title, one stub each (schema 3, DESIGN v2.6.4). The store fills it with the entry's own source when a
    /// schema-2 file is read, so a loaded entry always has at least one; until the sync writes more, the first is the primary (the entry itself).
    /// </summary>
    public List<CatalogSource> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets when the sync last asked the peer again for the trickplay levels of this title because it had none (a peer may generate its thumbnails later;
    /// DESIGN v2.6.5), or null. Optional: an older catalog file simply has none.
    /// </summary>
    public DateTime? TrickplayCheckedUtc { get; set; }
}

/// <summary>
/// One media source of a title and its stub (catalog schema 3). The first source is the primary: it carries the entry's stub key and path.
/// </summary>
public sealed class CatalogSource
{
    /// <summary>The most sources a title keeps (DESIGN v2.6.4).</summary>
    public const int MaxPerTitle = 4;

    /// <summary>Gets or sets the random key of this source's stub.</summary>
    public string StubKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's media source id (32 hex characters), or empty.</summary>
    public string PeerMediaSourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the label built by Home from numbers only (<c>1080p</c>, <c>720p</c>), or empty for the primary.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the validated container list, or null.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the video codec, or null.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the width, or 0.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the height, or 0.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the bitrate, or 0.</summary>
    public int Bitrate { get; set; }

    /// <summary>Gets or sets the size of the file on the peer, or 0.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the duration in ticks, or 0.</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Gets or sets the stub's <c>.strm</c> path relative to the library folder, or null while there is no stub.</summary>
    public string? StubRelPath { get; set; }

    /// <summary>Gets or sets the trickplay levels the peer has for this source (at most 4 widths; DESIGN v2.6.5).</summary>
    public List<TrickplayLevel> Trickplay { get; set; } = [];
}

/// <summary>
/// One trickplay width of a source, as validated at sync (catalog schema 3, DESIGN v2.6.5).
/// </summary>
public sealed class TrickplayLevel
{
    /// <summary>The most levels a source keeps.</summary>
    public const int MaxPerSource = 4;

    /// <summary>Gets or sets the thumbnail width, 1..1024.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the thumbnail height, 1..1024.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the thumbnails per tile row, 1..32.</summary>
    public int TileWidth { get; set; }

    /// <summary>Gets or sets the thumbnails per tile column, 1..32.</summary>
    public int TileHeight { get; set; }

    /// <summary>Gets or sets the number of thumbnails, 1..100,000.</summary>
    public int ThumbnailCount { get; set; }

    /// <summary>Gets or sets the milliseconds between thumbnails, 1..3,600,000.</summary>
    public int IntervalMs { get; set; }

    /// <summary>Gets or sets the bandwidth, 0..100,000,000.</summary>
    public int Bandwidth { get; set; }

    /// <summary>
    /// Whether every number is inside its range and one tile image stays within 16 megapixels and 8192 pixels on a side (DESIGN v2.6.5).
    /// </summary>
    /// <returns>True when the level may be kept.</returns>
    public bool IsValid()
    {
        if (Width is < 1 or > 1024 || Height is < 1 or > 1024 || TileWidth is < 1 or > 32 || TileHeight is < 1 or > 32
            || ThumbnailCount is < 1 or > 100_000 || IntervalMs is < 1 or > 3_600_000 || Bandwidth is < 0 or > 100_000_000)
        {
            return false;
        }

        var tileW = (long)Width * TileWidth;
        var tileH = (long)Height * TileHeight;
        return tileW <= 8192 && tileH <= 8192 && tileW * tileH <= 16_000_000;
    }
}

/// <summary>
/// <c>peers/&lt;peerKey&gt;/catalog.json</c> (DESIGN 3.3). Admin-only: it holds the peer's item ids.
/// </summary>
public sealed class CatalogDocument
{
    /// <summary>The schema this build writes (3 adds <c>sources</c> and <c>trickplay</c>, DESIGN v2.6.4; 4 adds the entry <c>kind</c>, parents, numbers and container entries, DESIGN v2.7.2).</summary>
    public const int CurrentSchema = 4;

    /// <summary>The oldest schema this build still reads (it is migrated in memory and written as the current one; nothing on disk is renamed).</summary>
    public const int OldestReadableSchema = 2;

    /// <summary>The most playable entries (movies and episodes) a peer may have (DESIGN 5.10). Series and season entries do not count toward it (<see cref="TvLimits.MaxContainerEntries"/>).</summary>
    public const int MaxEntries = 50_000;

    /// <summary>Gets or sets the schema number.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Gets or sets the peer key this file belongs to.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a sync has completed once (the first sync is exempt from the new-items guard).</summary>
    public bool CompletedOnce { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stubs of this catalog match the <c>AlternateVersionsEnabled</c> switch (DESIGN v2.6.4): set after a
    /// complete sync with the switch on (every title was looked at once) and cleared by a sync with it off (the extras were removed).
    /// </summary>
    public bool AlternateVersionsApplied { get; set; }

    /// <summary>Gets or sets the mapped libraries.</summary>
    public List<CatalogLibrary> Libraries { get; set; } = [];

    /// <summary>Gets or sets the record of the last sync, or null before the first.</summary>
    public LastSyncInfo? LastSync { get; set; }

    /// <summary>Gets or sets the stub keys whose removal is held back.</summary>
    public List<string> HeldRemovals { get; set; } = [];

    /// <summary>Gets or sets the peer item ids whose addition is held back.</summary>
    public List<string> HeldAdditions { get; set; } = [];

    /// <summary>Gets or sets the peer item ids (series and episodes of TV libraries) whose addition is held back. Apart from <see cref="HeldAdditions"/>, which the movie sync prunes against its own listing.</summary>
    public List<string> TvHeldAdditions { get; set; } = [];

    /// <summary>Gets or sets the series whose additions are held back (one group per series, so the admin can release one alone; schema 4, TV).</summary>
    public List<HeldSeries> HeldSeries { get; set; } = [];

    /// <summary>Gets or sets how long the held removals have been the same.</summary>
    public HeldRemovalTracker? HeldTracker { get; set; }

    /// <summary>Gets or sets how long the held TV removals (episodes) have been the same, for their own automatic release (schema 4).</summary>
    public HeldRemovalTracker? TvHeldTracker { get; set; }

    /// <summary>Gets or sets the budgets.</summary>
    public CatalogUsage Usage { get; set; } = new();

    /// <summary>Gets or sets the names kept for the people, genre and studio caps.</summary>
    public CatalogNames Names { get; set; } = new();

    /// <summary>Gets or sets the overlap report.</summary>
    public OverlapReport? Report { get; set; }

    /// <summary>Gets or sets the peer item ids whose details were not fetched yet; the next run continues with them (schema 4, TV).</summary>
    public List<string> PendingDetail { get; set; } = [];

    /// <summary>Gets or sets the stub keys whose images were not fetched yet; the next run continues with them (schema 4, TV).</summary>
    public List<string> ImagesPending { get; set; } = [];

    /// <summary>Gets or sets how many image follow-up runs came one after the other (at most <see cref="TvLimits.MaxImageFollowUps"/>).</summary>
    public int ImageFollowUps { get; set; }

    /// <summary>Gets or sets the entries.</summary>
    public List<CatalogEntry> Entries { get; set; } = [];
}
