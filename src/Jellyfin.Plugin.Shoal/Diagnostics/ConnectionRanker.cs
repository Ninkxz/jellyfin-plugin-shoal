using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>
/// The order in which the servers that carry a title are tried (PLAN v2.9 K54).
/// </summary>
public interface IConnectionRanking
{
    /// <summary>
    /// The connections, best first. A play still skips the ones that are down at that moment.
    /// </summary>
    /// <returns>The peer keys.</returns>
    IReadOnlyList<string> Order();

    /// <summary>
    /// T of a connection (the median of its last three speed tests), as of the last evaluation.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>T in Mbps, or null when never measured.</returns>
    double? Throughput(string peerKey);

    /// <summary>
    /// Why a connection holds its place, as of the last evaluation (PLAN v2.9 K53).
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>The explanation, or null before the first evaluation.</returns>
    RankExplanation? Explain(string peerKey) => null;

    /// <summary>
    /// Whether the last speed test of a connection found it unstable (jitter over 30 ms or loss over 2 percent), as of the last evaluation.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>True when unstable.</returns>
    bool IsUnstable(string peerKey) => false;

    /// <summary>
    /// Evaluates the order now (after the admin changed a tier).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RecomputeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Runs <see cref="VerdictEngine.RankConnections"/> (PLAN v2.9 K54): after each speed test, when a server goes offline or comes back, and every
/// <see cref="Every"/> (so the 30-minute demotion and the 2-hour promotion happen on time). The state is written only when something changed.
/// </summary>
public sealed class ConnectionRanker : BackgroundService, IConnectionRanking
{
    /// <summary>How often the ranking is evaluated without a trigger.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly IStateStore _state;
    private readonly PeerRegistry _registry;
    private readonly Func<string, IReadOnlyList<SpeedSample>> _history;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Action<string, PeerStatus, PeerStatus, DateTimeOffset?> _onStatus;
    private readonly Action<string>? _onSample;
    private readonly SpeedHistoryStore? _store;
    private volatile Snapshot _last = new([], new Dictionary<string, double?>(StringComparer.Ordinal));

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionRanker"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="registry">The peer statuses.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ConnectionRanker(IStateStore state, PeerRegistry registry, SpeedHistoryStore history, TimeProvider time, ILogger<ConnectionRanker> logger)
        : this(state, registry, key => history.Read(key), time, logger)
    {
        _store = history;
        _onSample = _ => Wake();
        history.Appended += _onSample;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionRanker"/> class (tests: any history and logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="registry">The peer statuses.</param>
    /// <param name="history">Reads the speed samples of a connection, oldest first.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    internal ConnectionRanker(IStateStore state, PeerRegistry registry, Func<string, IReadOnlyList<SpeedSample>> history, TimeProvider time, ILogger logger)
    {
        _state = state;
        _registry = registry;
        _history = history;
        _time = time;
        _logger = logger;
        _onStatus = (_, was, now, _) =>
        {
            if (CatalogSyncService.IsReachable(was) != CatalogSyncService.IsReachable(now))
            {
                Wake();
            }
        };
        _registry.StatusChanged += _onStatus;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Order()
    {
        var last = _last;
        if (last.Order.Count > 0 || _state.Status != StateStatus.Ready)
        {
            return last.Order;
        }

        // Not evaluated yet (just started): the tier, then the old rank rule.
        return [.. PairingOrder(_state.Current.Home.Peers).OrderBy(static p => p.PriorityTier switch { PriorityTier.Prefer => 0, PriorityTier.Normal => 1, _ => 2 }).Select(static p => p.PeerKey)];
    }

    /// <inheritdoc />
    public double? Throughput(string peerKey) => _last.Throughput.GetValueOrDefault(peerKey);

    /// <inheritdoc />
    public RankExplanation? Explain(string peerKey) => _last.Explanations?.GetValueOrDefault(peerKey);

    /// <inheritdoc />
    public bool IsUnstable(string peerKey) => _last.Unstable?.Contains(peerKey) == true;

    /// <inheritdoc />
    public Task RecomputeAsync(CancellationToken cancellationToken) => EvaluateAsync(cancellationToken);

    /// <summary>
    /// Evaluates the ranking now and stores what changed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome, or null when the state cannot be read.</returns>
    public async Task<RankOutcome?> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status != StateStatus.Ready)
            {
                return null;
            }

            var peers = PairingOrder(_state.Current.Home.Peers);
            var inputs = new List<RankInput>(peers.Count);
            var throughput = new Dictionary<string, double?>(StringComparer.Ordinal);
            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                var status = _registry.StatusOf(peer.PeerKey);
                var samples = _history(peer.PeerKey);
                var t = VerdictEngine.Throughput(samples);
                var latest = samples.LastOrDefault(static s => s.Kind != SpeedKinds.Playback && s.Error is null && (s.JitterMs is not null || s.LossPct is not null));
                throughput[peer.PeerKey] = t;
                inputs.Add(new RankInput(
                    peer.PeerKey,
                    peer.PriorityTier,
                    CatalogSyncService.IsReachable(status),
                    peer.Enabled && status is not (PeerStatus.AuthFailed or PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged or PeerStatus.Disabled),
                    VerdictEngine.Unstable(latest?.JitterMs, latest?.LossPct),
                    t,
                    _registry.StatusSinceUtc(peer.PeerKey),
                    i,
                    peer.AutoRank));
            }

            var outcome = VerdictEngine.RankConnections(inputs, _time.GetUtcNow().UtcDateTime);
            _last = new Snapshot(outcome.Order, throughput, VerdictEngine.ExplainRanks(outcome, inputs), [.. inputs.Where(static i => i.Unstable).Select(static i => i.PeerKey)]);
            if (outcome.Changed)
            {
                await _state.UpdateAsync(
                    s =>
                    {
                        foreach (var peer in s.Home.Peers)
                        {
                            if (outcome.States.TryGetValue(peer.PeerKey, out var rank))
                            {
                                peer.AutoRank = rank;
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _registry.StatusChanged -= _onStatus;
        if (_store is not null && _onSample is not null)
        {
            _store.Appended -= _onSample;
        }

        _gate.Dispose();
        _wake.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The connection ranking could not be evaluated ({Error})", ex.GetType().Name);
                }

                await _wake.WaitAsync(Every, stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken).ConfigureAwait(false); // a burst of status changes is one evaluation
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static List<PeerState> PairingOrder(IEnumerable<PeerState> peers)
    {
        var list = peers.ToList();
        list.Sort(static (a, b) => CatalogSyncService.Outranks(a, b) ? -1 : CatalogSyncService.Outranks(b, a) ? 1 : 0);
        return list;
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // already woken, or shutting down
        }
    }

    private sealed record Snapshot(IReadOnlyList<string> Order, Dictionary<string, double?> Throughput, IReadOnlyDictionary<string, RankExplanation>? Explanations = null, HashSet<string>? Unstable = null);
}
