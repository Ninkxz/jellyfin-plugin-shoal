using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K3a: the server-side batch (sequential, stop rules, Stop, switch off, settle pass, limits) against a fake core side.</summary>
public sealed class LocalDeleteServiceTests
{
    internal const string Peer = "11111111111111111111111111111111";
    private const string Hash = "abababababababababababababababababababababababababababababababab";

    [Fact]
    public async Task ItemsRunStrictlyOneAtATimeAndTheAuditLineComesBeforeEachCall()
    {
        using var f = new Fixture(3);
        var start = f.Service.Start(f.Admin, Peer, f.Targets);
        Assert.Equal(LocalDeleteBatchState.Running, start.State);
        Assert.All(start.Items, i => Assert.Equal(LocalDeleteItemState.Queued, i.State));
        var batch = await f.Finish(start.BatchId);

        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.All(batch.Items, i => Assert.Equal((LocalDeleteItemState.Done, LocalDeleteShared.Present, LocalDeleteHistory.Carried), (i.State, i.Shared!.Value, i.History!.Value)));
        Assert.Equal(1, f.Library.MaxParallel);
        Assert.Equal(1, f.Syncs); // one sync for the whole batch, not one per item
        var order = f.Log.ToArray();
        foreach (var t in f.Targets)
        {
            Assert.True(Array.IndexOf(order, "started " + t.Title) < Array.IndexOf(order, "delete " + t.Title));
            Assert.True(Array.IndexOf(order, "delete " + t.Title) < Array.IndexOf(order, "finished " + t.Title));
        }

        Assert.Equal(3, f.Handoffs);
        Assert.Null(f.Service.ActiveBatchId);
    }

    // W23-6: a batch that dies after a deletion still closes the audit record of every started item.
    [Fact]
    public async Task ABatchThatDiesAfterADeleteStillWritesTheFinishedLine()
    {
        using var f = new Fixture(2);
        f.Library.FindSharedThrows = true;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.All(batch.Items, i => Assert.Equal(LocalDeleteItemState.Done, i.State));
        var order = f.Log.ToArray();
        foreach (var t in f.Targets)
        {
            Assert.Single(order, l => l == "finished " + t.Title);
            Assert.True(Array.IndexOf(order, "started " + t.Title) < Array.IndexOf(order, "delete " + t.Title));
        }
    }

    [Theory]
    [InlineData("originUnavailable", "OriginUnavailable")]
    [InlineData("originRateLimited", "OriginRateLimited")]
    public async Task AFriendThatCannotProveStopsTheBatchAndNothingAfterIsTried(string word, string reasonName)
    {
        var reason = Enum.Parse<LocalDeleteReason>(reasonName);
        using var f = new Fixture(3);
        f.Origin.Fail[f.Targets[1].PeerItemId] = word;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[0].State);
        Assert.Equal((LocalDeleteItemState.Refused, reason), (batch.Items[1].State, batch.Items[1].Reason!.Value));
        Assert.Equal((LocalDeleteItemState.NotAttempted, reason), (batch.Items[2].State, batch.Items[2].Reason!.Value));
        Assert.Equal([f.Targets[0].Title], f.Library.Deleted);
    }

    // W23-3: a friend that reports the right length and digest but does not serve the same bytes at ranges Home picks is refused.
    [Fact]
    public async Task AFriendWhoseBytesDifferAtRandomRangesIsRefusedDespiteTheRightHash()
    {
        using var f = new Fixture(2);
        f.Targets[0] = f.Targets[0] with { Bytes = 2L << 30 }; // bigger than one range, so the random ranges are not all at 0
        f.Origin.Tamper.Add(f.Targets[0].PeerItemId);
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.ContentNotEquivalent), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[1].State);
        Assert.DoesNotContain(f.Targets[0].Title, f.Library.Deleted);
    }

    [Fact]
    public async Task EveryItemIsChallengedAtTheStartAndTwoRandomRangesInsideTheFile()
    {
        using var f = new Fixture(1);
        var target = f.Targets[0] with { Bytes = 10L << 30 };
        f.Targets[0] = target;
        var offsets = new List<long>();
        for (var run = 0; run < 3; run++)
        {
            while (f.Origin.Reads.TryDequeue(out _))
            {
            }

            await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
            var reads = f.Origin.Reads.ToArray();
            Assert.Equal(3, reads.Length);
            Assert.Equal(0, reads[0].Offset);
            Assert.All(reads, r => Assert.InRange(r.Offset + r.Length, 1, target.Bytes));
            Assert.All(reads, r => Assert.Equal(RemovalOriginClient.SampleBytes, r.Length));
            offsets.AddRange(reads.Skip(1).Select(static r => r.Offset));
        }

        Assert.True(offsets.Distinct().Count() >= 5, "random ranges change from run to run");
    }

    [Fact]
    public async Task AFriendThatCannotServeTheRangesStopsTheBatch()
    {
        using var f = new Fixture(2);
        f.Origin.SampleFails.Add(f.Targets[0].PeerItemId);
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.OriginUnavailable), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(LocalDeleteItemState.NotAttempted, batch.Items[1].State);
        Assert.Empty(f.Library.Deleted);
    }

    // Lab fix (Pilot 2 check 2): the friend allows 6 proofs a minute per share (burst 3) and answers 429 beyond that. A batch of a few dozen
    // movies must delete every one: the batch paces its proofs below the friend's limit.
    [Fact]
    public async Task ABatchLargerThanTheFriendsProofLimitDeletesEveryMovie()
    {
        using var f = new Fixture(30);
        f.Origin.Limit = new RateLimiter(f.Time, perMinute: 6, burst: 3); // RemovalOriginProof's per-share bucket, on the same clock
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.All(batch.Items, i => Assert.Equal((LocalDeleteItemState.Done, (LocalDeleteReason?)null), (i.State, i.Reason)));
        Assert.Equal(30, f.Library.Deleted.Count);
        Assert.Equal(0, f.Origin.Limited); // paced below the limit, not only retried
    }

    // Lab fix: a 429 (another friend, the friend's own admin, the global bucket) is a wait, not a stop: the same item is tried again after
    // Retry-After, and while it waits the batch says so (state checking, reason originRateLimited).
    [Fact]
    public async Task ARateLimitedProofWaitsAndRetriesTheSameItem()
    {
        using var f = new Fixture(2);
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = 2;
        LocalDeleteItemDto? waiting = null;
        f.OnFriendWait = () => waiting ??= f.Service.Current()!.Items[0];
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.All(batch.Items, i => Assert.Equal((LocalDeleteItemState.Done, (LocalDeleteReason?)null), (i.State, i.Reason)));
        Assert.Equal((LocalDeleteItemState.Checking, LocalDeleteReason.OriginRateLimited), (waiting!.State, waiting.Reason!.Value));
        Assert.Equal(3, f.Origin.Proofs[f.Targets[0].PeerItemId]); // two 429s, then the proof
        Assert.True(f.FriendWaited >= TimeSpan.FromSeconds(20), "waited the friend's Retry-After twice");
    }

    [Fact]
    public async Task ARateLimitedRangeReadWaitsAndRetries()
    {
        using var f = new Fixture(1);
        f.Origin.SampleLimitedTimes[f.Targets[0].PeerItemId] = 1;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[0].State);
        Assert.Equal(4, f.Origin.Reads.Count); // the limited range was read again
    }

    // A friend that stays limited is waited for a bounded time, then the batch stops as before (nothing deleted, the rest not tried).
    [Fact]
    public async Task AFriendThatStaysRateLimitedStopsTheBatchAfterABoundedWait()
    {
        using var f = new Fixture(2);
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = int.MaxValue;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.OriginRateLimited), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal((LocalDeleteItemState.NotAttempted, LocalDeleteReason.OriginRateLimited), (batch.Items[1].State, batch.Items[1].Reason!.Value));
        Assert.Empty(f.Library.Deleted);
        Assert.InRange(f.FriendWaited, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)); // at most 2 min of Retry-After, plus the proof spacing between tries
    }

    // W24-1: a hostile friend answers 429 with Retry-After 1 forever. The item's cap is real elapsed time (pacing included), not the sum of
    // the friend's Retry-After values: the item ends after about 2 minutes, not about 24.
    [Fact]
    public async Task ARetryAfterOneFriendHoldsAnItemAtMostTheCapInRealTime()
    {
        using var f = new Fixture(2);
        f.Origin.RetryAfterSeconds = 1;
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = int.MaxValue;
        var began = f.Time.GetUtcNow();
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.OriginRateLimited), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal((LocalDeleteItemState.NotAttempted, LocalDeleteReason.OriginRateLimited), (batch.Items[1].State, batch.Items[1].Reason!.Value));
        Assert.Empty(f.Library.Deleted);
        Assert.InRange(f.Time.GetUtcNow() - began, TimeSpan.Zero, LocalDeleteService.MaxFriendWait + TimeSpan.FromSeconds(1));
    }

    // W24-1: a friend that takes minutes to answer 429 holds the item that long; that time counts toward the item's cap.
    [Fact]
    public async Task ASlowRateLimitedAnswerCountsTowardTheItemCap()
    {
        using var f = new Fixture(1);
        f.Origin.LimitedTakes = TimeSpan.FromMinutes(5);
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = int.MaxValue;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.OriginRateLimited), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(1, f.Origin.Proofs[f.Targets[0].PeerItemId]); // not asked again after 5 minutes of 429
        Assert.Empty(f.Library.Deleted);
    }

    // W24-1: each item stays under its own 2 minutes, but the batch's total friend waiting is capped too (MaxBatchFriendWait).
    [Fact]
    public async Task TheBatchStopsWhenItsTotalFriendWaitingReachesTheCap()
    {
        using var f = new Fixture(12);
        foreach (var t in f.Targets)
        {
            f.Origin.LimitedTimes[t.PeerItemId] = 8; // about 96 s of waiting per item: under the item cap, 12 items far over the batch cap
        }

        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        var refused = batch.Items.Single(static i => i.State == LocalDeleteItemState.Refused);
        Assert.Equal(LocalDeleteReason.OriginRateLimited, refused.Reason);
        Assert.InRange(batch.Items.Count(static i => i.State == LocalDeleteItemState.Done), 4, 7);
        Assert.All(batch.Items.SkipWhile(static i => i.State != LocalDeleteItemState.Refused).Skip(1), static i => Assert.Equal(LocalDeleteItemState.NotAttempted, i.State));
        // Counted: everything the friend forced. Not counted: Home's own pacing before each item's first proof and its successful retry (12 s each).
        var tried = batch.Items.Count(static i => i.State != LocalDeleteItemState.NotAttempted);
        Assert.InRange(f.FriendWaited, LocalDeleteService.MaxBatchFriendWait - TimeSpan.FromMinutes(2), LocalDeleteService.MaxBatchFriendWait + (2 * tried * LocalDeleteService.ProofSpacing));
    }

    // W24-1 run cap: the whole batch may take its items' hashing deadlines plus an allowance each plus the batch's friend waiting.
    // A run beyond that ends before the next item (nothing deleted for it).
    [Fact]
    public async Task ARunPastItsRunCapStopsBeforeTheNextItem()
    {
        using var f = new Fixture(3);
        f.ProbeTakes = TimeSpan.FromMinutes(20); // past the cap after two items: 3 x (6.25 + 3) + 10 = 37.75 minutes
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal([LocalDeleteItemState.Done, LocalDeleteItemState.Done, LocalDeleteItemState.NotAttempted], batch.Items.Select(static i => i.State));
        Assert.Equal(LocalDeleteReason.OriginUnavailable, batch.Items[2].Reason);
        Assert.Equal(2, f.Library.Deleted.Count);
    }

    // The run cap never cuts a legitimate long hash: two 100 GiB files that each hash for 129 of their 130 minutes both finish.
    [Fact]
    public async Task TheRunCapDoesNotCutHugeFilesThatHashWithinTheirDeadline()
    {
        using var f = new Fixture(2, RemovalPolicy.MaxDeleteFileBytes);
        f.ProbeTakes = RemovalPolicy.DeleteHashingMaximum - TimeSpan.FromMinutes(1);
        Assert.True(LocalDeleteService.RunCapFor(f.Targets) >= (2 * RemovalPolicy.DeleteHashingMaximum) + LocalDeleteService.MaxBatchFriendWait);
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.Equal(2, f.Library.Deleted.Count);
    }

    // W24-1 stall rule: a friend that answers each proof near its deadline (sends nothing for most of it) is slow; after MaxSlowItems slow
    // proofs the batch stops (those items were proved and end normally; the rest are not tried).
    [Fact]
    public async Task AFriendThatStallsNearTheDeadlineStopsTheBatchAfterTwoSlowItems()
    {
        using var f = new Fixture(4);
        f.Origin.ProofTakes = RemovalPolicy.DeleteHashingDeadlineFor(f.Targets[0].Bytes) * 0.9;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal([LocalDeleteItemState.Done, LocalDeleteItemState.Done, LocalDeleteItemState.NotAttempted, LocalDeleteItemState.NotAttempted], batch.Items.Select(static i => i.State));
        Assert.Equal(LocalDeleteReason.OriginUnavailable, batch.Items[2].Reason);
    }

    // W24-2: Stop during a friend wait ends the wait at once; the item is not attempted, nothing is deleted, no audit line.
    [Fact]
    public async Task StopDuringAFriendWaitEndsTheWaitPromptly()
    {
        using var f = new Fixture(2);
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = int.MaxValue;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.FriendDelay = async (by, token) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var id = f.Service.Start(f.Admin, Peer, f.Targets).BatchId;
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Service.Stop(id);
        var batch = await f.Finish(id);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.All(batch.Items, static i => Assert.Equal((LocalDeleteItemState.NotAttempted, (LocalDeleteReason?)null), (i.State, i.Reason)));
        Assert.Empty(f.Library.Deleted);
        Assert.DoesNotContain(f.Log, static l => l.StartsWith("started", StringComparison.Ordinal));
    }

    // W24-3: the friend's Retry-After is clamped to 1 to 30 s; none sent means 10 s.
    [Theory]
    [InlineData(9999, 30)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(null, 10)]
    public async Task TheFriendsRetryAfterIsClamped(int? sent, int honoured)
    {
        using var f = new Fixture(1);
        f.Origin.RetryAfterSeconds = sent;
        f.Origin.LimitedTimes[f.Targets[0].PeerItemId] = 1;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[0].State);
        Assert.Equal(TimeSpan.FromSeconds(honoured), f.FriendWaits[0]);
    }

    // Lab fix (Pilot 2 check 3): the friend proved a different file (the client's word for another length or runtime). That is the identical
    // content proof failing, an item refusal; it does not look like an unreachable friend and does not stop the batch.
    [Fact]
    public async Task AFriendCopyThatDiffersIsContentNotEquivalentAndTheBatchGoesOn()
    {
        using var f = new Fixture(2);
        f.Origin.Fail[f.Targets[0].PeerItemId] = "contentNotEquivalent";
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.ContentNotEquivalent), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[1].State);
        Assert.Equal([f.Targets[1].Title], f.Library.Deleted);
    }

    [Fact]
    public async Task NotConfirmedStopsTheBatch()
    {
        using var f = new Fixture(2);
        f.Library.Outcomes[f.Targets[0].LocalItemId] = LocalDeleteOutcome.Unconfirmed;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal((LocalDeleteItemState.NotConfirmed, LocalDeleteReason.NotConfirmed), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(LocalDeleteItemState.NotAttempted, batch.Items[1].State);
        Assert.Contains("finished " + f.Targets[0].Title, f.Log);
    }

    [Theory]
    [InlineData("digest", "ContentNotEquivalent")]
    [InlineData("length", "ContentNotEquivalent")]
    [InlineData("history", "HistoryChanged")]
    [InlineData("access", "AccessNotShared")]
    [InlineData("folder", "FolderNotDedicated")]
    [InlineData("localProbe", "RemovalChanged")]
    [InlineData("resolve", "CandidateChanged")]
    [InlineData("deleteFailed", "DeleteFailed")]
    public async Task AnItemRefusalSkipsOnlyThatItem(string kind, string reasonName)
    {
        var reason = Enum.Parse<LocalDeleteReason>(reasonName);
        using var f = new Fixture(2);
        var first = f.Targets[0];
        switch (kind)
        {
            case "digest": f.Origin.Digest[first.PeerItemId] = new string('c', 64); break;
            case "length": f.Origin.Length[first.PeerItemId] = first.Bytes + 1; break;
            case "history": f.Library.HistoryMoves.Add(first.LocalItemId); break;
            case "access": f.Library.AccessDenied.Add(first.LocalItemId); break;
            case "folder": f.Library.InspectRefusal[first.LocalItemId] = LocalDeleteReason.FolderNotDedicated; break;
            case "localProbe": f.ProbeFails.Add(first.Path); break;
            case "resolve": f.Library.Unresolvable.Add(first.LocalItemId); break;
            case "deleteFailed": f.Library.Outcomes[first.LocalItemId] = LocalDeleteOutcome.Failed; break;
        }

        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.Equal((LocalDeleteItemState.Refused, reason), (batch.Items[0].State, batch.Items[0].Reason!.Value));
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[1].State);
        Assert.DoesNotContain(first.Title, f.Library.Deleted);
    }

    [Fact]
    public async Task StopEndsTheBatchAfterTheItemInFlight()
    {
        using var f = new Fixture(3);
        var gate = f.Library.Block(f.Targets[0].LocalItemId);
        var id = f.Service.Start(f.Admin, Peer, f.Targets).BatchId;
        await f.Library.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = f.Service.Stop(id)!;
        Assert.True(stopping.Stopping);
        Assert.Equal(LocalDeleteItemState.Deleting, stopping.Items[0].State);
        Assert.True(f.Service.Stop(id)!.Stopping); // idempotent
        gate.SetResult();
        var batch = await f.Finish(id);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.False(batch.Stopping);
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[0].State);
        Assert.All(batch.Items.Skip(1), i => Assert.Equal(LocalDeleteItemState.NotAttempted, i.State));
        Assert.Single(f.Library.Deleted);
    }

    [Theory]
    [InlineData("switchOff", "Disabled")]
    [InlineData("adminLost", "AdministratorRequired")]
    public async Task ConsentIsCheckedBeforeEveryItem(string change, string reasonName)
    {
        var reason = Enum.Parse<LocalDeleteReason>(reasonName);
        using var f = new Fixture(3);
        var gate = f.Library.Block(f.Targets[0].LocalItemId);
        var id = f.Service.Start(f.Admin, Peer, f.Targets).BatchId;
        await f.Library.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "switchOff") f.Enabled = false;
        else f.Library.AdminValid = false;
        gate.SetResult();
        var batch = await f.Finish(id);
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.Equal(LocalDeleteItemState.Done, batch.Items[0].State);
        Assert.All(batch.Items.Skip(1), i => Assert.Equal((LocalDeleteItemState.NotAttempted, reason), (i.State, i.Reason!.Value)));
    }

    // W23-1 (High): turning the switch off while an item is being hashed must stop that item too, not only the ones after it.
    [Theory]
    [InlineData(true)] // the Enabled route: switch saved off and StopRunning
    [InlineData(false)] // only the saved switch changed (another tab, a config edit)
    public async Task SwitchingOffDuringAHashStopsThatItemBeforeTheDelete(bool stopRunning)
    {
        using var f = new Fixture(2);
        f.HashGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = f.Service.Start(f.Admin, Peer, f.Targets).BatchId;
        await f.HashReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Enabled = false;
        if (stopRunning)
        {
            f.Service.StopRunning();
        }
        else
        {
            f.HashGate.SetResult();
        }

        var batch = await f.Finish(id);
        Assert.Empty(f.Library.Deleted);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("delete ", StringComparison.Ordinal) || l.StartsWith("started ", StringComparison.Ordinal));
        Assert.Equal(LocalDeleteBatchState.Stopped, batch.State);
        Assert.All(batch.Items, i => Assert.NotEqual(LocalDeleteItemState.Done, i.State));
        Assert.All(batch.Items, i => Assert.Equal(LocalDeleteReason.Disabled, i.Reason));
    }

    [Fact]
    public async Task TheDeleteCallItselfIsGivenTheSwitch()
    {
        using var f = new Fixture(1);
        f.Library.SwitchOffInsideDelete = () => f.Enabled = false;
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.False(f.Library.AllowedAtDelete);
        Assert.Empty(f.Library.Deleted);
        Assert.Equal((LocalDeleteItemState.Refused, LocalDeleteReason.Disabled), (batch.Items[0].State, batch.Items[0].Reason!.Value));
    }

    [Fact]
    public async Task OneBatchAtATimeAndTheRequestLimits()
    {
        using var f = new Fixture(51);
        Assert.Equal("invalidRequest", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, f.Targets)).Message); // 51 > 50
        Assert.Equal("invalidRequest", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [])).Message);
        Assert.Equal("invalidRequest", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [f.Targets[0], f.Targets[0]])).Message);
        Assert.Equal("invalidRequest", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, "22222222222222222222222222222222", [f.Targets[0]])).Message);
        f.Enabled = false;
        Assert.Equal("disabled", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [f.Targets[0]])).Message);
        f.Enabled = true;
        f.Library.AdminValid = false;
        Assert.Equal("administratorRequired", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [f.Targets[0]])).Message);
        f.Library.AdminValid = true;

        using (f.Gate.TryEnter())
        {
            Assert.Equal("busy", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [f.Targets[0]])).Message); // a legacy Restore runs
        }

        var gate = f.Library.Block(f.Targets[0].LocalItemId);
        var id = f.Service.Start(f.Admin, Peer, f.Targets.Take(50).ToArray()).BatchId;
        Assert.Equal(id, f.Service.ActiveBatchId);
        Assert.Equal("busy", Assert.Throws<InvalidOperationException>(() => f.Service.Start(f.Admin, Peer, [f.Targets[50]])).Message);
        Assert.Equal(id, f.Service.Current()!.BatchId);
        gate.SetResult();
        await f.Finish(id);
        Assert.NotNull(f.Gate.TryEnter()); // released for Restore
    }

    [Fact]
    public async Task ACopyThatDoesNotAppearIsPendingWithoutHistoryAndNotAFailure()
    {
        using var f = new Fixture(2);
        f.Library.SharedFor.Remove(f.Targets[1].LocalItemId);
        var batch = await f.Finish(f.Service.Start(f.Admin, Peer, f.Targets).BatchId);
        Assert.Equal(LocalDeleteBatchState.Finished, batch.State);
        Assert.Equal((LocalDeleteShared.Present, LocalDeleteHistory.Carried), (batch.Items[0].Shared!.Value, batch.Items[0].History!.Value));
        Assert.Equal((LocalDeleteItemState.Done, LocalDeleteShared.Pending, LocalDeleteHistory.NotCarried), (batch.Items[1].State, batch.Items[1].Shared!.Value, batch.Items[1].History!.Value));
        Assert.True(f.Waited >= LocalDeleteService.SettleWait, "waited the 90 s");
        Assert.True(f.Waited < LocalDeleteService.SettleWait + LocalDeleteService.SettlePoll + LocalDeleteService.SettlePoll);
        Assert.Contains("finished " + f.Targets[1].Title, f.Log);
    }

    [Fact]
    public async Task NoReplyCarriesAPathAndBatchesExpireOrVanishOnRestart()
    {
        using var f = new Fixture(2);
        var id = f.Service.Start(f.Admin, Peer, f.Targets).BatchId;
        var batch = await f.Finish(id);
        var text = JsonSerializer.Serialize(batch, AdminJson.Options) + JsonSerializer.Serialize(f.Service.Current(), AdminJson.Options);
        Assert.All(f.Targets, t => Assert.DoesNotContain("media", text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Hash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Targets[0].PeerItemId.ToString("N"), text, StringComparison.Ordinal);
        Assert.NotNull(f.Service.Get(id));
        f.Time.Advance(LocalDeleteService.KeptFor + TimeSpan.FromSeconds(1));
        Assert.Null(f.Service.Get(id));
        Assert.Null(f.Service.Current());
        Assert.Null(f.Service.Stop(id));
        using var restarted = new Fixture(1);
        Assert.Null(restarted.Service.Get(id));
    }

    [Fact]
    public async Task OnlyTheLastFiveEndedBatchesAreKept()
    {
        using var f = new Fixture(6);
        var ids = new List<string>();
        foreach (var t in f.Targets)
        {
            var id = f.Service.Start(f.Admin, Peer, [t]).BatchId;
            await f.Finish(id);
            ids.Add(id);
        }

        Assert.Null(f.Service.Get(ids[0]));
        Assert.All(ids.Skip(1), id => Assert.NotNull(f.Service.Get(id)));
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly Guid Admin = Guid.NewGuid();
        internal readonly ManualTimeProvider Time = new();
        internal readonly RemovalGate Gate = new();
        internal readonly ConcurrentQueue<string> Log = new();
        internal readonly HashSet<string> ProbeFails = [];
        internal readonly FakeLibrary Library;
        internal readonly FakeOrigin Origin = new();
        internal readonly RemovalTarget[] Targets;
        internal readonly LocalDeleteService Service;

        internal Fixture(int count, long bytes = 0)
        {
            Targets = Enumerable.Range(0, count).Select(i => new RemovalTarget
            {
                PeerKey = Peer, MappingKey = "movies-1", PeerItemId = Guid.NewGuid(), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Movie " + i, Year = 2000 + i,
                Root = "/media/Movies", Path = "/media/Movies/Movie " + i + "/m.mkv", RecoveryRoot = string.Empty, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                RuntimeTicks = 1, Bytes = bytes > 0 ? bytes - i : 1000 + i, Generation = "g",
            }).ToArray();
            Library = new FakeLibrary(this);
            Service = new LocalDeleteService(Library, Origin, Probe, () => Enabled, (_, _) => { Syncs++; return Task.CompletedTask; }, Gate, new Audit(Log), Time, readLocal: (path, offset, length, _) => Task.FromResult(Content(Targets.Single(t => t.Path == path), offset, length)))
            {
                Delay = (by, _) => { Time.Advance(by); Waited += by; return Task.CompletedTask; },
                FriendDelay = (by, token) => { token.ThrowIfCancellationRequested(); OnFriendWait?.Invoke(); Time.Advance(by); FriendWaited += by; FriendWaits.Add(by); return Task.CompletedTask; },
            };
            Origin.Fixture = this;
        }

        internal bool Enabled { get; set; } = true;

        // W23-1: the first item's hash waits here (a long hash on a slow share) until released or cancelled.
        internal TaskCompletionSource? HashGate { get; set; }

        internal TaskCompletionSource HashReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The bytes of a movie file in these tests: both copies hold the same bytes unless a test tampers with the friend's.
        internal static byte[] Content(RemovalTarget target, long offset, int length) => Enumerable.Range(0, length).Select(i => (byte)((offset + i) * 31 + target.Bytes)).ToArray();

        internal int Syncs { get; set; }

        internal int Handoffs { get; set; }

        internal TimeSpan Waited { get; set; }

        // Waits for the friend (pacing and Retry-After), kept apart from the settle pass.
        internal TimeSpan FriendWaited { get; set; }

        internal Action? OnFriendWait { get; set; }

        internal List<TimeSpan> FriendWaits { get; } = [];

        // How long each local hash takes on the fake clock (a big file on a slow share).
        internal TimeSpan ProbeTakes { get; set; }

        private Task<RemovalFileSnapshot> Probe(string root, string path, CancellationToken token)
        {
            if (HashGate is { } hold && path == Targets[0].Path)
            {
                return Held(hold, root, path, token);
            }

            if (ProbeFails.Contains(path))
            {
                return Task.FromException<RemovalFileSnapshot>(new System.IO.IOException("mediaFileUnavailable"));
            }

            Time.Advance(ProbeTakes);
            return Task.FromResult(new RemovalFileSnapshot(path, root, Targets.Single(t => t.Path == path).Bytes, Hash, "p:1:1"));
        }

        private async Task<RemovalFileSnapshot> Held(TaskCompletionSource hold, string root, string path, CancellationToken token)
        {
            HashReached.TrySetResult();
            await hold.Task.WaitAsync(token);
            return new RemovalFileSnapshot(path, root, Targets.Single(t => t.Path == path).Bytes, Hash, "p:1:1");
        }

        internal async Task<LocalDeleteBatchDto> Finish(string id)
        {
            await Service.Running.WaitAsync(TimeSpan.FromSeconds(30));
            return Service.Get(id)!;
        }

        public void Dispose()
        {
            Service.Dispose();
            Gate.Dispose();
        }
    }

    internal sealed class Audit(ConcurrentQueue<string> log) : ILocalDeleteAudit
    {
        public void Started(LocalDeleteAuditEntry entry) => log.Enqueue("started " + entry.Title);

        public void Finished(LocalDeleteAuditEntry entry) => log.Enqueue("finished " + entry.Title);
    }

    internal sealed class FakeOrigin : IRemovalOriginClient
    {
        internal Fixture Fixture { get; set; } = null!;

        internal Dictionary<Guid, string> Fail { get; } = [];

        internal Dictionary<Guid, string> Digest { get; } = [];

        internal Dictionary<Guid, long> Length { get; } = [];

        internal HashSet<Guid> Tamper { get; } = [];

        internal HashSet<Guid> SampleFails { get; } = [];

        internal ConcurrentQueue<(Guid Item, long Offset, int Length)> Reads { get; } = new();

        // The friend's proof bucket (null: unlimited) and how many proofs it refused.
        internal RateLimiter? Limit { get; set; }

        internal int Limited { get; private set; }

        // Answer 429 this many times before the next answer (proof or range read).
        internal ConcurrentDictionary<Guid, int> LimitedTimes { get; } = new();

        internal ConcurrentDictionary<Guid, int> SampleLimitedTimes { get; } = new();

        internal ConcurrentDictionary<Guid, int> Proofs { get; } = new();

        // The friend's Retry-After in seconds (RemovalOriginProof's bucket answer is 10; a hostile friend says 1; null: none sent).
        internal int? RetryAfterSeconds { get; set; } = 10;

        // How long the friend takes on the fake clock before a proof answer, and before a 429 answer.
        internal TimeSpan ProofTakes { get; set; }

        internal TimeSpan LimitedTakes { get; set; }

        // What the real client throws for the friend's 429.
        private Exception RateLimited()
        {
            Fixture.Time.Advance(LimitedTakes);
            return new RemovalOriginRateLimitedException(RetryAfterSeconds);
        }

        private static bool TakeLimited(ConcurrentDictionary<Guid, int> times, Guid id)
        {
            if (times.TryGetValue(id, out var left) && left > 0)
            {
                times[id] = left - 1;
                return true;
            }

            return false;
        }

        public Task<byte[]> ReadRangeAsync(RemovalTarget target, long offset, int length, CancellationToken cancellationToken)
        {
            Reads.Enqueue((target.PeerItemId, offset, length));
            if (TakeLimited(SampleLimitedTimes, target.PeerItemId))
            {
                return Task.FromException<byte[]>(RateLimited());
            }

            if (SampleFails.Contains(target.PeerItemId))
            {
                return Task.FromException<byte[]>(new InvalidOperationException("originUnavailable"));
            }

            var bytes = Fixture.Content(target, offset, length);
            if (Tamper.Contains(target.PeerItemId) && offset > 0)
            {
                bytes[0] ^= 0xff; // a friend that reported the right hash but does not hold the bytes (only the first KB looked right)
            }

            return Task.FromResult(bytes);
        }

        public Task<RemovalOriginDto> ProveAsync(RemovalTarget target, CancellationToken cancellationToken)
        {
            Proofs.AddOrUpdate(target.PeerItemId, 1, static (_, n) => n + 1);
            if (TakeLimited(LimitedTimes, target.PeerItemId))
            {
                return Task.FromException<RemovalOriginDto>(RateLimited());
            }

            if (Limit is { } limit && !limit.TryTake(Guid.Empty))
            {
                Limited++;
                return Task.FromException<RemovalOriginDto>(RateLimited());
            }

            Fixture.Time.Advance(ProofTakes);
            return Fail.TryGetValue(target.PeerItemId, out var word)
                ? Task.FromException<RemovalOriginDto>(new InvalidOperationException(word))
                : Task.FromResult(new RemovalOriginDto(1, Digest.GetValueOrDefault(target.PeerItemId, Hash), Length.GetValueOrDefault(target.PeerItemId, target.Bytes), target.ProviderIds, 1, new string('d', 64)));
        }
    }

    internal sealed class FakeLibrary : ILocalDeleteLibrary
    {
        private readonly Fixture _f;
        private readonly Dictionary<Guid, TaskCompletionSource> _blocks = [];
        private int _active;

        internal FakeLibrary(Fixture f)
        {
            _f = f;
            foreach (var t in f.Targets)
            {
                SharedFor[t.LocalItemId] = Guid.NewGuid();
            }
        }

        internal bool AdminValid { get; set; } = true;

        internal bool AnyAdmin { get; set; }

        internal int MaxParallel { get; private set; }

        internal List<string> Deleted { get; } = [];

        internal Dictionary<Guid, LocalDeleteOutcome> Outcomes { get; } = [];

        internal Dictionary<Guid, LocalDeleteReason> InspectRefusal { get; } = [];

        internal Dictionary<Guid, Guid> SharedFor { get; } = [];

        internal HashSet<Guid> HistoryMoves { get; } = [];

        internal HashSet<Guid> AccessDenied { get; } = [];

        internal HashSet<Guid> Unresolvable { get; } = [];

        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Block(Guid id) => _blocks[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool CandidatesFail { get; set; }

        public IReadOnlyList<RemovalTarget> Candidates(string peerKey) => CandidatesFail ? throw new InvalidOperationException("removalChanged") : _f.Targets.Where(t => t.PeerKey == peerKey).ToArray();

        public RemovalTarget Resolve(string peerKey, Guid localItemId, Guid peerItemId) => Unresolvable.Contains(localItemId)
            ? throw new LocalDeleteRefusedException(LocalDeleteReason.CandidateChanged)
            : _f.Targets.Single(t => t.LocalItemId == localItemId && t.PeerItemId == peerItemId && t.PeerKey == peerKey);

        public IReadOnlyList<RemovalUserState> Capture(Guid itemId)
        {
            var moved = HistoryMoves.Contains(itemId) && _captures.Add(itemId) is false;
            return [new RemovalUserState(Guid.Empty, true, false, moved ? 99 : 1, 1, null, false, null, null, null, null)];
        }

        public void VerifyAccess(RemovalTarget target, IReadOnlyList<RemovalUserState> history)
        {
            if (AccessDenied.Contains(target.LocalItemId))
            {
                throw new InvalidOperationException("accessNotShared");
            }
        }

        public void VerifyAdministrator(Guid admin)
        {
            if (!AdminValid || (admin != _f.Admin && !AnyAdmin))
            {
                throw new InvalidOperationException("administratorRequired");
            }
        }

        public IReadOnlyList<string> Inspect(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed) => InspectRefusal.TryGetValue(target.LocalItemId, out var r)
            ? throw new LocalDeleteRefusedException(r) : [target.Path];

        internal Action? SwitchOffInsideDelete { get; set; }

        internal bool AllowedAtDelete { get; private set; } = true;

        public LocalDeleteOutcome Delete(RemovalTarget target, Guid admin, RemovalFileSnapshot hashed, Func<bool> allowed, CancellationToken cancellationToken)
        {
            MaxParallel = Math.Max(MaxParallel, Interlocked.Increment(ref _active));
            try
            {
                SwitchOffInsideDelete?.Invoke();
                AllowedAtDelete = allowed();
                if (!AllowedAtDelete)
                {
                    throw new LocalDeleteRefusedException(LocalDeleteReason.Disabled); // as the real adapter does under the hold
                }

                _f.Log.Enqueue("delete " + target.Title);
                if (_blocks.TryGetValue(target.LocalItemId, out var block))
                {
                    Reached.TrySetResult();
                    block.Task.Wait(TimeSpan.FromSeconds(10));
                }

                var outcome = Outcomes.GetValueOrDefault(target.LocalItemId, LocalDeleteOutcome.Done);
                if (outcome.State == LocalDeleteItemState.Done)
                {
                    Deleted.Add(target.Title);
                }

                return outcome;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal bool FindSharedThrows { get; set; }

        public Guid? FindSharedFor(RemovalTarget target) => FindSharedThrows ? throw new NullReferenceException("core state") : SharedFor.TryGetValue(target.LocalItemId, out var id) ? id : null;

        public void Handoff(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history) => _f.Handoffs++;

        private readonly HashSet<Guid> _captures = [];
    }
}
