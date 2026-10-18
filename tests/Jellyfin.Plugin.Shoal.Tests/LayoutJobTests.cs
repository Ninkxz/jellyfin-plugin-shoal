using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ShoalLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A synced Home (real stubs from the sync rig) with the layout services and the move job on top.
/// </summary>
internal sealed class JobRig : IAsyncDisposable
{
    private JobRig(SyncRig sync)
    {
        Sync = sync;
        Admin = Accounts.Add("admin", admin: true);
        Alice = Accounts.Add("alice");
        Bob = Accounts.Add("bob");
        Tags = new TagService(sync.State, Accounts, Libraries, NullLogger.Instance);
        Visibility = new VisibilityService(sync.State, Libraries, Accounts, TimeProvider.System, NullLogger.Instance, Tags);
        Layout = new LibraryLayoutService(sync.State, Libraries, Accounts, Visibility, Lock, TimeProvider.System, NullLogger.Instance, Tags);
        Legacy = new ShoalLibraryService(sync.State, Libraries, Visibility, TimeProvider.System, NullLogger<ShoalLibraryService>.Instance);
        Groups = new GroupBook(new GroupStore(() => sync.StubRoot, sync.Log), sync.Log);
        Job = NewJob();
    }

    public SyncRig Sync { get; }

    public FakeLibraries Libraries { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public LayoutLock Lock { get; } = new();

    public TagService Tags { get; }

    public VisibilityService Visibility { get; }

    public LibraryLayoutService Layout { get; }

    public ShoalLibraryService Legacy { get; }

    public GroupBook Groups { get; }

    public LayoutJobService Job { get; private set; }

    public AccountInfo Admin { get; }

    public AccountInfo Alice { get; }

    public AccountInfo Bob { get; }

    public StateStore State => Sync.State;

    public string LegacyKey => StateUpgrade.LegacyLibKey(SyncRig.PeerKey, SyncRig.MappingKey);

    public static async Task<JobRig> CreateAsync(Action<FakeLibraryPeer> seed)
    {
        var sync = await SyncRig.CreateAsync(seed);
        await sync.SyncAsync();
        var rig = new JobRig(sync);

        // What the upgrade (and v1) left: a per-friend library of the mapping, its Jellyfin library, and a legacy layout entry.
        await rig.Legacy.CreateAsync(SyncRig.PeerKey, SyncRig.MappingKey, rig.Admin.Id);
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [rig.Admin.Id, rig.Alice.Id] };
            StateUpgradeLike(s);
        });
        return rig;
    }

    /// <summary>Makes a new job service on the same data (a restart).</summary>
    /// <returns>The new service.</returns>
    public LayoutJobService NewJob(TimeProvider? time = null)
    {
        Job = new LayoutJobService(Sync.State, Sync.Catalogs, Layout, Accounts, Sync.Refresh, Sync.Paths, time ?? TimeProvider.System, NullLogger.Instance, Sync.Sync, Groups, Legacy);
        return Job;
    }

    public async Task<string> NewLibraryAsync(string name, params string[] feeds)
    {
        var result = await Layout.CreateAsync(new LibraryCreateRequest(name, "movies", LibraryPlacement.Shoal), Admin.Id);
        Assert.True(result.Ok);
        if (feeds.Length > 0)
        {
            await State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == result.LibKey).Feeds.AddRange(feeds));
        }

        return result.LibKey!;
    }

    public string LibFolder(string libKey) => Path.Combine(Sync.StubRoot, "libs", libKey);

    public ValueTask DisposeAsync()
    {
        Tags.Dispose();
        Visibility.Dispose();
        Lock.Dispose();
        return Sync.DisposeAsync();
    }

    private static void StateUpgradeLike(ShoalState s)
    {
        if (s.Home.Libraries.Count == 0)
        {
            var upgraded = new ShoalState { Schema = StateUpgrade.LegacySchema, StubRoot = s.StubRoot, Home = s.Home };
            StateUpgrade.Upgrade(upgraded);
        }
    }
}

/// <summary>
/// PLAN v2.9 K44, the move job: journaled before anything changes, one connection at a time, sole-member folders renamed (with the target's library tag), shared
/// group copies deleted (only that connection's recorded folder), the catalog and groups follow, a targeted scan per affected library, libraries left empty go;
/// refused during a sync or another layout change; a crash mid-way resumes from the last checkpoint and ends as if nothing happened.
/// </summary>
public sealed class LayoutJobTests
{
    private static void Seed(FakeLibraryPeer p)
    {
        p.Add("Alien", 1979, tmdb: 348);
        p.Add("Heat", 1995, tmdb: 949);
        p.Add("Brazil", 1985, tmdb: 68);
    }

    [Fact]
    public async Task Move_ALegacyConnectionIntoANewLibrary_RenamesItsFolders_RetagsThem_AndRemovesTheEmptiedPerFriendLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var legacyFolder = rig.Sync.LibraryFolder;
        var tops = Directory.EnumerateDirectories(legacyFolder).Select(Path.GetFileName).OrderBy(static n => n).ToList();
        Assert.Equal(3, tops.Count);
        var before = rig.Sync.Catalog().Entries.Select(static e => e.StubRelPath).OrderBy(static p => p).ToList();

        var error = await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", null, new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal))], rig.Admin.Id);
        await rig.Job.Running;

        Assert.Equal(LayoutJobError.None, error);
        var status = rig.Job.Status();
        Assert.Equal(LayoutJobPhase.Done, status.Phase);
        Assert.Equal(3, status.StepsDone);
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.Equal([SyncRig.PeerKey], target.Feeds);
        Assert.Equal(tops, Directory.EnumerateDirectories(rig.LibFolder(target.LibKey)).Select(Path.GetFileName).OrderBy(static n => n).ToList());
        foreach (var nfo in Directory.EnumerateFiles(rig.LibFolder(target.LibKey), "*.nfo", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(nfo);
            Assert.Contains("<tag>" + LibraryFolders.LibTag(target.LibKey) + "</tag>", text, StringComparison.Ordinal);
            Assert.Equal(2, text.Split("<tag>shoal-lib-").Length); // exactly one library tag
        }

        Assert.Equal(before, rig.Sync.Catalog().Entries.Select(static e => e.StubRelPath).OrderBy(static p => p).ToList()); // the names did not have to change
        Assert.False(Directory.Exists(legacyFolder)); // emptied and deleted
        Assert.DoesNotContain(rig.State.Current.Home.Libraries, static l => l.IsLegacy);
        Assert.DoesNotContain(rig.Libraries.List(), static l => l.Name == "Hops Movies");
        Assert.Contains(rig.LibFolder(target.LibKey), rig.Sync.Refresh.Reported);
        Assert.Contains(legacyFolder, rig.Sync.Refresh.Reported);

        // The new library got the source's access (admin and alice): nobody lost access, and bob never had it.
        Assert.Equal(new[] { rig.Admin.Id, rig.Alice.Id }.OrderBy(static g => g), target.Access.SelectedUserIds.OrderBy(static g => g));
        Assert.Empty(status.LostAccess);

        // The sync writes into the new folder from now on (and the stubs it finds are its own: nothing is added again).
        var report = await rig.Sync.SyncAsync();
        Assert.Equal(0, report.Added);
        Assert.False(Directory.Exists(legacyFolder));
    }

    [Fact]
    public async Task Move_ACrashMidJob_ResumesFromTheCheckpoint_AndEndsLikeAnUninterruptedMove()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var tops = Directory.EnumerateDirectories(rig.Sync.LibraryFolder).Select(Path.GetFileName).OrderBy(static n => n).ToList();
        rig.Job.CrashAfterStep = steps => steps == 2;

        await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", null, new LibraryCreateRequest("Friends' movies", "movies", LibraryPlacement.Shoal))], rig.Admin.Id);
        await rig.Job.Running;

        // The "crash" left a journal that is not done, two folders moved, one still in the old folder, and the layout lock and sync lock free again (process gone).
        var target = rig.State.Current.Home.Libraries.Single(l => !l.IsLegacy);
        Assert.Equal(2, Directory.EnumerateDirectories(rig.LibFolder(target.LibKey)).Count());
        Assert.Single(Directory.EnumerateDirectories(rig.Sync.LibraryFolder));
        Assert.NotEqual(LayoutJobPhase.Done, rig.Job.Status().Phase);
        Assert.Equal(LayoutJobError.Busy, await rig.NewJob().StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", target.LibKey)], rig.Admin.Id)); // the unfinished job comes first

        var restarted = rig.NewJob();
        Assert.True(restarted.Resume());
        await restarted.Running;

        Assert.Equal(LayoutJobPhase.Done, restarted.Status().Phase);
        Assert.Equal(tops, Directory.EnumerateDirectories(rig.LibFolder(target.LibKey)).Select(Path.GetFileName).OrderBy(static n => n).ToList());
        Assert.False(Directory.Exists(rig.Sync.LibraryFolder));
        Assert.DoesNotContain(rig.State.Current.Home.Libraries, static l => l.IsLegacy);
        Assert.Equal(3, rig.Sync.Catalog().Entries.Count(static e => e.State == EntryState.Active));
        Assert.False(restarted.Resume()); // nothing left to resume
    }

    [Fact]
    public async Task Move_IsRefusedWhileASyncRuns_OrAnotherLayoutChangeRuns_AndHoldsTheSyncLockWhileRunning()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var key = await rig.NewLibraryAsync("Friends' movies");

        using (rig.Sync.Sync.TryHold())
        {
            Assert.Equal(LayoutJobError.Syncing, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id));
        }

        using (rig.Lock.TryEnter())
        {
            Assert.Equal(LayoutJobError.Busy, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id));
        }

        var during = default(SyncReport);
        rig.Job.CrashAfterStep = _ =>
        {
            during = rig.Sync.SyncAsync().GetAwaiter().GetResult(); // a sync that starts while the job runs
            return false;
        };
        Assert.Equal(LayoutJobError.None, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id));
        await rig.Job.Running;

        Assert.Equal("busy", during!.Error);
        Assert.NotNull(rig.Sync.Sync.TryHold()); // released at the end
    }

    [Fact]
    public async Task Move_RefusesAnUnknownConnection_AWrongType_OrAMissingTarget()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var key = await rig.NewLibraryAsync("Friends' movies");

        Assert.Equal(LayoutJobError.NotFound, await rig.Job.StartAsync([new MoveRequest("ffffffffffffffffffffffffffffffff", "movies", key)], rig.Admin.Id));
        Assert.Equal(LayoutJobError.NotFound, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "tvshows", key)], rig.Admin.Id));
        Assert.Equal(LayoutJobError.NotFound, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", "0000000000000000")], rig.Admin.Id));
        Assert.Equal(LayoutJobError.NotFound, await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", rig.LegacyKey)], rig.Admin.Id)); // never into a legacy library
        Assert.Equal(LayoutJobError.Invalid, await rig.Job.StartAsync([], rig.Admin.Id));
        Assert.Null(rig.Job.Status().Phase); // nothing was journaled
    }

    [Fact]
    public async Task Move_IntoAnExistingLibrary_WithANameAlreadyThere_GetsAUniqueName_AndTheCatalogFollows()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Barley", Libraries = { new LibraryMapping { MappingKey = "m", RemoteName = "M", Subfolder = "m" } } }));
        var key = await rig.NewLibraryAsync("Friends' movies", StateSamples.PeerKeyB);
        var alien = rig.Sync.Catalog().Entries.Single(e => e.Title == "Alien");
        var top = alien.StubRelPath!.Split('/')[0];
        Directory.CreateDirectory(Path.Combine(rig.LibFolder(key), top)); // another connection's title with that name

        await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id);
        await rig.Job.Running;

        var moved = rig.Sync.Catalog().Entries.Single(e => e.Title == "Alien");
        var newTop = moved.StubRelPath!.Split('/')[0];
        Assert.NotEqual(top, newTop);
        Assert.True(File.Exists(Path.Combine(rig.LibFolder(key), moved.StubRelPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(rig.LibFolder(key), top))); // the other title's folder was not touched
        Assert.Equal([StateSamples.PeerKeyB, SyncRig.PeerKey], rig.State.Current.Home.Libraries.Single(l => l.LibKey == key).Feeds);
    }

    [Fact]
    public async Task Move_FromASharedLibraryLeftEmpty_DeletesThatLibrary_AndNamesWhoLostAccess()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var first = await rig.NewLibraryAsync("First");
        await rig.State.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == first).Access = new LibraryAccess { Mode = AccessMode.Everyone });
        await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", first)], rig.Admin.Id);
        await rig.Job.Running;
        var second = await rig.NewLibraryAsync("Second"); // admin only

        await rig.NewJob().StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", second)], rig.Admin.Id);
        await rig.Job.Running;

        Assert.DoesNotContain(rig.State.Current.Home.Libraries, l => l.LibKey == first);
        Assert.False(Directory.Exists(rig.LibFolder(first)));
        Assert.DoesNotContain(rig.Libraries.List(), static l => l.Name == "First");
        Assert.Equal(3, Directory.EnumerateDirectories(rig.LibFolder(second)).Count());
        Assert.Equal(new[] { rig.Alice.Id, rig.Bob.Id }.OrderBy(static g => g), rig.Job.Status().LostAccess.OrderBy(static g => g));
    }

    [Fact]
    public async Task Move_ASharedGroupCopy_DeletesOnlyThatConnectionsFolder_AndItsTitleIsAddedAgainInTheTarget()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var key = await rig.NewLibraryAsync("Friends' movies");
        var alien = rig.Sync.Catalog().Entries.Single(e => e.Title == "Alien");
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Barley", Libraries = { new LibraryMapping { MappingKey = "m", RemoteName = "M", Subfolder = "m" } } }));
        rig.Groups.Update(d =>
        {
            d.Groups.Add(new TitleGroup
            {
                GroupKey = "grp0000000000001",
                MetaOwner = SyncRig.PeerKey,
                Members =
                [
                    new GroupMember { PeerKey = SyncRig.PeerKey, PeerItemId = alien.PeerItemId, LibKey = rig.LegacyKey },
                    new GroupMember { PeerKey = StateSamples.PeerKeyB, PeerItemId = new string('b', 32), LibKey = rig.LegacyKey }, // still feeds the old library
                ],
                Copies = [new GroupCopy { LibKey = rig.LegacyKey, StubKey = alien.StubKey, FolderRel = alien.StubRelPath!.Split('/')[0] }],
            });
            return true;
        });

        await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id);
        await rig.Job.Running;

        Assert.DoesNotContain(rig.Sync.Catalog().Entries, e => e.Title == "Alien"); // re-added by the next sync
        Assert.Equal(2, Directory.EnumerateDirectories(rig.LibFolder(key)).Count());
        Assert.Empty(rig.Groups.All());

        var report = await rig.Sync.SyncAsync();
        Assert.True(report.Added == 1, report.Error + " " + report.Result);
        Assert.Equal(1, report.Added);
        Assert.Equal(3, Directory.EnumerateDirectories(rig.LibFolder(key)).Count());
    }

    [Fact]
    public async Task Move_TheOwnerIntoALibraryThatHeldAnExtraCopy_RetiresTheExtraCopy_OneCopyPerLibrary()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Barley", Libraries = { new LibraryMapping { MappingKey = "m", RemoteName = "M", Subfolder = "m" } } }));
        var key = await rig.NewLibraryAsync("Friends' movies", StateSamples.PeerKeyB);
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
                    new GroupMember { PeerKey = StateSamples.PeerKeyB, PeerItemId = new string('b', 32), LibKey = key },
                ],
                Copies =
                [
                    new GroupCopy { LibKey = rig.LegacyKey, StubKey = alien.StubKey, FolderRel = alien.StubRelPath!.Split('/')[0] },
                    new GroupCopy { LibKey = key, StubKey = "extracopy0000001", FolderRel = alien.StubRelPath!.Split('/')[0], Extra = true, Written = true },
                ],
            });
            return true;
        });

        await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", key)], rig.Admin.Id);
        await rig.Job.Running;

        var group = rig.Groups.All().Single();
        var copy = Assert.Single(group.Copies);
        Assert.False(copy.Extra);
        Assert.Equal(key, copy.LibKey);
        Assert.Equal(["extracopy0000001"], rig.Groups.RetiredCopies().Select(static c => c.StubKey)); // its folder is deleted from this record by the copy writer
    }

    [Fact]
    public async Task Journal_ADeleteStepForAFolderTheCatalogDoesNotRecord_DeletesNothing()
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var key = await rig.NewLibraryAsync("Friends' movies");
        var planted = Path.Combine(rig.Sync.LibraryFolder, "Not Shoal's");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "keep.txt"), "x");
        var journal = new LayoutJournal
        {
            JobId = "job0000000000001",
            Phase = LayoutJobPhase.Running,
            Moves =
            [
                new LayoutMove
                {
                    PeerKey = SyncRig.PeerKey,
                    ToLibKey = key,
                    StateApplied = true,
                    Sources = [new LayoutSource { LibKey = rig.LegacyKey, PathRel = SyncRig.PeerKey + "/movies" }],
                    Ops = [new LayoutOp { Kind = LayoutOpKind.Delete, StubKey = "aaaaaaaaaaaaaaaa", MappingKey = SyncRig.MappingKey, FromLibKey = rig.LegacyKey, FromTop = "Not Shoal's" }],
                },
            ],
        };
        await File.WriteAllBytesAsync(Path.Combine(rig.Sync.Paths.Root, "layout-job.json"), JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }));

        var job = rig.NewJob();
        Assert.True(job.Resume());
        await job.Running;

        Assert.True(File.Exists(Path.Combine(planted, "keep.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("libs/0000000000000000")]
    [InlineData("libs/../x")]
    public async Task Journal_WithASourceFolderThatIsNotARecordedLibraryFolder_IsNeverActedOn(string pathRel)
    {
        await using var rig = await JobRig.CreateAsync(Seed);
        var key = await rig.NewLibraryAsync("Friends' movies");
        var journal = $$"""{"schema":1,"jobId":"job0000000000001","phase":"running","moves":[{"peerKey":"{{SyncRig.PeerKey}}","type":"movies","toLibKey":"{{key}}","stateApplied":true,"sources":[{"libKey":"1111111111111111","pathRel":"{{pathRel}}"}],"ops":[]}]}""";
        await File.WriteAllTextAsync(Path.Combine(rig.Sync.Paths.Root, "layout-job.json"), journal);

        Assert.False(rig.NewJob().Resume());
        Assert.Null(rig.Job.Status().Phase);
    }

    [Fact]
    public void Intersection_OfSources_KeepsOnlyWhatEveryoneAdmits()
    {
        var admin = new AccountInfo(Guid.NewGuid(), "admin", false, true, false, true, []);
        var alice = new AccountInfo(Guid.NewGuid(), "alice", false, false, false, true, []);
        var kid = new AccountInfo(Guid.NewGuid(), "kid", false, false, true, true, []);
        var accounts = new[] { admin, alice, kid };
        var none = new HashSet<Guid>();
        var everyone = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true };
        var adults = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = false };
        var adminOnly = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [admin.Id, kid.Id] };

        var both = LayoutJobService.Intersection([everyone, adults], accounts, none, DateTime.UtcNow);
        Assert.Equal(AccessMode.Everyone, both.Mode);
        Assert.False(both.IncludeRestricted);

        var mixed = LayoutJobService.Intersection([adults, adminOnly], accounts, none, DateTime.UtcNow);
        Assert.Equal(AccessMode.Selected, mixed.Mode);
        Assert.Equal([admin.Id], mixed.SelectedUserIds); // the kid is restricted in the Everyone source

        var shared = LayoutJobService.Intersection([everyone], accounts, new HashSet<Guid> { alice.Id }, DateTime.UtcNow);
        Assert.Equal(AccessMode.Everyone, shared.Mode); // Everyone never admits a sharing account anyway
    }

    [Fact]
    public void NfoRetag_ReplacesEveryLibraryTag_WithTheTargets_AndLeavesTheRestByteForByte()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var folder = Path.Combine(root, "libs", "0123456789abcdef", "Alien (1979)");
        Directory.CreateDirectory(folder);
        var nfo = Path.Combine(folder, "Alien (1979).nfo");
        File.WriteAllText(nfo, "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n  <title>Alien</title>\n  <tag>Shoal</tag>\n  <tag>shoal-lib-fedcba98</tag>\n</movie>");

        Assert.Equal(1, NfoRetag.Apply(root, folder, "0123456789abcdef"));
        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n  <title>Alien</title>\n  <tag>Shoal</tag>\n  <tag>shoal-lib-01234567</tag>\n</movie>", File.ReadAllText(nfo));
        Assert.Equal(0, NfoRetag.Apply(root, folder, "0123456789abcdef")); // already right: not rewritten
    }
}
