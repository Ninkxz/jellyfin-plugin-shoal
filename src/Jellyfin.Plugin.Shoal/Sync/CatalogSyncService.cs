using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// What "Release" applies.
/// </summary>
public enum ReleaseKind
{
    /// <summary>The removals the mass-deletion guard held back.</summary>
    Removals,

    /// <summary>The additions the new-items guard held back.</summary>
    Additions,

    /// <summary>Both.</summary>
    Both,
}

/// <summary>
/// Syncs a peer's movie catalog into stubs (DESIGN 5.1 to 5.3, 5.8 to 5.10): enumerate, diff, dedup, guard, fetch details, write the stubs,
/// save the catalog and tell core which folders changed. One sync runs at a time, whichever peer it is for.
/// </summary>
public sealed class CatalogSyncService : IDisposable, ISyncActivity
{
    /// <summary>The shortest time between two manual syncs of one peer.</summary>
    public static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(5);

    /// <summary>The overall budget of one sync.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(15);

    private readonly Dependencies _d;
    private readonly IStateStore _state;
    private readonly Func<string, PeerEndpoint?> _find;
    private readonly Func<SyncSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TvSyncEngine? _tv;
    private readonly Func<bool> _tvEnabled;
    private volatile string? _syncing;
    private volatile Func<Task>? _stopRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogSyncService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="reader">The peer catalog reader.</param>
    /// <param name="store">The catalog store.</param>
    /// <param name="writer">The stub writer.</param>
    /// <param name="local">Home's own movies.</param>
    /// <param name="refresh">The library refresh.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tv">The TV part of a sync, or null (a build without TV shows).</param>
    /// <param name="registry">The peer statuses, so an unreachable peer does not hold titles against a reachable one, or null.</param>
    public CatalogSyncService(
        IStateStore state,
        PeerDirectory peers,
        PeerTrust trust,
        PeerCatalogReader reader,
        CatalogStore store,
        StubWriter writer,
        ILocalMovies local,
        ILibraryRefresh refresh,
        ILogger<CatalogSyncService> logger,
        TvSyncEngine? tv = null,
        PeerRegistry? registry = null)
        : this(state, peers.Find, trust, reader, store, writer, local, refresh, () => SyncSettings.From(Plugin.Instance?.Configuration), TimeProvider.System, logger, tv, () => Plugin.Instance?.Configuration.TvShowsEnabled ?? false, registry is null ? null : new DedupReachability(registry, TimeProvider.System).IsUp)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogSyncService"/> class (tests: own peer lookup, settings and clock).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="find">Finds a peer with its credentials.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="reader">The peer catalog reader.</param>
    /// <param name="store">The catalog store.</param>
    /// <param name="writer">The stub writer.</param>
    /// <param name="local">Home's own movies.</param>
    /// <param name="refresh">The library refresh.</param>
    /// <param name="settings">The settings in force.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tv">The TV part of a sync, or null.</param>
    /// <param name="tvEnabled">Whether TV libraries are synced (the <c>TvShowsEnabled</c> switch); off by default.</param>
    /// <param name="isUp">Whether a peer can be reached (tests); null counts every peer as reachable.</param>
    internal CatalogSyncService(
        IStateStore state,
        Func<string, PeerEndpoint?> find,
        PeerTrust trust,
        PeerCatalogReader reader,
        CatalogStore store,
        StubWriter writer,
        ILocalMovies local,
        ILibraryRefresh refresh,
        Func<SyncSettings> settings,
        TimeProvider time,
        ILogger logger,
        TvSyncEngine? tv = null,
        Func<bool>? tvEnabled = null,
        Func<string, bool>? isUp = null)
    {
        _tv = tv;
        _tvEnabled = tvEnabled ?? (static () => false);
        _state = state;
        _find = find;
        _settings = settings;
        _d = new Dependencies(trust, reader, store, writer, local, refresh, time, logger, isUp ?? (static _ => true));
    }

    /// <summary>
    /// Gets or sets the overall budget of one sync (tests shorten it).
    /// </summary>
    internal TimeSpan SyncBudget { get; set; } = Budget;

    /// <summary>
    /// Whether a peer with this status can hold a title against a lower-ranked peer (D19). A peer that is offline, paused or stopped does not: its titles must not
    /// keep another peer's copy of the same title out of the library. When it is back, the tie is resolved by rank again at the next sync.
    /// </summary>
    /// <param name="status">The peer's status.</param>
    /// <returns>False for a peer that cannot be reached.</returns>
    public static bool IsReachable(PeerStatus status) =>
        status is not (PeerStatus.Offline or PeerStatus.Disabled or PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Pairing or PeerStatus.PairingAborted);

    /// <summary>
    /// Whether peer <paramref name="a"/> wins over <paramref name="b"/> when both carry a title: lower priority number first, then the
    /// earlier pairing, then the key (DESIGN 5.3).
    /// </summary>
    /// <param name="a">One peer.</param>
    /// <param name="b">The other peer.</param>
    /// <returns>True when <paramref name="a"/> ranks higher.</returns>
    public static bool Outranks(PeerState a, PeerState b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Priority != b.Priority)
        {
            return a.Priority < b.Priority;
        }

        var paired = (a.Pairing?.PairedUtc ?? DateTime.MaxValue).CompareTo(b.Pairing?.PairedUtc ?? DateTime.MaxValue);
        return paired != 0 ? paired < 0 : string.CompareOrdinal(a.PeerKey, b.PeerKey) < 0;
    }

    /// <summary>
    /// Syncs one peer.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="trigger">What started it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report; a sync that cannot run says why in <see cref="SyncReport.Error"/>.</returns>
    public Task<SyncReport> SyncAsync(string peerKey, SyncTrigger trigger, CancellationToken cancellationToken = default) =>
        RunAsync(peerKey, trigger, bypassRemovals: false, bypassAdditions: false, releaseSeries: null, cancellationToken);

    /// <summary>
    /// Applies what the guards held back, by running a sync that does not hold.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="kind">What to release.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    public Task<SyncReport> ReleaseAsync(string peerKey, ReleaseKind kind, CancellationToken cancellationToken = default) =>
        RunAsync(peerKey, SyncTrigger.Scheduled, kind is ReleaseKind.Removals or ReleaseKind.Both, kind is ReleaseKind.Additions or ReleaseKind.Both, releaseSeries: null, cancellationToken);

    /// <summary>
    /// Applies what the guard held back for one series only ("Release by series"): a sync that lets that series through the guards and holds the rest.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="removals">True for the held removals, false for the held additions.</param>
    /// <param name="seriesKey">The group's key as <c>GET Peers/{h}/Holds</c> lists it (Home's own random key, not a peer id).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report; <c>notFound</c> in its error when there is no such held group.</returns>
    public Task<SyncReport> ReleaseSeriesAsync(string peerKey, bool removals, string seriesKey, CancellationToken cancellationToken = default)
    {
        var load = _d.Store.Load(peerKey);
        string? peerSeriesId = removals
            ? load.Document.Entries.FirstOrDefault(e => e.Kind == EntryKind.Series && string.Equals(e.StubKey, seriesKey, StringComparison.Ordinal))?.PeerItemId
            : load.Document.HeldSeries.FirstOrDefault(h => string.Equals(h.Key, seriesKey, StringComparison.Ordinal))?.PeerSeriesId;
        if (load.Unreadable || !Guid.TryParseExact(peerSeriesId, "N", out var id))
        {
            return Task.FromResult(SyncReport.Failed("notFound"));
        }

        return RunAsync(peerKey, SyncTrigger.Scheduled, bypassRemovals: false, bypassAdditions: false, new HashSet<Guid> { id }, cancellationToken);
    }

    /// <summary>
    /// Syncs every enabled peer, one after the other.
    /// </summary>
    /// <param name="trigger">What started it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report of each peer.</returns>
    public async Task<IReadOnlyList<SyncReport>> SyncAllAsync(SyncTrigger trigger, CancellationToken cancellationToken = default)
    {
        var reports = new List<SyncReport>();
        foreach (var peer in _state.Current.Home.Peers.Where(static p => p.Enabled).ToList())
        {
            reports.Add(await SyncAsync(peer.PeerKey, trigger, cancellationToken).ConfigureAwait(false));
        }

        return reports;
    }

    /// <summary>Whether a server has any title in its catalog (a server with none has nothing to settle).</summary>
    /// <param name="peerKey">The server.</param>
    /// <returns>True when its catalog lists a title.</returns>
    public bool HasTitles(string peerKey) => _d.Store.Load(peerKey).Document.Entries.Count > 0;

    /// <inheritdoc />
    public bool IsSyncing(string peerKey) => string.Equals(_syncing, peerKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task StopAsync(string peerKey, TimeSpan wait)
    {
        if (!IsSyncing(peerKey))
        {
            return;
        }

        try
        {
            if (_stopRun is { } stop)
            {
                await stop().ConfigureAwait(false); // the run ends like one whose time ran out: it stops at the next checkpoint and keeps what it wrote
            }
        }
        catch (ObjectDisposedException)
        {
            // it just ended
        }

        var until = DateTime.UtcNow + wait;
        while (IsSyncing(peerKey) && DateTime.UtcNow < until)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<SyncReport> RunAsync(string peerKey, SyncTrigger trigger, bool bypassRemovals, bool bypassAdditions, IReadOnlySet<Guid>? releaseSeries, CancellationToken cancellationToken)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            return SyncReport.Skipped("busy");
        }

        _syncing = peerKey;
        try
        {
            return await RunLockedAsync(peerKey, trigger, bypassRemovals, bypassAdditions, releaseSeries, cancellationToken).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _d.Logger.LogInformation("A sync of peer {Peer} failed ({Kind})", Redact.Id(peerKey), ex.Kind);
            return SyncReport.Failed(ex.Kind switch
            {
                PeerFailureKind.CircuitOpen => "circuitOpen",
                PeerFailureKind.TrustLapsed => "trustLapsed",
                PeerFailureKind.Blocked => "blocked",
                _ => "unreachable",
            });
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // One peer's hostile or malformed data must never stop the sync of the peers behind it (K11 M-1). The type only: a message may carry peer text.
            _d.Logger.LogWarning("A sync of peer {Peer} failed internally ({Type})", Redact.Id(peerKey), ex.GetType().Name);
            return SyncReport.Failed("internal");
        }
        finally
        {
            _syncing = null;
            _stopRun = null;
            _gate.Release();
        }
    }

    private async Task<SyncReport> RunLockedAsync(string peerKey, SyncTrigger trigger, bool bypassRemovals, bool bypassAdditions, IReadOnlySet<Guid>? releaseSeries, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return SyncReport.Skipped("recovery");
        }

        var state = _state.Current;
        var peer = state.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey);
        if (peer is null)
        {
            return SyncReport.Skipped("unknownPeer");
        }

        if (!peer.Enabled)
        {
            return SyncReport.Skipped("disabled");
        }

        if (string.IsNullOrWhiteSpace(state.StubRoot) || !System.IO.Path.IsPathRooted(state.StubRoot))
        {
            return SyncReport.Skipped("noStubRoot"); // nothing is federated without a valid stub root
        }

        var load = _d.Store.Load(peerKey);
        if (load.Unreadable)
        {
            return SyncReport.Failed("catalogUnreadable");
        }

        var now = _d.Time.GetUtcNow().UtcDateTime;
        var document = load.Document;
        var bypass = bypassRemovals || bypassAdditions;

        // A mapping with no stubs yet (new, or re-added) is never throttled: its first sync is what fills it.
        var hasEmptyMapping = peer.Libraries.Any(m => m.Import && (document.Libraries.FirstOrDefault(l => l.MappingKey == m.MappingKey)?.LastCount ?? 0) == 0);
        if (trigger == SyncTrigger.Manual && !bypass && !hasEmptyMapping && document.LastSync is { Result: not SyncResultKind.Skipped } last && now - last.EndedUtc < ManualInterval)
        {
            return SyncReport.Skipped("tooSoon");
        }

        if (_d.Refresh.IsScanRunning)
        {
            return SyncReport.Skipped("scanRunning"); // the scheduler tries again in 10 minutes
        }

        var endpoint = _find(peerKey);
        if (endpoint is null || !endpoint.HasToken)
        {
            return SyncReport.Failed("noCredentials");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SyncBudget);
        _stopRun = budget.CancelAsync;
        var settings = _settings();
        var run = new SyncRun(_d, state, peer, endpoint, document, settings, now, bypassRemovals, bypassAdditions);
        var report = await run.RunAsync(cancellationToken, budget.Token).ConfigureAwait(false);
        if (_tv is null || !_tvEnabled() || report.Result is SyncResultKind.Failed or SyncResultKind.Skipped)
        {
            return report;
        }

        // The TV libraries of the peer, after the movies and under the same lock and budget (DESIGN v2.7.2).
        var tvReport = await _tv.RunAsync(_d, state, peer, endpoint, document, settings, now, bypassRemovals, bypassAdditions, cancellationToken, budget.Token, releaseSeries).ConfigureAwait(false);
        return tvReport is null ? report : MergeTv(report, tvReport, document);
    }

    private SyncReport MergeTv(SyncReport movies, SyncReport tv, CatalogDocument document)
    {
        movies.Added += tv.Added;
        movies.Updated += tv.Updated;
        movies.Removed += tv.Removed;
        movies.Moved += tv.Moved;
        movies.Duplicates += tv.Duplicates;
        movies.SkippedItems += tv.SkippedItems;
        movies.ImagesStored += tv.ImagesStored;
        movies.ImagesPending |= tv.ImagesPending;
        movies.ChangedFolders += tv.ChangedFolders;
        movies.Complete &= tv.Complete;
        movies.Error ??= tv.Error;
        movies.HeldRemovals = document.Entries.Count(static e => e.State == EntryState.HeldRemoval && e.Kind is EntryKind.Movie or EntryKind.Episode);
        movies.HeldAdditions = document.HeldAdditions.Count + document.TvHeldAdditions.Count;
        foreach (var alert in tv.Alerts.Where(a => !movies.Alerts.Contains(a)))
        {
            movies.Alerts.Add(alert);
        }

        movies.Result = !movies.Complete ? SyncResultKind.Partial : (movies.HeldRemovals > 0 || movies.HeldAdditions > 0 ? SyncResultKind.Held : SyncResultKind.Ok);
        if (document.LastSync is { } last)
        {
            last.EndedUtc = _d.Time.GetUtcNow().UtcDateTime;
            last.Result = movies.Result;
            last.Added = movies.Added;
            last.Updated = movies.Updated;
            last.Removed = movies.Removed;
            last.HeldRemovals = movies.HeldRemovals;
            last.HeldAdditions = movies.HeldAdditions;
            last.Error = movies.Error;
            last.Alerts = [.. movies.Alerts.Select(static a => a.ToString()).OrderBy(static a => a, StringComparer.Ordinal)];
        }

        _d.Store.Save(document);
        return movies;
    }

    /// <summary>
    /// The services a <see cref="SyncRun"/> uses.
    /// </summary>
#pragma warning disable SA1600 // internal plumbing
    internal sealed class Dependencies
    {
        private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, DateTime> _scans = new(StringComparer.Ordinal);
        private DateTime _lastPromotionScan;

        public Dependencies(PeerTrust trust, PeerCatalogReader reader, CatalogStore store, StubWriter writer, ILocalMovies local, ILibraryRefresh refresh, TimeProvider time, ILogger logger, Func<string, bool> isUp)
        {
            IsUp = isUp;
            Trust = trust;
            Reader = reader;
            Store = store;
            Writer = writer;
            Local = local;
            Refresh = refresh;
            Time = time;
            Logger = logger;
        }

        public Func<string, bool> IsUp { get; }

        public PeerTrust Trust { get; }

        public PeerCatalogReader Reader { get; }

        public CatalogStore Store { get; }

        public StubWriter Writer { get; }

        public ILocalMovies Local { get; }

        public ILibraryRefresh Refresh { get; }

        public TimeProvider Time { get; }

        public ILogger Logger { get; }

        /// <summary>
        /// Says whether a library scan may be queued for this server now (once in five minutes at most), and notes it when it may.
        /// </summary>
        /// <param name="peerKey">The server.</param>
        /// <param name="now">The time.</param>
        /// <param name="promotion">True for a scan asked for only because titles a better-ranked server held back got their stubs: those are limited to one in five minutes across all servers.</param>
        /// <returns>True when the scan may be queued (never while a scan is running).</returns>
        public bool TryStartScan(string peerKey, DateTime now, bool promotion = false)
        {
            if (Refresh.IsScanRunning)
            {
                return false;
            }

            lock (_scans)
            {
                if (_scans.TryGetValue(peerKey, out var last) && now - last < ScanEvery)
                {
                    return false;
                }

                if (promotion && _lastPromotionScan != default && now - _lastPromotionScan < ScanEvery)
                {
                    return false;
                }

                _scans[peerKey] = now;
                if (promotion)
                {
                    _lastPromotionScan = now;
                }

                return true;
            }
        }
    }
}
