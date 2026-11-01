using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalCoordinatorTests
{
    [Fact]
    public async Task DefaultOffDiscoveryAndUnsupportedPlansDoNotTouchStorage()
    {
        using var f = new Fixture();
        f.Settings.Current.LocalRemovalEnabled = false;
        var state = Json(await f.Service.StateAsync("friend", default));
        Assert.Empty(state.GetProperty("candidates").EnumerateArray());
        Assert.Equal(0, f.Probes);
        f.Supported = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PlanAsync(f.Admin, "unused", default));
        Assert.Equal("unsupportedPlatform", error.Message);
        Assert.Equal(0, f.Probes);
        Assert.Equal(0, f.Prepares);
    }

    [Fact]
    public async Task PlanIsAdminBoundAndExpiresWithoutMoving()
    {
        using var f = new Fixture();
        var plan = await f.Plan();
        Assert.Equal("planChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmAsync(Guid.NewGuid(), plan, default))).Message);
        f.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal("planExpired", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmAsync(f.Admin, plan, default))).Message);
        Assert.Equal(0, f.Moves);
        Assert.Empty(f.Journal.Jobs);
    }

    [Fact]
    public async Task ConfirmationIsSingleUseAndFreshProofRunsAfterMove()
    {
        using var f = new Fixture();
        var plan = await f.Plan();
        var job = Json(await f.Service.ConfirmAsync(f.Admin, plan, default));
        Assert.Equal("replacementReady", job.GetProperty("phase").GetString());
        Assert.Equal(3, f.OriginCalls);
        Assert.True(f.PostMoveProof);
        Assert.Equal(1, f.Moves);
        var replay = Json(await f.Service.ConfirmAsync(f.Admin, plan, default));
        Assert.Equal(job.GetProperty("jobId").GetString(), replay.GetProperty("jobId").GetString());
        Assert.Equal(1, f.Moves);
        Assert.DoesNotContain(f.Target.Path, job.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Target.PeerKey, job.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedPostQuarantineOriginAutomaticallyRestoresOriginalBytes()
    {
        using var f = new Fixture { FailPostMoveOrigin = true };
        var result = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        Assert.Equal("restored", result.GetProperty("phase").GetString());
        Assert.True(f.Files.ContainsKey(f.Target.Path));
        Assert.Single(f.Files);
        Assert.Equal(2, f.Moves);
        Assert.Equal(0, f.Library.Imports);
    }

    [Fact]
    public async Task NativeFailureAfterRenameUsesPreparedDeterministicRecoveryPath()
    {
        using var f = new Fixture { ThrowAfterFirstMove = true };
        var result = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        Assert.Equal("restored", result.GetProperty("phase").GetString());
        Assert.Single(f.Files);
        Assert.True(f.Files.ContainsKey(f.Target.Path));
        Assert.Equal(2, f.Moves);
    }

    [Fact]
    public async Task RestoreWaitsForNoDeleteBatchThroughTheSharedGate()
    {
        // 2.10.4 K4b: a Restore never overlaps a delete-like-Jellyfin batch.
        using var f = new Fixture();
        var id = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default)).GetProperty("jobId").GetString()!;
        using var gate = new RemovalGate();
        f.Service.Gate = gate;
        using (gate.TryEnter())
        {
            Assert.Equal("busy", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreAsync(id, default))).Message);
        }

        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
        Assert.NotNull(gate.TryEnter()); // released after the Restore
    }

    [Fact]
    public async Task RestoreWorksDisabledAndOfflineCapturesLatestSharedBeforeIndexing()
    {
        using var f = new Fixture();
        var result = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        var id = result.GetProperty("jobId").GetString()!;
        f.Service.Enable(false);
        f.FailAllOrigin = true;
        f.Journal.Uncertain = true;
        f.Library.Latest = f.Library.History[0] with { Position = 0, PlayCount = 3, Favorite = true, Rating = 9 };
        var before = f.OriginCalls;
        var restored = Json(await f.Service.RestoreAsync(id, default));
        Assert.Equal("restored", restored.GetProperty("phase").GetString());
        Assert.Equal(before, f.OriginCalls);
        Assert.Equal(f.Library.Latest, Assert.Single(f.Library.RestoredHistory));
        Assert.True(f.Library.CapturedBeforeRestore);
        await f.Service.RestoreAsync(id, default);
        Assert.Equal(2, f.Moves);
    }

    [Fact]
    public async Task ConflictingOriginalPreservesBothCopiesForRecovery()
    {
        using var f = new Fixture();
        var job = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        f.Files[f.Target.Path] = f.Original with { Sha256 = new string('b', 64) };
        var restored = Json(await f.Service.RestoreAsync(job.GetProperty("jobId").GetString()!, default));
        Assert.Equal("needsRecovery", restored.GetProperty("phase").GetString());
        Assert.Equal(2, f.Files.Count);
        Assert.Equal(1, f.Moves);
        Assert.True(restored.GetProperty("canRestore").GetBoolean());
    }

    [Fact]
    public async Task PartiallyFailedHandoffHasDurableReplacementIdentityForLatestHistoryRecovery()
    {
        using var f = new Fixture();
        f.Library.FailHandoff = true;
        f.Library.Latest = f.Library.History[0] with { PlayCount = 3, Position = 0, Favorite = false };
        var result = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        Assert.Equal("restored", result.GetProperty("phase").GetString());
        Assert.Equal(f.Library.Latest, Assert.Single(f.Library.RestoredHistory));
        Assert.True(f.Library.CapturedBeforeRestore);
        Assert.True(f.Files.ContainsKey(f.Target.Path));
    }

    [Fact]
    public async Task RetriedRestoreRecapturesSharedProgressAfterEarlierCollision()
    {
        using var f = new Fixture();
        var job = Json(await f.Service.ConfirmAsync(f.Admin, await f.Plan(), default));
        var id = job.GetProperty("jobId").GetString()!;
        f.Files[f.Target.Path] = f.Original with { Sha256 = new string('b', 64) };
        await f.Service.RestoreAsync(id, default);
        f.Files.Remove(f.Target.Path);
        f.Library.Latest = f.Library.History[0] with { Position = 120_000_000, PlayCount = 7, Favorite = false };
        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
        Assert.Equal(f.Library.Latest, Assert.Single(f.Library.RestoredHistory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownRetainedMediaBlocksPlanOrConfirmationBeforeNewHashOrMove(bool afterPlan)
    {
        using var f = new Fixture();
        var plan = afterPlan ? await f.Plan() : null;
        var path = Path.Combine(f.Target.RecoveryRoot, Guid.NewGuid().ToString("N") + ".media");
        f.Files.Add(path, f.Original with { FullPath = path, Root = f.Target.RecoveryRoot });
        var probes = f.Probes;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => afterPlan ? f.Service.ConfirmAsync(f.Admin, plan!, default) : f.Plan());
        Assert.Equal("recoveryRequired", error.Message);
        Assert.Equal(probes, f.Probes);
        Assert.Equal(0, f.Moves);
        Assert.Equal(2, f.Files.Count);
        Assert.Empty(f.Journal.Jobs);
    }

    [Fact]
    public async Task UncertainJournalBlocksConfirmationBeforeMutation()
    {
        using var f = new Fixture();
        var plan = await f.Plan();
        f.Journal.Uncertain = true;
        Assert.Equal("recoveryRequired", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmAsync(f.Admin, plan, default))).Message);
        Assert.Equal(0, f.Moves);
        Assert.Empty(f.Journal.Jobs);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // 2.10.3 W20-5, Pilot P30-KILL-STUCK (docs/tests/pilot-simple-removal-live.md 6.7a): Jellyfin was killed the instant the PurgeCommitting record
    // (generation 6) appeared; the movie sat intact under its stage name `.purge-<job>-<op>.media`, the status API said purgeCommitting /
    // recoveryRequired, and Restore, every purge plan and every new removal plan answered 409 recoveryRequired.
    private static (string JobId, string Stage) KilledWhilePurgeCommitting(Fixture f, string id, bool fileAtStage = true)
    {
        var ready = f.Journal.Jobs[id];
        var op = Guid.NewGuid().ToString("N");
        var stage = ".purge-" + id + "-" + op + ".media";
        f.Journal.Jobs[id] = ready with { Phase = RemovalPhase.PurgeCommitting, Purge = new RemovalPurgeAuthority(op, stage, f.Admin, f.Clock.Now, new string('a', 64)) };
        if (fileAtStage)
        {
            var held = ready.Held!;
            f.Files.Remove(held.FullPath);
            var staged = Path.Combine(f.Target.RecoveryRoot, stage);
            f.Files.Add(staged, held with { FullPath = staged });
        }

        return (id, Path.Combine(f.Target.RecoveryRoot, stage));
    }

    [Fact]
    public async Task AfterAKillWhilePurgeCommittingTheIntactMovieCanBeRestoredWithoutEditingFiles()
    {
        using var f = new Fixture();
        var (id, stage) = KilledWhilePurgeCommitting(f, await f.Ready());
        // The process restarted: Restore runs the start-up sweep first if it has not run yet.
        var restored = Json(await f.Service.RestoreAsync(id, default));
        Assert.Equal("restored", restored.GetProperty("phase").GetString());
        Assert.True(f.Files.ContainsKey(f.Target.Path));
        Assert.False(f.Files.ContainsKey(stage));
        Assert.Equal(0, f.Unlinks);
        Assert.Contains(f.Journal.Saved, j => j.JobId == id && j.Phase == RemovalPhase.ReplacementReady && j.Purge is null);
    }

    [Fact]
    public async Task TheSweepReturnsAnIntactInterruptedDeleteToRecoverableAndNewRemovalsWorkAgain()
    {
        using var f = new Fixture();
        var other = f.Target with { LocalItemId = Guid.NewGuid(), PeerItemId = Guid.NewGuid(), Title = "Other movie", Path = Path.GetFullPath("/movies/other.mkv") };
        f.Files.Add(other.Path, f.Original with { FullPath = other.Path, Identity = "native-id-2" });
        var (id, stage) = KilledWhilePurgeCommitting(f, await f.Ready());
        Assert.Equal(1, await f.Service.SweepInterruptedAsync(default));
        var job = f.Journal.Jobs[id];
        Assert.Equal(RemovalPhase.ReplacementReady, job.Phase);
        Assert.Null(job.Purge);
        Assert.True(f.Files.ContainsKey(job.Held!.FullPath));
        Assert.False(f.Files.ContainsKey(stage));
        Assert.Equal(0, await f.Service.SweepInterruptedAsync(default)); // once per process
        f.Library.Many = [other];
        var state = Json(await f.Service.StateAsync(f.Target.PeerKey, default));
        await f.Service.PlanAsync(f.Admin, state.GetProperty("candidates")[0].GetProperty("candidateId").GetString()!, default);
        Assert.Equal(0, f.Unlinks);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("neither")]
    public async Task ADoubtfulInterruptedDeleteIsMarkedUnknownForThatJobAloneAndNothingIsDeleted(string files)
    {
        using var f = new Fixture();
        var other = f.Target with { LocalItemId = Guid.NewGuid(), PeerItemId = Guid.NewGuid(), Title = "Other movie", Path = Path.GetFullPath("/movies/other.mkv") };
        f.Files.Add(other.Path, f.Original with { FullPath = other.Path, Identity = "native-id-2" });
        var (id, stage) = KilledWhilePurgeCommitting(f, await f.Ready());
        var held = f.Journal.Jobs[id].Held!;
        if (files == "both") f.Files.Add(held.FullPath, held);
        else f.Files.Remove(stage);
        var before = f.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(1, await f.Service.SweepInterruptedAsync(default));
        Assert.Equal(RemovalPhase.PurgeOutcomeUnknown, f.Journal.Jobs[id].Phase);
        Assert.Equal(before, f.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()); // nothing renamed or deleted
        Assert.Equal(0, f.Unlinks);
        // The doubtful job keeps its stage file, and an unrelated new removal still plans (the stage name is known, not foreign).
        f.Library.Many = [other];
        var state = Json(await f.Service.StateAsync(f.Target.PeerKey, default));
        await f.Service.PlanAsync(f.Admin, state.GetProperty("candidates")[0].GetProperty("candidateId").GetString()!, default);
    }

    [Fact]
    public async Task AnInterruptedPurgePreparedWithTheFileStillAtItsHeldNameReturnsToRecoverable()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var (_, _) = KilledWhilePurgeCommitting(f, id, fileAtStage: false);
        f.Journal.Jobs[id] = f.Journal.Jobs[id] with { Phase = RemovalPhase.PurgePrepared };
        Assert.Equal(1, await f.Service.SweepInterruptedAsync(default));
        Assert.Equal(RemovalPhase.ReplacementReady, f.Journal.Jobs[id].Phase);
    }

    // Warden 22 W22-1: shutdown (or an aborted Restore request) while the sweep re-hashes the file after renaming it back. The native probe turns the
    // cancelled hash into NotRestored; the old sweep saved PurgeOutcomeUnknown for good. Now nothing is saved and the next start returns the job.
    [Fact]
    public async Task ACancelledSweepSavesNothingAndTheNextStartReturnsTheJob()
    {
        using var f = new Fixture();
        var (id, _) = KilledWhilePurgeCommitting(f, await f.Ready());
        using var shutdown = new CancellationTokenSource();
        f.RehashFailures = 1;
        f.CancelAtRehash = shutdown;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SweepInterruptedAsync(shutdown.Token));
        Assert.Equal(RemovalPhase.PurgeCommitting, f.Journal.Jobs[id].Phase);
        Assert.DoesNotContain(f.Journal.Saved, j => j.JobId == id && j.Phase == RemovalPhase.PurgeOutcomeUnknown);
        Assert.Equal(1, await f.Service.SweepInterruptedAsync(default)); // the next start: the file is at its held name now
        Assert.Equal(RemovalPhase.ReplacementReady, f.Journal.Jobs[id].Phase);
        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
        Assert.Equal(0, f.Unlinks);
    }

    // W22-1: the rename back worked but its re-hash failed once (not cancelled): asked again, the probe finds the exact object at the held name.
    [Fact]
    public async Task ARenameBackWhoseRehashFailedIsFoundAtTheHeldNameNotMarkedUnknown()
    {
        using var f = new Fixture();
        var (id, _) = KilledWhilePurgeCommitting(f, await f.Ready());
        f.RehashFailures = 1;
        Assert.Equal(1, await f.Service.SweepInterruptedAsync(default));
        Assert.Equal(RemovalPhase.ReplacementReady, f.Journal.Jobs[id].Phase);
    }

    // W22-2: a commit that returned NoUnlink (refused or cancelled before any unlink) is rolled back at once instead of being recorded unknown.
    [Fact]
    public async Task ACommitThatUnlinkedNothingIsRolledBackAndTheMovieStaysRecoverable()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        f.PurgeFault = "noUnlink";
        Assert.Equal("removalChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(f.Admin, plan, default))).Message);
        var job = f.Journal.Jobs[id];
        Assert.Equal(RemovalPhase.ReplacementReady, job.Phase);
        Assert.Null(job.Purge);
        Assert.True(f.Files.ContainsKey(job.Held!.FullPath));
        Assert.Equal(0, f.Unlinks);
        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
    }

    // W22-2: an outcome-unknown job from an older build whose file still sits at its stage name is probed again at the next start and returned;
    // one with no proof stays exactly as recorded (no second Unknown record is written).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOutcomeUnknownJobIsProbedAgainAtStartAndReturnedOnlyWithProof(bool intact)
    {
        using var f = new Fixture();
        var (id, stage) = KilledWhilePurgeCommitting(f, await f.Ready());
        f.Journal.Jobs[id] = f.Journal.Jobs[id] with { Phase = RemovalPhase.PurgeOutcomeUnknown };
        if (!intact) f.Files.Remove(stage); // unlinked: neither name exists
        var saves = f.Journal.Saved.Count;
        Assert.Equal(intact ? 1 : 0, await f.Service.SweepInterruptedAsync(default));
        Assert.Equal(intact ? RemovalPhase.ReplacementReady : RemovalPhase.PurgeOutcomeUnknown, f.Journal.Jobs[id].Phase);
        Assert.Equal(intact ? saves + 1 : saves, f.Journal.Saved.Count);
        Assert.Equal(0, f.Unlinks);
    }

    // W22-3: a save the journal refused leaves the sweep not done, so the next Restore sweeps again instead of refusing until a restart.
    [Fact]
    public async Task ASweepWhoseSaveFailedRunsAgainOnTheNextRestore()
    {
        using var f = new Fixture();
        var (id, _) = KilledWhilePurgeCommitting(f, await f.Ready());
        f.Journal.FailPhase = RemovalPhase.ReplacementReady;
        Assert.Equal(0, await f.Service.SweepInterruptedAsync(default));
        Assert.Equal(RemovalPhase.PurgeCommitting, f.Journal.Jobs[id].Phase);
        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
    }

    // W22-4: a Restore that must run the sweep first answers recoveryRequired, not a raw error, when the journal cannot be read.
    [Fact]
    public async Task ARestoreWhoseSweepCannotReadTheJournalIsRefusedPlainly()
    {
        using var f = new Fixture();
        var (id, _) = KilledWhilePurgeCommitting(f, await f.Ready());
        f.Journal.FailLoad = true;
        Assert.Equal("recoveryRequired", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreAsync(id, default))).Message);
    }

    [Fact]
    public void AnOutcomeUnknownJobMayReturnOnlyToRecoverable()
    {
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.PurgeOutcomeUnknown, RemovalPhase.ReplacementReady));
        Assert.All(Enum.GetValues<RemovalPhase>().Where(p => p != RemovalPhase.ReplacementReady), next => Assert.False(RemovalPolicy.CanTransition(RemovalPhase.PurgeOutcomeUnknown, next)));
    }

    // 2.10.3 K1: a candidate handle lives 60 minutes (plans still 5), survives another list read, and is cleared only by disable.
    [Fact]
    public async Task CandidateLivesSixtyMinutesSurvivesARelistAndIsClearedByDisable()
    {
        using var f = new Fixture();
        string Candidate(JsonElement state) => state.GetProperty("candidates")[0].GetProperty("candidateId").GetString()!;
        var first = Candidate(Json(await f.Service.StateAsync(f.Target.PeerKey, default)));
        f.Clock.Now += TimeSpan.FromMinutes(30);
        var second = Candidate(Json(await f.Service.StateAsync(f.Target.PeerKey, default)));
        Assert.NotEqual(first, second);
        // The re-list did not clear the first handle, and minute 30 is inside its lifetime.
        var plan = Json(await f.Service.PlanAsync(f.Admin, first, default));
        Assert.True(DateTimeOffset.Parse(plan.GetProperty("expiresUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture) <= f.Clock.Now + RemovalPolicy.PlanLifetime, "plans keep their five minutes");
        f.Clock.Now += TimeSpan.FromMinutes(31);
        Assert.Equal("candidateChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PlanAsync(f.Admin, first, default))).Message);
        // The second handle (listed at minute 30) is still valid at minute 61, until disable clears it.
        await f.Service.PlanAsync(f.Admin, second, default);
        f.Service.Enable(false);
        f.Service.Enable(true);
        Assert.Equal("candidateChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PlanAsync(f.Admin, second, default))).Message);
        Assert.Equal(0, f.Moves);
        Assert.Equal(TimeSpan.FromMinutes(60), RemovalPolicy.CandidateLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), RemovalPolicy.PlanLifetime);
    }

    [Fact]
    public async Task CandidateTableIsCappedAtThreeHundredAndDropsTheOldestFirst()
    {
        using var f = new Fixture();
        f.Library.Many = Enumerable.Repeat(f.Target, 100).ToArray();
        var batches = new List<string[]>();
        for (var i = 0; i < 4; i++)
        {
            var state = Json(await f.Service.StateAsync(f.Target.PeerKey, default));
            batches.Add(state.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("candidateId").GetString()!).ToArray());
            f.Clock.Now += TimeSpan.FromMinutes(1);
        }

        Assert.All(batches, b => Assert.Equal(100, b.Length));
        // Four reads of 100 against a cap of 300: the first read's handles were dropped, the latest still plan.
        Assert.Equal("candidateChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PlanAsync(f.Admin, batches[0][^1], default))).Message);
        await f.Service.PlanAsync(f.Admin, batches[1][0], default);
        await f.Service.PlanAsync(f.Admin, batches[3][^1], default);
    }

    // 2.10.3 K2: the real controller to coordinator path for two items from one list read, in sequence (plan, confirm, plan, confirm).
    // The second candidate still works after the first confirm, and each confirm uses the new body `{}`.
    [Fact]
    public async Task TwoItemsFromOneListMoveInSequenceThroughTheController()
    {
        using var f = new Fixture();
        var other = f.Target with { LocalItemId = Guid.NewGuid(), PeerItemId = Guid.NewGuid(), Title = "Second movie", Path = Path.GetFullPath("/movies/second.mkv") };
        f.Files.Add(other.Path, f.Original with { FullPath = other.Path, Identity = "native-id-2" });
        f.Library.Many = [f.Target, other];
        var claims = new[] { new System.Security.Claims.Claim(Jellyfin.Plugin.Shoal.Authorization.CallerContext.UserIdClaim, f.Admin.ToString("N")), new System.Security.Claims.Claim(Jellyfin.Plugin.Shoal.Authorization.CallerContext.IsApiKeyClaim, "false") };
        var controller = new AdminRemovalOperationsController(f.Service)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "core")) } },
        };
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var list = AdminTestSupport.Body(Jellyfin.Plugin.Shoal.Api.Admin.AdminReply.Json(200, await f.Service.StateAsync("a1b2c3d4e5f60718", default))); // the route takes a well-formed key; the fake library ignores it
        var ids = list.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("candidateId").GetString()!).ToArray();
        Assert.Equal(2, ids.Length);
        var jobs = new List<JsonElement>();
        foreach (var id in ids)
        {
            var plan = AdminTestSupport.Body(Jellyfin.Plugin.Shoal.Api.Admin.AdminReply.Json(200, await f.Service.PlanAsync(f.Admin, id, default)));
            f.Clock.Now += TimeSpan.FromMinutes(3); // each Check can take minutes; the next candidate must not expire
            var job = AdminTestSupport.Body(Jellyfin.Plugin.Shoal.Api.Admin.AdminReply.Json(200, await f.Service.ConfirmAsync(f.Admin, plan.GetProperty("planId").GetString()!, default))); // 2.10.4 K4b: the move routes are gone; the dormant coordinator is driven directly
            jobs.Add(job);
        }

        Assert.All(jobs, j => Assert.Equal("replacementReady", j.GetProperty("phase").GetString()));
        Assert.NotEqual(jobs[0].GetProperty("jobId").GetString(), jobs[1].GetProperty("jobId").GetString());
        Assert.Equal(2, f.Journal.Jobs.Count);
        Assert.False(f.Files.ContainsKey(f.Target.Path));
        Assert.False(f.Files.ContainsKey(other.Path));
    }

    [Fact]
    public async Task PermanentDeletionIsSeparateDefaultOffAndDoesNotTouchBytesWithoutOptIn()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        Assert.Equal("disabled", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CreatePurgePlanAsync(f.Admin, id, default))).Message);
        Assert.Equal(0, f.Stages);
        Assert.Single(f.Files);
    }

    [Fact]
    public async Task PermanentDeletionConsumesFreshAdminBoundConsentAndNeverOffersRestore()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        Assert.Equal("planChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(Guid.NewGuid(), plan, default))).Message);
        var result = Json(await f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal("purged", result.GetProperty("phase").GetString());
        Assert.False(result.GetProperty("canRestore").GetBoolean());
        Assert.False(result.GetProperty("canReviewPermanentDeletion").GetBoolean());
        Assert.Empty(f.Files);
        Assert.Equal(1, f.Unlinks);
        Assert.Equal("planChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(f.Admin, plan, default))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreAsync(id, default));
        Assert.Equal(1, f.Unlinks);
    }

    [Fact]
    public async Task PermanentDeletionPublicResponsesMatchTheUiGoldenContracts()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        f.Service.EnablePermanentDeletion(true);
        ContractGolden.Match("removal-operations.purge-ready", AdminTestSupport.Render(AdminReply.Json(200, await f.Service.StateAsync(null, default))).Text);
        var plan = await f.Service.CreatePurgePlanAsync(f.Admin, id, default);
        ContractGolden.Match("removal-operations.purge-plan", AdminTestSupport.Render(AdminReply.Json(200, plan)).Text);
        var result = await f.Service.ConfirmPurgeAsync(f.Admin, Json(plan).GetProperty("planId").GetString()!, default);
        ContractGolden.Match("removal-operations.purged", AdminTestSupport.Render(AdminReply.Json(200, result)).Text);
    }

    // 2.10.3 U5: the multi-item list the UI mock serves (three overlapped movies, one held, one deleted forever) is the real public shape.
    [Fact]
    public async Task ManyItemListMatchesTheUiGoldenContract()
    {
        using var f = new Fixture();
        var second = f.Target with { LocalItemId = Guid.NewGuid(), PeerItemId = Guid.NewGuid(), Title = "Second movie", Path = Path.GetFullPath("/movies/second.mkv") };
        var third = f.Target with { LocalItemId = Guid.NewGuid(), PeerItemId = Guid.NewGuid(), Title = "Third movie", Year = null, Path = Path.GetFullPath("/movies/third.mkv") };
        f.Files.Add(second.Path, f.Original with { FullPath = second.Path, Identity = "native-id-2" });
        f.Files.Add(third.Path, f.Original with { FullPath = third.Path, Identity = "native-id-3" });
        f.Library.Many = [f.Target, second, third];
        var held = await f.Ready();
        f.Clock.Now += TimeSpan.FromMinutes(1);
        var state = Json(await f.Service.StateAsync(f.Target.PeerKey, default));
        var plan = Json(await f.Service.PlanAsync(f.Admin, state.GetProperty("candidates")[1].GetProperty("candidateId").GetString()!, default)).GetProperty("planId").GetString()!;
        var gone = Json(await f.Service.ConfirmAsync(f.Admin, plan, default)).GetProperty("jobId").GetString()!;
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Service.ConfirmPurgeAsync(f.Admin, await f.PurgePlan(gone), default);
        var list = AdminTestSupport.Render(AdminReply.Json(200, await f.Service.StateAsync(f.Target.PeerKey, default))).Text;
        Assert.NotEqual(held, gone);
        ContractGolden.Match("removal-operations.many", list);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("expiry")]
    [InlineData("disabled")]
    [InlineData("revokedAdmin")]
    [InlineData("origin")]
    [InlineData("journal")]
    public async Task ChangedPurgePlanRefusesBeforeStaging(string change)
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        switch (change)
        {
            case "history": f.Library.PurgeFingerprint = "changed"; break;
            case "expiry": f.Clock.Now += TimeSpan.FromMinutes(6); break;
            case "disabled": f.Service.EnablePermanentDeletion(false); break;
            case "revokedAdmin": f.Library.AdminRevoked = true; break;
            case "origin": f.FailAllOrigin = true; break;
            case "journal": f.Journal.Uncertain = true; break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal(0, f.Stages);
        Assert.Single(f.Files);
        Assert.Equal(RemovalPhase.ReplacementReady, f.Journal.Jobs[id].Phase);
    }

    // Only failures after PurgeCommitting was recorded stay outcome-unknown; pre-commit failures roll back below.
    [Theory]
    [InlineData("unlinkUnknown")]
    [InlineData("duringNativeHash")]
    [InlineData("committingSave")]
    public async Task InterruptedDeletionNeverAdvertisesRestoreOrRetriesUnlink(string fault)
    {
        using var f = new Fixture();
        Assert.Equal(0, await f.Service.SweepInterruptedAsync(default)); // this process's start-up sweep already ran; an Unknown job is probed again only at the next start (W22-2)
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        f.PurgeFault = fault;
        var result = Json(await f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal("purgeOutcomeUnknown", result.GetProperty("phase").GetString());
        Assert.False(result.GetProperty("canRestore").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CreatePurgePlanAsync(f.Admin, id, default));
        Assert.Equal(fault == "unlinkUnknown" ? 1 : 0, f.Unlinks);
        Assert.Equal(fault == "unlinkUnknown" ? 0 : 1, f.Files.Count);
        Assert.Equal(0, f.Unstages);
        Assert.Equal(RemovalPhase.PurgeOutcomeUnknown, f.Journal.Jobs[id].Phase);
    }

    [Theory]
    [InlineData("stage", "removalChanged")]
    [InlineData("stageAfterRename", "removalChanged")]
    [InlineData("postStageHistory", "planChanged")]
    [InlineData("originOffline", "originUnavailable")]
    [InlineData("originRateLimited", "originRateLimited")]
    [InlineData("canceled", "removalChanged")]
    [InlineData("libraryThrows", "removalChanged")]
    public async Task PreCommitFailureRollsBackToReviewableRestorableOriginal(string fault, string code)
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var held = f.Journal.Jobs[id].Held!;
        var plan = await f.PurgePlan(id);
        f.PurgeFault = fault;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal(code, error.Message);
        var job = f.Journal.Jobs[id];
        Assert.Equal(RemovalPhase.ReplacementReady, job.Phase);
        Assert.Null(job.Purge);
        Assert.DoesNotContain(RemovalPhase.PurgeCommitting, f.Journal.Saved.Select(j => j.Phase));
        Assert.Equal(1, f.Unstages);
        Assert.Equal(0, f.Unlinks);
        Assert.Equal(held.FullPath, Assert.Single(f.Files).Key);
        var listed = Json(await f.Service.StateAsync(null, default)).GetProperty("jobs")[0];
        Assert.True(listed.GetProperty("canRestore").GetBoolean());
        Assert.True(listed.GetProperty("canReviewPermanentDeletion").GetBoolean());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("error").ValueKind);

        f.PurgeFault = null;
        f.Library.ThrowOnState = false;
        f.Library.PurgeFingerprint = "current-history";
        var again = Json(await f.Service.ConfirmPurgeAsync(f.Admin, await f.PurgePlan(id), default));
        Assert.Equal("purged", again.GetProperty("phase").GetString());
        Assert.Equal(1, f.Unlinks);
    }

    [Fact]
    public async Task RolledBackOriginalStillRestores()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        f.PurgeFault = "originOffline";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        f.PurgeFault = null;
        Assert.Equal("restored", Json(await f.Service.RestoreAsync(id, default)).GetProperty("phase").GetString());
        Assert.Equal(f.Target.Path, Assert.Single(f.Files).Key);
        Assert.Equal(0, f.Unlinks);
    }

    [Fact]
    public async Task UnexpectedExceptionTypeWithUnprovedRollbackEndsUnknownNotStranded()
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var plan = await f.PurgePlan(id);
        f.PurgeFault = "libraryThrows";
        f.UnstageFault = "unstageFails";
        var result = Json(await f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal("purgeOutcomeUnknown", result.GetProperty("phase").GetString());
        Assert.Equal(RemovalPhase.PurgeOutcomeUnknown, f.Journal.Jobs[id].Phase);
        Assert.Equal(1, f.Unstages);
        Assert.Equal(0, f.Unlinks);
    }

    [Theory]
    [InlineData("unstageFails")]
    [InlineData("heldOccupied")]
    [InlineData("rollbackSaveFails")]
    [InlineData("journalMovedOn")]
    public async Task UnprovedRollbackStaysUnknownAndOverwritesNothing(string variant)
    {
        using var f = new Fixture();
        var id = await f.Ready();
        var held = f.Journal.Jobs[id].Held!;
        var plan = await f.PurgePlan(id);
        f.PurgeFault = "originOffline";
        f.UnstageFault = variant;
        var result = Json(await f.Service.ConfirmPurgeAsync(f.Admin, plan, default));
        Assert.Equal("purgeOutcomeUnknown", result.GetProperty("phase").GetString());
        Assert.False(result.GetProperty("canRestore").GetBoolean());
        Assert.Equal(0, f.Unlinks);
        Assert.Equal(variant == "journalMovedOn" ? 0 : 1, f.Unstages);
        if (variant == "heldOccupied")
        {
            Assert.Equal(new string('b', 64), f.Files[held.FullPath].Sha256);
            Assert.Equal(2, f.Files.Count);
        }
        else if (variant == "rollbackSaveFails")
        {
            Assert.Equal(held.FullPath, Assert.Single(f.Files).Key);
        }
        else
        {
            Assert.EndsWith(f.Journal.Jobs[id].Purge!.StageName, Assert.Single(f.Files).Key, StringComparison.Ordinal);
        }
    }

    private sealed class Fixture : IDisposable, IRemovalOriginClient
    {
        internal readonly Guid Admin = Guid.NewGuid();
        internal readonly Settings Settings = new();
        internal readonly Clock Clock = new();
        internal readonly Journal Journal = new();
        internal readonly Library Library = new();
        internal readonly Dictionary<string, RemovalFileSnapshot> Files = new(StringComparer.Ordinal);
        internal readonly RemovalTarget Target = new() { PeerKey = "private-peer", MappingKey = "mapping", PeerItemId = Guid.NewGuid(), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Movie", Year = 2026, Root = Path.GetFullPath("/movies"), Path = Path.GetFullPath("/movies/movie.mkv"), RecoveryRoot = Path.GetFullPath("/recovery"), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "42" }, RuntimeTicks = 200_000_000, Bytes = 100, Generation = "core-generation" };
        internal readonly RemovalCoordinator Service;
        internal readonly RemovalFileSnapshot Original;
        internal bool Supported = true, FailPostMoveOrigin, FailAllOrigin, ThrowAfterFirstMove, PostMoveProof;
        internal int Moves, Probes, Prepares, OriginCalls, Stages, Unlinks, Unstages;
        internal string? PurgeFault, UnstageFault;
        internal int RehashFailures; // W22-1: the rename back happens, then its re-hash fails (and cancels CancelAtRehash when set)
        internal CancellationTokenSource? CancelAtRehash;

        internal Fixture()
        {
            Original = new(Target.Path, Target.Root, 100, new string('a', 64), "native-id");
            Files.Add(Target.Path, Original);
            Library.Target = Target;
            Service = new(Library, this, Journal, Settings, new Hold(), Clock, new(() => Supported,
                (root, path, _) => { Probes++; return Task.FromResult(Files.TryGetValue(path, out var file) ? file with { Root = root } : throw new IOException("missing")); },
                (file, root, name, _) => { Moves++; var path = Path.Combine(root, name); if (Files.ContainsKey(path)) throw new IOException("collision"); Files.Remove(file.FullPath); var moved = file with { FullPath = path, Root = root }; Files.Add(path, moved); if (ThrowAfterFirstMove && Moves == 1) throw new IOException("moveNeedsRecovery"); return Task.FromResult(moved); },
                Files.ContainsKey, _ => Prepares++, (_, _) => { },
                (root, expected) => { if (Files.Keys.Where(p => Path.GetDirectoryName(p) == root).Any(p => !expected.Contains(Path.GetFileNameWithoutExtension(p)) && !expected.Contains(Path.GetFileName(p)))) throw new InvalidOperationException("recoveryRequired"); })
                {
                    PurgeStage = (job, _) =>
                    {
                        Stages++;
                        if (PurgeFault == "stage") throw new IOException("stageRefused");
                        var staged = job.Held! with { FullPath = Path.Combine(Target.RecoveryRoot, job.Purge!.StageName) };
                        Files.Remove(job.Held!.FullPath);
                        Files.Add(staged.FullPath, staged);
                        if (PurgeFault == "stageAfterRename") throw new IOException("purgeStageFlush");
                        if (PurgeFault == "postStageHistory") Library.PurgeFingerprint = "changed";
                        if (PurgeFault == "libraryThrows") Library.ThrowOnState = true;
                        if (UnstageFault == "heldOccupied") Files.Add(job.Held.FullPath, job.Held with { Sha256 = new string('b', 64) });
                        if (PurgeFault == "committingSave") Journal.FailPhase = RemovalPhase.PurgeCommitting;
                        return Task.FromResult(new RemovalPurgeStage(staged, (_, authorize, _) =>
                        {
                            if (PurgeFault == "duringNativeHash") Library.PurgeFingerprint = "changed";
                            authorize();
                            if (PurgeFault == "noUnlink") return Task.FromResult(RemovalPurgeResult.NoUnlink);
                            Unlinks++;
                            Files.Remove(staged.FullPath);
                            return Task.FromResult(PurgeFault == "unlinkUnknown" ? RemovalPurgeResult.UnlinkOutcomeUnknown : RemovalPurgeResult.UnlinkedAndFlushed);
                        }, () => { }, (prepared, _) => Unstage(prepared)));
                    },
                    PurgeUnstage = (job, stage, token) => stage is not null ? stage.UnstageAsync(job, token) : Unstage(job),
                });
        }

        // Models both native rollback paths: rename the staged name back, or prove nothing moved.
        private Task<RemovalUnstageOutcome> Unstage(RemovalJob job)
        {
            Unstages++;
            if (UnstageFault == "rollbackSaveFails") Journal.FailPhase = RemovalPhase.ReplacementReady;
            var staged = Path.Combine(Target.RecoveryRoot, job.Purge!.StageName);
            var heldPresent = Files.ContainsKey(job.Held!.FullPath);
            if (UnstageFault is "unstageFails" || heldPresent == Files.ContainsKey(staged)) return Task.FromResult(RemovalUnstageOutcome.NotRestored);
            if (!heldPresent)
            {
                Files.Remove(staged);
                Files.Add(job.Held.FullPath, job.Held);
            }

            if (RehashFailures > 0)
            {
                RehashFailures--;
                CancelAtRehash?.Cancel();
                return Task.FromResult(RemovalUnstageOutcome.NotRestored);
            }

            return Task.FromResult(new RemovalUnstageOutcome(RemovalUnstageResult.Restored, job.Held));
        }

        internal async Task<string> Ready() => Json(await Service.ConfirmAsync(Admin, await Plan(), default)).GetProperty("jobId").GetString()!;

        internal async Task<string> PurgePlan(string id)
        {
            Service.EnablePermanentDeletion(true);
            return Json(await Service.CreatePurgePlanAsync(Admin, id, default)).GetProperty("planId").GetString()!;
        }

        internal async Task<string> Plan()
        {
            var state = Json(await Service.StateAsync(Target.PeerKey, default));
            var candidate = state.GetProperty("candidates")[0].GetProperty("candidateId").GetString()!;
            return Json(await Service.PlanAsync(Admin, candidate, default)).GetProperty("planId").GetString()!;
        }

        public Task<RemovalOriginDto> ProveAsync(RemovalTarget target, CancellationToken cancellationToken)
        {
            OriginCalls++;
            if (!Files.ContainsKey(Target.Path)) PostMoveProof = true;
            if (FailAllOrigin || (FailPostMoveOrigin && !Files.ContainsKey(Target.Path))) throw new InvalidOperationException("originUnavailable");
            if (Files.Keys.Any(k => Path.GetFileName(k).StartsWith(".purge-", StringComparison.Ordinal)))
            {
                if (UnstageFault == "journalMovedOn") Journal.Jobs[Journal.Jobs.Keys.Single()] = Journal.Jobs.Values.Single() with { Phase = RemovalPhase.PurgeCommitting };
                if (PurgeFault == "originOffline") throw new InvalidOperationException("originUnavailable");
                if (PurgeFault == "originRateLimited") throw new InvalidOperationException("originRateLimited");
                if (PurgeFault == "canceled") throw new OperationCanceledException();
            }

            return Task.FromResult(new RemovalOriginDto(1, Original.Sha256, Original.Length, target.ProviderIds, target.RuntimeTicks, new string('c', 64)));
        }

        public void Dispose() => Service.Dispose();
    }

    private sealed class Settings : ISettingsAccessor
    {
        public PluginConfiguration Current { get; private set; } = new() { LocalRemovalEnabled = true };
        public void Save(PluginConfiguration configuration) => Current = configuration;
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Journal : IRemovalJournal
    {
        internal readonly Dictionary<string, RemovalJob> Jobs = new();
        internal readonly List<RemovalJob> Saved = [];
        internal bool Uncertain;
        internal RemovalPhase? FailPhase;
        internal bool FailLoad;
        public Task EnsureCanPlanAsync(CancellationToken cancellationToken) => Uncertain ? throw new InvalidOperationException("recoveryRequired") : Task.CompletedTask;
        public Task EnsureCanPurgeAsync(string jobId, CancellationToken cancellationToken) => EnsureCanPlanAsync(cancellationToken);
        public Task<IReadOnlyList<RemovalJob>> LoadAsync(CancellationToken cancellationToken) => FailLoad ? throw new IOException("journalUnreadable") : Task.FromResult<IReadOnlyList<RemovalJob>>(Jobs.Values.ToArray());
        public Task SaveAsync(RemovalJob job, CancellationToken cancellationToken)
        {
            if (job.Phase == FailPhase)
            {
                // A committing record is modelled as durable despite the failure; a rollback record is not.
                if (job.Phase == RemovalPhase.PurgeCommitting) Jobs[job.JobId] = job;
                FailPhase = null;
                throw new IOException("injected");
            }

            Jobs[job.JobId] = job;
            Saved.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class Hold : ISyncHold, IDisposable
    {
        public IDisposable TryHold() => this;
        public void Dispose() { }
    }

    private sealed class Library : IRemovalLibrary
    {
        internal RemovalTarget Target = null!;
        internal readonly RemovalUserState[] History = [new(Guid.NewGuid(), true, false, 80_000_000, 2, null, true, 8, true, 1, 2)];
        internal RemovalUserState? Latest;
        internal IReadOnlyList<RemovalUserState> RestoredHistory = [];
        internal bool CapturedBeforeRestore, FailHandoff;
        internal int Imports;
        internal bool AdminRevoked, ThrowOnState;
        internal string PurgeFingerprint = "current-history";
        public void VerifyPurgeAdministrator(Guid admin) { if (AdminRevoked) throw new InvalidOperationException("administratorRequired"); }
        public RemovalPurgeState VerifyPurgeState(RemovalTarget target, Guid sharedItemId, IReadOnlyList<RemovalUserState> baseline) => ThrowOnState ? throw new KeyNotFoundException("item vanished") : new(PurgeFingerprint, History);
        internal IReadOnlyList<RemovalTarget>? Many;
        public IReadOnlyList<RemovalTarget> Discover(string peerKey) => Many ?? [Target];
        public RemovalTarget Revalidate(RemovalTarget target) => target;
        public IReadOnlyList<RemovalUserState> Capture(Guid itemId) => History;
        public void VerifyAccess(RemovalTarget target, IReadOnlyList<RemovalUserState> history) { }
        public void Deindex(RemovalTarget target) { }
        public Task<Guid> ImportAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> history, CancellationToken cancellationToken) { Imports++; return Task.FromResult(Guid.NewGuid()); }
        public void Handoff(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history) { if (FailHandoff) throw new InvalidOperationException("historyConflict"); }
        public IReadOnlyList<RemovalUserState> CaptureShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> baseline) { CapturedBeforeRestore = true; return [Latest ?? History[0]]; }
        public void VerifyShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history) { }
        public Task<Guid> RestoreAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> baseline, IReadOnlyList<RemovalUserState> shared, CancellationToken cancellationToken) { RestoredHistory = shared; return Task.FromResult(target.LocalItemId); }
    }
}
