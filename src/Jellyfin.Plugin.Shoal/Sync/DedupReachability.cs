using System;
using Jellyfin.Plugin.Shoal.Peers;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Says whether a better-ranked peer still holds its titles against a worse-ranked one (D19, Warden 16 M2). A peer nobody has checked in this run counts as reachable, so a
/// Home that has just started does not promote lower-ranked duplicates before the first health pass. A peer that is offline or stopped (failed auth, changed server id or key) keeps its claim until it has been so for
/// <see cref="Grace"/> without a break, so a flapping peer does not flap the stubs of the others; a paused peer lets go at once.
/// </summary>
public sealed class DedupReachability
{
    /// <summary>How long a peer must have been offline, without a break, before it stops holding titles.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private readonly PeerRegistry _registry;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="DedupReachability"/> class.
    /// </summary>
    /// <param name="registry">The peer statuses.</param>
    /// <param name="time">The clock.</param>
    public DedupReachability(PeerRegistry registry, TimeProvider time)
    {
        _registry = registry;
        _time = time;
    }

    /// <summary>
    /// Whether the peer still holds its titles.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>True when it is reachable, unchecked so far, or has been offline for less than <see cref="Grace"/>.</returns>
    public bool IsUp(string peerKey)
    {
        if (!_registry.IsKnown(peerKey))
        {
            return true;
        }

        var status = _registry.StatusOf(peerKey);
        if (CatalogSyncService.IsReachable(status))
        {
            return true;
        }

        // Offline, or stopped by a failed auth, a changed server id or a changed key, for as long as that status has stood (a peer that has been like that for hours lets go
        // at once; one that was fine a minute ago, or whose status was set at this start, waits). A paused one is the admin's choice and lets go at once.
        if (status == PeerStatus.Disabled)
        {
            return false;
        }

        return _time.GetUtcNow() - (_registry.StatusSinceUtc(peerKey) ?? _time.GetUtcNow()) < Grace;
    }
}
