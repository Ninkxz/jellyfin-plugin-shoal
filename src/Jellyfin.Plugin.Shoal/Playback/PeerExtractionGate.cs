#pragma warning disable SA1600 // internal plumbing; the public surface is documented

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// How a brokered subtitle call ended.
/// </summary>
public enum BrokerStatus
{
    /// <summary>The call ran (or was joined); <see cref="BrokerResult.Fetch"/> holds its outcome.</summary>
    Done,

    /// <summary>The gate and lanes are full, or the budget ran out while waiting: 429 with Retry-After 30.</summary>
    Busy,

    /// <summary>The user already has an open lane: 429 with Retry-After 10.</summary>
    UserLane,
}

/// <summary>
/// The validated outcome of one Peer subtitle call, shared by every request that joined it. Immutable.
/// </summary>
/// <param name="Status">200 when <paramref name="Body"/> is the rebuilt, canary-checked payload; otherwise the status to answer.</param>
/// <param name="Body">The rebuilt payload.</param>
public sealed record SubtitleFetch(int Status, ReadOnlyMemory<byte> Body);

/// <summary>
/// What the broker hands back.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Fetch">The shared outcome when <paramref name="Status"/> is <see cref="BrokerStatus.Done"/>.</param>
/// <param name="RetryAfterSeconds">The Retry-After for a busy answer.</param>
public sealed record BrokerResult(BrokerStatus Status, SubtitleFetch? Fetch, int RetryAfterSeconds);

/// <summary>
/// Keeps extraction on a Peer (subtitle files, and attachments from v2.6.6) from starving the Peer's connection pool (DESIGN 6.6a step 7): identical requests join one call, one call per item lane at
/// a time, two calls per peer at once with 16 waiting, one open lane per user, a shared call that survives its waiters after 10 s, and nothing is
/// cached once a call has no waiters.
/// </summary>
public sealed class PeerExtractionGate
{
    /// <summary>The Peer calls one peer may run at once.</summary>
    public const int GateSlots = 2;

    /// <summary>The requests that may wait on one peer's gate and lanes together.</summary>
    public const int MaxWaiting = 16;

    /// <summary>The requests of one user that may wait on the gate and lanes together (Warden 07 L-2).</summary>
    public const int MaxWaitingPerUser = 3;

    /// <summary>The budget of one request, waiting included.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(150);

    /// <summary>How long a call must have run before it survives the loss of every waiter.</summary>
    public static readonly TimeSpan DetachAfter = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _userLanes = [];
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _userWaiting = [];
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerExtractionGate"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public PeerExtractionGate(TimeProvider time) => _time = time;

    /// <summary>Gets the number of Peer calls that run or wait now (tests).</summary>
    public int ActiveCalls
    {
        get
        {
            lock (_lock)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>
    /// Runs or joins a call.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="itemKey">The (peer, item, media source) key of the lane.</param>
    /// <param name="requestKey">The full request key.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="exemptFromUserLane">True for HLS subtitle segments (6.4).</param>
    /// <param name="call">The Peer call; it must not throw other than for cancellation.</param>
    /// <param name="cancellationToken">The caller's abort token.</param>
    /// <returns>The outcome.</returns>
    public async Task<BrokerResult> RunAsync(string peerKey, string itemKey, string requestKey, Guid userId, bool exemptFromUserLane, Func<CancellationToken, Task<SubtitleFetch>> call, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        while (true)
        {
            Call? joined = null;
            Task? waitFor = null;
            Call? own = null;
            lock (_lock)
            {
                if (_calls.TryGetValue(requestKey, out var existing))
                {
                    existing.Waiters++;
                    joined = existing;
                }
                else if (_lanes.TryGetValue(itemKey, out var lane))
                {
                    if (WaitingOn(peerKey) >= MaxWaiting || _userWaiting.GetValueOrDefault(userId) >= MaxWaitingPerUser)
                    {
                        return new BrokerResult(BrokerStatus.Busy, null, 30);
                    }

                    _userWaiting[userId] = _userWaiting.GetValueOrDefault(userId) + 1;
                    _waiting[peerKey] = WaitingOn(peerKey) + 1;
                    waitFor = lane.Ended.Task;
                }
                else
                {
                    if (!exemptFromUserLane && _userLanes.GetValueOrDefault(userId) >= 1)
                    {
                        return new BrokerResult(BrokerStatus.UserLane, null, 10);
                    }

                    if (WaitingOn(peerKey) >= MaxWaiting || _userWaiting.GetValueOrDefault(userId) >= MaxWaitingPerUser)
                    {
                        return new BrokerResult(BrokerStatus.Busy, null, 30);
                    }

                    own = new Call { Waiters = 1, Cts = new CancellationTokenSource(Budget), Created = _time.GetUtcNow() };
                    var newLane = new Lane { Ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Owner = exemptFromUserLane ? Guid.Empty : userId };
                    _calls[requestKey] = own;
                    _lanes[itemKey] = newLane;
                    if (!exemptFromUserLane)
                    {
                        _userLanes[userId] = _userLanes.GetValueOrDefault(userId) + 1;
                    }

                    _userWaiting[userId] = _userWaiting.GetValueOrDefault(userId) + 1;
                    _waiting[peerKey] = WaitingOn(peerKey) + 1;
                    _ = Task.Run(() => ExecuteAsync(peerKey, itemKey, requestKey, userId, own, newLane, call), CancellationToken.None);
                }
            }

            if (joined is not null)
            {
                return await AwaitAsync(joined, cancellationToken).ConfigureAwait(false);
            }

            if (own is not null)
            {
                return await AwaitAsync(own, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var remaining = Budget - (_time.GetUtcNow() - started);
                await waitFor!.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new BrokerResult(BrokerStatus.Busy, null, 30);
            }
            finally
            {
                lock (_lock)
                {
                    _waiting[peerKey] = Math.Max(0, WaitingOn(peerKey) - 1);
                    ReleaseUserWaiting(userId);
                }
            }
        }
    }

    private int WaitingOn(string peerKey) => _waiting.GetValueOrDefault(peerKey);

    private void ReleaseUserWaiting(Guid userId)
    {
        var left = _userWaiting.GetValueOrDefault(userId) - 1;
        if (left <= 0)
        {
            _userWaiting.Remove(userId);
        }
        else
        {
            _userWaiting[userId] = left;
        }
    }

    private SemaphoreSlim GateOf(string peerKey)
    {
        lock (_lock)
        {
            if (!_gates.TryGetValue(peerKey, out var gate))
            {
                gate = new SemaphoreSlim(GateSlots);
                _gates[peerKey] = gate;
            }

            return gate;
        }
    }

    private async Task<BrokerResult> AwaitAsync(Call call, CancellationToken cancellationToken)
    {
        try
        {
            var result = await call.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_lock)
            {
                call.Waiters--;
                if (call.Waiters == 0 && !call.Done.Task.IsCompleted && (!call.Running || _time.GetUtcNow() - call.RunningSince < DetachAfter))
                {
                    _ = call.Cts.CancelAsync(); // the last waiter left a young call: stop it; an old one runs on and its result is discarded
                }
            }
        }
    }

    private async Task ExecuteAsync(string peerKey, string itemKey, string requestKey, Guid userId, Call call, Lane lane, Func<CancellationToken, Task<SubtitleFetch>> work)
    {
        var gate = GateOf(peerKey);
        var gateTaken = false;
        try
        {
            try
            {
                await gate.WaitAsync(call.Cts.Token).ConfigureAwait(false);
                gateTaken = true;
            }
            finally
            {
                lock (_lock)
                {
                    _waiting[peerKey] = Math.Max(0, WaitingOn(peerKey) - 1);
                    ReleaseUserWaiting(userId);
                }
            }

            lock (_lock)
            {
                call.Running = true;
                call.RunningSince = _time.GetUtcNow();
            }

            var fetch = await work(call.Cts.Token).ConfigureAwait(false);
            call.Done.TrySetResult(new BrokerResult(BrokerStatus.Done, fetch, 0));
        }
        catch (OperationCanceledException)
        {
            call.Done.TrySetResult(new BrokerResult(BrokerStatus.Busy, null, 30)); // the budget ran out while waiting, or every waiter left
        }
#pragma warning disable CA1031 // A failed call is never retained: every waiter gets a plain failure and the next request tries again.
        catch (Exception)
#pragma warning restore CA1031
        {
            call.Done.TrySetResult(new BrokerResult(BrokerStatus.Done, new SubtitleFetch(StatusCodes.Status502BadGateway, ReadOnlyMemory<byte>.Empty), 0));
        }
        finally
        {
            if (gateTaken)
            {
                gate.Release();
            }

            lock (_lock)
            {
                _calls.Remove(requestKey); // no cache after completion
                _lanes.Remove(itemKey);
                if (lane.Owner != Guid.Empty)
                {
                    var open = _userLanes.GetValueOrDefault(lane.Owner) - 1;
                    if (open <= 0)
                    {
                        _userLanes.Remove(lane.Owner);
                    }
                    else
                    {
                        _userLanes[lane.Owner] = open;
                    }
                }
            }

            lane.Ended.TrySetResult();
            call.Cts.Dispose();
        }
    }

    private sealed class Call
    {
        public TaskCompletionSource<BrokerResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Waiters { get; set; }

        public required CancellationTokenSource Cts { get; init; }

        public DateTimeOffset Created { get; init; }

        public bool Running { get; set; }

        public DateTimeOffset RunningSince { get; set; }
    }

    private sealed class Lane
    {
        public required TaskCompletionSource Ended { get; init; }

        public Guid Owner { get; init; }
    }
}
