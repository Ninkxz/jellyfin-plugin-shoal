using System;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Decides which titles without trickplay are asked about again in one sync (DESIGN v2.6.5, v2.7.8). A peer may generate its thumbnails long after a title was
/// synced and the etag need not change, so a title without trickplay is looked at again after a while, with a budget per sync so a big library never turns one
/// sync into thousands of extra requests: a movie once a day, 50 per sync; an episode once a week, 200 per sync.
/// </summary>
public sealed class TrickplayRecheck
{
    /// <summary>The most movies looked at in one sync.</summary>
    public const int MoviesPerSync = 50;

    /// <summary>The most episodes looked at in one sync.</summary>
    public const int EpisodesPerSync = 200;

    /// <summary>How long a movie waits between two looks.</summary>
    public static readonly TimeSpan MovieEvery = TimeSpan.FromHours(24);

    /// <summary>How long an episode waits between two looks.</summary>
    public static readonly TimeSpan EpisodeEvery = TimeSpan.FromDays(7);

    private readonly DateTime _now;
    private int _movies = MoviesPerSync;
    private int _episodes = EpisodesPerSync;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayRecheck"/> class for one sync.
    /// </summary>
    /// <param name="now">The time of the sync (UTC).</param>
    public TrickplayRecheck(DateTime now) => _now = now;

    /// <summary>
    /// Whether a title is due for another look, and if so spends one place of the budget and stamps the entry.
    /// </summary>
    /// <param name="entry">The entry; only a movie or an episode without trickplay can be due.</param>
    /// <returns>True when the title should be asked about again in this sync.</returns>
    public bool Due(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var episode = entry.Kind == EntryKind.Episode;
        if (entry.Kind is not (EntryKind.Movie or EntryKind.Episode)
            || entry.Sources.Any(static s => s.Trickplay.Count > 0)
            || (episode ? _episodes : _movies) <= 0
            || _now - (entry.TrickplayCheckedUtc ?? entry.FirstSeenUtc) < (episode ? EpisodeEvery : MovieEvery))
        {
            return false;
        }

        if (episode)
        {
            _episodes--;
        }
        else
        {
            _movies--;
        }

        entry.TrickplayCheckedUtc = _now;
        return true;
    }
}
