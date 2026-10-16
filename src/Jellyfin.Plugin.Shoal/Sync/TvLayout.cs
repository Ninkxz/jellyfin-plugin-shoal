using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The pure naming and arrangement rules of the TV stub layout (DESIGN v2.7.1). No file is touched. A season folder and an episode file stem come from integers
/// alone, so no peer text is in them (an episode titled "S09E09" or a series called "24" cannot confuse Jellyfin's parser, and there is nothing to traverse
/// with); the series folder goes through <see cref="PathSanitizer"/>.
/// </summary>
public sealed class TvLayout : ITvLayout
{
    /// <inheritdoc />
    public string SeriesFolder(SanitizedSeries series, string stubKey, ISet<string> takenNormalized)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(takenNormalized);
        ArgumentException.ThrowIfNullOrEmpty(stubKey);

        // PathSanitizer builds "Title (Year) [shoal-key8]" when it is given no ids; the id tag of a series is tvdb, then tmdb, then imdb, else that shoal tag.
        var plain = PathSanitizer.FolderName(series.Title, series.Year, default, stubKey);
        var tagAt = plain.LastIndexOf(" [shoal-", StringComparison.Ordinal);
        var head = tagAt > 0 ? plain[..tagAt] : plain;
        var tag = series.ProviderIds switch
        {
            { Tvdb: { } tvdb } => " [tvdbid-" + tvdb.ToString(CultureInfo.InvariantCulture) + "]",
            { Tmdb: { } tmdb } => " [tmdbid-" + tmdb.ToString(CultureInfo.InvariantCulture) + "]",
            { Imdb: { } imdb } => " [imdbid-" + imdb + "]",
            _ => tagAt > 0 ? plain[tagAt..] : string.Empty,
        };
        var name = head + tag;
        if (!takenNormalized.Contains(PathSanitizer.CollisionKey(name)))
        {
            return name;
        }

        var withKey = head + tag + " [shoal-" + stubKey[..Math.Min(8, stubKey.Length)] + "]";
        return takenNormalized.Contains(PathSanitizer.CollisionKey(withKey)) ? head + tag + " [shoal-" + stubKey + "]" : withKey;
    }

    /// <inheritdoc />
    public string SeasonFolder(int season)
    {
        if (season is < 0 or > TvLimits.MaxSeasonNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(season));
        }

        return "Season " + season.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public string EpisodeStem(int season, int episode, int? episodeEnd)
    {
        if (season is < 0 or > TvLimits.MaxSeasonNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(season));
        }

        if (episode is < 0 or > TvLimits.MaxEpisodeNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(episode));
        }

        var stem = "S" + season.ToString("00", CultureInfo.InvariantCulture) + "E" + episode.ToString("00", CultureInfo.InvariantCulture);
        if (episodeEnd is { } end)
        {
            if (end < episode || end > episode + TvLimits.MaxEpisodeSpan || end > TvLimits.MaxEpisodeNumber + TvLimits.MaxEpisodeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(episodeEnd));
            }

            if (end > episode)
            {
                stem += "-E" + end.ToString("00", CultureInfo.InvariantCulture);
            }
        }

        return stem;
    }

    /// <inheritdoc />
    public TvEpisodeSet Resolve(IReadOnlyList<SanitizedEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var accepted = new List<SanitizedEpisode>(episodes.Count);
        var lastEnd = new Dictionary<int, int>();
        var duplicates = 0;
        foreach (var episode in episodes
            .OrderBy(static e => e.Season)
            .ThenBy(static e => e.Number)
            .ThenBy(static e => e.NumberEnd ?? e.Number)
            .ThenBy(static e => e.PeerItemId))
        {
            // Sorted by start, so the accepted ranges of a season are disjoint and rising: one number says whether a new range overlaps any of them.
            if (lastEnd.TryGetValue(episode.Season, out var end) && episode.Number <= end)
            {
                duplicates++;
                continue;
            }

            lastEnd[episode.Season] = episode.NumberEnd ?? episode.Number;
            accepted.Add(episode);
        }

        return new TvEpisodeSet(accepted, duplicates);
    }

    /// <inheritdoc />
    public IReadOnlyList<TvMove> MatchSeriesMoves(IReadOnlyList<CatalogEntry> goneSeries, IReadOnlyList<SanitizedSeries> newSeries)
    {
        ArgumentNullException.ThrowIfNull(goneSeries);
        ArgumentNullException.ThrowIfNull(newSeries);

        // Indexes built once (Warden 12 M1): the new series by each strong key and, when it has no ids, by its weak key. A match is then a lookup, not a scan.
        var byStrong = new Dictionary<string, List<SanitizedSeries>>(StringComparer.Ordinal);
        var byWeak = new Dictionary<string, List<SanitizedSeries>>(StringComparer.Ordinal);
        foreach (var n in newSeries)
        {
            if (n.ProviderIds.HasAny)
            {
                foreach (var key in n.ProviderIds.DedupKeys())
                {
                    Add(byStrong, key, n);
                }
            }
            else
            {
                Add(byWeak, Weak(n.Title, n.Year), n);
            }
        }

        var matches = new Dictionary<string, List<SanitizedSeries>>(StringComparer.Ordinal);
        var wanted = new Dictionary<Guid, int>();
        foreach (var gone in goneSeries)
        {
            var ids = MetadataSanitizer.CanonicalProviderIds(gone.ProviderIds);
            var candidates = new List<SanitizedSeries>();
            var seen = new HashSet<Guid>();
            if (ids.HasAny)
            {
                foreach (var key in ids.DedupKeys())
                {
                    if (byStrong.TryGetValue(key, out var list))
                    {
                        candidates.AddRange(list.Where(c => seen.Add(c.PeerItemId)));
                    }
                }
            }
            else if (byWeak.TryGetValue(Weak(gone.Title, gone.Year), out var list))
            {
                candidates.AddRange(list.Where(c => seen.Add(c.PeerItemId)));
            }

            matches[gone.StubKey] = candidates;
            foreach (var c in candidates)
            {
                wanted[c.PeerItemId] = wanted.GetValueOrDefault(c.PeerItemId) + 1;
            }
        }

        var moves = new List<TvMove>();
        var taken = new HashSet<Guid>();
        foreach (var gone in goneSeries)
        {
            var candidates = matches[gone.StubKey];

            // Unique on both sides: one new series for this one, and no other gone series wants the same new series.
            if (candidates.Count == 1 && !taken.Contains(candidates[0].PeerItemId) && wanted[candidates[0].PeerItemId] == 1)
            {
                taken.Add(candidates[0].PeerItemId);
                moves.Add(new TvMove(gone.StubKey, candidates[0].PeerItemId));
            }
        }

        return moves;
    }

    /// <inheritdoc />
    public IReadOnlyList<TvMove> MatchEpisodeMoves(IReadOnlyList<CatalogEntry> goneEpisodes, IReadOnlyList<SanitizedEpisode> newEpisodes)
    {
        ArgumentNullException.ThrowIfNull(goneEpisodes);
        ArgumentNullException.ThrowIfNull(newEpisodes);
        var moves = new List<TvMove>();
        var fresh = new Dictionary<(int Season, int Number, int? End), List<SanitizedEpisode>>();
        foreach (var n in newEpisodes)
        {
            var slot = (n.Season, n.Number, n.NumberEnd);
            if (!fresh.TryGetValue(slot, out var list))
            {
                fresh[slot] = list = [];
            }

            list.Add(n);
        }

        var numbered = goneEpisodes.Where(static e => e.Season is not null && e.Episode is not null).ToList();
        var goneCount = new Dictionary<(int Season, int Number, int? End), int>();
        foreach (var o in numbered)
        {
            var slot = SlotOf(o);
            goneCount[slot] = goneCount.GetValueOrDefault(slot) + 1;
        }

        foreach (var gone in numbered)
        {
            var slot = SlotOf(gone);
            if (fresh.TryGetValue(slot, out var same) && same.Count == 1 && goneCount[slot] == 1)
            {
                moves.Add(new TvMove(gone.StubKey, same[0].PeerItemId));
            }
        }

        return moves;
    }

    private static (int Season, int Number, int? End) SlotOf(CatalogEntry e) =>
        (e.Season!.Value, e.Episode!.Value, e.EpisodeEnd is { } end && end > e.Episode ? end : null);

    private static void Add(Dictionary<string, List<SanitizedSeries>> index, string key, SanitizedSeries series)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(series);
    }

    private static string Weak(string title, int? year) => PathSanitizer.CollisionKey(title) + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}
