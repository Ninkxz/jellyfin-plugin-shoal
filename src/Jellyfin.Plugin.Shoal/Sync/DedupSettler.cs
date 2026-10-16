using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Settles the dedup of other servers when a server stops or starts holding its titles (Pilot 09 F11). A better-ranked server that is paused, or has been offline for the
/// grace time, lets go of its titles, and the lower-ranked servers must get theirs; when it is back, the stubs the others wrote for the same titles must go again, in one pass
/// after it came back (not at the next scheduled sync, which left every title listed twice). The pass is an ordinary scheduled sync of each other server, so the guards,
/// the mass-removal guard and the one rate-limited scan work as they always do.
/// </summary>
public sealed class DedupSettler : BackgroundService
{
    /// <summary>How long a change settles before the others are synced (a peer that flaps does not start a sync each time).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private readonly PeerRegistry _registry;
    private readonly CatalogSyncService _sync;
    private readonly IStateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<DedupSettler> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Due, bool ExpectUp)> _due = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="DedupSettler"/> class.
    /// </summary>
    /// <param name="registry">The peer statuses.</param>
    /// <param name="sync">The sync service.</param>
    /// <param name="state">The state store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public DedupSettler(PeerRegistry registry, CatalogSyncService sync, IStateStore state, TimeProvider time, ILogger<DedupSettler> logger)
    {
        _registry = registry;
        _sync = sync;
        _state = state;
        _time = time;
        _logger = logger;
        _registry.StatusChanged += OnStatusChanged;
    }

    /// <summary>Syncs, once the settle time has passed, the servers that a change of other servers concerns.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of servers synced.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        List<string> changed;
        Dictionary<string, bool> expected;
        lock (_lock)
        {
            changed = [.. _due.Where(d => d.Value.Due <= now).Select(static d => d.Key)];
            expected = _due.Where(d => d.Value.Due <= now).ToDictionary(static d => d.Key, static d => d.Value.ExpectUp, StringComparer.Ordinal);
            foreach (var key in changed)
            {
                _due.Remove(key);
            }
        }

        // A server whose change was undone meanwhile (down, then back before the grace time; back, then down again) has nothing to settle.
        var relevant = changed.Where(k => _state.Status == StateStatus.Ready && CatalogSyncService.IsReachable(_registry.StatusOf(k)) == expected[k]).ToList();
        if (relevant.Count == 0)
        {
            return 0;
        }

        var synced = 0;
        foreach (var peer in _state.Current.Home.Peers.Where(p => p.Enabled && !relevant.Contains(p.PeerKey, StringComparer.Ordinal)).ToList())
        {
            if (!_sync.HasTitles(peer.PeerKey) || !CatalogSyncService.IsReachable(_registry.StatusOf(peer.PeerKey)))
            {
                continue;
            }

            var report = await _sync.SyncAsync(peer.PeerKey, SyncTrigger.Scheduled, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Dedup settle sync of a server finished ({Result})", report.Error ?? "ok");
            synced++;
        }

        return synced;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _registry.StatusChanged -= OnStatusChanged;
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                try
                {
                    await RunDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The dedup settle pass failed ({Error})", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void OnStatusChanged(string peerKey, PeerStatus was, PeerStatus now, DateTimeOffset? wasSince)
    {
        var wasUp = CatalogSyncService.IsReachable(was);
        var isUp = CatalogSyncService.IsReachable(now);
        if (wasUp == isUp)
        {
            return;
        }

        var at = _time.GetUtcNow();
        lock (_lock)
        {
            if (isUp)
            {
                // Back. It held its titles all along unless it was paused or out for the grace time; only then did anyone write stubs in its place.
                if (was == PeerStatus.Disabled || (wasSince is { } since && at - since >= DedupReachability.Grace))
                {
                    _due[peerKey] = (at + Settle, true);
                }

                return;
            }

            // Down: a paused server lets go at once; any other one after the grace time (it is checked again then; if it is back, nothing is done).
            _due[peerKey] = (at + (now == PeerStatus.Disabled ? Settle : DedupReachability.Grace + Settle), false);
        }
    }
}
