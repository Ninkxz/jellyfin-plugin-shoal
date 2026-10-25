using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>Indexes full coverage and exact backups once per series.</summary>
internal sealed class TvCoverageIndex
{
    private readonly Dictionary<int, List<(int Start, int End)>> _seasons = [];
    private readonly Dictionary<(int Season, int Start, int End), string> _exact = [];

    /// <summary>Initializes a new instance of the <see cref="TvCoverageIndex"/> class.</summary>
    /// <param name="episodes">Known episodes in source preference order.</param>
    internal TvCoverageIndex(IReadOnlyList<TvOwnedEpisode> episodes)
    {
        foreach (var episode in episodes)
        {
            _exact.TryAdd((episode.Season, episode.Number, episode.End), episode.StubKey);
        }

        foreach (var season in episodes.GroupBy(static e => e.Season))
        {
            var merged = new List<(int Start, int End)>();
            foreach (var episode in season.OrderBy(static e => e.Number))
            {
                if (merged.Count > 0 && episode.Number <= merged[^1].End + 1)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, episode.End));
                }
                else
                {
                    merged.Add((episode.Number, episode.End));
                }
            }

            _seasons.Add(season.Key, merged);
        }
    }

    /// <summary>Checks for any intersection.</summary>
    /// <param name="episode">Candidate range.</param>
    /// <returns>Whether any number is already represented.</returns>
    internal bool Overlaps(SanitizedEpisode episode) => Find(episode.Season, episode.NumberEnd ?? episode.Number) is { } range && range.End >= episode.Number;

    /// <summary>Checks that all numbers are represented.</summary>
    /// <param name="episode">Candidate range.</param>
    /// <returns>Whether the union covers the candidate.</returns>
    internal bool Covers(SanitizedEpisode episode) => Find(episode.Season, episode.Number) is { } range && range.End >= (episode.NumberEnd ?? episode.Number);

    /// <summary>Finds an interchangeable backup without inventing a range.</summary>
    /// <param name="episode">Candidate range.</param>
    /// <returns>The exact backup stub key, or null.</returns>
    internal string? Exact(SanitizedEpisode episode) => _exact.GetValueOrDefault((episode.Season, episode.Number, episode.NumberEnd ?? episode.Number));

    private (int Start, int End)? Find(int season, int number)
    {
        if (!_seasons.TryGetValue(season, out var ranges))
        {
            return null;
        }

        var lo = 0;
        var hi = ranges.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (ranges[mid].Start <= number)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo > 0 ? ranges[lo - 1] : null;
    }
}
