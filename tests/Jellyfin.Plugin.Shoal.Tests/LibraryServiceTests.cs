using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ShoalLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A Home with two friends, a main movie library, three accounts, and the K44/K45 services wired as the plugin wires them.
/// </summary>
internal sealed class LayoutRig : IDisposable
{
    public const string PeerA = "4b0c1d2e3f405162738495a6b7c8d9e0";
    public const string PeerB = "00112233445566778899aabbccddeeff";

    public LayoutRig(LayoutPreset preset = LayoutPreset.AllMixed)
    {
        Dir = new TempDirectory();
        StubRoot = Dir.Combine("stubs");
        Paths = new ShoalPaths(Dir.Path);
        State = new StateStore(Paths, NullLogger.Instance, TimeProvider.System, null);
        State.UpdateAsync(s =>
        {
            s.StubRoot = StubRoot;
            s.Home.Layout.Default = preset;
            foreach (var (key, name) in new[] { (PeerA, "Hops"), (PeerB, "Barley") })
            {
                s.Home.Peers.Add(new PeerState
                {
                    PeerKey = key,
                    DisplayName = name,
                    BaseUrl = "http://" + name.ToLowerInvariant() + ".tail1234.ts.net:8096",
                    Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = name + " Movies", Subfolder = "movies" } },
                });
            }
        }).GetAwaiter().GetResult();
        Main = Libraries.AddMain("Movies", "movies");
        Shows = Libraries.AddMain("Shows", "tvshows");
        Admin = Accounts.Add("admin", admin: true);
        Alice = Accounts.Add("alice");
        Bob = Accounts.Add("bob");
        Tags = new TagService(State, Accounts, Libraries, NullLogger.Instance);
        Visibility = new VisibilityService(State, Libraries, Accounts, TimeProvider.System, NullLogger.Instance, Tags);
        Access = new AccessService(State, Accounts, Visibility, TimeProvider.System);
        Layout = new LibraryLayoutService(State, Libraries, Accounts, Visibility, Lock, TimeProvider.System, NullLogger.Instance, Tags);
        Libs = new ShoalLibraryService(State, Libraries, Visibility, TimeProvider.System, NullLogger<ShoalLibraryService>.Instance, Layout);
    }

    public TempDirectory Dir { get; }

    public string StubRoot { get; }

    public ShoalPaths Paths { get; }

    public StateStore State { get; }

    public FakeLibraries Libraries { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public LayoutLock Lock { get; } = new();

    public TagService Tags { get; }

    public VisibilityService Visibility { get; }

    public AccessService Access { get; }

    public LibraryLayoutService Layout { get; }

    public ShoalLibraryService Libs { get; }

    public LibraryInfo Main { get; }

    public LibraryInfo Shows { get; }

    public AccountInfo Admin { get; }

    public AccountInfo Alice { get; }

    public AccountInfo Bob { get; }

    public ShoalLibrary Library(string libKey) => State.Current.Home.Libraries.Single(l => l.LibKey == libKey);

    public IReadOnlyList<string> TagsOf(AccountInfo account) => Accounts.Get(account.Id).BlockedTags ?? [];

    public void Dispose()
    {
        Tags.Dispose();
        Visibility.Dispose();
        Lock.Dispose();
        State.Dispose();
        Dir.Dispose();
    }
}

/// <summary>
/// PLAN v2.9 K44 (library service) and K45 (access): create, rename, delete and order libraries; placement Shoal (EnabledFolders) and Main (the shoal-lib tag, on
/// before the folder); the tag record so Shoal removes only what it added; per-user "Hide friends' titles"; the share rule; a new connection joins the layout.
/// </summary>
public sealed class LibraryServiceTests
{
    [Fact]
    public async Task Create_PlacementShoal_MakesAJellyfinLibraryOfItsFolder_SelectedForTheAdminOnly()
    {
        using var rig = new LayoutRig();

        var result = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);

        Assert.True(result.Ok);
        var library = rig.Library(result.LibKey!);
        Assert.Equal("libs/" + result.LibKey, library.PathRel);
        Assert.Equal([rig.Admin.Id], library.Access.SelectedUserIds);
        Assert.Equal(AccessMode.Selected, library.Access.Mode);
        var jellyfin = rig.Libraries.List().Single(l => l.Name == "Friends' movies");
        Assert.Equal(Path.Combine(rig.StubRoot, "libs", result.LibKey!), jellyfin.Locations.Single());
        Assert.Equal(jellyfin.Id, library.JellyfinLibraryId);
        Assert.True(Directory.Exists(jellyfin.Locations.Single()));
        Assert.Contains(result.LibKey!, rig.State.Current.Home.LibraryOrder);

        // EnabledFolders: alice and bob (all libraries) are converted so the new library stays hidden from them; the admin keeps it.
        Assert.DoesNotContain(jellyfin.Id, rig.Accounts.Get(rig.Alice.Id).EnabledFolders);
        Assert.True(rig.Accounts.Get(rig.Admin.Id).CanOpen(jellyfin.Id));
    }

    [Fact]
    public async Task Create_PlacementMain_PutsTheLibraryTagOnEveryoneElse_BeforeTheFolderIsAdded()
    {
        using var rig = new LayoutRig();
        var tagged = false;
        rig.Libraries.OnAddPath = _ => tagged = rig.TagsOf(rig.Alice).Any(t => t.StartsWith("shoal-lib-", StringComparison.Ordinal)) && rig.TagsOf(rig.Bob).Any(t => t.StartsWith("shoal-lib-", StringComparison.Ordinal));

        var result = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id);

        Assert.True(result.Ok);
        var tag = LibraryFolders.LibTag(result.LibKey!);
        Assert.True(tagged); // the tag was on before the path went into the main library
        Assert.Contains("addpath:Movies", rig.Libraries.Operations);
        Assert.Contains(Path.Combine(rig.StubRoot, "libs", result.LibKey!), rig.Libraries.List().Single(l => l.Id == rig.Main.Id).Locations);
        Assert.Contains(tag, rig.TagsOf(rig.Alice));
        Assert.Contains(tag, rig.TagsOf(rig.Bob));
        Assert.DoesNotContain(tag, rig.TagsOf(rig.Admin));
        Assert.Equal([tag], rig.State.Current.Home.Visibility.ManagedBlockedTags[rig.Alice.Id]);

        // The main library is the admin's: Shoal does not manage its EnabledFolders.
        Assert.True(rig.Accounts.Get(rig.Alice.Id).EnableAllFolders);
    }

    [Fact]
    public async Task Create_PlacementMain_RefusesAMissingLibrary_AnotherType_OrALibraryBelowTheStubRoot()
    {
        using var rig = new LayoutRig();
        var shoal = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);
        var shoalId = rig.Library(shoal.LibKey!).JellyfinLibraryId;

        Assert.Equal(LayoutError.MainLibraryInvalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest("X", "movies", LibraryPlacement.Main, Guid.NewGuid()), rig.Admin.Id)).Error);
        Assert.Equal(LayoutError.MainLibraryInvalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest("X", "movies", LibraryPlacement.Main, rig.Shows.Id), rig.Admin.Id)).Error);
        Assert.Equal(LayoutError.MainLibraryInvalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest("X", "movies", LibraryPlacement.Main, shoalId), rig.Admin.Id)).Error);
        Assert.Single(rig.State.Current.Home.Libraries);
    }

    [Fact]
    public async Task Create_RefusesADuplicateName_ABadType_AndMoreThanTheCap()
    {
        using var rig = new LayoutRig();
        Assert.Equal(LayoutError.NameInUse, (await rig.Layout.CreateAsync(new LibraryCreateRequest("movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).Error); // Jellyfin's "Movies"
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest("Music", "music", LibraryPlacement.Shoal), rig.Admin.Id)).Error);
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest("  ", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).Error);

        for (var i = 0; i < LibraryFolders.MaxLibraries; i++)
        {
            Assert.True((await rig.Layout.CreateAsync(new LibraryCreateRequest("L" + i, "movies", LibraryPlacement.Shoal), rig.Admin.Id)).Ok);
        }

        Assert.Equal(LayoutError.TooMany, (await rig.Layout.CreateAsync(new LibraryCreateRequest("One more", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).Error);
    }

    [Fact]
    public async Task Create_WhenJellyfinRefuses_LeavesNoRecordAndNoFolder()
    {
        using var rig = new LayoutRig();
        rig.Libraries.FailCreate = true;

        var result = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id);

        Assert.Equal(LayoutError.Failed, result.Error);
        Assert.Empty(rig.State.Current.Home.Libraries);
        Assert.False(Directory.Exists(Path.Combine(rig.StubRoot, "libs")) && Directory.EnumerateDirectories(Path.Combine(rig.StubRoot, "libs")).Any());
    }

    [Fact]
    public async Task Rename_APlacementShoalLibrary_RenamesTheJellyfinLibrary_AndFindsItsNewIdByItsFolder()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var before = rig.Library(key).JellyfinLibraryId;

        var result = await rig.Layout.RenameAsync(key, "Pals' films");

        Assert.True(result.Ok);
        Assert.Equal("Pals' films", rig.Library(key).Name);
        Assert.Contains("rename:Friends' movies>Pals' films", rig.Libraries.Operations);
        var renamed = rig.Libraries.List().Single(l => l.Name == "Pals' films");
        Assert.Equal(renamed.Id, rig.Library(key).JellyfinLibraryId);
        Assert.NotEqual(before, renamed.Id);
        Assert.True(rig.Accounts.Get(rig.Admin.Id).CanOpen(renamed.Id)); // the admin still opens it after the id changed
        Assert.Equal(LayoutError.NameInUse, (await rig.Layout.RenameAsync(key, "Shows")).Error);
        Assert.Equal(LayoutError.NotFound, (await rig.Layout.RenameAsync("ffffffffffffffff", "X")).Error);
    }

    [Theory]
    [InlineData("../../../x")]
    [InlineData("/tmp/x")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData("..")]
    [InlineData("films.")]
    [InlineData(" lead")]
    [InlineData("lead ")]
    public async Task W10M3_RenameAndCreate_RefuseANameThatIsNotOnePlainFolderName_AndJellyfinIsNeverAsked(string name)
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;

        Assert.Equal(LayoutError.Invalid, (await rig.Layout.RenameAsync(key, name)).Error);
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.CreateAsync(new LibraryCreateRequest(name, "tvshows", LibraryPlacement.Shoal), rig.Admin.Id)).Error);
        Assert.DoesNotContain(rig.Libraries.Operations, static o => o.StartsWith("rename:", StringComparison.Ordinal));
        Assert.Equal("Friends' movies", rig.Library(key).Name);
    }

    [Fact]
    public async Task Pilot14_ARenameJellyfinDoesNotShow_KeepsTheOldNameInShoal()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var id = rig.Library(key).JellyfinLibraryId;
        rig.Libraries.SilentRename = true;

        Assert.Equal(LayoutError.Failed, (await rig.Layout.RenameAsync(key, "Friends mix")).Error);

        Assert.Equal("Friends' movies", rig.Library(key).Name);
        Assert.Equal(id, rig.Library(key).JellyfinLibraryId);
    }

    [Fact]
    public async Task Rename_WhenJellyfinRefuses_KeepsTheOldName()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        rig.Libraries.FailRename = true;

        Assert.Equal(LayoutError.Failed, (await rig.Layout.RenameAsync(key, "Pals' films")).Error);
        Assert.Equal("Friends' movies", rig.Library(key).Name);
    }

    [Fact]
    public async Task Delete_RefusesALibraryWithFeeds_OrALegacyOne_AndAMainLibraryLosesOnlyItsFolder()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == key).Feeds.Add(LayoutRig.PeerA));
        Assert.Equal(LayoutError.NotEmpty, (await rig.Layout.DeleteAsync(key)).Error);
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Libraries.First(l => l.LibKey == key).Feeds.Clear();
            s.Home.Libraries.Add(new ShoalLibrary { LibKey = "aaaaaaaaaaaaaaaa", Name = "Hops Movies", PathRel = LayoutRig.PeerA + "/movies", Feeds = [LayoutRig.PeerA] });
        });
        Assert.Equal(LayoutError.Legacy, (await rig.Layout.DeleteAsync("aaaaaaaaaaaaaaaa")).Error);
        var originalPaths = rig.Main.Locations.ToList();

        var result = await rig.Layout.DeleteAsync(key);

        Assert.True(result.Ok);
        Assert.Equal(["purgebelow:Movies", "removepath:Movies"], rig.Libraries.Operations.Where(o => o.Contains(":Movies", StringComparison.Ordinal) && !o.StartsWith("addpath", StringComparison.Ordinal)));
        Assert.Equal(originalPaths, rig.Libraries.List().Single(l => l.Id == rig.Main.Id).Locations);
        Assert.DoesNotContain(rig.State.Current.Home.Libraries, l => l.LibKey == key);
        Assert.DoesNotContain(key, rig.State.Current.Home.LibraryOrder);
        Assert.DoesNotContain(LibraryFolders.LibTag(key), rig.TagsOf(rig.Alice)); // its tag went with it
        Assert.False(Directory.Exists(Path.Combine(rig.StubRoot, "libs", key)));
    }

    [Fact]
    public async Task Delete_APlacementShoalLibrary_PurgesAndRemovesItsJellyfinLibrary()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;

        Assert.True((await rig.Layout.DeleteAsync(key)).Ok);

        Assert.Contains("purge:Friends' movies", rig.Libraries.Operations);
        Assert.Contains("remove:Friends' movies", rig.Libraries.Operations);
        Assert.DoesNotContain(rig.Libraries.List(), l => l.Name == "Friends' movies");
    }

    [Fact]
    public async Task Order_MustNameEveryLibraryOnce()
    {
        using var rig = new LayoutRig();
        var a = (await rig.Layout.CreateAsync(new LibraryCreateRequest("A", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var b = (await rig.Layout.CreateAsync(new LibraryCreateRequest("B", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;

        Assert.Equal(LayoutError.Invalid, (await rig.Layout.SetOrderAsync([a])).Error);
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.SetOrderAsync([a, a])).Error);
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.SetOrderAsync([a, "ffffffffffffffff"])).Error);
        Assert.True((await rig.Layout.SetOrderAsync([b, a])).Ok);
        Assert.Equal([b, a], rig.State.Current.Home.LibraryOrder);
    }

    [Fact]
    public async Task LayoutChanges_AreRefusedWhileTheLockIsHeld()
    {
        using var rig = new LayoutRig();
        using var job = rig.Lock.TryEnter(job: true);

        Assert.Equal(LayoutError.Busy, (await rig.Layout.CreateAsync(new LibraryCreateRequest("A", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).Error);
        Assert.Equal(LayoutError.Busy, (await rig.Layout.SetOrderAsync([])).Error);
        Assert.True(rig.Lock.JobRunning);
    }

    [Fact]
    public async Task TheDefaultPicker_SetsThePresetAndTargets_WithoutMovingAnything_AndRefusesABadTarget()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var shows = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' TV", "tvshows", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;

        Assert.True((await rig.Layout.SetDefaultAsync(LayoutPreset.Custom, new Dictionary<string, string?> { ["movies"] = key })).Ok);
        Assert.Equal(LayoutPreset.Custom, rig.State.Current.Home.Layout.Default);
        Assert.Equal(key, rig.State.Current.Home.Layout.DefaultTargets["movies"]);

        Assert.Equal(LayoutError.NotFound, (await rig.Layout.SetDefaultAsync(LayoutPreset.AllMixed, new Dictionary<string, string?> { ["movies"] = shows })).Error); // wrong type
        Assert.Equal(LayoutError.Invalid, (await rig.Layout.SetDefaultAsync(LayoutPreset.AllMixed, new Dictionary<string, string?> { ["music"] = key })).Error);
        Assert.True((await rig.Layout.SetDefaultAsync(LayoutPreset.AllMixed, new Dictionary<string, string?> { ["movies"] = null })).Ok);
        Assert.False(rig.State.Current.Home.Layout.DefaultTargets.ContainsKey("movies"));
    }

    [Fact]
    public async Task W10M4_ATagAnotherWriterSetsBetweenTheReadAndTheWrite_IsKept_AndShoalsTagHolds()
    {
        using var rig = new LayoutRig();
        await rig.State.UpdateAsync(s => s.Home.Visibility.HiddenFriendTitles.Add(rig.Alice.Id));
        var listCalls = rig.Accounts.ListCalls;
        rig.Accounts.OnList = n =>
        {
            if (n == listCalls + 2)
            {
                rig.Accounts.SetTags(rig.Alice.Id, "AdminSetThis"); // the dashboard writes between Shoal's snapshot and Shoal's write
            }
        };

        await rig.Tags.ReconcileAsync();
        rig.Accounts.OnList = null;

        Assert.Contains("AdminSetThis", rig.TagsOf(rig.Alice));
        Assert.Contains("Shoal", rig.TagsOf(rig.Alice));
    }

    [Fact]
    public async Task W10M4_AWriteThatDoesNotHold_IsNotCounted_AndTheNextReconcileTriesAgain()
    {
        using var rig = new LayoutRig();
        await rig.State.UpdateAsync(s => s.Home.Visibility.HiddenFriendTitles.Add(rig.Alice.Id));
        rig.Accounts.DropTagWrites = true; // something reverts the policy at once

        var first = await rig.Tags.ReconcileAsync();
        Assert.Equal(0, first.Accounts);
        Assert.False(first.AllHeld); // Warden 11 L2: the copy writer reads this
        Assert.DoesNotContain("Shoal", rig.TagsOf(rig.Alice));

        rig.Accounts.DropTagWrites = false;
        var second = await rig.Tags.ReconcileAsync();
        Assert.Equal(1, second.Accounts);
        Assert.True(second.AllHeld);
        Assert.Contains("Shoal", rig.TagsOf(rig.Alice));
    }

    [Fact]
    public async Task W11L4_RemoveAll_TakesOnlyShoalsTags_FromTheListAsItIsAtTheWrite_AndKeepsTheRecordOfAWriteThatDidNotHold()
    {
        using var rig = new LayoutRig();
        await rig.State.UpdateAsync(s => s.Home.Visibility.HiddenFriendTitles.AddRange([rig.Alice.Id, rig.Bob.Id]));
        await rig.Tags.ReconcileAsync();
        var calls = rig.Accounts.ListCalls;
        rig.Accounts.OnList = n =>
        {
            if (n == calls + 2)
            {
                rig.Accounts.SetTags(rig.Alice.Id, [.. rig.TagsOf(rig.Alice), "AdminSetThis"]);
            }
        };
        rig.Accounts.FailFor = rig.Bob.Id;

        await rig.Tags.RemoveAllAsync();
        rig.Accounts.OnList = null;
        rig.Accounts.FailFor = null;

        Assert.Equal(["AdminSetThis"], rig.TagsOf(rig.Alice));
        Assert.Contains("Shoal", rig.TagsOf(rig.Bob));
        Assert.True(rig.State.Current.Home.Visibility.ManagedBlockedTags.ContainsKey(rig.Bob.Id)); // still recorded: a later uninstall can take it back
        Assert.False(rig.State.Current.Home.Visibility.ManagedBlockedTags.ContainsKey(rig.Alice.Id));
    }

    [Fact]
    public void W10M4_TheTagChange_RemovesAndAddsOnlyItsOwnTags_AndTheCheckSeesAWriteThatDidNotHold()
    {
        IReadOnlyList<string> current = ["Kids", "shoal-lib-0a1b2c3d", "Admin"];

        var next = PolicyGate.Next(current, new[] { "shoal-lib-0a1b2c3d" }, new[] { "Shoal", "Kids" });

        Assert.Equal(["Kids", "Admin", "Shoal"], next);
        Assert.True(PolicyGate.Holds(next, new[] { "shoal-lib-0a1b2c3d" }, new[] { "Shoal" }));
        Assert.False(PolicyGate.Holds(current, new[] { "shoal-lib-0a1b2c3d" }, new[] { "Shoal" }));
    }

    [Fact]
    public async Task HideFriendTitles_AddsTheHideAllTag_AndTurningItOffRemovesOnlyShoalsOwn()
    {
        using var rig = new LayoutRig();
        rig.Accounts.SetTags(rig.Bob.Id, "Kids");

        Assert.True((await rig.Layout.SetFriendTitlesHiddenAsync(rig.Alice.Id, hidden: true)).Ok);
        Assert.True((await rig.Layout.SetFriendTitlesHiddenAsync(rig.Bob.Id, hidden: true)).Ok);
        Assert.Equal(["Shoal"], rig.TagsOf(rig.Alice));
        Assert.Equal(["Kids", "Shoal"], rig.TagsOf(rig.Bob));
        Assert.Contains(rig.Alice.Id, rig.State.Current.Home.Visibility.HiddenFriendTitles);

        await rig.Layout.SetFriendTitlesHiddenAsync(rig.Bob.Id, hidden: false);

        Assert.Equal(["Kids"], rig.TagsOf(rig.Bob));
        Assert.Equal(LayoutError.NotFound, (await rig.Layout.SetFriendTitlesHiddenAsync(Guid.NewGuid(), true)).Error);
    }

    [Fact]
    public async Task Tags_AnAdminsOwnCopyOfAShoalTag_IsNeverRemoved()
    {
        using var rig = new LayoutRig();
        rig.Accounts.SetTags(rig.Alice.Id, "Shoal"); // the admin hid friends' titles from alice by hand, before Shoal did anything

        await rig.Layout.SetFriendTitlesHiddenAsync(rig.Alice.Id, hidden: true);
        Assert.False(rig.State.Current.Home.Visibility.ManagedBlockedTags.ContainsKey(rig.Alice.Id)); // Shoal did not add it, so it does not own it
        await rig.Layout.SetFriendTitlesHiddenAsync(rig.Alice.Id, hidden: false);

        Assert.Equal(["Shoal"], rig.TagsOf(rig.Alice));
    }

    [Fact]
    public async Task Tags_WideningAMainLibraryToEveryone_RemovesItsTag_AndNarrowingPutsItBack()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        var tag = LibraryFolders.LibTag(key);

        var wide = await rig.Access.SetLibraryAsync(key, new AccessChange(AccessMode.Everyone, null, false, false), rig.Admin.Id);
        Assert.True(wide.Ok);
        Assert.Contains(rig.Alice.Id, wide.Impact!.Gaining);
        Assert.DoesNotContain(tag, rig.TagsOf(rig.Alice));

        var narrow = await rig.Access.SetLibraryAsync(key, new AccessChange(AccessMode.Selected, [rig.Admin.Id, rig.Bob.Id], false, false), rig.Admin.Id);
        Assert.Contains(rig.Alice.Id, narrow.Impact!.Losing);
        Assert.Contains(tag, rig.TagsOf(rig.Alice));
        Assert.DoesNotContain(tag, rig.TagsOf(rig.Bob));
    }

    [Fact]
    public async Task Tags_ShareUsers_AreLeftToTheShareService()
    {
        using var rig = new LayoutRig();
        var share = rig.Accounts.Add("shoal-friend-1a2b", all: false);
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "s1", ShareUserId = share.Id, FriendName = "Friend" }));

        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id);

        Assert.DoesNotContain(rig.Accounts.TagWrites, w => w.UserId == share.Id);
        Assert.False(rig.State.Current.Home.Visibility.ManagedBlockedTags.ContainsKey(share.Id));
    }

    [Fact]
    public async Task Tags_AnAccountWhoseListCannotBeRead_KeepsItsRecord_AndGetsNoWrite()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        var writes = rig.Accounts.TagWrites.Count;
        var index = rig.Accounts.List().ToList().FindIndex(a => a.Id == rig.Alice.Id);
        rig.Accounts.SetTags(rig.Alice.Id, null!);

        await rig.Tags.ReconcileAsync();

        Assert.Equal(writes, rig.Accounts.TagWrites.Count);
        Assert.Equal([LibraryFolders.LibTag(key)], rig.State.Current.Home.Visibility.ManagedBlockedTags[rig.Alice.Id]);
        Assert.True(index >= 0);
    }

    [Fact]
    public async Task Tags_AltTag_IsBlockedForAccountsThatCanOpenItsLibrary_Only()
    {
        using var rig = new LayoutRig();
        var key = (await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        await rig.Access.SetLibraryAsync(key, new AccessChange(AccessMode.Selected, [rig.Admin.Id, rig.Alice.Id], false, false), rig.Admin.Id);
        rig.Tags.AltTagsInUse = () => new HashSet<string>(StringComparer.Ordinal) { LibraryFolders.AltTag(key) };

        await rig.Tags.ReconcileAsync();

        Assert.Contains(LibraryFolders.AltTag(key), rig.TagsOf(rig.Alice));
        Assert.Contains(LibraryFolders.AltTag(key), rig.TagsOf(rig.Admin));
        Assert.DoesNotContain(LibraryFolders.AltTag(key), rig.TagsOf(rig.Bob)); // bob cannot open it, so he keeps seeing the lower copy
    }

    [Fact]
    public async Task RemoveAll_TakesBackOnlyTheRecordedTags()
    {
        using var rig = new LayoutRig();
        rig.Accounts.SetTags(rig.Alice.Id, "Kids");
        await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id);

        var removed = await rig.Libs.RemoveAllAsync();

        Assert.Equal(1, removed.LibrariesRemoved);
        Assert.Equal(["Kids"], rig.TagsOf(rig.Alice));
        Assert.Empty(rig.TagsOf(rig.Bob));
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedBlockedTags);
    }

    [Fact]
    public async Task NewMapping_AllMixed_CreatesTheMixedLibraryOnce_AndTheNextFriendJoinsIt()
    {
        using var rig = new LayoutRig(LayoutPreset.AllMixed);

        var first = await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);
        var second = await rig.Libs.CreateAsync(LayoutRig.PeerB, "movies-1", rig.Admin.Id);

        Assert.True(first.Ok);
        Assert.NotNull(first.SharedLibKey);
        Assert.Equal(first.SharedLibKey, second.SharedLibKey);
        var library = rig.Library(first.SharedLibKey!);
        Assert.Equal("Friends' movies", library.Name);
        Assert.Equal([LayoutRig.PeerA, LayoutRig.PeerB], library.Feeds);
        Assert.Equal(first.SharedLibKey, rig.State.Current.Home.Layout.DefaultTargets["movies"]);
        Assert.DoesNotContain(rig.Libraries.List(), l => l.Name == "Hops Movies"); // no per-friend library
        Assert.Equal(1, rig.Libraries.CreateCalls);
    }

    [Fact]
    public async Task Warden14_ANewFriendJoiningAMainLibrary_NamesOnlyAccountsThatCanOpenTheMainLibrary()
    {
        using var rig = new LayoutRig(LayoutPreset.CombineIntoMine);
        var main = await rig.Layout.CreateAsync(new LibraryCreateRequest("Friends in Movies", "movies", LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id);
        Assert.True(main.Ok);
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Libraries.First(l => l.LibKey == main.LibKey).Access = new LibraryAccess { Mode = AccessMode.Everyone };
            s.Home.Layout.DefaultTargets["movies"] = main.LibKey!;
        });
        rig.Accounts.Edit(rig.Bob.Id, all: false, folders: []); // Everyone admits Bob, but Movies is not on his list

        var added = await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);

        Assert.Equal(main.LibKey, added.SharedLibKey);
        Assert.Contains(rig.Alice.Id, added.Gained!.Users); // "all libraries"
        Assert.DoesNotContain(rig.Bob.Id, added.Gained.Users);
    }

    [Fact]
    public async Task W10M1_ANewFriendJoiningAnEveryoneLibrary_IsNotBlocked_AndTheAnswerNamesWhoSeesItsTitles()
    {
        using var rig = new LayoutRig(LayoutPreset.AllMixed);
        var first = await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == first.SharedLibKey).Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true });

        var second = await rig.Libs.CreateAsync(LayoutRig.PeerB, "movies-1", rig.Admin.Id);

        Assert.True(second.Ok);
        Assert.Equal(first.SharedLibKey, second.SharedLibKey);
        var everyone = rig.Accounts.List().Where(a => !a.IsDisabled).Select(static a => a.Id).OrderBy(static g => g);
        Assert.Equal(everyone, second.Gained!.Users.OrderBy(static g => g));
        Assert.Equal(rig.Accounts.List().Where(static a => a.IsRestricted && !a.IsDisabled).Select(static a => a.Id).OrderBy(static g => g), second.Gained.RestrictedUsers.OrderBy(static g => g));
    }

    [Fact]
    public async Task NewMapping_PerFriend_MakesAPerFriendLibrary_WithItsLegacyLayoutEntry()
    {
        using var rig = new LayoutRig(LayoutPreset.PerFriend);

        var result = await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);

        Assert.True(result.Ok);
        Assert.Null(result.SharedLibKey);
        var entry = rig.State.Current.Home.Libraries.Single();
        Assert.True(entry.IsLegacy);
        Assert.Equal(LayoutRig.PeerA + "/movies", entry.PathRel);
        Assert.Equal(StateUpgrade.LegacyLibKey(LayoutRig.PeerA, "movies-1"), entry.LibKey);
        Assert.Contains(rig.Libraries.List(), l => l.Name == "Hops Movies");
    }

    [Fact]
    public async Task MappingAccess_OfAConnectionInASharedLibrary_CannotBeChangedThroughTheMapping()
    {
        using var rig = new LayoutRig(LayoutPreset.AllMixed);
        await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);

        var result = await rig.Access.SetAsync(LayoutRig.PeerA, "movies-1", new AccessChange(AccessMode.Everyone, null, true, true), rig.Admin.Id);

        Assert.Equal(AccessError.SharedLibrary, result.Error);
    }

    [Fact]
    public async Task LibraryAccess_OfALegacyLibrary_GoesToItsMappingToo()
    {
        using var rig = new LayoutRig(LayoutPreset.PerFriend);
        await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id);
        var key = rig.State.Current.Home.Libraries.Single().LibKey;

        var result = await rig.Access.SetLibraryAsync(key, new AccessChange(AccessMode.Selected, [rig.Admin.Id, rig.Alice.Id], false, false), rig.Admin.Id);

        Assert.True(result.Ok);
        Assert.Equal([rig.Admin.Id, rig.Alice.Id], rig.State.Current.Home.Peers.First(p => p.PeerKey == LayoutRig.PeerA).Libraries[0].Access.SelectedUserIds);
    }
}
