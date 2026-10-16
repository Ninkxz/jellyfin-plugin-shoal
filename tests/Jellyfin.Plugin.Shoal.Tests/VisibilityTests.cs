using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

internal sealed class FakeLibraries : ILibrarySource
{
    private readonly List<LibraryInfo> _libraries = [];

    public int CreateCalls { get; private set; }

    public string? LastCollectionType { get; private set; }

    public Task CreateAsync(string name, string path, string collectionType, CancellationToken cancellationToken)
    {
        LastCollectionType = collectionType;
        return CreateAsync(name, path, cancellationToken);
    }

    public bool FailCreate { get; set; }

    public IReadOnlyList<LibraryInfo> List() => _libraries.ToList();

    public LibraryInfo AddLocal(string name)
    {
        var library = new LibraryInfo(Guid.NewGuid(), name, [Path.Combine(Path.GetTempPath(), "media-" + name)]);
        _libraries.Add(library);
        return library;
    }

    public Task CreateAsync(string name, string path, CancellationToken cancellationToken)
    {
        CreateCalls++;
        if (FailCreate)
        {
            throw new InvalidOperationException("refused");
        }

        _libraries.Add(new LibraryInfo(Guid.NewGuid(), name, [path]));
        return Task.CompletedTask;
    }

    public List<string> Operations { get; } = [];

    public Task PurgeItemsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        Operations.Add("purge:" + _libraries.FirstOrDefault(l => l.Id == libraryId)?.Name);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        Operations.Add("remove:" + name);
        _libraries.RemoveAll(l => l.Name == name);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAccounts : IAccountSource
{
    private readonly List<AccountInfo> _accounts = [];

    public List<(Guid UserId, bool All, Guid[] Folders)> Writes { get; } = [];

    public int ListCalls { get; private set; }

    public Guid? FailFor { get; set; }

    /// <summary>Runs on every List call, with the number of calls so far (simulates an edit in the dashboard in the middle of a run).</summary>
    public Action<int>? OnList { get; set; }

    public AccountInfo Add(string name, bool all = true, IEnumerable<Guid>? folders = null, bool admin = false, bool restricted = false, bool disabled = false)
    {
        var account = new AccountInfo(Guid.NewGuid(), name, disabled, admin, restricted, all, [.. folders ?? []]);
        _accounts.Add(account);
        return account;
    }

    public AccountInfo Get(Guid id) => _accounts.Single(a => a.Id == id);

    public void Edit(Guid id, bool all, IEnumerable<Guid> folders)
    {
        var index = _accounts.FindIndex(a => a.Id == id);
        _accounts[index] = _accounts[index] with { EnableAllFolders = all, EnabledFolders = [.. folders] };
    }

    public IReadOnlyList<AccountInfo> List()
    {
        ListCalls++;
        OnList?.Invoke(ListCalls);
        return _accounts.ToList();
    }

    public Task SetFoldersAsync(Guid userId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders, CancellationToken cancellationToken)
    {
        if (FailFor == userId)
        {
            throw new InvalidOperationException("refused");
        }

        Writes.Add((userId, enableAllFolders, [.. enabledFolders]));
        Edit(userId, enableAllFolders, enabledFolders);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A Home with one peer and one mapped library, a few local libraries and a few accounts.
/// </summary>
internal sealed class VisibilityRig : IDisposable
{
    public const string PeerKey = "4b0c1d2e3f405162738495a6b7c8d9e0";
    public const string MappingKey = "movies-1";

    public VisibilityRig(bool createLibrary = true, AccessMode mode = AccessMode.Selected)
    {
        Dir = new TempDirectory();
        StubRoot = Dir.Combine("stubs");
        State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, TimeProvider.System, null);
        State.UpdateAsync(s =>
        {
            s.StubRoot = StubRoot;
            s.Home.Peers.Add(new PeerState
            {
                PeerKey = PeerKey,
                DisplayName = "Hops",
                BaseUrl = "http://hops.tail1234.ts.net:8096",
                Libraries = { new LibraryMapping { MappingKey = MappingKey, RemoteName = "Movies", LocalLibraryName = "Hops Movies", Subfolder = "movies", Access = new LibraryAccess { Mode = mode } } },
            });
        }).GetAwaiter().GetResult();
        Movies = Libraries.AddLocal("Movies");
        Shows = Libraries.AddLocal("Shows");
        Log = new CapturingLogger();
        Visibility = new VisibilityService(State, Libraries, Accounts, TimeProvider.System, Log);
        Access = new AccessService(State, Accounts, Visibility, TimeProvider.System);
        Service = new ShoalLibraryService(State, Libraries, Visibility, TimeProvider.System, NullLogger<ShoalLibraryService>.Instance);
        Admin = Accounts.Add("admin", admin: true);
        if (createLibrary)
        {
            ShoalLibrary = Libraries.List().FirstOrDefault(l => l.Name == "Hops Movies");
        }
    }

    public TempDirectory Dir { get; }

    public string StubRoot { get; }

    public StateStore State { get; }

    public FakeLibraries Libraries { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public CapturingLogger Log { get; }

    public VisibilityService Visibility { get; }

    public AccessService Access { get; }

    public ShoalLibraryService Service { get; }

    public LibraryInfo Movies { get; }

    public LibraryInfo Shows { get; }

    public AccountInfo Admin { get; }

    public LibraryInfo? ShoalLibrary { get; set; }

    public LibraryAccess CurrentAccess => State.Current.Home.Peers[0].Libraries[0].Access;

    public Guid ShoalId => Libraries.List().Single(l => l.Name == "Hops Movies").Id;

    public async Task<LibraryCreateResult> CreateLibraryAsync(Guid? admin = null)
    {
        var result = await Service.CreateAsync(PeerKey, MappingKey, admin ?? Admin.Id);
        Assert.True(result.Ok, result.Error.ToString());
        return result;
    }

    public Task<AccessChangeResult> SetAsync(AccessChange change) => Access.SetAsync(PeerKey, MappingKey, change, Admin.Id);

    public void Dispose() => Dir.Dispose();
}

/// <summary>
/// K10 acceptance (DESIGN 5.6): the locked options, T-VIS-1 to T-VIS-7, idempotent reconcile, removal and uninstall.
/// </summary>
public sealed class VisibilityTests
{
    private static readonly AccessChange Everyone = new(AccessMode.Everyone, null, null, false);
    private static readonly AccessChange SelectedOnly = new(AccessMode.Selected, null, null, false);

    // --- the created library

    [Fact]
    public void TheCreatedLibrary_ShowsTheLockedOptionsOfDesign56()
    {
        var options = LibraryOptionsFactory.Create(Path.Combine(Path.GetTempPath(), "stubs", "peerkey", "movies"), ["OpenSubtitles", "Podnapisi"], ["Intro Skipper", "intro-skipper-1a2b"]);

        var path = Assert.Single(options.PathInfos);
        Assert.EndsWith("movies", path.Path, StringComparison.Ordinal);
        Assert.True(options.Enabled);
        Assert.False(options.EnableRealtimeMonitor);
        Assert.False(options.SaveLocalMetadata);
        Assert.Empty(options.MetadataSavers ?? ["x"]);
#pragma warning disable CS0618
        Assert.False(options.EnableInternetProviders);
#pragma warning restore CS0618
        Assert.Equal(["Movie", "Video", "Trailer", "BoxSet"], options.TypeOptions.Select(static t => t.Type)); // an item type without an entry falls back to the server-wide providers
        foreach (var type in options.TypeOptions)
        {
            Assert.Empty(type.MetadataFetchers ?? ["x"]);
            Assert.Empty(type.ImageFetchers ?? ["x"]);
        }

        Assert.Equal(["Intro Skipper", "intro-skipper-1a2b"], options.DisabledMediaSegmentProviders);
        Assert.False(options.ExtractChapterImagesDuringLibraryScan);
        Assert.False(options.EnableTrickplayImageExtraction);
        Assert.False(options.ExtractTrickplayImagesDuringLibraryScan);
        Assert.False(options.SaveTrickplayWithMedia);
        Assert.False(options.EnableLUFSScan);
        Assert.False(options.EnableEmbeddedTitles);
        Assert.Equal(["OpenSubtitles", "Podnapisi"], options.DisabledSubtitleFetchers);
        Assert.Empty(options.SubtitleDownloadLanguages ?? ["x"]);
        Assert.False(options.AutomaticallyAddToCollection);
        Assert.Equal(0, options.AutomaticRefreshIntervalDays);
    }

    [Fact]
    public async Task ANewLibrary_IsCreatedAtThePeerFolder_WithOnlyTheAdminSelected()
    {
        using var rig = new VisibilityRig();

        var result = await rig.CreateLibraryAsync();

        var library = rig.Libraries.List().Single(l => l.Name == "Hops Movies");
        Assert.Equal(library.Id, result.LibraryId);
        Assert.Equal(Path.Combine(rig.StubRoot, VisibilityRig.PeerKey, "movies"), Assert.Single(library.Locations));
        Assert.True(Directory.Exists(library.Locations[0]));
        Assert.Equal(AccessMode.Selected, rig.CurrentAccess.Mode);
        Assert.Equal([rig.Admin.Id], rig.CurrentAccess.SelectedUserIds);
        Assert.False(rig.CurrentAccess.IncludeRestricted);
        Assert.Equal(rig.Admin.Id, rig.CurrentAccess.ChangedByUserId);
    }

    [Fact]
    public async Task CreatingALibraryTwice_ReportsTheExistingOne_AndANameInUse_AndABadPath()
    {
        using var rig = new VisibilityRig();
        var first = await rig.CreateLibraryAsync();

        var again = await rig.Service.CreateAsync(VisibilityRig.PeerKey, VisibilityRig.MappingKey, rig.Admin.Id);
        Assert.Equal(LibraryCreateError.AlreadyExists, again.Error);
        Assert.Equal(first.LibraryId, again.LibraryId);
        Assert.Equal(1, rig.Libraries.CreateCalls);

        Assert.Equal(LibraryCreateError.UnknownLibrary, (await rig.Service.CreateAsync("nope", VisibilityRig.MappingKey, rig.Admin.Id)).Error);
        Assert.Equal(LibraryCreateError.UnknownLibrary, (await rig.Service.CreateAsync(VisibilityRig.PeerKey, "nope", rig.Admin.Id)).Error);
    }

    [Fact]
    public async Task ANameThatIsTaken_IsRefused()
    {
        using var rig = new VisibilityRig();
        rig.Libraries.AddLocal("Hops Movies");

        Assert.Equal(LibraryCreateError.NameInUse, (await rig.Service.CreateAsync(VisibilityRig.PeerKey, VisibilityRig.MappingKey, rig.Admin.Id)).Error);
        Assert.Equal(0, rig.Libraries.CreateCalls);
    }

    [Theory]
    [InlineData("", "peer", "movies")]
    [InlineData("relative/stubs", "peer", "movies")]
    [InlineData("/stubs", "", "movies")]
    [InlineData("/stubs", "peer", "")]
    [InlineData("/stubs", "peer", "..")]
    [InlineData("/stubs", "peer", ".")]
    [InlineData("/stubs", "peer", "../other")]
    [InlineData("/stubs", "peer", "a/b")]
    [InlineData("/stubs", "peer", "a\\b")]
    public void AMappingPath_MustBeOnePlainSegmentBelowThePeerFolder(string stubRoot, string peerKey, string subfolder)
    {
        var path = VisibilityService.MappingPath(stubRoot, peerKey, subfolder);

        if (stubRoot == "relative/stubs" && peerKey.Length > 0 && subfolder == "movies")
        {
            Assert.NotNull(path); // GetFullPath resolves it; CreateAsync additionally insists on a rooted stub root
        }
        else
        {
            Assert.Null(path);
        }
    }

    [Fact]
    public void AGoodMappingPath_IsStubRootPeerSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "stubs");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "peer", "movies"), VisibilityService.MappingPath(root, "peer", "movies"));
    }


    [Fact]
    public async Task WhenJellyfinRefuses_TheErrorIsReported_AndNothingElseChanges()
    {
        using var rig = new VisibilityRig();
        rig.Libraries.FailCreate = true;

        var result = await rig.Service.CreateAsync(VisibilityRig.PeerKey, VisibilityRig.MappingKey, rig.Admin.Id);

        Assert.Equal(LibraryCreateError.Failed, result.Error);
        Assert.Empty(rig.Accounts.Writes);
    }

    // --- T-VIS-1

    [Fact]
    public async Task T_VIS_1_ANewLibrary_IsSeenByTheAdminOnly_AndOtherLibrariesAreUnchanged()
    {
        using var rig = new VisibilityRig();
        var otherAdmin = rig.Accounts.Add("admin2", admin: true);
        var allLibraries = rig.Accounts.Add("alice");
        var explicitList = rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);

        await rig.CreateLibraryAsync();

        var shoal = rig.ShoalId;
        Assert.True(rig.Accounts.Get(rig.Admin.Id).EnableAllFolders); // admitted to everything: nothing to do
        foreach (var converted in new[] { otherAdmin, allLibraries })
        {
            var now = rig.Accounts.Get(converted.Id);
            Assert.False(now.EnableAllFolders);
            Assert.DoesNotContain(shoal, now.EnabledFolders);
            Assert.Equal(new[] { rig.Movies.Id, rig.Shows.Id }.Order(), now.EnabledFolders.Order()); // every other library, unchanged
            Assert.Contains(converted.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);
        }

        Assert.Equal([rig.Movies.Id], rig.Accounts.Get(explicitList.Id).EnabledFolders);
        Assert.DoesNotContain(rig.Admin.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);

        // And playback follows the same rule.
        var admitted = rig.Accounts.List().Where(a => AccessPolicy.Admitted(a.ToCallerUser(), rig.CurrentAccess, new HashSet<Guid>())).Select(a => a.Id);
        Assert.Equal([rig.Admin.Id], admitted);
    }

    [Fact]
    public async Task ASecondReconcile_WritesNothing()
    {
        using var rig = new VisibilityRig();
        rig.Accounts.Add("alice");
        rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);
        await rig.CreateLibraryAsync();
        var writes = rig.Accounts.Writes.Count;
        var generation = rig.State.Current.Generation;

        var second = await rig.Visibility.ReconcileAsync();

        Assert.Empty(second.Changes);
        Assert.Equal(0, second.Writes);
        Assert.Equal(writes, rig.Accounts.Writes.Count);
        Assert.Equal(generation, rig.State.Current.Generation);
    }

    // --- T-VIS-2

    [Fact]
    public async Task T_VIS_2_Everyone_AdmitsEveryNonRestrictedAccount_AndRestrictedOnesOnlyWhenIncluded()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);
        var kid = rig.Accounts.Add("kid", restricted: true);
        await rig.CreateLibraryAsync();
        var shoal = rig.ShoalId;

        var result = await rig.SetAsync(Everyone);

        Assert.True(result.Ok);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders); // managed account got "all libraries" back
        Assert.DoesNotContain(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);
        Assert.Contains(shoal, rig.Accounts.Get(bob.Id).EnabledFolders);
        Assert.Contains(rig.Movies.Id, rig.Accounts.Get(bob.Id).EnabledFolders);
        var restricted = rig.Accounts.Get(kid.Id);
        Assert.False(restricted.EnableAllFolders);
        Assert.DoesNotContain(shoal, restricted.EnabledFolders);

        var include = await rig.SetAsync(new AccessChange(null, null, true, RatingsAcknowledged: true));

        Assert.True(include.Ok);
        Assert.True(rig.Accounts.Get(kid.Id).EnableAllFolders);
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
    }

    // --- T-VIS-3

    [Fact]
    public async Task T_VIS_3_EveryoneMode_NewAccountsGetTheLibraryWithoutAnyShoalAction()
    {
        using var rig = new VisibilityRig();
        await rig.CreateLibraryAsync();
        await rig.SetAsync(Everyone);
        using var watch = new UserCreationWatch(rig.Visibility, TimeProvider.System, NullLogger<UserCreationWatch>.Instance, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(30));
        var explicitList = rig.Accounts.Add("new-explicit", all: false, folders: [rig.Movies.Id]);
        var allLibraries = rig.Accounts.Add("new-all");

        watch.UserCreated(explicitList.Id);
        watch.UserCreated(allLibraries.Id);
        await UntilAsync(() => rig.Accounts.Get(explicitList.Id).EnabledFolders.Contains(rig.ShoalId));

        Assert.Contains(rig.ShoalId, rig.Accounts.Get(explicitList.Id).EnabledFolders);
        Assert.True(rig.Accounts.Get(allLibraries.Id).EnableAllFolders); // already sees it through "all libraries"
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
    }

    [Fact]
    public async Task T_VIS_3_TheFirstPolicyUpdateAfterCreation_ReconcilesAtOnce_AndOnlyTheFirst()
    {
        using var rig = new VisibilityRig();
        await rig.CreateLibraryAsync();
        await rig.SetAsync(Everyone);
        using var watch = new UserCreationWatch(rig.Visibility, TimeProvider.System, NullLogger<UserCreationWatch>.Instance, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var created = rig.Accounts.Add("new", all: false, folders: []);
        watch.UserCreated(created.Id);

        rig.Accounts.Edit(created.Id, all: false, [rig.Movies.Id]); // the dashboard then posts the library access
        watch.UserUpdated(created.Id);
        await UntilAsync(() => rig.Accounts.Get(created.Id).EnabledFolders.Contains(rig.ShoalId));
        Assert.Contains(rig.ShoalId, rig.Accounts.Get(created.Id).EnabledFolders);

        var calls = rig.Accounts.ListCalls;
        watch.UserUpdated(created.Id);
        watch.UserUpdated(created.Id);
        await Task.Delay(150);
        Assert.Equal(calls, rig.Accounts.ListCalls);

        // An account Shoal never saw created does not trigger anything.
        watch.UserUpdated(Guid.NewGuid());
        await Task.Delay(100);
        Assert.Equal(calls, rig.Accounts.ListCalls);
    }

    // --- T-VIS-4

    [Fact]
    public async Task T_VIS_4_SwitchingBack_RestoresTheSelection_AndUsersCreatedMeanwhileLoseAccess()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);
        await rig.CreateLibraryAsync();
        await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, bob.Id], null, false));
        var shoal = rig.ShoalId;
        Assert.Contains(shoal, rig.Accounts.Get(bob.Id).EnabledFolders);
        Assert.False(rig.Accounts.Get(alice.Id).EnableAllFolders); // managed, hidden from

        await rig.SetAsync(Everyone);
        var late = rig.Accounts.Add("late", all: false, folders: [rig.Movies.Id]);
        await rig.Visibility.ReconcileAsync();
        Assert.Contains(shoal, rig.Accounts.Get(late.Id).EnabledFolders);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders); // nothing is hidden from her now

        var back = await rig.SetAsync(SelectedOnly);

        Assert.True(back.Ok);
        Assert.Equal([rig.Admin.Id, bob.Id], rig.CurrentAccess.SelectedUserIds); // exactly as before
        Assert.Equal(AccessMode.Selected, rig.CurrentAccess.Mode);
        Assert.Contains(late.Id, back.Impact!.Losing);
        Assert.Contains(alice.Id, back.Impact.Losing);
        Assert.DoesNotContain(shoal, rig.Accounts.Get(late.Id).EnabledFolders);
        Assert.Contains(shoal, rig.Accounts.Get(bob.Id).EnabledFolders);
        Assert.False(rig.Accounts.Get(alice.Id).EnableAllFolders);
        Assert.DoesNotContain(shoal, rig.Accounts.Get(alice.Id).EnabledFolders);
    }

    [Fact]
    public async Task TheImpact_ListsWhoGainsAndWhoLoses_BeforeAnythingChanges()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        await rig.CreateLibraryAsync();

        var preview = rig.Access.Preview(VisibilityRig.PeerKey, VisibilityRig.MappingKey, Everyone);

        Assert.True(preview.Ok);
        Assert.Equal([alice.Id], preview.Impact!.Gaining);
        Assert.Empty(preview.Impact.Losing);
        Assert.Equal(AccessMode.Selected, rig.CurrentAccess.Mode); // a preview changes nothing
    }

    // --- T-VIS-5

    [Fact]
    public async Task T_VIS_5_AManagedAccount_GetsNewLocalLibraries_UntilAnAdminEditsIt()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        await rig.CreateLibraryAsync();
        Assert.Contains(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);

        var anime = rig.Libraries.AddLocal("Anime");
        var added = await rig.Visibility.ReconcileAsync();

        Assert.Contains(anime.Id, rig.Accounts.Get(alice.Id).EnabledFolders);
        Assert.Contains(added.Changes, c => c.UserId == alice.Id && c.Kind == VisibilityChangeKind.ManagedRefreshed);
        Assert.Contains(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);

        rig.Accounts.Edit(alice.Id, all: false, [rig.Movies.Id]); // the admin removes Shows and Anime in the dashboard
        var edited = await rig.Visibility.ReconcileAsync();

        Assert.Contains(edited.Changes, c => c.UserId == alice.Id && c.Kind == VisibilityChangeKind.StoppedManaging);
        Assert.DoesNotContain(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);
        Assert.Equal([rig.Movies.Id], rig.Accounts.Get(alice.Id).EnabledFolders); // left exactly as the admin set it
        Assert.Contains("stopped managing", rig.Log.All, StringComparison.Ordinal);
        Assert.Contains(alice.Id, rig.Visibility.Drift().StoppedManaging);

        var another = rig.Libraries.AddLocal("Music");
        await rig.Visibility.ReconcileAsync();
        Assert.DoesNotContain(another.Id, rig.Accounts.Get(alice.Id).EnabledFolders); // an explicit list from now on
    }

    [Fact]
    public async Task AnAdminTickingAllLibraries_OnAManagedAccount_ReleasesItToo()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        await rig.CreateLibraryAsync();

        rig.Accounts.Edit(alice.Id, all: true, []);
        var result = await rig.Visibility.ReconcileAsync();

        Assert.Contains(result.Changes, c => c.UserId == alice.Id && c.Kind == VisibilityChangeKind.StoppedManaging);
        Assert.Contains(result.Changes, c => c.UserId == alice.Id && c.Kind == VisibilityChangeKind.Converted); // and hidden from again
        Assert.Contains(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);
    }

    // --- T-VIS-6

    [Fact]
    public async Task T_VIS_6_ARestrictedProfile_CannotBeSelectedWithoutTheAcknowledgement()
    {
        using var rig = new VisibilityRig();
        var kid = rig.Accounts.Add("kid", restricted: true);
        await rig.CreateLibraryAsync();

        var refused = await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, kid.Id], null, RatingsAcknowledged: false));
        Assert.Equal(AccessError.AcknowledgementRequired, refused.Error);
        Assert.Equal([rig.Admin.Id], rig.CurrentAccess.SelectedUserIds);

        var includeRefused = await rig.SetAsync(new AccessChange(AccessMode.Everyone, null, true, RatingsAcknowledged: false));
        Assert.Equal(AccessError.AcknowledgementRequired, includeRefused.Error);
        Assert.Equal(AccessMode.Selected, rig.CurrentAccess.Mode);

        var accepted = await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, kid.Id], null, RatingsAcknowledged: true));
        Assert.True(accepted.Ok);
        Assert.True(rig.Accounts.Get(kid.Id).EnableAllFolders); // admitted to every Shoal library again: "all libraries" is back
    }

    [Fact]
    public async Task ARestrictedProfile_AlreadyInTheSelection_NeedsNoNewAcknowledgementToChangeTheMode()
    {
        using var rig = new VisibilityRig();
        var kid = rig.Accounts.Add("kid", restricted: true);
        await rig.CreateLibraryAsync();
        await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, kid.Id], null, RatingsAcknowledged: true));

        var result = await rig.SetAsync(Everyone);

        Assert.True(result.Ok);
        Assert.Contains(kid.Id, rig.CurrentAccess.SelectedUserIds); // kept for switching back
        Assert.Contains(kid.Id, result.Impact!.Losing); // but in Everyone mode restricted profiles are excluded
    }

    // --- T-VIS-7

    [Fact]
    public async Task T_VIS_7_AShareUser_IsNeverAdmitted_NeverSelected_AndNeverKeepsAShoalLibrary()
    {
        using var rig = new VisibilityRig();
        var share = rig.Accounts.Add("shoal-x-1a2b", all: false, folders: [rig.Movies.Id]);
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "x", FriendName = "X", ShareUserId = share.Id }));
        await rig.CreateLibraryAsync();

        var refused = await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, share.Id], null, false));
        Assert.Equal(AccessError.ShareUser, refused.Error);

        await rig.SetAsync(Everyone);
        Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(share.Id).EnabledFolders);
        Assert.DoesNotContain(share.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);

        // A Shoal library that ended up in the share user's list (an edit in the dashboard) is taken out again.
        rig.Accounts.Edit(share.Id, all: false, [rig.Movies.Id, rig.ShoalId]);
        var cleaned = await rig.Visibility.ReconcileAsync();

        Assert.Contains(cleaned.Changes, c => c.UserId == share.Id && c.Kind == VisibilityChangeKind.ShareUserCleaned);
        Assert.Equal([rig.Movies.Id], rig.Accounts.Get(share.Id).EnabledFolders);
        Assert.Contains("sharing account", rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task W6_L6_AShareUserWithAllLibraries_IsNarrowedToTheNonShoalLibraries_NotManaged()
    {
        using var rig = new VisibilityRig();
        var share = rig.Accounts.Add("shoal-x-1a2b");
        await rig.State.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "x", FriendName = "X", ShareUserId = share.Id }));
        await rig.CreateLibraryAsync();

        var now = rig.Accounts.Get(share.Id);
        Assert.False(now.EnableAllFolders);
        Assert.DoesNotContain(rig.ShoalId, now.EnabledFolders);
        Assert.Contains(rig.Movies.Id, now.EnabledFolders);
        Assert.DoesNotContain(share.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys); // narrowed, never "managed"
    }

    [Theory]
    [InlineData(AccessMode.Everyone)]
    [InlineData(AccessMode.Selected)]
    public void TheShareUserRule_HoldsInBothModes(AccessMode mode)
    {
        var share = Guid.NewGuid();
        var user = new CallerUser(share, false, false, true, true, true, false, null);
        var access = new LibraryAccess { Mode = mode, SelectedUserIds = [share], IncludeRestricted = true };

        Assert.False(AccessPolicy.Admitted(user, access, new HashSet<Guid> { share }));
        Assert.True(AccessPolicy.Admitted(user, access, new HashSet<Guid>()));
    }

    // --- removal, uninstall, drift, recovery

    [Fact]
    public async Task RemovingTheLibrary_TakesItsIdOutOfEveryList_AndGivesManagedAccountsAllLibrariesBack()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);
        await rig.CreateLibraryAsync();
        await rig.SetAsync(new AccessChange(null, [rig.Admin.Id, bob.Id], null, false));
        var shoal = rig.ShoalId;
        Assert.Contains(shoal, rig.Accounts.Get(bob.Id).EnabledFolders);

        var removed = await rig.Service.RemoveAsync(VisibilityRig.PeerKey, VisibilityRig.MappingKey);

        Assert.True(removed);
        Assert.DoesNotContain(rig.Libraries.List(), l => l.Id == shoal);
        Assert.Equal([rig.Movies.Id], rig.Accounts.Get(bob.Id).EnabledFolders);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders);
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
        Assert.False(await rig.Service.RemoveAsync(VisibilityRig.PeerKey, VisibilityRig.MappingKey)); // nothing left to remove
    }

    [Fact]
    public async Task T_VIS_8a_Uninstalling_WhileTheLibraryExists_NarrowsNeverWidens_AndWarnsOnce()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob");
        await rig.CreateLibraryAsync();
        var aliceList = rig.Accounts.Get(alice.Id).EnabledFolders.ToList();
        rig.Accounts.Edit(bob.Id, all: false, [rig.Movies.Id, rig.ShoalId]); // an admin edits bob in the dashboard

        var restored = await rig.Visibility.RestoreManagedAccountsAsync();

        Assert.Equal(0, restored);
        Assert.False(rig.Accounts.Get(alice.Id).EnableAllFolders); // never widened
        Assert.Equal(aliceList, rig.Accounts.Get(alice.Id).EnabledFolders);
        Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(alice.Id).EnabledFolders);
        Assert.Contains(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders); // an admin owns bob: untouched
        Assert.Contains("alice", rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", rig.Log.All, StringComparison.Ordinal);
        Assert.Contains(rig.Log.All.Split('\n'), line => line.Contains("keep the explicit library list", StringComparison.Ordinal));
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
    }

    [Fact]
    public async Task RemovingAShoalLibrary_DeletesItsItemsFirst_SoNoOrphanTitleStaysListed()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        await rig.CreateLibraryAsync();
        var name = rig.Libraries.List().Single(l => l.Id == rig.ShoalId).Name;
        rig.Libraries.Operations.Clear();

        var (removed, _) = await rig.Service.RemoveAllAsync();

        Assert.Equal(1, removed);
        Assert.Equal(["purge:" + name, "remove:" + name], rig.Libraries.Operations); // the items go while the library still exists, then the library
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders);
    }

    [Fact]
    public async Task T_VIS_8b_RemoveEverything_DeletesTheLibrariesFirst_ThenRestoresAllLibraries()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob");
        await rig.CreateLibraryAsync();
        var shoal = rig.ShoalId;
        rig.Accounts.Edit(bob.Id, all: false, [rig.Movies.Id, rig.ShoalId]);

        var (removed, restored) = await rig.Service.RemoveAllAsync();

        Assert.Equal(1, removed);
        Assert.Equal(0, restored); // alice was already restored while the library went away
        Assert.DoesNotContain(rig.Libraries.List(), l => l.Id == shoal);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders);
        Assert.False(rig.Accounts.Get(bob.Id).EnableAllFolders); // the admin's edit is not overwritten
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
    }

    [Fact]
    public async Task T_VIS_8c_Uninstalling_WhenNoShoalLibraryIsLeft_GivesAllLibrariesBack()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        await rig.CreateLibraryAsync();
        Assert.Single(rig.State.Current.Home.Visibility.ManagedUsers);
        await rig.Libraries.RemoveAsync(rig.Libraries.List().Single(l => l.Id == rig.ShoalId).Name, CancellationToken.None); // removed behind Shoal's back

        var restored = await rig.Visibility.RestoreManagedAccountsAsync();

        Assert.Equal(1, restored);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders);
        Assert.Empty(rig.Accounts.Get(alice.Id).EnabledFolders);
        Assert.Empty(rig.State.Current.Home.Visibility.ManagedUsers);
    }

    [Fact]
    public async Task AnAccountThatCannotBeWritten_IsSkipped_AndTheRestStillWork()
    {
        using var rig = new VisibilityRig();
        var broken = rig.Accounts.Add("broken", all: false, folders: []);
        var fine = rig.Accounts.Add("fine", all: false, folders: []);
        await rig.CreateLibraryAsync();
        await rig.SetAsync(Everyone);
        rig.Accounts.Edit(broken.Id, false, []);
        rig.Accounts.Edit(fine.Id, false, []);
        rig.Accounts.FailFor = broken.Id;

        var result = await rig.Visibility.ReconcileAsync();

        Assert.DoesNotContain(result.Changes, c => c.UserId == broken.Id);
        Assert.Contains(rig.ShoalId, rig.Accounts.Get(fine.Id).EnabledFolders);
        Assert.Contains("could not update", rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDriftList_ShowsWhereCoreAccessDiffersFromTheRule_AndWhoIsManaged()
    {
        using var rig = new VisibilityRig();
        var alice = rig.Accounts.Add("alice");
        var bob = rig.Accounts.Add("bob", all: false, folders: [rig.Movies.Id]);
        await rig.CreateLibraryAsync();

        rig.Accounts.Edit(bob.Id, all: false, [rig.Movies.Id, rig.ShoalId]); // the dashboard adds the Shoal library to bob

        var drift = rig.Visibility.Drift();
        var difference = Assert.Single(drift.Differences);
        Assert.Equal(new AccessDrift(bob.Id, rig.ShoalId, HasAccess: true, ShouldHaveAccess: false), difference);
        Assert.Equal([alice.Id], drift.Managed);
        Assert.Empty(drift.StoppedManaging);

        await rig.Visibility.ReconcileAsync();
        Assert.Empty(rig.Visibility.Drift().Differences);
        Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders);
    }

    [Fact]
    public async Task InRecoveryMode_NothingIsEvaluatedOrWritten()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, "{ this is not json");
        File.WriteAllText(paths.StateFile + ".bak", "{ neither is this");
        using var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        var accounts = new FakeAccounts();
        accounts.Add("alice");
        var visibility = new VisibilityService(store, new FakeLibraries(), accounts, TimeProvider.System, NullLogger.Instance);

        Assert.NotEqual(StateStatus.Ready, store.Status);
        var result = await visibility.ReconcileAsync();

        Assert.True(result.Skipped);
        Assert.Empty(accounts.Writes);
        Assert.Equal(0, await visibility.RestoreManagedAccountsAsync());
    }

    [Fact]
    public async Task APeerThatIsNotInTheState_OrALibraryWithoutAPath_IsNotAShoalLibrary()
    {
        using var rig = new VisibilityRig();
        rig.Libraries.AddLocal("Hops Movies"); // same name, but not at the mapping's path

        var alice = rig.Accounts.Add("alice");
        var result = await rig.Visibility.ReconcileAsync();

        Assert.Empty(result.Changes);
        Assert.True(rig.Accounts.Get(alice.Id).EnableAllFolders);
        _ = rig.ShoalLibrary;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
