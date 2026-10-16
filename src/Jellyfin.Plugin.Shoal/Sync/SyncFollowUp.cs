using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// When the scheduler runs again (DESIGN v2.7.3): normally after the sync interval, but 10 minutes after a run that left images of a TV library to fetch, at most 12
/// times in a row (so a peer whose images never finish cannot make the server sync all day).
/// </summary>
public static class SyncFollowUp
{
    /// <summary>The most follow-up runs in a row.</summary>
    public const int MaxInARow = 12;

    /// <summary>The wait before a follow-up run.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Decides how long to wait after a run.
    /// </summary>
    /// <param name="reports">The reports of the run.</param>
    /// <param name="regular">The normal wait.</param>
    /// <param name="inARow">How many follow-ups have run in a row.</param>
    /// <returns>The wait, and the new count of follow-ups in a row.</returns>
    public static (TimeSpan Wait, int InARow) Next(IEnumerable<SyncReport> reports, TimeSpan regular, int inARow)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Any(static r => r.ImagesPending) && inARow < MaxInARow)
        {
            return (Delay, inARow + 1);
        }

        return (regular, 0);
    }
}
