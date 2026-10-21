using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The Combine into my libraries preset (owner decision, v2.9): friends' titles go into the admin's own main library of each type (placement Main). The target is
/// picked automatically when there is one candidate, else the preview lists the candidates and Apply waits for a pick; a library of Shoal's is never a candidate;
/// moving away again removes only Shoal's own folder from the main library.
/// </summary>
public sealed class CombineIntoMineTests
{
    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
    }

    private static LayoutPresetService Presets(JobRig rig) =>
        new(rig.State, rig.Accounts, rig.Job, rig.Groups, rig.Libraries, new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance));

    [Fact]
    public async Task OneMainMovieLibrary_IsPickedAutomatically_AndThePreviewCreatesOneMainPlacementLibraryInIt()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        rig.Libraries.SetSavesNfo(movies.Id, true);

        var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;

        Assert.False(preview.NeedsTargets);
        var choice = Assert.Single(preview.Targets);
        Assert.Equal("movies", choice.Type);
        Assert.Equal(movies.Id, choice.Chosen);
        Assert.Equal([movies.Id], choice.Candidates.Select(static c => c.Id)); // the per-friend library (Shoal's) is not a candidate
        Assert.Equal([new PresetNewLibrary("Friends in Movies", "movies")], preview.Created);
        Assert.Equal(["Movies"], preview.TargetsSavingNfo);
    }

    [Fact]
    public async Task TwoMainMovieLibraries_TheAdminMustPick_ApplyWaits_ThenRunsWithThePick()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        var kids = rig.Libraries.AddMain("Kids movies", "movies");

        var open = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;
        Assert.True(open.NeedsTargets);
        Assert.Null(open.Targets.Single().Chosen);
        Assert.Equal(2, open.Targets.Single().Candidates.Count);
        Assert.Empty(open.Moves);
        Assert.Equal(LayoutJobError.TargetRequired, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, open.PlanHash));

        var targets = new Dictionary<string, Guid> { ["movies"] = kids.Id };
        var picked = Presets(rig).Preview(LayoutPreset.CombineIntoMine, targets)!;
        Assert.False(picked.NeedsTargets);
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, picked.PlanHash, targets));
        await rig.Job.Running;

        var combined = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.Equal(LibraryPlacement.Main, combined.Placement);
        Assert.Contains(rig.Libraries.List().Single(l => l.Id == kids.Id).Locations, l => LibraryFolders.SamePath(l, rig.LibFolder(combined.LibKey)));
        Assert.Equal(LayoutPreset.CombineIntoMine, rig.State.Current.Home.Layout.Default);
        Assert.Equal(combined.LibKey, rig.State.Current.Home.Layout.DefaultTargets["movies"]); // new connections join it too
    }

    [Fact]
    public async Task AShoalLibraryOrAWrongType_IsRejectedAsATarget()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        var shows = rig.Libraries.AddMain("Shows", "tvshows");
        var shoal = rig.Libraries.List().Single(l => l.Name != "Movies" && l.Name != "Shows"); // the per-friend library

        foreach (var bad in new[] { shoal.Id, shows.Id })
        {
            var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine, new Dictionary<string, Guid> { ["movies"] = bad })!;
            Assert.True(preview.NeedsTargets);
            Assert.True(preview.Targets.Single().Rejected);
            Assert.Empty(preview.Moves);
        }
    }

    [Fact]
    public async Task Undo_ToAllMixed_RemovesOnlyShoalsFolderFromTheMainLibrary_ItsOwnFoldersStay()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        await rig.Libraries.AddPathAsync("Movies", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "media-Movies-4k"), default); // the admin's second folder
        var own = rig.Libraries.List().Single(l => l.Id == movies.Id).Locations.ToList();

        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.CombineIntoMine)!.PlanHash));
        await rig.Job.Running;
        Assert.Equal(own.Count + 1, rig.Libraries.List().Single(l => l.Id == movies.Id).Locations.Count);

        var back = Presets(rig).Preview(LayoutPreset.AllMixed)!;
        Assert.Single(back.Removed);
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, back.PlanHash));
        await rig.Job.Running;

        Assert.Equal(own, rig.Libraries.List().Single(l => l.Id == movies.Id).Locations);
        Assert.DoesNotContain("remove:Movies", rig.Libraries.Operations);
        Assert.Contains("removepath:Movies", rig.Libraries.Operations);
        Assert.Equal(LayoutPresetService.MixedMovies, rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy).Name);
    }

    [Fact]
    public async Task APlanHashFromAnotherPick_IsRefused()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var a = rig.Libraries.AddMain("Movies", "movies");
        var b = rig.Libraries.AddMain("Kids movies", "movies");
        var hashA = Presets(rig).Preview(LayoutPreset.CombineIntoMine, new Dictionary<string, Guid> { ["movies"] = a.Id })!.PlanHash;

        Assert.Equal(LayoutJobError.PlanChanged, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, hashA, new Dictionary<string, Guid> { ["movies"] = b.Id }));
    }

    [Fact]
    public async Task Pilot17_AfterCombine_SyncsReconcilesScansAndARestart_KeepTheLayout_AndCreateNoLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.CombineIntoMine)!.PlanHash));
        await rig.Job.Running;
        var libraries = rig.State.Current.Home.Libraries.Select(static l => (l.LibKey, l.Placement, l.Name, string.Join(',', l.Feeds))).ToList();
        var creates = rig.Libraries.CreateCalls;
        var operations = rig.Libraries.Operations.Count;
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);

        for (var round = 0; round < 4; round++)
        {
            rig.Sync.Time.Advance(TimeSpan.FromHours(7));
            await rig.Sync.SyncAsync(); // the periodic sync
            await rig.Visibility.ReconcileAsync(default); // what a scan's post-scan task runs
            guard.Enforce();
            var restarted = rig.NewJob(); // a restart: the job service resumes nothing (the last job is Done)
            await restarted.StartAsync(default);
            await restarted.Running;
        }

        Assert.Equal(LayoutPreset.CombineIntoMine, rig.State.Current.Home.Layout.Default);
        Assert.Equal(libraries, rig.State.Current.Home.Libraries.Select(static l => (l.LibKey, l.Placement, l.Name, string.Join(',', l.Feeds))).ToList());
        Assert.Equal(creates, rig.Libraries.CreateCalls);
        Assert.Equal(operations, rig.Libraries.Operations.Count); // no library created, removed or re-pathed
    }

    [Fact]
    public async Task Warden13L3_AUserWhoCannotOpenTheMainLibrary_IsReportedAsLosingAccess()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        var shoal = rig.Libraries.List().Single(l => l.Name != "Movies"); // the per-friend library Alice opens today

        // Alice is admitted to the friend's titles and has the per-friend library on her list, but not the admin's Movies.
        rig.Accounts.Edit(rig.Alice.Id, all: false, folders: [shoal.Id]);

        var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;

        Assert.Equal(movies.Id, preview.Targets.Single().Chosen);
        Assert.Contains(rig.Alice.Id, preview.UsersLosingAccess);
        Assert.DoesNotContain(rig.Admin.Id, preview.UsersLosingAccess); // "all libraries" opens Movies

        // Once Movies is on her list she keeps access.
        rig.Accounts.Edit(rig.Alice.Id, all: false, folders: [shoal.Id, movies.Id]);
        Assert.DoesNotContain(rig.Alice.Id, Presets(rig).Preview(LayoutPreset.CombineIntoMine)!.UsersLosingAccess);
    }

    // Warden 14: the friend sits in Friends' movies (All mixed); a placement-Main library in Movies already exists (Everyone) but its stored id is lost.
    // Alice opens Friends' movies but not Movies; Bob opens nothing. Combine moves the friend into the existing Main library.
    private static async Task<(LibraryInfo Movies, LayoutPresetService Presets)> ExistingMainWithNoStoredId(JobRig rig)
    {
        var movies = rig.Libraries.AddMain("Movies", "movies");
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash));
        await rig.Job.Running;
        var mixed = rig.Libraries.List().Single(l => l.Name == LayoutPresetService.MixedMovies);
        var main = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, movies.Id), rig.Admin.Id);
        Assert.True(main.Ok);
        await rig.State.UpdateAsync(s =>
        {
            var library = s.Home.Libraries.First(l => l.LibKey == main.LibKey);
            library.JellyfinLibraryId = null; // null or stale (Warden 13 L1)
            library.Access = new LibraryAccess { Mode = AccessMode.Everyone };
        });
        rig.Accounts.Edit(rig.Alice.Id, all: false, folders: [mixed.Id]);
        rig.Accounts.Edit(rig.Bob.Id, all: false, folders: []);
        return (movies, Presets(rig));
    }

    [Fact]
    public async Task Warden14_AnExistingMainTargetWithoutAStoredId_StillChecksWhoCanOpenTheMainLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var (_, presets) = await ExistingMainWithNoStoredId(rig);

        var preview = presets.Preview(LayoutPreset.CombineIntoMine)!;

        Assert.Equal("Friends in Movies", Assert.Single(preview.Moves).To);
        Assert.Contains(rig.Alice.Id, preview.UsersLosingAccess);
        Assert.DoesNotContain(rig.Bob.Id, preview.UsersGainingAccess); // Everyone admits Bob, but he cannot open Movies
    }

    [Fact]
    public async Task Warden14_TheJobsLostAndGainedAccess_AgreeWithThePreview()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var (_, presets) = await ExistingMainWithNoStoredId(rig);
        var preview = presets.Preview(LayoutPreset.CombineIntoMine)!;

        Assert.Equal(LayoutJobError.None, await presets.ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, preview.PlanHash));
        await rig.Job.Running;
        var status = rig.Job.Status();

        Assert.Contains(rig.Alice.Id, status.LostAccess);
        Assert.Equal(preview.UsersLosingAccess.Order(), status.LostAccess.Order());
        Assert.Equal(preview.UsersGainingAccess.Order(), (status.GainedAccess ?? []).Order());
    }

    [Fact]
    public async Task Warden14_UndoFromAMainLibrary_ReportsAUserWhoCouldNotOpenItAsGaining()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.CombineIntoMine)!.PlanHash));
        await rig.Job.Running;
        await rig.State.UpdateAsync(s => s.Home.Libraries.Single(l => !l.IsLegacy).JellyfinLibraryId = null);
        rig.Accounts.Edit(rig.Alice.Id, all: false, folders: []); // admitted by Shoal, but Movies is not on her list: she sees no friend titles today

        var back = Presets(rig).Preview(LayoutPreset.AllMixed)!;

        Assert.Contains(rig.Alice.Id, back.UsersGainingAccess); // Friends' movies is Shoal's own library: she will see it
        Assert.DoesNotContain(rig.Alice.Id, back.UsersLosingAccess);
    }

    [Fact]
    public void ANewInstall_StartsWithAllMixed()
    {
        Assert.Equal(LayoutPreset.AllMixed, new ShoalState().Home.Layout.Default);
    }
}
