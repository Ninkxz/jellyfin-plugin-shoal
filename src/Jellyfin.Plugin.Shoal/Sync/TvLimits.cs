using System;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The hard caps and fixed numbers of TV show support (DESIGN v2.7.2 to v2.7.4). The planner stops before the store; nothing here is configurable.
/// </summary>
public static class TvLimits
{
    /// <summary>The most series one peer may have; extra series are dropped, counted and alerted.</summary>
    public const int MaxSeriesPerPeer = 5_000;

    /// <summary>The most seasons one series may have.</summary>
    public const int MaxSeasonsPerSeries = 200;

    /// <summary>The most seasons one peer may have.</summary>
    public const int MaxSeasonsPerPeer = 25_000;

    /// <summary>The most episodes one season may have.</summary>
    public const int MaxEpisodesPerSeason = 2_000;

    /// <summary>The most episodes one series may have.</summary>
    public const int MaxEpisodesPerSeries = 10_000;

    /// <summary>The most actors written to a series.</summary>
    public const int MaxActorsPerSeries = 5;

    /// <summary>The highest season number (year-style seasons exist); the lowest is 0 (specials).</summary>
    public const int MaxSeasonNumber = 2_100;

    /// <summary>The highest episode number; the lowest is 0.</summary>
    public const int MaxEpisodeNumber = 9_999;

    /// <summary>The most episodes one file may span (<c>IndexNumberEnd</c> is at most the start plus this).</summary>
    public const int MaxEpisodeSpan = 99;

    /// <summary>The listing page size for all three listings.</summary>
    public const int PageSize = 200;

    /// <summary>The ids per details request.</summary>
    public const int DetailBatch = 50;

    /// <summary>The factor the movie guards' minimum counts are multiplied by when episodes are counted.</summary>
    public const int TvFactor = 4;

    /// <summary>The fewest gone series that can trip the mass-deletion guard, whatever the percentage says.</summary>
    public const int GoneSeriesFloor = 3;

    /// <summary>The fewest new series that can trip the new-items guard, whatever the percentage says.</summary>
    public const int NewSeriesFloor = 10;

    /// <summary>The most series folders a sync reports one by one; more than this and it reports the library root once.</summary>
    public const int MaxSeriesRefreshes = 50;

    /// <summary>The most image follow-up runs in a row.</summary>
    public const int MaxImageFollowUps = 12;

    /// <summary>The most container entries (series and seasons) a catalog keeps. They do not count toward <see cref="CatalogDocument.MaxEntries"/>.</summary>
    public const int MaxContainerEntries = MaxSeriesPerPeer + MaxSeasonsPerPeer;

    /// <summary>The time the image stage may use in one run.</summary>
    public static readonly TimeSpan ImageBudget = TimeSpan.FromMinutes(10);

    /// <summary>The gap before an image follow-up run.</summary>
    public static readonly TimeSpan ImageFollowUpDelay = TimeSpan.FromMinutes(10);
}
