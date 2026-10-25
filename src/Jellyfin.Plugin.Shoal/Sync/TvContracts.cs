#pragma warning disable SA1201 // The contract file reads top to bottom: each type is followed by the types it uses.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Shoal.Sync;

// The TV contracts of DESIGN v2.7 (K34). Every interface here has a do-nothing default in SourceHooks.cs (registered with TryAdd in Registrations/Tv.cs); K35
// (sanitizer, layout, NFO, dedup keys), K36 (reader, planner, writer) and K37 (image work) Replace them. A task may add members with defaults or new records,
// and never changes or removes a member another task builds against.

/// <summary>Why a peer episode was not taken (counted, never shown with peer text).</summary>
public enum TvSkip
{
    /// <summary>Taken.</summary>
    None,

    /// <summary>The season or episode number is missing or out of range (<c>skippedNoNumber</c>).</summary>
    NoNumber,

    /// <summary>The peer's item is a virtual (missing) episode.</summary>
    Virtual,

    /// <summary>An id is not a GUID, or the series id is missing.</summary>
    BadId,

    /// <summary>Its numbers overlap an accepted episode (<c>duplicate-peer</c>).</summary>
    DuplicatePeer,

    /// <summary>Its series is not in this run's series listing.</summary>
    Orphan,

    /// <summary>A cap of <see cref="TvLimits"/> was reached.</summary>
    OverCap,
}

/// <summary>
/// The answer of <see cref="ITvSanitizer.Episode"/>.
/// </summary>
/// <param name="Episode">The sanitized episode, or null when it was skipped.</param>
/// <param name="Skip">Why it was skipped, or <see cref="TvSkip.None"/>.</param>
public readonly record struct TvEpisodeResult(SanitizedEpisode? Episode, TvSkip Skip);

/// <summary>
/// Turns the peer's hostile series, season and episode items into <see cref="SanitizedSeries"/>, <see cref="SanitizedSeason"/> and <see cref="SanitizedEpisode"/>
/// by the rules of DESIGN v2.7.4. Only an implementation of this creates those records.
/// </summary>
public interface ITvSanitizer
{
    /// <summary>Sanitizes a series.</summary>
    /// <param name="dto">The peer's item (hostile input).</param>
    /// <returns>The series, or null when it has no usable id.</returns>
    SanitizedSeries? Series(BaseItemDto dto);

    /// <summary>Sanitizes a season.</summary>
    /// <param name="dto">The peer's item (hostile input).</param>
    /// <returns>The season, or null when it has no usable id or number.</returns>
    SanitizedSeason? Season(BaseItemDto dto);

    /// <summary>Sanitizes an episode.</summary>
    /// <param name="dto">The peer's item (hostile input).</param>
    /// <returns>The episode, or the reason it was skipped.</returns>
    TvEpisodeResult Episode(BaseItemDto dto);
}

/// <summary>
/// The episodes that survive the overlap rule (sort by season, episode, end, peer id; accept one only if its range overlaps no accepted one).
/// </summary>
/// <param name="Accepted">The accepted episodes, in that order.</param>
/// <param name="Held">Crossing episode ranges requiring corrected numbering.</param>
/// <param name="DuplicatePeer">How many were skipped as <c>duplicate-peer</c>.</param>
public sealed record TvEpisodeSet(IReadOnlyList<SanitizedEpisode> Accepted, int DuplicatePeer, IReadOnlyList<SanitizedEpisode>? Held = null);

/// <summary>
/// A series (or an episode inside a moved series) that the peer re-imported under a new id: the stub, the local id and the watch state stay.
/// </summary>
/// <param name="StubKey">The stub key of the existing entry.</param>
/// <param name="NewPeerItemId">The peer's new id for the same series or episode.</param>
public sealed record TvMove(string StubKey, Guid NewPeerItemId);

/// <summary>
/// The pure naming and arrangement rules of the stub layout (DESIGN v2.7.1). No file is touched. Names come from integers, from
/// <c>PathSanitizer</c> (the series folder) or from a fixed table, never from peer text.
/// </summary>
public interface ITvLayout
{
    /// <summary>Builds the series folder name: the sanitized title, the year, and the first provider id in the order tvdb, tmdb, imdb, else <c>shoal-&lt;key8&gt;</c>.</summary>
    /// <param name="series">The series.</param>
    /// <param name="stubKey">The series entry's stub key (its first eight characters name a series without ids).</param>
    /// <param name="takenNormalized">The folder names already used, case- and normalization-insensitively; the result is not in it and is not added.</param>
    /// <returns>A folder name that is one plain segment.</returns>
    string SeriesFolder(SanitizedSeries series, string stubKey, ISet<string> takenNormalized);

    /// <summary>Builds the season folder name from the number: <c>Season 00</c>, <c>Season 01</c>, <c>Season 2024</c>.</summary>
    /// <param name="season">0..2100.</param>
    /// <returns>The name.</returns>
    string SeasonFolder(int season);

    /// <summary>Builds the episode file stem from integers: <c>S01E01</c>, or <c>S01E02-E03</c> for a multi-episode file.</summary>
    /// <param name="season">0..2100.</param>
    /// <param name="episode">0..9999.</param>
    /// <param name="episodeEnd">The last episode of a multi-episode file, or null.</param>
    /// <returns>The stem, without extension.</returns>
    string EpisodeStem(int season, int episode, int? episodeEnd);

    /// <summary>Applies the overlap and duplicate rule to the episodes of one series.</summary>
    /// <param name="episodes">The sanitized episodes of the series.</param>
    /// <returns>The accepted ones and the count of the others.</returns>
    TvEpisodeSet Resolve(IReadOnlyList<SanitizedEpisode> episodes);

    /// <summary>Finds the series that were re-imported on the peer: a gone series whose canonical provider ids match a new series, or an exact unique title and year match.</summary>
    /// <param name="goneSeries">The series entries the peer no longer lists.</param>
    /// <param name="newSeries">The series the peer lists that Home has no entry for.</param>
    /// <returns>The moves.</returns>
    IReadOnlyList<TvMove> MatchSeriesMoves(IReadOnlyList<CatalogEntry> goneSeries, IReadOnlyList<SanitizedSeries> newSeries);

    /// <summary>Finds the episodes that were re-imported inside a same or moved series: a gone episode whose (season, episode, end) equals a new episode's.</summary>
    /// <param name="goneEpisodes">The episode entries the peer no longer lists.</param>
    /// <param name="newEpisodes">The episodes the peer lists that Home has no entry for.</param>
    /// <returns>The moves.</returns>
    IReadOnlyList<TvMove> MatchEpisodeMoves(IReadOnlyList<CatalogEntry> goneEpisodes, IReadOnlyList<SanitizedEpisode> newEpisodes);
}

/// <summary>
/// Writes the NFO files of DESIGN v2.7.1 with <c>XmlWriter</c> (<c>CheckCharacters</c> on). Pure: the XML text is returned, nothing is written. No URL, no id of the
/// peer and no token is ever in it.
/// </summary>
public interface ITvNfoWriter
{
    /// <summary>Writes <c>tvshow.nfo</c>.</summary>
    /// <param name="series">The series.</param>
    /// <param name="tags">The tags (<c>Shoal</c>, <c>Shoal: &lt;Peer&gt;</c>).</param>
    /// <returns>The XML.</returns>
    /// <param name="friendName">The friend the plot label names (2.9.2 K4), or null for the generic label.</param>
    string Series(SanitizedSeries series, IReadOnlyList<string> tags, string? friendName = null);

    /// <summary>Writes <c>season.nfo</c>.</summary>
    /// <param name="season">The season.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>The XML.</returns>
    string Season(SanitizedSeason season, IReadOnlyList<string> tags);

    /// <summary>Writes an episode's <c>.nfo</c> (<c>episodedetails</c>).</summary>
    /// <param name="episode">The episode.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>The XML.</returns>
    /// <param name="friendName">The friend the plot label names (2.9.2 K4), or null for the generic label.</param>
    string Episode(SanitizedEpisode episode, IReadOnlyList<string> tags, string? friendName = null);
}

/// <summary>
/// The three cheap listings of a mapping and whether each is complete (<c>|collected - Total| &lt;= max(5, 1%)</c> each). If one is partial, no removal happens
/// anywhere in the mapping.
/// </summary>
/// <param name="Series">The series items (hostile; not yet sanitized).</param>
/// <param name="Seasons">The season items.</param>
/// <param name="Episodes">The episode items.</param>
/// <param name="SeriesComplete">Whether the series listing is complete.</param>
/// <param name="SeasonsComplete">Whether the season listing is complete.</param>
/// <param name="EpisodesComplete">Whether the episode listing is complete.</param>
public sealed record TvListings(IReadOnlyList<BaseItemDto> Series, IReadOnlyList<BaseItemDto> Seasons, IReadOnlyList<BaseItemDto> Episodes, bool SeriesComplete, bool SeasonsComplete, bool EpisodesComplete)
{
    /// <summary>Gets a value indicating whether all three listings are complete.</summary>
    public bool Complete => SeriesComplete && SeasonsComplete && EpisodesComplete;
}

/// <summary>
/// Reads a TV library from a peer with stock Jellyfin calls and the share user's token (DESIGN v2.7.2). Every listing uses page size
/// <see cref="TvLimits.PageSize"/> and the same <c>PeerCallProfile</c>s as movies.
/// </summary>
public interface IPeerTvReader
{
    /// <summary>Lists the series, seasons and episodes of one peer library.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="peerLibraryId">The peer's library id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The listings.</returns>
    Task<TvListings> ListAsync(PeerEndpoint peer, Guid peerLibraryId, CancellationToken cancellationToken);

    /// <summary>Fetches the details of up to <see cref="TvLimits.DetailBatch"/> items.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="ids">The peer item ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The items the peer returned (hostile).</returns>
    Task<IReadOnlyList<BaseItemDto>> DetailsAsync(PeerEndpoint peer, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>The guard settings a plan is made with.</summary>
/// <param name="MassDeletionMinCount">The configured minimum count of the removal guard (the planner multiplies by <see cref="TvLimits.TvFactor"/>).</param>
/// <param name="MassDeletionPercent">The percentage of the removal guard.</param>
/// <param name="NewItemsMinCount">The configured minimum count of the additions guard (times <see cref="TvLimits.TvFactor"/>).</param>
/// <param name="NewItemsPercent">The percentage of the additions guard.</param>
public sealed record TvGuardSettings(int MassDeletionMinCount, int MassDeletionPercent, int NewItemsMinCount, int NewItemsPercent);

/// <summary>
/// What the planner works from.
/// </summary>
/// <param name="MappingKey">The mapping the plan is for.</param>
/// <param name="Existing">The catalog entries of this mapping (all kinds).</param>
/// <param name="Series">The sanitized series.</param>
/// <param name="Seasons">The sanitized seasons.</param>
/// <param name="Episodes">The sanitized episodes (before the overlap rule).</param>
/// <param name="Complete">Whether all three listings were complete.</param>
/// <param name="FirstSyncOfMapping">Whether this mapping has never completed a sync (exempt from the additions guard).</param>
/// <param name="LocalSeries">The complete identities of Home's own series outside the stub root.</param>
/// <param name="Guards">The guard settings.</param>
/// <param name="BypassRemovals">Whether held removals are released now.</param>
/// <param name="BypassAdditions">Whether held additions are released now.</param>
/// <param name="HeldAdditionIds">The peer item ids (32 hex) held back by an earlier sync; they stay held until released. Optional (K36).</param>
/// <param name="ReleasedSeries">Peer series ids the admin released by hand: their held additions and removals go through the guards (DESIGN v2.7.2, release works by series group). Optional.</param>
/// <param name="OtherPlayable">How many playable entries (movies and episodes) the catalog already holds in other mappings. The 50,000 cap is per catalog, so this mapping may have the rest (Warden 12 M2).</param>
/// <param name="PeerOwned">PLAN v2.9 K43: the series another (higher-ranked) server already shows, by dedup key, with the episodes it has. Optional.</param>
public sealed record TvPlanInput(
    string MappingKey,
    IReadOnlyList<CatalogEntry> Existing,
    IReadOnlyList<SanitizedSeries> Series,
    IReadOnlyList<SanitizedSeason> Seasons,
    IReadOnlyList<SanitizedEpisode> Episodes,
    bool Complete,
    bool FirstSyncOfMapping,
    IReadOnlyList<LocalSeries> LocalSeries,
    TvGuardSettings Guards,
    bool BypassRemovals,
    bool BypassAdditions,
    IReadOnlySet<string>? HeldAdditionIds = null,
    int OtherPlayable = 0,
    IReadOnlySet<Guid>? ReleasedSeries = null,
    IReadOnlyList<TvOwnedSeries>? PeerOwned = null);

/// <summary>A series whose additions are held back by the new-items guard, so the admin can release it alone.</summary>
/// <param name="PeerSeriesId">The peer's series id.</param>
/// <param name="Title">The sanitized title, or empty.</param>
/// <param name="Year">The year, or null.</param>
/// <param name="Episodes">How many episodes are held.</param>
public sealed record TvHeldGroup(Guid PeerSeriesId, string Title, int? Year, int Episodes);

/// <summary>What a plan skipped or dropped, for the admin screens.</summary>
/// <param name="SkippedNoNumber">Episodes without a usable season or episode number.</param>
/// <param name="SkippedVirtual">Virtual (missing) episodes.</param>
/// <param name="SkippedDuplicate">Episodes whose numbers overlap an accepted one.</param>
/// <param name="DroppedOrphans">Episodes whose series is not in the listing.</param>
/// <param name="DroppedOverCap">Series, seasons or episodes dropped for a cap.</param>
public sealed record TvCounts(int SkippedNoNumber, int SkippedVirtual, int SkippedDuplicate, int DroppedOrphans, int DroppedOverCap);

/// <summary>
/// The planner's decision for one mapping. It changes nothing by itself: <c>TvStubWriter</c> carries it out.
/// </summary>
/// <param name="AddSeries">Series to add (a series folder is written when its first episode is).</param>
/// <param name="AddSeasons">Seasons to add.</param>
/// <param name="AddEpisodes">Episodes to add.</param>
/// <param name="UpdateKeys">Stub keys whose sanitized content changed (the <c>contentHash</c> differs).</param>
/// <param name="RemoveKeys">Episode stub keys to remove now (containers go when empty).</param>
/// <param name="Moves">Series and episodes rebound in place.</param>
/// <param name="HeldRemovalKeys">Episode stub keys whose removal is held back.</param>
/// <param name="HeldAdditionPeerIds">Peer item ids (32 hex) whose addition is held back.</param>
/// <param name="DuplicateLocalSeriesKeys">Peer series ids (32 hex) hidden because Home has the series (<c>duplicate-local</c>).</param>
/// <param name="PendingDetail">Peer item ids whose details are still to be fetched.</param>
/// <param name="Counts">What was skipped or dropped.</param>
/// <param name="Alerts">Alert codes (<c>SyncAlert</c> names).</param>
/// <param name="HeldAdditionGroups">The series behind <paramref name="HeldAdditionPeerIds"/>, one per series, for release by series. Optional.</param>
/// <param name="HiddenEpisodes">PLAN v2.9 K43: episodes kept as hidden sources of another server's series (no stub). Optional.</param>
/// <param name="PeerOwnedSeries">PLAN v2.9 K43: peer series ids (32 hex) another server shows, to the stub key of that server's series. Optional.</param>
public sealed record TvPlan(
    IReadOnlyList<SanitizedSeries> AddSeries,
    IReadOnlyList<SanitizedSeason> AddSeasons,
    IReadOnlyList<SanitizedEpisode> AddEpisodes,
    IReadOnlyList<string> UpdateKeys,
    IReadOnlyList<string> RemoveKeys,
    IReadOnlyList<TvMove> Moves,
    IReadOnlyList<string> HeldRemovalKeys,
    IReadOnlyList<string> HeldAdditionPeerIds,
    IReadOnlyList<string> DuplicateLocalSeriesKeys,
    IReadOnlyList<string> PendingDetail,
    TvCounts Counts,
    IReadOnlyList<string> Alerts,
    IReadOnlyList<TvHeldGroup>? HeldAdditionGroups = null,
    IReadOnlyList<TvHiddenEpisode>? HiddenEpisodes = null,
    IReadOnlyDictionary<string, string>? PeerOwnedSeries = null)
{
    /// <summary>Gets the plan that does nothing.</summary>
    public static TvPlan Empty { get; } = new([], [], [], [], [], [], [], [], [], [], new TvCounts(0, 0, 0, 0, 0), []);
}

/// <summary>
/// Plans one mapping: diff, moves, orphan and cap handling, guards that count episodes, the partial-listing rule, <c>contentHash</c> and
/// <c>pendingDetail</c> (DESIGN v2.7.2). Pure: it reads its input and returns a plan.
/// </summary>
public interface ITvPlanner
{
    /// <summary>Makes the plan.</summary>
    /// <param name="input">What the planner works from.</param>
    /// <returns>The plan.</returns>
    TvPlan Plan(TvPlanInput input);
}

/// <summary>The kinds of image a TV library stores (DESIGN v2.7.3).</summary>
public enum TvImageKind
{
    /// <summary>A series poster (primary), width 600.</summary>
    SeriesPoster,

    /// <summary>A season poster, width 400.</summary>
    SeasonPoster,

    /// <summary>A series backdrop, width 1280.</summary>
    SeriesBackdrop,

    /// <summary>A series banner, width 1000, at most 1.5 MB.</summary>
    SeriesBanner,

    /// <summary>A series landscape (the peer's Thumb), width 1000.</summary>
    SeriesLandscape,

    /// <summary>An episode thumbnail (the peer's Primary), width 480, at most 2 MB, stored as <c>-thumb.jpg</c>.</summary>
    EpisodeThumb,
}

/// <summary>
/// One image to fetch.
/// </summary>
/// <param name="StubKey">The stub key of the series, season or episode entry the image belongs to.</param>
/// <param name="Kind">Which image.</param>
/// <param name="PeerItemId">The peer item the image is fetched from.</param>
/// <param name="Tag">The validated image tag.</param>
/// <param name="MaxWidth">The width to ask for.</param>
/// <param name="MaxBytes">The size cap of the answer.</param>
public sealed record TvImageJob(string StubKey, TvImageKind Kind, Guid PeerItemId, string Tag, int MaxWidth, long MaxBytes);

/// <summary>
/// Orders the image work of a TV library (DESIGN v2.7.3): series posters, season posters, then backdrops, banners and landscapes, then episode thumbnails
/// (newest series first). Fetching stays serial.
/// </summary>
public interface ITvImageWork
{
    /// <summary>Lists the images still to fetch, in order.</summary>
    /// <param name="entries">The entries of the mapping.</param>
    /// <param name="pendingKeys">The stub keys left over from earlier runs (they come first within their class).</param>
    /// <param name="episodeThumbs">Whether episode thumbnails are wanted (<c>TvEpisodeThumbs</c>).</param>
    /// <returns>The jobs.</returns>
    IReadOnlyList<TvImageJob> Queue(IReadOnlyList<CatalogEntry> entries, IReadOnlyCollection<string> pendingKeys, bool episodeThumbs);
}

/// <summary>
/// A series another server already shows (PLAN v2.9 K43): its owner, its series stub (the group's copy) and the episode numbers it has.
/// </summary>
/// <param name="OwnerPeerKey">The server that shows it.</param>
/// <param name="OwnerSeriesStubKey">Its series entry's stub key.</param>
/// <param name="SameFolder">Whether the owner writes into the same library folder as this mapping (then this server's other episodes join the series through
/// the library's series grouping; otherwise they stay hidden).</param>
/// <param name="Episodes">The owner's episodes.</param>
/// <param name="Identity">The complete provider identity of this candidate.</param>
public sealed record TvOwnedSeries(string OwnerPeerKey, string OwnerSeriesStubKey, bool SameFolder, IReadOnlyList<TvOwnedEpisode> Episodes, LocalSeries Identity)
{
    /// <summary>
    /// The owner's episode an episode overlaps (same season, number ranges meet), or null. Overlapping ranges keep the owner's numbering.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="number">The first episode number.</param>
    /// <param name="numberEnd">The last episode number, or null for one.</param>
    /// <returns>The owner's episode, or null.</returns>
    public TvOwnedEpisode? Overlap(int season, int number, int? numberEnd)
    {
        var end = numberEnd ?? number;
        return Episodes.FirstOrDefault(e => e.Season == season && e.Number <= end && number <= e.End);
    }
}

/// <summary>One episode of a series another server shows.</summary>
/// <param name="Season">The season.</param>
/// <param name="Number">The first episode number.</param>
/// <param name="End">The last episode number (the first for one).</param>
/// <param name="StubKey">Its stub key on that server.</param>
public sealed record TvOwnedEpisode(int Season, int Number, int End, string StubKey);

/// <summary>An episode kept as a hidden source (PLAN v2.9 K43).</summary>
/// <param name="Episode">The episode as listed.</param>
/// <param name="DuplicateOf">The owner's episode stub key it stands in for, or null when the owner has no such episode (legacy layout: it stays hidden).</param>
public sealed record TvHiddenEpisode(SanitizedEpisode Episode, string? DuplicateOf);
