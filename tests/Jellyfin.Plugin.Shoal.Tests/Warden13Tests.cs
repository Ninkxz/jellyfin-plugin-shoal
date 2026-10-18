using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ShoalLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 13 (Combine preset and fix batches): M1 shared removal rule, M2 Combine with a type the admin has no library of, L1 lock only by folder, L2 undo from
/// Combine for a sole feeder, L4 dangling path after a failed add, L5 missing main library, L6 rewrite warning and the shoal-lib tag check.
/// </summary>
public sealed class Warden13Tests
{
    private static readonly string AdminFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admin-own-media");

    private static void Seed(FakeLibraryPeer p) => p.Add("Alien", 1979, tmdb: 348);

    private static LayoutPresetService Presets(JobRig rig) =>
        new(rig.State, rig.Accounts, rig.Job, rig.Groups, rig.Libraries, new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance));

    // A second friend with a TV mapping and its per-friend library (state only; enough for planning).
    private static Task AddTvFriendAsync(JobRig rig) => rig.State.UpdateAsync(s =>
    {
        s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Barley", Libraries = { new LibraryMapping { MappingKey = "tv-1", RemoteName = "TV", LocalLibraryName = "Barley TV", Subfolder = "tv", CollectionType = "tvshows" } } });
        s.Home.Libraries.Add(new ShoalLibrary { LibKey = StateUpgrade.LegacyLibKey(StateSamples.PeerKeyB, "tv-1"), Name = "Barley TV", Type = "tvshows", PathRel = StateSamples.PeerKeyB + "/tv", Feeds = [StateSamples.PeerKeyB] });
    });

    [Fact]
    public async Task M1_RemoveEverything_KeepsALibraryTheAdminAddedFoldersTo_OnlyShoalsFolderGoes()
    {
        using var rig = new LayoutRig();
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);
        await rig.Libraries.AddPathAsync("Friends' movies", AdminFolder, default);

        await rig.Layout.RemoveAllFromJellyfinAsync();

        var kept = rig.Libraries.List().Single(l => l.Name == "Friends' movies");
        Assert.Equal([AdminFolder], kept.Locations);
        Assert.DoesNotContain("remove:Friends' movies", rig.Libraries.Operations);
        Assert.DoesNotContain("purge:Friends' movies", rig.Libraries.Operations);
        Assert.Contains("purgebelow:Friends' movies", rig.Libraries.Operations);
    }

    [Fact]
    public async Task M1_TheLegacyRemoval_KeepsAPerFriendLibraryTheAdminAddedFoldersTo()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var legacy = rig.Libraries.List().Single();
        await rig.Libraries.AddPathAsync(legacy.Name, AdminFolder, default);

        Assert.True(await rig.Legacy.RemoveAsync(SyncRig.PeerKey, SyncRig.MappingKey));

        Assert.Equal([AdminFolder], rig.Libraries.List().Single(l => l.Name == legacy.Name).Locations);
        Assert.DoesNotContain("remove:" + legacy.Name, rig.Libraries.Operations);
    }

    [Fact]
    public async Task M1_ALibraryOfShoalsAlone_StillGoesWhole()
    {
        using var rig = new LayoutRig();
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);

        await rig.Layout.RemoveAllFromJellyfinAsync();

        Assert.Contains("remove:Friends' movies", rig.Libraries.Operations);
    }

    [Fact]
    public async Task M2_NoTvLibraryOfTheAdmins_CombineApplies_FriendsShowsStayWhereTheyAre()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddTvFriendAsync(rig);
        rig.Libraries.AddMain("Movies", "movies");

        var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;
        Assert.False(preview.NeedsTargets);
        Assert.Empty(preview.Targets.Single(t => t.Type == "tvshows").Candidates);
        Assert.All(preview.Moves, m => Assert.Equal("movies", m.Type));

        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, preview.PlanHash));
        await rig.Job.Running;
        Assert.Contains(rig.State.Current.Home.Libraries, l => l.Name == "Barley TV"); // the friend's shows stay in their own library
        Assert.Equal(LayoutPreset.CombineIntoMine, rig.State.Current.Home.Layout.Default);
    }

    [Fact]
    public async Task M2_NoMovieLibraryOfTheAdmins_CombineApplies_AndMovesNoMovies()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Shows", "tvshows");

        var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;
        Assert.False(preview.NeedsTargets);
        Assert.Empty(preview.Moves);
        Assert.Equal(LayoutJobError.None, await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, preview.PlanHash));
        Assert.Equal(LayoutPreset.CombineIntoMine, rig.State.Current.Home.Layout.Default);
    }

    [Fact]
    public async Task L1_AStaleIdNeverLocksTheAdminsLibrary()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == key).JellyfinLibraryId = rig.Main.Id); // the admin swapped folders; the id is stale
        rig.Libraries.Hidden.Add("Friends' movies");
        rig.Libraries.Unlocked.Add(rig.Main.Name);

        new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance).Enforce();

        Assert.DoesNotContain(rig.Main.Name, rig.Libraries.Locked);
    }

    [Fact]
    public async Task L2_OnePerFriend_AfterCombine_MovesASoleFeederOutOfTheMainLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        rig.Libraries.AddMain("Movies", "movies");
        await Presets(rig).ApplyAsync(LayoutPreset.CombineIntoMine, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.CombineIntoMine)!.PlanHash);
        await rig.Job.Running;
        Assert.Single(rig.State.Current.Home.Libraries.Single(l => l.Placement == LibraryPlacement.Main).Feeds);

        var back = Presets(rig).Preview(LayoutPreset.PerFriend)!;

        Assert.Single(back.Moves);
        Assert.Single(back.Created);
        Assert.Single(back.Removed);
    }

    [Fact]
    public async Task L4_AHalfDoneAddIntoAMainLibrary_TakesShoalsFolderOutAgain()
    {
        using var rig = new LayoutRig();
        rig.Libraries.FailAfterAddPath = true;

        var result = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id);

        Assert.Equal(LayoutError.Failed, result.Error);
        Assert.Equal(rig.Main.Locations, rig.Libraries.List().Single(l => l.Id == rig.Main.Id).Locations);
        Assert.Contains("removepath:Movies", rig.Libraries.Operations);
    }

    [Fact]
    public async Task L5_AMainRecordNoLibraryHolds_IsNamedInTheStatus()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        var guard = new LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);
        Assert.Empty(guard.MainLibrariesMissing());

        await rig.Libraries.RemovePathAsync("Movies", LibraryFolders.PathOf(rig.StubRoot, rig.Library(key))!, default); // the admin took it out by hand

        Assert.Equal(["Friends in Movies"], guard.MainLibrariesMissing());
    }

    [Fact]
    public async Task L6_ThePreviewNamesTargetsWhoseProvidersCanRewriteFriendsTitles()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var movies = rig.Libraries.AddMain("Movies", "movies");
        rig.Libraries.Change(movies.Id, static l => l with { FetchesMetadata = true });

        var preview = Presets(rig).Preview(LayoutPreset.CombineIntoMine)!;

        Assert.Equal(["Movies"], preview.TargetsRewritingTitles);
        Assert.Empty(preview.TargetsSavingNfo);
    }

    [Fact]
    public void L6_AStubWhoseNfoLostItsLibraryTag_IsDamaged()
    {
        using var dir = new TempDirectory();
        var entry = new CatalogEntry { Kind = EntryKind.Movie, StubKey = Guid.NewGuid().ToString("N")[..16], StubRelPath = "Alien/Alien.strm" };
        var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Alien")).FullName;
        var label = System.Security.SecurityElement.Escape(FriendLabel.Text);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Alien.nfo"), "<movie><plot>" + label + "</plot><tag>Shoal</tag></movie>");

        Assert.Equal(StubDamage.TagMissing, StubInspector.Of(dir.Path, entry, ["shoal-lib-0123abcd"]));
        Assert.Equal(StubDamage.None, StubInspector.Of(dir.Path, entry));

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Alien.nfo"), "<movie><plot>" + label + "</plot><tag>shoal-lib-0123abcd</tag></movie>");
        Assert.Equal(StubDamage.None, StubInspector.Of(dir.Path, entry, ["shoal-lib-0123abcd"]));
    }
}
