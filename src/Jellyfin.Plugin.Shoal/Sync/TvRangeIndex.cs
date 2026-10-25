using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>Checks overlap against actual surviving stubs without quadratic scans.</summary>
internal sealed class TvRangeIndex
{
    private readonly Dictionary<(string Series, int? Season), (int Start, int End)[]> _ranges;

    /// <summary>Initializes a new instance of the <see cref="TvRangeIndex"/> class.</summary>
    /// <param name="entries">Surviving episode stubs.</param>
    internal TvRangeIndex(IEnumerable<CatalogEntry> entries)
    {
        _ranges = entries.Where(static e => e.Season is not null && e.Episode is not null)
            .GroupBy(static e => (e.PeerSeriesId, e.Season)).ToDictionary(static g => g.Key, static g =>
            {
                var ranges = g.OrderBy(static e => e.Episode).Select(static e => (Start: e.Episode!.Value, End: e.EpisodeEnd ?? e.Episode.Value)).ToArray();
                for (var i = 1; i < ranges.Length; i++)
                {
                    ranges[i].End = Math.Max(ranges[i].End, ranges[i - 1].End);
                }

                return ranges;
            });
    }

    /// <summary>Checks an incoming range against the surviving stubs.</summary>
    /// <param name="episode">The proposed addition.</param>
    /// <returns>Whether it would duplicate any represented episode.</returns>
    internal bool Overlaps(SanitizedEpisode episode)
    {
        if (!_ranges.TryGetValue((episode.PeerSeriesId.ToString("N"), episode.Season), out var ranges))
        {
            return false;
        }

        var lo = 0;
        var hi = ranges.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (ranges[mid].Start <= (episode.NumberEnd ?? episode.Number))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo > 0 && ranges[lo - 1].End >= episode.Number;
    }
}
