using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;
using ShoalLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K44, presets: Preview names the libraries created and removed, the titles that collapse and the users who lose access; Apply runs that plan through the
/// move job and remembers the preset for new connections. A combined library admits only what all its sources admit.
/// </summary>
public sealed class LayoutPresetTests
{
    private const string PeerB = StateSamples.PeerKeyB;

    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
    }

    private static LayoutPresetService Presets(JobRig rig) => new(rig.State, rig.Accounts, rig.Job, rig.Groups);

    // A second friend with a per-friend library whose access is Everyone (no titles on disk; enough for planning and access).
    private static Task AddBarleyAsync(JobRig rig) => rig.State.UpdateAsync(s =>
    {
        s.Home.Peers.Add(new PeerState
        {
            PeerKey = PeerB,
            DisplayName = "Barley",
            Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = "Barley Movies", Subfolder = "movies", Access = new LibraryAccess { Mode = AccessMode.Everyone } } },
        });
        s.Home.Libraries.Add(new ShoalLibrary { LibKey = StateUpgrade.LegacyLibKey(PeerB, "movies-1"), Name = "Barley Movies", PathRel = PeerB + "/movies", Feeds = [PeerB], Access = new LibraryAccess { Mode = AccessMode.Everyone } });
    });

    [Fact]
    public async Task Preview_AllMixed_FromPerFriend_CreatesOneLibrary_RemovesThePerFriendOnes_AndNamesWhoLosesAccess()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);
        var before = File.ReadAllText(rig.Sync.Paths.StateFile);

        var preview = Presets(rig).Preview(LayoutPreset.AllMixed)!;

        Assert.Equal([new PresetNewLibrary(LayoutPresetService.MixedMovies, "movies")], preview.Created);
        Assert.Equal(["Barley Movies", "Hops Movies"], preview.Removed.Select(static r => r.Name).OrderBy(static n => n));
        Assert.Equal(2, preview.Moves.Count);
        Assert.All(preview.Moves, m => Assert.Equal(LayoutPresetService.MixedMovies, m.To));

        // Hops admits admin and alice, Barley everyone: the combined library admits admin and alice only, so bob (Barley's titles) loses access.
        Assert.Equal([rig.Bob.Id], preview.UsersLosingAccess);
        Assert.Equal(before, File.ReadAllText(rig.Sync.Paths.StateFile)); // a preview changes nothing
        Assert.Null(rig.Job.Status().Phase);
    }

    [Fact]
    public async Task Apply_AllMixed_MovesEveryone_SetsTheDefault_AndTheCombinedAccessIsTheIntersection()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);

        var started = await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash);
        await rig.Job.Running;

        Assert.Equal(LayoutJobError.None, started);
        var state = rig.State.Current;
        var mixed = state.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.Equal(LayoutPresetService.MixedMovies, mixed.Name);
        Assert.Equal(new[] { PeerB, SyncRig.PeerKey }.OrderBy(static k => k), mixed.Feeds.OrderBy(static k => k));
        Assert.Equal(AccessMode.Selected, mixed.Access.Mode);
        Assert.Equal(new[] { rig.Admin.Id, rig.Alice.Id }.OrderBy(static g => g), mixed.Access.SelectedUserIds.OrderBy(static g => g));
        Assert.Equal(LayoutPreset.AllMixed, state.Home.Layout.Default);
        Assert.Equal(mixed.LibKey, state.Home.Layout.DefaultTargets["movies"]);
        Assert.DoesNotContain(state.Home.Libraries, static l => l.IsLegacy);
        Assert.Equal(2, Directory.EnumerateDirectories(rig.LibFolder(mixed.LibKey)).Count());
        Assert.Equal([rig.Bob.Id], rig.Job.Status().LostAccess);
    }

    [Fact]
    public async Task Apply_PerFriend_FromAMixedLibrary_GivesEachFriendItsOwn_AndRemovesTheMixedOne()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);
        await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash);
        await rig.Job.Running;
        var mixed = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy).LibKey;

        var preview = Presets(rig).Preview(LayoutPreset.PerFriend)!;
        Assert.Equal(["Barley movies", "Hops movies"], preview.Created.Select(static c => c.Name).OrderBy(static n => n));
        Assert.Equal([mixed], preview.Removed.Select(static r => r.LibKey));
        Assert.Empty(preview.UsersLosingAccess); // each new library starts with the mixed library's access

        await Presets(rig).ApplyAsync(LayoutPreset.PerFriend, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.PerFriend)!.PlanHash);
        await rig.Job.Running;

        var state = rig.State.Current;
        Assert.DoesNotContain(state.Home.Libraries, l => l.LibKey == mixed);
        Assert.Equal(2, state.Home.Libraries.Count);
        Assert.All(state.Home.Libraries, static l => Assert.Single(l.Feeds));
        var hops = state.Home.Libraries.Single(l => l.Feeds[0] == SyncRig.PeerKey);
        Assert.Equal(2, Directory.EnumerateDirectories(rig.LibFolder(hops.LibKey)).Count());
        Assert.Equal(LayoutPreset.PerFriend, state.Home.Layout.Default);
    }

    [Fact]
    public async Task Custom_MovesNothing_AndOnlyRemembersThePreset()
    {
        await using var rig = await JobRig.CreateAsync(Seed);

        var preview = Presets(rig).Preview(LayoutPreset.Custom)!;
        var started = await Presets(rig).ApplyAsync(LayoutPreset.Custom, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.Custom)!.PlanHash);

        Assert.Empty(preview.Moves);
        Assert.Empty(preview.Created);
        Assert.Equal(LayoutJobError.None, started);
        Assert.Equal(LayoutPreset.Custom, rig.State.Current.Home.Layout.Default);
        Assert.Null(rig.Job.Status().Phase);
        Assert.True(Directory.Exists(rig.Sync.LibraryFolder));
    }

    [Fact]
    public async Task Preview_CountsTitlesThatCollapse_WhenTheirServersEndUpInOneLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);
        var alien = rig.Sync.Catalog().Entries.Single(e => e.Title == "Alien");
        rig.Groups.Update(d =>
        {
            d.Groups.Add(new TitleGroup
            {
                GroupKey = "grp0000000000001",
                MetaOwner = SyncRig.PeerKey,
                Members =
                [
                    new GroupMember { PeerKey = SyncRig.PeerKey, PeerItemId = alien.PeerItemId, LibKey = rig.LegacyKey },
                    new GroupMember { PeerKey = PeerB, PeerItemId = new string('b', 32), LibKey = StateUpgrade.LegacyLibKey(PeerB, "movies-1") },
                ],
                Copies = [new GroupCopy { LibKey = rig.LegacyKey, StubKey = alien.StubKey, FolderRel = alien.StubRelPath!.Split('/')[0] }],
            });
            return true;
        });

        Assert.Equal(1, Presets(rig).Preview(LayoutPreset.AllMixed)!.TitlesCollapsing);
        Assert.Equal(0, Presets(rig).Preview(LayoutPreset.PerFriend)!.TitlesCollapsing);
    }

    [Fact]
    public async Task W10L3_Apply_NeedsThePreviewsPlanHash_AndRefusesAPlanThatChangedSince()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);
        var hash = Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash;
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash); // stable while nothing changes

        Assert.Equal(LayoutJobError.Invalid, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, null));
        Assert.Equal(LayoutJobError.PlanChanged, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.PerFriend)!.PlanHash));

        // Someone else changes who may open Barley's titles after the preview: the approved plan is no longer the one that would run.
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.Name == "Barley Movies").Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [rig.Admin.Id] });
        await rig.State.UpdateAsync(s => s.Home.Peers.First(p => p.PeerKey == PeerB).Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [rig.Admin.Id] });

        Assert.Equal(LayoutJobError.PlanChanged, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, hash));
        Assert.Null(rig.Job.Status().Phase);
    }

    [Fact]
    public async Task W10M1_MovingIntoAnEveryoneLibrary_IsNotBlocked_ButPreviewAndJobNameWhoGainsAccess_AndTheRestrictedOnes()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var child = rig.Accounts.Add("kid", restricted: true);
        var everyone = await rig.NewLibraryAsync("Everyone's movies");
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Libraries.First(l => l.LibKey == everyone).Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true };
            s.Home.Layout.Default = LayoutPreset.Custom;
        });

        var error = await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", everyone)], rig.Admin.Id);
        await rig.Job.Running;

        Assert.Equal(LayoutJobError.None, error); // the owner's rule: Everyone keeps meaning everyone
        var status = rig.Job.Status();
        Assert.Equal(LayoutJobPhase.Done, status.Phase);
        Assert.Equal(new[] { rig.Bob.Id, child.Id }.OrderBy(static g => g), status.GainedAccess!.OrderBy(static g => g)); // admin and alice could open Hops already
        Assert.Equal([child.Id], status.GainedRestricted);
    }

    [Fact]
    public void W10M1_TheGain_IsWhoTheTargetAdmitsAndNoSourceDid_NeverASharingAccount()
    {
        var accounts = new FakeAccounts();
        var admin = accounts.Add("admin", admin: true);
        var alice = accounts.Add("alice");
        var kid = accounts.Add("kid", restricted: true);
        var share = accounts.Add("shoal-bob");
        var shareUsers = new System.Collections.Generic.HashSet<Guid> { share.Id };
        var everyone = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true };
        var adminOnly = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [admin.Id] };

        var gain = AccessGainReport.Of(AccessGain.Gaining(accounts.List(), everyone, [adminOnly], shareUsers));
        Assert.Equal(new[] { alice.Id, kid.Id }.OrderBy(static g => g), gain.Users.OrderBy(static g => g));
        Assert.Equal([kid.Id], gain.RestrictedUsers);

        var newFriend = AccessGainReport.Of(AccessGain.Gaining(accounts.List(), everyone, [], shareUsers)); // a new connection had no titles before
        Assert.Equal(3, newFriend.Users.Count);
        Assert.DoesNotContain(share.Id, newFriend.Users);
    }

    [Fact]
    public async Task W10M1_ThePreview_NamesWhoGainsAccess()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await AddBarleyAsync(rig);
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.Name == "Hops Movies").Access = new LibraryAccess { Mode = AccessMode.Everyone });
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Everyone });

        var preview = Presets(rig).Preview(LayoutPreset.AllMixed)!;

        Assert.Empty(preview.UsersGainingAccess); // both sources admit everyone: the combined library adds nobody
        Assert.Empty(preview.UsersLosingAccess);
    }

    [Fact]
    public async Task Apply_IsRefusedWhileASyncRuns_AndChangesNothing()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        using var held = rig.Sync.Sync.TryHold();

        Assert.Equal(LayoutJobError.Syncing, await Presets(rig).ApplyAsync(LayoutPreset.AllMixed, rig.Admin.Id, Presets(rig).Preview(LayoutPreset.AllMixed)!.PlanHash));
        Assert.Equal(LayoutPreset.PerFriend, rig.State.Current.Home.Layout.Default);
        Assert.DoesNotContain(rig.State.Current.Home.Libraries, static l => !l.IsLegacy);
    }
}
