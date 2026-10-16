#pragma warning disable SA1600 // internal plumbing; the public surface is documented

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// One playback event of a local session, as the reporter needs it.
/// </summary>
/// <param name="UserId">The local user the event belongs to.</param>
/// <param name="PlaySessionId">The PlaySessionId the client holds (Home's own, from PlaybackInfo).</param>
/// <param name="PositionTicks">The position, or null.</param>
/// <param name="IsPaused">Whether playback is paused.</param>
public sealed record PlaybackNote(Guid UserId, string? PlaySessionId, long? PositionTicks, bool IsPaused);

/// <summary>
/// The exact session reports Home sends a Peer (DESIGN 6.8, 6.11): only the listed fields, and never a request that could stop more than one job.
/// </summary>
public static class PeerSessionRequests
{
    /// <summary>
    /// Builds <c>POST /Sessions/Playing</c> or <c>POST /Sessions/Playing/Stopped</c>.
    /// </summary>
    /// <param name="stopped">True for the stop report.</param>
    /// <param name="grant">The grant (its Peer ids and play method).</param>
    /// <param name="positionTicks">The position in ticks.</param>
    /// <param name="isPaused">Whether playback is paused.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException">The grant has no Peer PlaySessionId.</exception>
    public static PeerRequest Report(bool stopped, Grant grant, long positionTicks, bool isPaused)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.PeerPlaySessionId);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("ItemId", grant.PeerItemId.ToString("N"));
            writer.WriteString("MediaSourceId", grant.PeerMediaSourceId.ToString("N"));
            writer.WriteString("PlaySessionId", grant.PeerPlaySessionId);
            writer.WriteString("PlayMethod", grant.Method == PlayMethod.Transcode ? "Transcode" : "DirectPlay");
            writer.WriteBoolean("CanSeek", true);
            writer.WriteNumber("PositionTicks", Math.Clamp(positionTicks, 0, grant.RuntimeTicks + 36_000_000_000L));
            writer.WriteBoolean("IsPaused", isPaused);
            writer.WriteEndObject();
        }

        return PeerRequest.Post(stopped ? "/Sessions/Playing/Stopped" : "/Sessions/Playing", stream.ToArray()).WithTrust(TrustRequirement.PinAndAuthFresh);
    }

    /// <summary>
    /// Builds <c>POST /Sessions/Playing/Ping?playSessionId=..</c>.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException">The grant has no Peer PlaySessionId.</exception>
    public static PeerRequest Ping(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.PeerPlaySessionId);
        return PeerRequest.Post("/Sessions/Playing/Ping?playSessionId=" + Uri.EscapeDataString(grant.PeerPlaySessionId), null).WithTrust(TrustRequirement.PinAndAuthFresh);
    }
}

/// <summary>
/// Tells a Peer what its share user is playing, keeps a paused transcode alive and ends the Peer job when playback stops (DESIGN 6.8). All Peer calls go
/// through a bounded queue (256, the oldest dropped) so core's event thread never waits. A report is forwarded only when the grant found by
/// PlaySessionId also belongs to the local user of the event (W2 N-19).
/// </summary>
public sealed class PeerSessionReporter : IAsyncDisposable
{
    /// <summary>The most queued reports.</summary>
    public const int QueueSize = 256;

    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);

    private readonly GrantStore _grants;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly HlsJobControl _jobs;
    private readonly StreamSlots _slots;
    private readonly TimeProvider _time;
    private readonly ILogger<PeerSessionReporter> _logger;
    private readonly Channel<Func<CancellationToken, Task>> _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(QueueSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _positions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStart = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _pending;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSessionReporter"/> class.
    /// </summary>
    /// <param name="grants">The grants.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="jobs">The job control (the scoped kill).</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PeerSessionReporter(GrantStore grants, IPeerEndpoints peers, PeerClient client, HlsJobControl jobs, StreamSlots slots, TimeProvider time, ILogger<PeerSessionReporter> logger)
    {
        _grants = grants;
        _peers = peers;
        _client = client;
        _jobs = jobs;
        _slots = slots;
        _time = time;
        _logger = logger;
        _slots.HlsSuperseded += OnSuperseded;
    }

    /// <summary>Gets the number of queued or running reports (tests).</summary>
    public int Pending => _queue.Reader.Count + Volatile.Read(ref _pending);

    /// <summary>
    /// Starts the queue consumer.
    /// </summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);

    /// <summary>
    /// A local session started playing.
    /// </summary>
    /// <param name="note">The event.</param>
    public void OnStart(PlaybackNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (Find(note) is not { } grant)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (_lastStart.TryGetValue(grant.GrantId, out var started) && now - started < PingEvery)
        {
            return; // a client that repeats PlaybackStart cannot fill the queue (Warden 07 L-3)
        }

        _lastStart[grant.GrantId] = now;
        _positions[grant.GrantId] = note.PositionTicks ?? 0;
        _lastPing[grant.GrantId] = now;
        Enqueue(token => SendAsync(PeerSessionRequests.Report(false, grant, note.PositionTicks ?? 0, note.IsPaused), grant, token));
    }

    /// <summary>
    /// A local session reported progress: at most one ping per 20 seconds per grant.
    /// </summary>
    /// <param name="note">The event.</param>
    public void OnProgress(PlaybackNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (Find(note) is not { } grant)
        {
            return;
        }

        _positions[grant.GrantId] = note.PositionTicks ?? _positions.GetValueOrDefault(grant.GrantId);
        PingIfDue(grant);
    }

    /// <summary>
    /// A local session stopped: tell the Peer, end its job, give the slots back and drop the grant.
    /// </summary>
    /// <param name="note">The event.</param>
    public void OnStop(PlaybackNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (Find(note) is not { } grant)
        {
            return;
        }

        var position = note.PositionTicks ?? _positions.GetValueOrDefault(grant.GrantId);
        _positions.TryRemove(grant.GrantId, out _);
        _lastPing.TryRemove(grant.GrantId, out _);
        _lastStart.TryRemove(grant.GrantId, out _);

        // Home's own books are settled at once, on the caller's thread: a full or slow report queue can never keep a slot or a grant alive (Warden 07 L-3).
        var request = PeerSessionRequests.Report(true, grant, position, false);
        _slots.ReleaseHls(grant.GrantId);
        _slots.ReleaseStatic(grant.LocalUserId, grant.SlotItem);
        _grants.Remove(grant.GrantId);
        Enqueue(async token =>
        {
            await SendAsync(request, grant, token).ConfigureAwait(false);
            if (grant.Method == PlayMethod.Transcode && grant.PeerJobStarted)
            {
                await _jobs.SendScopedKillAsync(grant, token).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// The 30 second keepalive: pings every grant that is playing, so a paused transcode outlives the Peer's 60 second idle kill.
    /// </summary>
    public void KeepAlive()
    {
        foreach (var grantId in _positions.Keys)
        {
            if (_grants.Get(grantId) is { } grant)
            {
                PingIfDue(grant, force: true);
            }
            else
            {
                _positions.TryRemove(grantId, out _);
                _lastPing.TryRemove(grantId, out _);
            }
        }
    }

    /// <summary>
    /// Waits until the queue is empty (tests and shutdown).
    /// </summary>
    /// <param name="timeout">The longest to wait.</param>
    /// <returns>True when it emptied.</returns>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        return Pending == 0;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // the host and the container both dispose it at shutdown
        }

        _slots.HlsSuperseded -= OnSuperseded;
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }

        _stop.Dispose();
    }

    private Grant? Find(PlaybackNote note)
    {
        if (string.IsNullOrEmpty(note.PlaySessionId))
        {
            return null;
        }

        // FindByPlaySession only returns a grant of this user: one local user can never stop another user's Peer transcode.
        var grant = _grants.FindByPlaySession(note.UserId, note.PlaySessionId);
        return grant is not null && grant.LocalUserId == note.UserId && !string.IsNullOrEmpty(grant.PeerPlaySessionId) ? grant : null;
    }

    private void PingIfDue(Grant grant, bool force = false)
    {
        var now = _time.GetUtcNow();
        var due = !_lastPing.TryGetValue(grant.GrantId, out var last) || now - last >= PingEvery || force;
        if (!due || (force && _lastPing.TryGetValue(grant.GrantId, out var recent) && now - recent < TimeSpan.FromSeconds(5)))
        {
            return;
        }

        _lastPing[grant.GrantId] = now;
        Enqueue(token => SendAsync(PeerSessionRequests.Ping(grant), grant, token));
    }

    // The next episode took the slot of an HLS stream that went quiet: its job on the Peer is stopped now (best effort, off the caller's thread), not at the Peer's idle timeout.
    private void OnSuperseded(IReadOnlyList<string> grantIds)
    {
        foreach (var id in grantIds)
        {
            if (_grants.Get(id) is { Method: PlayMethod.Transcode, PeerJobStarted: true } grant)
            {
                Enqueue(token => _jobs.SendScopedKillAsync(grant, token));
            }
        }
    }

    private void Enqueue(Func<CancellationToken, Task> work)
    {
        _queue.Writer.TryWrite(work); // a full queue drops its oldest item
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _pending);
                try
                {
                    await work(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // A report never stops the queue; only the exception type is logged.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogInformation("A session report failed ({Type})", ex.GetType().Name);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SendAsync(PeerRequest request, Grant grant, CancellationToken token)
    {
        var peer = _peers.Find(grant.PeerKey);
        if (peer is null)
        {
            return;
        }

        try
        {
            using var response = await _client.SendAsync(peer, request, PeerCallProfile.SessionReport, token).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("A session report to peer {Peer} failed ({Kind})", Redact.Id(grant.PeerKey), ex.Kind);
        }
    }
}
