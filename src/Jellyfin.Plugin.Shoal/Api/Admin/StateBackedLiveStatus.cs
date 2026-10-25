using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The live status the Overview shows until the health monitor (K11 health, K20) exists: a pairing that is running shows as
/// <c>Pairing</c>, a pairing that stopped shows as <c>PairingAborted</c> with its kind (from <c>health.json</c>, so it survives a
/// restart and a page reload), the catalog gives the last sync, the title count, the held counts, the storage and the alerts, and
/// "Sync now" and the hold buttons start the real sync. Nothing here invents a speed, a time or an "online".
/// </summary>
public sealed class StateBackedLiveStatus : IPeerLiveStatus
{
    private const int MaxScanRetries = 40;
    private static readonly ShareLiveInfo NoShareInfo = new();

    private readonly PeerHealthStore _health;
    private readonly IPairingGate _gate;
    private readonly CatalogStore? _catalogs;
    private readonly CatalogSyncService? _sync;
    private readonly ISettingsAccessor? _settings;
    private readonly IStateStore? _state;
    private readonly Jellyfin.Plugin.Shoal.Sharing.IShareLiveSource? _shares;
    private readonly PeerRegistry? _registry;
    private readonly HealthMonitor? _monitor;
    private readonly Jellyfin.Plugin.Shoal.Playback.StreamSlots? _slots;
    private readonly Jellyfin.Plugin.Shoal.Diagnostics.SpeedSummaryProvider? _speed;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="StateBackedLiveStatus"/> class.
    /// </summary>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="gate">Tells whether a pairing session is live for a peer.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="sync">The sync service.</param>
    /// <param name="settings">The plugin toggles (the storage limit).</param>
    /// <param name="state">The state store.</param>
    /// <param name="shares">The friends' live information.</param>
    /// <param name="registry">The runtime peer table (the status the health monitor set).</param>
    /// <param name="monitor">The health monitor (round trips, next check, the share account policy).</param>
    /// <param name="slots">The stream slots (the streams running through each peer).</param>
    /// <param name="speed">The speed summaries (throughput, latency, verdicts).</param>
    /// <param name="logger">The logger.</param>
    public StateBackedLiveStatus(PeerHealthStore health, IPairingGate gate, CatalogStore catalogs, CatalogSyncService sync, ISettingsAccessor settings, IStateStore state, Jellyfin.Plugin.Shoal.Sharing.IShareLiveSource shares, PeerRegistry registry, HealthMonitor monitor, Jellyfin.Plugin.Shoal.Playback.StreamSlots slots, Jellyfin.Plugin.Shoal.Diagnostics.SpeedSummaryProvider speed, ILogger<StateBackedLiveStatus> logger)
    {
        _speed = speed;
        _slots = slots;
        _registry = registry;
        _monitor = monitor;
        _health = health;
        _gate = gate;
        _catalogs = catalogs;
        _sync = sync;
        _settings = settings;
        _state = state;
        _shares = shares;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateBackedLiveStatus"/> class without the sync service (what a test needs).
    /// </summary>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="gate">Tells whether a pairing session is live for a peer.</param>
    /// <param name="catalogs">The catalogs, or null.</param>
    /// <param name="settings">The plugin toggles, or null.</param>
    /// <param name="registry">The runtime peer table, or null.</param>
    /// <param name="monitor">The health monitor, or null.</param>
    /// <param name="slots">The stream slots, or null.</param>
    /// <param name="sync">The sync service, or null.</param>
    /// <param name="state">The state store, or null.</param>
    /// <param name="speed">The speed summaries, or null.</param>
    internal StateBackedLiveStatus(PeerHealthStore health, IPairingGate gate, CatalogStore? catalogs = null, ISettingsAccessor? settings = null, PeerRegistry? registry = null, HealthMonitor? monitor = null, Jellyfin.Plugin.Shoal.Playback.StreamSlots? slots = null, CatalogSyncService? sync = null, IStateStore? state = null, Jellyfin.Plugin.Shoal.Diagnostics.SpeedSummaryProvider? speed = null)
    {
        _speed = speed;
        _sync = sync;
        _state = state;
        _slots = slots;
        _registry = registry;
        _monitor = monitor;
        _health = health;
        _gate = gate;
        _catalogs = catalogs;
        _settings = settings;
        _logger = NullLogger.Instance;
    }

    /// <summary>Gets or sets the wait before a sync that a library scan blocked is tried again.</summary>
    internal TimeSpan ScanRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public PeerLiveInfo ForPeer(string peerKey)
    {
        if (_gate.IsLive(peerKey))
        {
            return new PeerLiveInfo { Status = "Pairing" };
        }

        if (_health.GetAbort(peerKey) is { } abort)
        {
            return new PeerLiveInfo
            {
                Status = "PairingAborted",
                PairingError = Word(abort.Kind.ToString()),
                PairingPhase = abort.Phase is { } phase ? Word(phase.ToString()) : null,
                PairingStoppedUtc = abort.AtUtc
            };
        }

        return WithHealth(peerKey, FromCatalog(peerKey));
    }

    /// <inheritdoc />
    public ShareLiveInfo ForShare(string shareId) => _shares?.Live(shareId) ?? NoShareInfo;

    /// <inheritdoc />
    public int StartSyncAll()
    {
        if (_sync is null)
        {
            return 0;
        }

        var peers = _state?.Current.Home.Peers.Count(static p => p.Enabled) ?? 0;
        Run("all", async () =>
        {
            foreach (var peer in _state?.Current.Home.Peers.Where(static p => p.Enabled).ToList() ?? [])
            {
                await SyncWithRetryAsync(peer.PeerKey).ConfigureAwait(false);
            }
        });
        return peers;
    }

    /// <inheritdoc />
    public bool StartSync(string peerKey)
    {
        if (_sync is null)
        {
            return false;
        }

        Run(peerKey, () => SyncWithRetryAsync(peerKey));
        return true;
    }

    /// <inheritdoc />
    public bool StartRelease(string peerKey, bool removals)
    {
        if (_sync is null)
        {
            return false;
        }

        Run(peerKey, () => _sync.ReleaseAsync(peerKey, removals ? ReleaseKind.Removals : ReleaseKind.Additions));
        return true;
    }

    /// <inheritdoc />
    public bool StartReleaseSeries(string peerKey, bool removals, string seriesKey)
    {
        if (_sync is null)
        {
            return false;
        }

        Run(peerKey, () => _sync.ReleaseSeriesAsync(peerKey, removals, seriesKey));
        return true;
    }

    // The enum names are the wire words with a capital first letter (RequestReplaced -> requestReplaced).
    private static string Word(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    // A scan that blocks a manual sync (a library just added is still empty) is waited out, not left to the 10 minute cycle.
    private async Task SyncWithRetryAsync(string peerKey)
    {
        var report = await _sync!.SyncAsync(peerKey, SyncTrigger.Manual).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxScanRetries && report.Error == "scanRunning"; attempt++)
        {
            Note(peerKey, report);
            await Task.Delay(ScanRetryDelay).ConfigureAwait(false);
            report = await _sync.SyncAsync(peerKey, SyncTrigger.Manual).ConfigureAwait(false);
        }

        Note(peerKey, report);
    }

    // A request that did not run (too soon, a scan, busy) is shown next to the last real sync, never in its place (Pilot 24 SKIPPED-INVISIBLE).
    private void Note(string peerKey, SyncReport report)
    {
        if (report.Result == SyncResultKind.Skipped)
        {
            _catalogs?.NoteManualRequest(peerKey, new SkippedSync(_sync!.UtcNow, report.Error ?? "unknown"));
            _logger.LogInformation("A manual sync of peer {Peer} was skipped ({Reason})", Redact.Id(peerKey), report.Error);
        }
        else
        {
            _catalogs?.NoteManualRequest(peerKey, null);
        }
    }

    private void Run(string key, Func<Task> work)
    {
        _running.AddOrUpdate(key, 1, static (_, count) => count + 1);
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A background sync must never take the server down; the failure is in the catalog's last sync.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning("A sync started from the admin page failed: {Error}", ex.GetType().Name);
            }
            finally
            {
                // Another request for this peer may still be queued or waiting for a scan.
                // Remove only the final job's count, without racing a new request's increment.
                while (_running.TryGetValue(key, out var count))
                {
                    if (count == 1)
                    {
                        if (((ICollection<KeyValuePair<string, int>>)_running).Remove(new(key, 1)))
                        {
                            break;
                        }
                    }
                    else if (_running.TryUpdate(key, count - 1, count))
                    {
                        break;
                    }
                }
            }
        });
    }

    // What the 60 second checks know: the status the monitor set, the round trip, the next check, and the share account's lock and session limit.
    private PeerLiveInfo WithHealth(string peerKey, PeerLiveInfo info)
    {
        info = info with { ActiveStreams = _slots?.ActiveSlotsOnPeer(peerKey) ?? info.ActiveStreams };
        if (_speed?.For(peerKey) is { } measured)
        {
            info = info with
            {
                Mbps = measured.Mbps,
                RttMs = measured.RttMs,
                JitterMs = measured.JitterMs,
                LossPct = measured.LossPct,
                Path = measured.Path == "unknown" ? info.Path : measured.Path,
                VerdictSummary = measured.Verdicts
            };
        }

        if (_registry is null)
        {
            return info;
        }

        var snapshot = _monitor?.Snapshot(peerKey);
        return info with
        {
            Status = _registry.StatusOf(peerKey).ToString(),
            RttMs = info.RttMs ?? snapshot?.RttMs,
            NextCheckUtc = snapshot?.NextCheckUtc.UtcDateTime,
        };
    }

    private PeerLiveInfo FromCatalog(string peerKey)
    {
        var syncing = _running.ContainsKey(peerKey) || _running.ContainsKey("all");
        if (_catalogs is null)
        {
            return new PeerLiveInfo { Syncing = syncing };
        }

        CatalogLoad load;
        try
        {
            load = _catalogs.Load(peerKey);
        }
#pragma warning disable CA1031 // A damaged catalog must not break the Overview; the sync reports it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The catalog of a peer could not be read for the Overview: {Error}", ex.GetType().Name);
            return new PeerLiveInfo { Syncing = syncing, AlertCount = 1, Alerts = [new PeerAlertInfo("catalogRecovery", "bad")] };
        }

        if (load.Unreadable)
        {
            return new PeerLiveInfo { Syncing = syncing, AlertCount = 1, Alerts = [new PeerAlertInfo("catalogRecovery", "bad")] };
        }

        var doc = load.Document;

        // A title is a movie or a series; seasons and episodes belong to their series and are not counted on their own.
        var stubs = TitleCounts.Of(doc.Entries).Local;
        var hidden = doc.Entries.Where(static e => e.State == EntryState.DuplicatePeer && e.Kind is EntryKind.Movie or EntryKind.Series).ToList();
        var last = doc.LastSync;
        var quota = (_settings?.Current.DiskQuotaGbPerPeer ?? 2) * 1_000_000_000L;
        var alerts = Alerts(doc);
        return new PeerLiveInfo
        {
            Syncing = syncing,
            TitleCount = stubs,
            HiddenByDedup = hidden.Count,
            DedupWinner = hidden.Count == 0 ? null : WinnerOf(peerKey, hidden),
            HeldRemovals = doc.HeldRemovals.Count,
            HeldAdditions = doc.HeldAdditions.Count + doc.TvHeldAdditions.Count,
            AlertCount = alerts.Count,
            LastSync = PeerSyncInfo.Of(last, _catalogs.LastSkipped(peerKey)),
            Usage = new PeerUsageInfo(doc.Usage.DiskBytes, quota, stubs, doc.Usage.People, doc.Usage.Genres, doc.Usage.Studios),
            Alerts = alerts
        };
    }

    // The peer whose catalog holds most of the stubs that won against these titles (the loser keeps the winner's stub key).
    private string? WinnerOf(string peerKey, List<CatalogEntry> hidden)
    {
        var wanted = hidden.Select(static e => e.DuplicateOf).Where(static k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 || _state is null || _catalogs is null)
        {
            return null;
        }

        string? best = null;
        var bestCount = 0;
        foreach (var other in _state.Current.Home.Peers.Where(p => p.PeerKey != peerKey && (_registry is null || CatalogSyncService.IsReachable(_registry.StatusOf(p.PeerKey)))))
        {
            var load = _catalogs.Load(other.PeerKey);
            if (load.Unreadable)
            {
                continue;
            }

            var count = load.Document.Entries.Count(e => wanted.Contains(e.StubKey));
            if (count > bestCount)
            {
                best = other.DisplayName;
                bestCount = count;
            }
        }

        return best;
    }

    // 2.9.2 K3: the first three held titles, "Title (Year)": movies from HeldAdditionInfo, then series from HeldSeries (no entry ever has the HeldAddition state).
    private static List<string> HeldExamples(CatalogDocument doc) =>
        [.. doc.HeldAdditionInfo.Select(static h => (h.Title, h.Year))
            .Concat(doc.HeldSeries.Select(static h => (h.Title, h.Year)))
            .Where(static t => t.Title.Length > 0)
            .Select(static t => t.Year is { } year ? t.Title + " (" + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : t.Title)
            .Take(3)];

    private static List<PeerAlertInfo> Alerts(CatalogDocument doc)
    {
        var result = new List<PeerAlertInfo>();
        foreach (var code in doc.LastSync?.Alerts ?? [])
        {
            switch (code)
            {
                case nameof(SyncAlert.HeldRemovals):
                    result.Add(new PeerAlertInfo("heldRemovals", "warn", doc.HeldRemovals.Count, doc.Entries.Where(e => e.State == EntryState.HeldRemoval).Select(e => e.Title).Take(3).ToList()));
                    break;
                case nameof(SyncAlert.HeldAdditions):
                    result.Add(new PeerAlertInfo("heldAdditions", "warn", doc.HeldAdditions.Count + doc.TvHeldAdditions.Count, HeldExamples(doc)));
                    break;
                case nameof(SyncAlert.QuotaReached):
                    result.Add(new PeerAlertInfo("diskQuota", "warn"));
                    break;
                case nameof(SyncAlert.EntryCap):
                    result.Add(new PeerAlertInfo("entryCap", "bad"));
                    break;
                case nameof(SyncAlert.NameCap):
                    result.Add(new PeerAlertInfo("entityCap", "warn"));
                    break;
                case nameof(SyncAlert.EpisodeRangeConflict):
                    result.Add(new PeerAlertInfo("episodeRangeConflict", "warn"));
                    break;
                case nameof(SyncAlert.WriteFailures):
                    result.Add(new PeerAlertInfo("stubFolder", "bad"));
                    break;
                default:
                    break;
            }
        }

        return result;
    }
}
