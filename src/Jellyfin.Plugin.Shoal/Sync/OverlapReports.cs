using System;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Computes the overlap and storage report of a catalog (DESIGN 5.8, v2.7 decision 3). Titles are movies and episodes; a series or a season is a folder and
/// never counts as a title. A series Home already has (<see cref="EntryState.DuplicateLocal"/>) hides all of the friend's episodes of it, so those are
/// reported apart: "your friend has K more episodes of N shows you already have".
/// </summary>
public static class OverlapReports
{
    /// <summary>
    /// Computes the report.
    /// </summary>
    /// <param name="document">The catalog.</param>
    /// <param name="now">The time of the sync (UTC).</param>
    /// <returns>The report.</returns>
    public static OverlapReport Compute(CatalogDocument document, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(document);
        var titles = document.Entries.Where(static e => e.Kind is EntryKind.Movie or EntryKind.Episode).ToList();
        var duplicates = titles.Where(static e => e.State == EntryState.DuplicateLocal).ToList();
        var active = titles.Where(static e => e.State == EntryState.Active).ToList();
        var sharedSeries = document.Entries.Where(static e => e.Kind == EntryKind.Series && e.State == EntryState.DuplicateLocal).ToList();
        return new OverlapReport
        {
            OverlapCount = duplicates.Count,
            OverlapLocalBytes = duplicates.Sum(static e => e.LocalSizeBytes),
            OverlapPeerBytes = duplicates.Sum(static e => e.SizeBytes),
            GainCount = active.Count,
            GainBytes = active.Sum(static e => e.SizeBytes),
            SharedSeries = sharedSeries.Count,
            SharedSeriesEpisodes = sharedSeries.Sum(static e => e.PeerEpisodeCount ?? 0),
            GainSeries = document.Entries.Count(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active),
            GainEpisodes = active.Count(static e => e.Kind == EntryKind.Episode),
            ComputedUtc = now,
        };
    }
}
