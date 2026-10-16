using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The file names of the images of a TV library (DESIGN v2.7.1): fixed words, or an episode stem built from integers plus <c>-thumb</c>. Never peer text.
/// </summary>
public static class TvImageNames
{
    /// <summary>
    /// The name of a series or season image, without extension.
    /// </summary>
    /// <param name="kind">Any kind but <see cref="TvImageKind.EpisodeThumb"/>.</param>
    /// <returns><c>poster</c>, <c>backdrop</c>, <c>banner</c> or <c>landscape</c>.</returns>
    public static string For(TvImageKind kind) => kind switch
    {
        TvImageKind.SeriesPoster or TvImageKind.SeasonPoster => "poster",
        TvImageKind.SeriesBackdrop => "backdrop",
        TvImageKind.SeriesBanner => "banner",
        TvImageKind.SeriesLandscape => "landscape",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "An episode thumbnail is named after its episode."),
    };

    /// <summary>
    /// The name of an episode thumbnail, without extension.
    /// </summary>
    /// <param name="layout">The layout that builds the stem.</param>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The episode number.</param>
    /// <param name="episodeEnd">The last episode of a multi-episode file, or null.</param>
    /// <returns>For example <c>S01E02-E03-thumb</c>.</returns>
    public static string Thumb(ITvLayout layout, int season, int episode, int? episodeEnd)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.EpisodeStem(season, episode, episodeEnd) + "-thumb";
    }

    /// <summary>
    /// Whether a name is one this class makes (so a caller cannot smuggle a path through it).
    /// </summary>
    /// <param name="baseName">The name without extension.</param>
    /// <returns>True for the four fixed words and for <c>SnnEnn[-Enn]-thumb</c>.</returns>
    public static bool IsSafeBaseName(string? baseName) =>
        baseName is "poster" or "backdrop" or "banner" or "landscape"
        || (baseName is { Length: >= 9 and <= 32 } && baseName.EndsWith("-thumb", StringComparison.Ordinal) && baseName[0] == 'S'
            && baseName[..^6].All(static c => c is (>= '0' and <= '9') or 'S' or 'E' or '-'));
}

/// <summary>
/// Orders the image work of a TV library (DESIGN v2.7.3): series posters, then season posters, then series backdrops, banners and landscapes, then episode
/// thumbnails with the newest series first. Only the entries whose stub key is in <c>pendingKeys</c> get a job (the sync puts a key there when the entry is new or
/// its tags changed, and takes it out when its images are stored), so an unchanged library queues nothing. Widths and size caps are the design's.
/// </summary>
public sealed class TvImageWork : ITvImageWork
{
    /// <summary>The size cap of a banner.</summary>
    public const long BannerMaxBytes = 1_572_864;

    /// <summary>The size cap of an episode thumbnail.</summary>
    public const long ThumbMaxBytes = 2 * 1024 * 1024;

    private const long PosterMaxBytes = 2 * 1024 * 1024;
    private const long BackdropMaxBytes = 4 * 1024 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<TvImageJob> Queue(IReadOnlyList<CatalogEntry> entries, IReadOnlyCollection<string> pendingKeys, bool episodeThumbs)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(pendingKeys);
        var pending = pendingKeys as ISet<string> ?? new HashSet<string>(pendingKeys, StringComparer.Ordinal);
        var series = entries.Where(static e => e.Kind == EntryKind.Series).ToDictionary(static e => e.StubKey, StringComparer.Ordinal);
        var jobs = new List<(int Class, long Newest, int Season, int Episode, TvImageJob Job)>();
        foreach (var entry in entries.Where(e => pending.Contains(e.StubKey) && e.State is EntryState.Active or EntryState.HeldRemoval))
        {
            if (!Guid.TryParseExact(entry.PeerItemId, "N", out var peerId))
            {
                continue;
            }

            switch (entry.Kind)
            {
                case EntryKind.Series:
                    Add(jobs, entry, 1, TvImageKind.SeriesPoster, "Primary", 600, PosterMaxBytes, peerId);
                    Add(jobs, entry, 3, TvImageKind.SeriesBackdrop, "Backdrop", 1280, BackdropMaxBytes, peerId);
                    Add(jobs, entry, 3, TvImageKind.SeriesBanner, "Banner", 1000, BannerMaxBytes, peerId);
                    Add(jobs, entry, 3, TvImageKind.SeriesLandscape, "Thumb", 1000, BackdropMaxBytes, peerId);
                    break;
                case EntryKind.Season:
                    Add(jobs, entry, 2, TvImageKind.SeasonPoster, "Primary", 400, PosterMaxBytes, peerId);
                    break;
                case EntryKind.Episode when episodeThumbs:
                    var newest = entry.ParentKey is { } parent && series.TryGetValue(parent, out var owner) ? owner.FirstSeenUtc.Ticks : 0;
                    Add(jobs, entry, 4, TvImageKind.EpisodeThumb, "Primary", 480, ThumbMaxBytes, peerId, newest);
                    break;
            }
        }

        return [.. jobs.OrderBy(static j => j.Class).ThenByDescending(static j => j.Newest).ThenBy(static j => j.Season).ThenBy(static j => j.Episode).Select(static j => j.Job)];
    }

    private static void Add(List<(int Class, long Newest, int Season, int Episode, TvImageJob Job)> jobs, CatalogEntry entry, int priority, TvImageKind kind, string tagKey, int width, long maxBytes, Guid peerId, long newest = 0)
    {
        if (entry.ImageTags.TryGetValue(tagKey, out var tag) && MetadataSanitizer.ImageTag(tag) is { } safe)
        {
            jobs.Add((priority, newest, entry.Season ?? 0, entry.Episode ?? 0, new TvImageJob(entry.StubKey, kind, peerId, safe, width, maxBytes)));
        }
    }
}

/// <summary>
/// What a run of the image stage did.
/// </summary>
/// <param name="Stored">Images written.</param>
/// <param name="Refused">Images the peer sent that were refused (wrong type, too large, bad header); they are not retried this run.</param>
/// <param name="Pending">The stub keys whose images are still to fetch (the budget ran out, or the peer stopped answering).</param>
/// <param name="QuotaReached">Whether the disk quota stopped the stage; the titles stay, with placeholder art, and nothing is pending.</param>
public sealed record TvImageRun(int Stored, int Refused, IReadOnlyList<string> Pending, bool QuotaReached);

/// <summary>
/// Runs the image stage of a TV sync (DESIGN v2.7.3): the jobs of <see cref="ITvImageWork"/> one after another, within a time budget of its own (10 minutes), then
/// stops and hands back the keys that are left for the next run. A peer that stops answering ends the stage early with everything left pending; a full quota ends it
/// with nothing pending (placeholders).
/// </summary>
public sealed class TvImageRunner
{
    /// <summary>The time budget of the image stage of one run.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(10);

    private readonly ImageFetcher _fetcher;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvImageRunner"/> class.
    /// </summary>
    /// <param name="fetcher">The image fetcher.</param>
    /// <param name="time">The clock.</param>
    public TvImageRunner(ImageFetcher fetcher, TimeProvider time)
    {
        _fetcher = fetcher;
        _time = time;
    }

    /// <summary>
    /// Runs the jobs until they are done, the budget is spent, the peer stops answering or the quota is full.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="jobs">The jobs in order (<see cref="ITvImageWork.Queue"/>).</param>
    /// <param name="locate">Where a job's image goes: the existing folder and the file name without extension, or null when the stub is gone.</param>
    /// <param name="stubRoot">The validated StubRoot.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <param name="budget">The time budget, or null for ten minutes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was done and what is left.</returns>
    public async Task<TvImageRun> RunAsync(PeerEndpoint peer, IReadOnlyList<TvImageJob> jobs, Func<TvImageJob, (string Folder, string BaseName)?> locate, string stubRoot, DiskQuota quota, TimeSpan? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(quota);
        var started = _time.GetUtcNow();
        var limit = budget ?? DefaultBudget;
        var stored = 0;
        var refused = 0;
        var left = new HashSet<string>(StringComparer.Ordinal);
        var quotaFull = false;
        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            if (_time.GetUtcNow() - started >= limit)
            {
                left.UnionWith(jobs.Skip(i).Select(static j => j.StubKey));
                break;
            }

            if (locate(job) is not { } place)
            {
                continue;
            }

            var result = await _fetcher.FetchTvAsync(peer, job, place.BaseName, stubRoot, place.Folder, quota, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ImageOutcome.Stored:
                    stored++;
                    break;
                case ImageOutcome.QuotaReached:
                    quotaFull = true;
                    break;
                case ImageOutcome.PeerError:
                    left.UnionWith(jobs.Skip(i).Select(static j => j.StubKey)); // the peer is not answering: try again next run
                    return Finish(stored, refused, left, quotaFull);
                case ImageOutcome.Skipped:
                    break;
                default:
                    refused++; // a bad image: no placeholder is lost, and retrying would fetch the same bytes
                    break;
            }

            if (quotaFull)
            {
                break; // titles stay with placeholder art; nothing is queued for a full disk
            }
        }

        return Finish(stored, refused, left, quotaFull);
    }

    // A key is pending while any of its jobs is left; a series has several jobs, and the ones already stored are fetched again next run (cheap, and correct).
    private static TvImageRun Finish(int stored, int refused, HashSet<string> left, bool quotaFull) =>
        new(stored, refused, quotaFull ? [] : [.. left.Order(StringComparer.Ordinal)], quotaFull);
}
