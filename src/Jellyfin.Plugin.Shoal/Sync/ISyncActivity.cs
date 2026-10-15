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
}
