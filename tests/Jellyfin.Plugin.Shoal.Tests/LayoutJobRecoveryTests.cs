using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 10 M2: an unfinished move job blocks syncs, can be resumed or abandoned by the admin, is retried at startup until the sync lock frees, and a taken
/// target folder is merged only when it is this very title (else a distinct error).
/// </summary>
public sealed class LayoutJobRecoveryTests
{
    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
        p.Add("Brazil", 1985, tmdb: 68);
    }

    private static MoveRequest NewLibraryMove() => new(SyncRig.PeerKey, "movies", null, new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal));

    private static async Task<JobRig> CrashedAfterTwoStepsAsync()
    {
        var rig = await JobRig.CreateAsync(Seed);
        rig.Sync.Sync.LayoutUnfinished = () => rig.Job.Unfinished;
        rig.Job.CrashAfterStep = steps => steps == 2;
        Assert.Equal(LayoutJobError.None, await rig.Job.StartAsync([NewLibraryMove()], rig.Admin.Id));
        await rig.Job.Running;
        rig.NewJob(); // the restart
        return rig;
    }

    [Fact]
    public async Task Pilot14_AFinishedMove_ScansTheLibraryItFilled_WithoutAFullScan()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var scansBefore = rig.Sync.Refresh.Scans;

        Assert.Equal(LayoutJobError.None, await rig.Job.StartAsync([NewLibraryMove()], rig.Admin.Id));
        await rig.Job.Running;

        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.NotNull(target.JellyfinLibraryId);
        Assert.Contains(Jellyfin.Plugin.Shoal.State.LibraryFolders.PathOf(rig.State.Current.StubRoot, target), rig.Sync.Refresh.LibraryScans); // found by its folder (Pilot 15)
        Assert.Equal(scansBefore, rig.Sync.Refresh.Scans); // never every library
    }

    [Fact]
    public async Task AnUnfinishedJob_BlocksEverySync_UntilItIsResumedAndDone()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();

        Assert.True(rig.Job.Unfinished);
        Assert.Equal("layoutJob", (await rig.Sync.SyncAsync()).Error);

        Assert.Equal(LayoutJobError.None, rig.Job.TryResume());
        await rig.Job.Running;

        Assert.False(rig.Job.Unfinished);
        Assert.Equal(LayoutJobPhase.Done, rig.Job.Status().Phase);
        Assert.Null((await rig.Sync.SyncAsync()).Error);
        Assert.Equal(LayoutJobError.NotFound, rig.Job.TryResume());
    }

    [Fact]
    public async Task Abandon_EndsTheWait_SyncsTheMovedConnectionsAgain_AndLetsANewJobStart()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();
        var synced = new List<string>();
        rig.Job.RequestSync = synced.Add;

        Assert.Equal(LayoutJobError.None, rig.Job.Abandon());

        Assert.Equal(LayoutJobPhase.Abandoned, rig.Job.Status().Phase);
        Assert.Equal("abandoned", rig.Job.Status().Error);
        Assert.False(rig.Job.Unfinished);
        Assert.Equal([SyncRig.PeerKey], synced);
        Assert.Equal(LayoutJobError.NotFound, rig.Job.Abandon());
        Assert.Equal(LayoutJobError.NotFound, rig.Job.TryResume());
        Assert.NotEqual("layoutJob", (await rig.Sync.SyncAsync()).Error);
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.NotEqual(LayoutJobError.Busy, await rig.NewJob().StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", target.LibKey)], rig.Admin.Id));
    }

    [Fact]
    public async Task AbandonAndResume_AreRefusedWhileTheJobRuns()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var outcomes = new List<LayoutJobError>();
        rig.Job.CrashAfterStep = _ =>
        {
            outcomes.Add(rig.Job.Abandon());
            outcomes.Add(rig.Job.TryResume());
            return false;
        };

        await rig.Job.StartAsync([NewLibraryMove()], rig.Admin.Id);
        await rig.Job.Running;

        Assert.All(outcomes, static o => Assert.Equal(LayoutJobError.Busy, o));
        Assert.Equal(LayoutJobPhase.Done, rig.Job.Status().Phase);
    }

    [Fact]
    public async Task ATakenTarget_ThatIsThisVeryTitle_IsMerged_AndTheJobFinishes()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        var left = Directory.EnumerateDirectories(rig.Sync.LibraryFolder).Single();
        var copy = Path.Combine(rig.LibFolder(target.LibKey), Path.GetFileName(left));
        Directory.CreateDirectory(copy);
        foreach (var strm in Directory.EnumerateFiles(left, "*.strm"))
        {
            File.Copy(strm, Path.Combine(copy, Path.GetFileName(strm))); // what a sync after the switch would have written: the same sentinel
        }

        Assert.Equal(LayoutJobError.None, rig.Job.TryResume());
        await rig.Job.Running;

        Assert.Equal(LayoutJobPhase.Done, rig.Job.Status().Phase);
        Assert.False(Directory.Exists(left));
        Assert.Equal(3, Directory.EnumerateDirectories(rig.LibFolder(target.LibKey)).Count());
    }

    [Fact]
    public async Task ATakenTarget_ThatIsAnotherTitle_StopsWithTargetTaken_AndTouchesNeither()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        var left = Directory.EnumerateDirectories(rig.Sync.LibraryFolder).Single();
        var other = Path.Combine(rig.LibFolder(target.LibKey), Path.GetFileName(left));
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.strm"), StubSentinel.Content("zzzzzzzzzzzzzzzz"));

        Assert.Equal(LayoutJobError.None, rig.Job.TryResume());
        await rig.Job.Running;

        var status = rig.Job.Status();
        Assert.Equal(LayoutJobPhase.Failed, status.Phase);
        Assert.Equal("targetTaken", status.Error);
        Assert.True(Directory.Exists(left));
        Assert.Equal(StubSentinel.Content("zzzzzzzzzzzzzzzz"), File.ReadAllText(Path.Combine(other, "x.strm")));
        Assert.True(rig.Job.Unfinished);
        Assert.Equal(LayoutJobError.None, rig.Job.Abandon()); // the way out without hand-deleting the journal
    }

    [Fact]
    public async Task W11L6_AJobThatStops_TellsTheAdmins_AndItsStatusOffersResumeAndAbandon()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();
        var admins = new FakeAdmins();
        rig.Job.Notifier = admins;
        Assert.True(rig.Job.Status().CanResume);
        Assert.True(rig.Job.Status().CanAbandon);
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        var left = Directory.EnumerateDirectories(rig.Sync.LibraryFolder).Single();
        var other = Path.Combine(rig.LibFolder(target.LibKey), Path.GetFileName(left));
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.strm"), StubSentinel.Content("zzzzzzzzzzzzzzzz"));

        rig.Job.TryResume();
        await rig.Job.Running;
        await Task.Delay(50);

        Assert.Equal(["Shoal: " + LayoutJobService.FailedNotice], admins.Messages);
        Assert.True(rig.Job.Status().CanResume);
        rig.Job.Abandon();
        Assert.False(rig.Job.Status().CanResume);
        Assert.False(rig.Job.Status().CanAbandon);
    }

    [Fact]
    public async Task AtStartup_AJobThatFindsTheSyncLockHeld_IsTriedAgainUntilItRuns()
    {
        await using var rig = await CrashedAfterTwoStepsAsync();
        var job = rig.NewJob();
        job.ResumeRetry = TimeSpan.FromMilliseconds(50);
        var held = rig.Sync.Sync.TryHold()!;

        await job.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.NotEqual(LayoutJobPhase.Done, job.Status().Phase);

        held.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.Status().Phase != LayoutJobPhase.Done && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await job.Running;
        Assert.Equal(LayoutJobPhase.Done, job.Status().Phase);
        await job.StopAsync(CancellationToken.None);
        job.Dispose();
    }
}
