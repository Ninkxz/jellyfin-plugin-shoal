using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K18 acceptance (DESIGN 6.6a step 7, T-CLI-16): joins, item lanes, the two-slot gate with 16 waiting, one open lane per user, the detached abort and
/// the coalescing rules (a failure is not retained, nothing is cached after completion).
/// </summary>
public sealed class PeerExtractionGateTests
{
    private static SubtitleFetch Ok(string text = "ok") => new(StatusCodes.Status200OK, System.Text.Encoding.UTF8.GetBytes(text));

    private static string Body(BrokerResult result) => System.Text.Encoding.UTF8.GetString(result.Fetch!.Body.Span);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task IdenticalRequests_JoinOnePeerCall_AndAllGetTheSameBuffer()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var a = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t, "shared"), default);
        await WaitUntilAsync(() => gate.Started == 1);
        var b = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t, "second"), default);
        var c = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t, "third"), default);

        gate.Release();
        var results = await Task.WhenAll(a, b, c);

        Assert.Equal(1, gate.Started);
        Assert.All(results, r => Assert.Equal("shared", Body(r)));
    }

    [Fact]
    public async Task ADifferentRequestForTheSameItem_WaitsForTheLane_ThenMakesItsOwnCall()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var first = broker.RunAsync("p", "item", "req-1", Guid.NewGuid(), true, t => gate.RunAsync(t), default);
        await WaitUntilAsync(() => gate.Started == 1);
        var second = broker.RunAsync("p", "item", "req-2", Guid.NewGuid(), true, t => gate.RunAsync(t), default);
        await Task.Delay(100);

        Assert.Equal(1, gate.Started); // one Peer call per lane at a time

        gate.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(2, gate.Started);
        Assert.Equal(1, gate.Peak);
    }

    [Fact]
    public async Task TheGate_AllowsTwoCallsPerPeer_AndAnotherPeerIsIndependent()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var other = new Gatekeeper();
        var tasks = Enumerable.Range(0, 3).Select(i => broker.RunAsync("p", "item" + i, "req" + i, Guid.NewGuid(), true, t => gate.RunAsync(t), default)).ToList();
        await WaitUntilAsync(() => gate.Started == 2);
        await Task.Delay(100);
        var elsewhere = broker.RunAsync("q", "itemq", "reqq", Guid.NewGuid(), true, t => other.RunAsync(t), default);

        Assert.Equal(2, gate.Started);
        await WaitUntilAsync(() => other.Started == 1);

        gate.Release();
        other.Release();
        await Task.WhenAll(tasks.Append(elsewhere));
        Assert.Equal(3, gate.Started);
        Assert.Equal(2, gate.Peak);
    }

    [Fact]
    public async Task TheSeventeenthWaiter_Gets429WithRetryAfter30_AtOnce()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var running = Enumerable.Range(0, 2).Select(i => broker.RunAsync("p", "run" + i, "run" + i, Guid.NewGuid(), true, t => gate.RunAsync(t), default)).ToList();
        await WaitUntilAsync(() => gate.Started == 2);
        var waiting = Enumerable.Range(0, PeerExtractionGate.MaxWaiting).Select(i => broker.RunAsync("p", "wait" + i, "wait" + i, Guid.NewGuid(), true, t => gate.RunAsync(t), default)).ToList();
        await Task.Delay(200);

        var refused = await broker.RunAsync("p", "extra", "extra", Guid.NewGuid(), true, t => gate.RunAsync(t), default);

        Assert.Equal(BrokerStatus.Busy, refused.Status);
        Assert.Equal(30, refused.RetryAfterSeconds);
        Assert.Equal(2, gate.Started);
        gate.Release();
        await Task.WhenAll(running.Concat(waiting));
    }


    [Fact]
    public async Task Warden07_L2_OneUser_CannotHaveMoreThanThreeRequestsWaiting_ButOthersStillGetIn()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var hog = Guid.NewGuid();
        var running = Enumerable.Range(0, 2).Select(i => broker.RunAsync("p", "run" + i, "run" + i, Guid.NewGuid(), true, t => gate.RunAsync(t), default)).ToList();
        await WaitUntilAsync(() => gate.Started == 2);
        var waiting = Enumerable.Range(0, PeerExtractionGate.MaxWaitingPerUser).Select(i => broker.RunAsync("p", "hog" + i, "hog" + i, hog, true, t => gate.RunAsync(t), default)).ToList();
        await Task.Delay(200);

        var refused = await broker.RunAsync("p", "hog-extra", "hog-extra", hog, true, t => gate.RunAsync(t), default);
        var other = broker.RunAsync("p", "other", "other", Guid.NewGuid(), true, t => gate.RunAsync(t), default);
        await Task.Delay(100);

        Assert.Equal(BrokerStatus.Busy, refused.Status);
        Assert.Equal(30, refused.RetryAfterSeconds);
        gate.Release();
        var all = await Task.WhenAll(running.Concat(waiting).Append(other));
        Assert.All(all, r => Assert.Equal(BrokerStatus.Done, r.Status));
        var again = await broker.RunAsync("p", "hog-late", "hog-late", hog, true, _ => Task.FromResult(Ok()), default);
        Assert.Equal(BrokerStatus.Done, again.Status); // the allowance came back
    }
    [Fact]
    public async Task AUser_MayHoldOneOpenLane_ButHlsSegmentsAreExempt()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var user = Guid.NewGuid();
        var first = broker.RunAsync("p", "a", "a", user, false, t => gate.RunAsync(t), default);
        await WaitUntilAsync(() => gate.Started == 1);

        var second = await broker.RunAsync("p", "b", "b", user, false, t => gate.RunAsync(t), default);
        var exempt = broker.RunAsync("p", "c", "c", user, true, t => gate.RunAsync(t), default);
        var other = broker.RunAsync("p", "d", "d", Guid.NewGuid(), false, t => gate.RunAsync(t), default);

        Assert.Equal(BrokerStatus.UserLane, second.Status);
        Assert.Equal(10, second.RetryAfterSeconds);
        gate.Release();
        await Task.WhenAll(first, exempt, other);
        var again = await broker.RunAsync("p", "e", "e", user, false, _ => Task.FromResult(Ok()), default);
        Assert.Equal(BrokerStatus.Done, again.Status); // the lane was given back
    }

    [Fact]
    public async Task TheLastWaiterLeaving_CancelsAYoungCall()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        using var leave = new CancellationTokenSource();
        var request = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t), leave.Token);
        await WaitUntilAsync(() => gate.Started == 1);

        await leave.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        await WaitUntilAsync(() => gate.Cancelled);
        await WaitUntilAsync(() => broker.ActiveCalls == 0);
    }

    [Fact]
    public async Task ACallOlderThanTenSeconds_RunsOn_AfterItsLastWaiterLeaves_AndAJoinerGetsIt()
    {
        var clock = new ManualTimeProvider();
        var broker = new PeerExtractionGate(clock);
        var gate = new Gatekeeper();
        using var leave = new CancellationTokenSource();
        var request = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t, "late"), leave.Token);
        await WaitUntilAsync(() => gate.Started == 1);
        clock.Advance(PeerExtractionGate.DetachAfter + TimeSpan.FromSeconds(1));

        await leave.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Task.Delay(100);
        Assert.False(gate.Cancelled);
        var joiner = broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, t => gate.RunAsync(t, "never"), default);
        gate.Release();

        Assert.Equal("late", Body(await joiner));
        Assert.Equal(1, gate.Started); // the joiner did not restart the extraction
    }

    [Fact]
    public async Task AFailedCall_IsNotRetained_AndNothingIsCachedAfterCompletion()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var calls = 0;
        Task<SubtitleFetch> Work(CancellationToken token)
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult(n == 1 ? new SubtitleFetch(502, ReadOnlyMemory<byte>.Empty) : Ok("fresh"));
        }

        var failed = await broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, Work, default);
        var retried = await broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, Work, default);
        var again = await broker.RunAsync("p", "item", "req", Guid.NewGuid(), false, Work, default);

        Assert.Equal(502, failed.Fetch!.Status);
        Assert.Equal(200, retried.Fetch!.Status);
        Assert.Equal(3, calls); // a finished call is gone: the next request asks the Peer again
        Assert.Equal(200, again.Fetch!.Status);
    }

    [Fact]
    public async Task ADifferentPeerOrMediaSource_NeverJoins()
    {
        var broker = new PeerExtractionGate(new ManualTimeProvider());
        var gate = new Gatekeeper();
        var a = broker.RunAsync("p1", "p1|item|ms", "p1|item|ms|req", Guid.NewGuid(), false, t => gate.RunAsync(t, "one"), default);
        var b = broker.RunAsync("p2", "p2|item|ms", "p2|item|ms|req", Guid.NewGuid(), false, t => gate.RunAsync(t, "two"), default);
        await WaitUntilAsync(() => gate.Started == 2);
        gate.Release();

        var results = await Task.WhenAll(a, b);

        Assert.Equal("one", Body(results[0]));
        Assert.Equal("two", Body(results[1]));
    }

    private sealed class Gatekeeper
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _running;
        private int _peak;
        private int _cancelled;

        public int Started => Volatile.Read(ref _started);

        public int Peak => Volatile.Read(ref _peak);

        public bool Cancelled => Volatile.Read(ref _cancelled) == 1;

        public void Release() => _release.TrySetResult();

        public async Task<SubtitleFetch> RunAsync(CancellationToken token, string text = "ok")
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
            }

            try
            {
                await _release.Task.WaitAsync(token);
                return Ok(text);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _cancelled, 1);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
