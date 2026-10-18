using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Tells whether a catalog sync of a server is running right now (a removal must not run behind one: the sync would recreate stubs and catalog files).
/// </summary>
public interface ISyncActivity
{
    /// <summary>
    /// Whether a sync of this server is running.
    /// </summary>
    /// <param name="peerKey">The server's handle.</param>
    /// <returns>True while one runs.</returns>
    bool IsSyncing(string peerKey);

    /// <summary>
    /// Ends the running sync of this server early (its time budget is spent, so it stops at the next checkpoint and reports what it did) and waits for it, up to a limit.
    /// </summary>
    /// <param name="peerKey">The server's handle.</param>
    /// <param name="wait">The longest wait.</param>
    /// <returns>A task that completes when no sync of the server runs any more, or the wait is over.</returns>
    Task StopAsync(string peerKey, TimeSpan wait) => Task.CompletedTask;
}

/// <summary>
/// Keeps every sync out while a layout move runs (PLAN v2.9 K44): a move rewrites the folders and catalogs a sync works on.
/// </summary>
public interface ISyncHold
{
    /// <summary>
    /// Takes the global sync lock without waiting. While it is held, a sync that starts answers "busy" and is tried again by the scheduler.
    /// </summary>
    /// <returns>A handle that releases the lock, or null when a sync is running now.</returns>
    IDisposable? TryHold();
}
