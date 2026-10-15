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
public sealed class CatalogSyncService : IDisposable
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
    public CatalogSyncService(
        IStateStore state,
        PeerDirectory peers,
        PeerTrust trust,
        PeerCatalogReader reader,
        CatalogStore store,
        StubWriter writer,
        ILocalMovies local,
        ILibraryRefresh refresh,
        ILogger<CatalogSyncService> logger)
        : this(state, peers.Find, trust, reader, store, writer, local, refresh, () => SyncSettings.From(Plugin.Instance?.Configuration), TimeProvider.System, logger)
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
        ILogger logger)
    {
        _state = state;
        _find = find;
        _settings = settings;
        _d = new Dependencies(trust, reader, store, writer, local, refresh, time, logger);
    }

    /// <summary>
    /// Gets or sets the overall budget of one sync (tests shorten it).
    /// </summary>
    internal TimeSpan SyncBudget { get; set; } = Budget;

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
        RunAsync(peerKey, trigger, bypassRemovals: false, bypassAdditions: false, cancellationToken);

    /// <summary>
    /// Applies what the guards held back, by running a sync that does not hold.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="kind">What to release.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    public Task<SyncReport> ReleaseAsync(string peerKey, ReleaseKind kind, CancellationToken cancellationToken = default) =>
        RunAsync(peerKey, SyncTrigger.Scheduled, kind is ReleaseKind.Removals or ReleaseKind.Both, kind is ReleaseKind.Additions or ReleaseKind.Both, cancellationToken);

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

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<SyncReport> RunAsync(string peerKey, SyncTrigger trigger, bool bypassRemovals, bool bypassAdditions, CancellationToken cancellationToken)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            return SyncReport.Skipped("busy");
        }

        try
        {
            return await RunLockedAsync(peerKey, trigger, bypassRemovals, bypassAdditions, cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
        }
    }

    private async Task<SyncReport> RunLockedAsync(string peerKey, SyncTrigger trigger, bool bypassRemovals, bool bypassAdditions, CancellationToken cancellationToken)
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
        var run = new SyncRun(_d, state, peer, endpoint, document, _settings(), now, bypassRemovals, bypassAdditions);
        return await run.RunAsync(cancellationToken, budget.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// The services a <see cref="SyncRun"/> uses.
    /// </summary>
#pragma warning disable SA1600 // internal plumbing
    internal sealed class Dependencies
    {
        public Dependencies(PeerTrust trust, PeerCatalogReader reader, CatalogStore store, StubWriter writer, ILocalMovies local, ILibraryRefresh refresh, TimeProvider time, ILogger logger)
        {
            Trust = trust;
            Reader = reader;
            Store = store;
            Writer = writer;
            Local = local;
            Refresh = refresh;
            Time = time;
            Logger = logger;
        }

        public PeerTrust Trust { get; }

        public PeerCatalogReader Reader { get; }

        public CatalogStore Store { get; }

        public StubWriter Writer { get; }

        public ILocalMovies Local { get; }

        public ILibraryRefresh Refresh { get; }

        public TimeProvider Time { get; }

        public ILogger Logger { get; }
    }
}
