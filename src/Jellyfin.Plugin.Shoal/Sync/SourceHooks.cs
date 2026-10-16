using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Chooses which of a peer item's validated media sources Shoal keeps (DESIGN v2.6.4, K28). <see cref="MetadataSanitizer"/> calls it after its own
/// per-source checks; the default keeps what the sanitizer found, which is v1 behavior.
/// </summary>
public interface ISourceSanitizer
{
    /// <summary>Gets how many of the peer's sources are examined (8 in v1; 64 when alternate versions are on).</summary>
    int MaxExamined => 8;

    /// <summary>
    /// Refines the list of validated sources.
    /// </summary>
    /// <param name="dto">The peer's item (hostile input; read only what the implementation validates).</param>
    /// <param name="sources">The sources that passed the sanitizer's checks (at most 8).</param>
    /// <returns>The sources to keep, in order; the first is the primary.</returns>
    IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources);
}

/// <summary>
/// Reads the trickplay levels of one peer source (DESIGN v2.6.5, K30). The default reads none, which is v1 behavior.
/// </summary>
public interface ITrickplaySanitizer
{
    /// <summary>
    /// Reads and validates the trickplay levels of one source.
    /// </summary>
    /// <param name="dto">The peer's item (hostile input).</param>
    /// <param name="peerSourceId">The peer's media source id.</param>
    /// <returns>The valid levels, at most four.</returns>
    IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId);
}

/// <summary>
/// The default <see cref="ISourceSanitizer"/>: every validated source is kept as found.
/// </summary>
public sealed class KeepSources : ISourceSanitizer
{
    /// <summary>Gets the only instance.</summary>
    public static KeepSources Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<SanitizedSource> Refine(BaseItemDto dto, IReadOnlyList<SanitizedSource> sources) => sources;
}

/// <summary>
/// The default <see cref="ITrickplaySanitizer"/>: no levels.
/// </summary>
public sealed class NoTrickplay : ITrickplaySanitizer
{
    /// <summary>Gets the only instance.</summary>
    public static NoTrickplay Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<SanitizedTrickplay> Read(BaseItemDto dto, Guid peerSourceId) => [];
}

/// <summary>
/// The default TV services (DESIGN v2.7, K34): they do nothing, which is v1 behavior with <c>TvShowsEnabled</c> off. K35, K36 and K37 Replace them.
/// </summary>
public sealed class NoTv : ITvSanitizer, ITvLayout, ITvNfoWriter, IPeerTvReader, ITvPlanner, ITvImageWork
{
    /// <summary>Gets the only instance.</summary>
    public static NoTv Instance { get; } = new();

    /// <inheritdoc />
    public SanitizedSeries? Series(BaseItemDto dto) => null;

    /// <inheritdoc />
    public SanitizedSeason? Season(BaseItemDto dto) => null;

    /// <inheritdoc />
    public TvEpisodeResult Episode(BaseItemDto dto) => new(null, TvSkip.NoNumber);

    /// <inheritdoc />
    public string SeriesFolder(SanitizedSeries series, string stubKey, ISet<string> takenNormalized) => "shoal-" + (stubKey.Length >= 8 ? stubKey[..8] : stubKey);

    /// <inheritdoc />
    public string SeasonFolder(int season) => "Season " + season.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string EpisodeStem(int season, int episode, int? episodeEnd) =>
        "S" + season.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "E" + episode.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
        + (episodeEnd is { } end ? "-E" + end.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

    /// <inheritdoc />
    public TvEpisodeSet Resolve(IReadOnlyList<SanitizedEpisode> episodes) => new(episodes, 0);

    /// <inheritdoc />
    public IReadOnlyList<TvMove> MatchSeriesMoves(IReadOnlyList<CatalogEntry> goneSeries, IReadOnlyList<SanitizedSeries> newSeries) => [];

    /// <inheritdoc />
    public IReadOnlyList<TvMove> MatchEpisodeMoves(IReadOnlyList<CatalogEntry> goneEpisodes, IReadOnlyList<SanitizedEpisode> newEpisodes) => [];

    /// <inheritdoc />
    string ITvNfoWriter.Series(SanitizedSeries series, IReadOnlyList<string> tags) => string.Empty;

    /// <inheritdoc />
    string ITvNfoWriter.Season(SanitizedSeason season, IReadOnlyList<string> tags) => string.Empty;

    /// <inheritdoc />
    string ITvNfoWriter.Episode(SanitizedEpisode episode, IReadOnlyList<string> tags) => string.Empty;

    /// <inheritdoc />
    public Task<TvListings> ListAsync(PeerEndpoint peer, Guid peerLibraryId, CancellationToken cancellationToken) =>
        Task.FromResult(new TvListings([], [], [], SeriesComplete: false, SeasonsComplete: false, EpisodesComplete: false));

    /// <inheritdoc />
    public Task<IReadOnlyList<BaseItemDto>> DetailsAsync(PeerEndpoint peer, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BaseItemDto>>([]);

    /// <inheritdoc />
    public TvPlan Plan(TvPlanInput input) => TvPlan.Empty;

    /// <inheritdoc />
    public IReadOnlyList<TvImageJob> Queue(IReadOnlyList<CatalogEntry> entries, IReadOnlyCollection<string> pendingKeys, bool episodeThumbs) => [];
}
