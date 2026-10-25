using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K44/K45, one stub per library per group: a grouped movie gets an extra copy in each shared library a member feeds; the users' alt tags are written
/// before the copy exists; the copy carries its library tag and the alt tag of every higher-ordered library holding the title; it is found by its folder, admitted
/// by its own library, and plays only from members the caller may open; a copy whose library no member feeds is retired and only its recorded folder deleted.
/// </summary>
public sealed class GroupCopyTests : IAsyncLifetime
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string L1 = "1111111111111111";
    private const string L2 = "2222222222222222";
    private const string OwnerStub = "ownerstub0000001";
    private const string Folder = "The Matrix (1999) [tmdbid-603]";

    private static readonly Guid OnlyL1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid OnlyL2 = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid Both = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private readonly TempDirectory _dir = new();
    private readonly FakeRefresh _refresh = new();
    private readonly List<string> _events = [];

    private bool TagsHold { get; set; } = true;
    private StateStore _state = null!;
    private CatalogStore _catalogs = null!;
    private StubCatalog _catalog = null!;
    private GroupBook _book = null!;
    private GroupCopyWriter _writer = null!;

    private string Root => _dir.Combine("stubs");

    public async Task InitializeAsync()
    {
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, TimeProvider.System, null);
        Directory.CreateDirectory(Root);
        await _state.UpdateAsync(s =>
        {
            s.StubRoot = Root;
            foreach (var key in new[] { A, B })
            {
                s.Home.Peers.Add(new PeerState { PeerKey = key, DisplayName = key[..1], BaseUrl = "http://" + key[..4] + ".tail1234.ts.net:8096", Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", Subfolder = "movies" } } });
            }

            s.Home.Libraries.Add(Library(L1, A, OnlyL1, Both));
            s.Home.Libraries.Add(Library(L2, B, OnlyL2, Both));
            s.Home.LibraryOrder = [L1, L2];
        });
        _catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance);
        _catalog = new StubCatalog(_catalogs);
        Save(A, new CatalogEntry { StubKey = OwnerStub, PeerItemId = new string('1', 32), PeerMediaSourceId = new string('1', 32), MappingKey = "movies-1", Title = "The Matrix", Year = 1999, State = EntryState.Active, StubRelPath = Folder + "/" + Folder + ".strm" });
        Save(B, new CatalogEntry { StubKey = "dupstub000000002", PeerItemId = new string('2', 32), PeerMediaSourceId = new string('2', 32), MappingKey = "movies-1", Title = "The Matrix", Year = 1999, State = EntryState.DuplicatePeer, DuplicateOf = OwnerStub });

        // The owner's stub on disk, as the sync wrote it.
        var owner = Path.Combine(Root, "libs", L1, Folder);
        Directory.CreateDirectory(owner);
        File.WriteAllText(Path.Combine(owner, Folder + ".strm"), StubSentinel.Content(OwnerStub));
        File.WriteAllText(Path.Combine(owner, Folder + ".nfo"), "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n  <title>The Matrix</title>\n  <tag>Shoal</tag>\n  <tag>shoal-lib-11111111</tag>\n</movie>");
        File.WriteAllBytes(Path.Combine(owner, "poster.jpg"), [1, 2, 3]);

        _book = new GroupBook(new GroupStore(() => Root, NullLogger.Instance), NullLogger.Instance);
        _book.Update(d =>
        {
            d.Groups.Add(new TitleGroup
            {
                GroupKey = "grp0000000000001",
                MetaOwner = A,
                Members =
                [
                    new GroupMember { PeerKey = A, PeerItemId = new string('1', 32), Sources = [new string('1', 32)], LibKey = L1 },
                    new GroupMember { PeerKey = B, PeerItemId = new string('2', 32), Sources = [new string('2', 32)], LibKey = L2 },
                ],
                Copies = [new GroupCopy { LibKey = L1, StubKey = OwnerStub, FolderRel = Folder }],
            });
            return true;
        });
        _writer = new GroupCopyWriter(_state, _book, _catalog, _refresh, NullLogger.Instance)
        {
            TagsFirst = _ =>
            {
                _events.Add("tags:" + string.Join(',', _book.AltTagsInUse().Order()) + (Directory.Exists(Path.Combine(Root, "libs", L2, Folder)) ? ":copyExists" : ":noCopyYet"));
                return Task.FromResult(TagsHold);
            },
        };
    }

    public Task DisposeAsync()
    {
        _writer.Dispose();
        _state.Dispose();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LostCatalog_HoldsCopyPlanningAndRetirement()
    {
        var path = Path.Combine(_dir.Path, "shoal", "peers", A, "catalog.json");
        File.Delete(path);
        File.Delete(path + ".bak");
        var before = System.Text.Json.JsonSerializer.Serialize(_book.All());
        Assert.Equal(new GroupCopyResult(0, 0, 0), await _writer.RunAsync());
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(_book.All()));
        Assert.False(Directory.Exists(Path.Combine(Root, "libs", L2, Folder)));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task NewPeer_SharedLibraryWithKnownCopiesStillAllowsFirstImport()
    {
        await _writer.RunAsync();
        var newPeer = new string('c', 32);
        await _state.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = newPeer, DisplayName = "C", BaseUrl = "https://example.invalid", Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", Subfolder = "movies" } } });
            s.Home.Libraries.Single(l => l.LibKey == L2).Feeds.Add(newPeer);
        });
        var store = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger<CatalogStore>.Instance, _state);
        Assert.False(store.Load(newPeer).Unreadable);
    }

    [Fact]
    public async Task CopyRetirement_FailedCaptureKeepsGroupOwnershipAndFile()
    {
        await _writer.RunAsync();
        var capture = new KeepMyPlaceRetentionTests.Capture();
        _writer.Retention = capture;
        await _state.UpdateAsync(s => s.Home.Libraries.Single(l => l.LibKey == L2).Feeds.Clear());
        var copy = _book.All().Single().Copies.Single(c => c.Extra);
        var path = Path.Combine(Root, "libs", L2, Folder, Folder + ".strm");
        Assert.Equal(new GroupCopyResult(0, 0, 0), await _writer.RunAsync());
        Assert.True(File.Exists(path));
        Assert.NotNull(_book.ByCopy(copy.StubKey));
        Assert.Empty(_book.RetiredCopies());
        Assert.Contains(path, capture.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderBackup_KnownKeyAtUnrecordedPathStillHolds(bool extraSource)
    {
        var paths = new ShoalPaths(_dir.Path);
        var store = new CatalogStore(paths, NullLogger<CatalogStore>.Instance, _state);
        var doc = store.Load(A).Document;
        var key = OwnerStub;
        if (extraSource)
        {
            key = "extrasource00001";
            doc.Entries[0].Sources.Add(new CatalogSource { StubKey = key, StubRelPath = Folder + "/Extra.strm" });
            File.WriteAllText(Path.Combine(Root, "libs", L1, Folder, "Extra.strm"), StubSentinel.Content(key));
            store.Save(doc);
        }

        var catalog = Path.Combine(paths.PeerDir(A), "catalog.json");
        File.Copy(catalog, catalog + ".bak", overwrite: true);
        File.Delete(catalog);
        Assert.False(store.Load(A).Unreadable); // complete backup explains every existing artifact
        var duplicate = Path.Combine(Root, "libs", L1, "Unrecorded");
        Directory.CreateDirectory(duplicate);
        File.WriteAllText(Path.Combine(duplicate, "Duplicate.strm"), StubSentinel.Content(key));
        Assert.True(store.Load(A).Unreadable);
        Assert.True(File.Exists(Path.Combine(duplicate, "Duplicate.strm")));
    }

    [Fact]
    public async Task AGroupedTitle_GetsACopyInTheOtherMembersLibrary_AfterTheUsersAltTags_CarryingItsLibraryAndAltTag()
    {
        var result = await _writer.RunAsync();

        Assert.Equal(1, result.Written);
        Assert.Equal(["tags:shoal-alt-11111111:noCopyYet"], _events); // the users' tags went first, before the folder existed
        var copyFolder = Path.Combine(Root, "libs", L2, Folder);
        var copy = _book.All().Single().Copies.Single(c => c.Extra);
        Assert.True(copy.Written);
        Assert.Equal(L2, copy.LibKey);
        Assert.Equal(["shoal-alt-11111111"], copy.AltTags); // L1 comes first and holds the title
        Assert.Empty(_book.All().Single().Copies.Single(c => !c.Extra).AltTags);
        Assert.Equal(StubSentinel.Content(copy.StubKey), File.ReadAllText(Path.Combine(copyFolder, Folder + ".strm")));
        var nfo = File.ReadAllText(Path.Combine(copyFolder, Folder + ".nfo"));
        Assert.Contains("<tag>shoal-lib-22222222</tag>", nfo, StringComparison.Ordinal);
        Assert.Contains("<tag>shoal-alt-11111111</tag>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("shoal-lib-11111111", nfo, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(copyFolder, "poster.jpg")));
        Assert.Contains(Path.Combine(Root, "libs", L2), _refresh.Reported);

        var again = await _writer.RunAsync();
        Assert.Equal(new GroupCopyResult(0, 0, 0), again); // nothing left to do
    }

    [Fact]
    public async Task W11L2_WhenAnAccountDidNotGetItsTag_NoCopyIsWritten_AndTheNextPassWritesIt()
    {
        TagsHold = false;

        var first = await _writer.RunAsync();

        Assert.Equal(0, first.Written);
        Assert.False(Directory.Exists(Path.Combine(Root, "libs", L2, Folder)));
        Assert.False(_book.All().Single().Copies.Single(c => c.Extra).Written); // still planned

        TagsHold = true;
        var second = await _writer.RunAsync();

        Assert.Equal(1, second.Written);
        Assert.True(Directory.Exists(Path.Combine(Root, "libs", L2, Folder)));
    }

    [Fact]
    public async Task TheLibraryOrder_DecidesWhichCopyCarriesTheAltTag_AndAReorderRetagsBoth()
    {
        await _writer.RunAsync();
        await _state.UpdateAsync(s => s.Home.LibraryOrder = [L2, L1]);

        var result = await _writer.RunAsync();

        Assert.Equal(2, result.Retagged);
        Assert.Contains("<tag>shoal-alt-22222222</tag>", File.ReadAllText(Path.Combine(Root, "libs", L1, Folder, Folder + ".nfo")), StringComparison.Ordinal);
        Assert.DoesNotContain("shoal-alt-", File.ReadAllText(Path.Combine(Root, "libs", L2, Folder, Folder + ".nfo")), StringComparison.Ordinal);
        Assert.Contains("<tag>shoal-lib-11111111</tag>", File.ReadAllText(Path.Combine(Root, "libs", L1, Folder, Folder + ".nfo")), StringComparison.Ordinal); // its own library tag stays
    }

    [Fact]
    public async Task ACopyFolder_IsFoundAsACopyOfTheOwnersStub_AndAdmittedByItsOwnLibrary()
    {
        await _writer.RunAsync();
        var owners = new LibraryTreeOwners(_catalog, _book);
        var state = _state.Current;

        var stub = ShoalIndex.Classify(state, Path.Combine(Root, "libs", L2, Folder, Folder + ".strm"), owners)!;

        Assert.True(stub.IsCopy);
        Assert.Equal(new IndexedStub(A, "movies-1", Folder, string.Empty, IndexKind.Playable, L2, IsCopy: true), stub);
        var access = LibraryFolders.AccessOfItem(state, stub.PeerKey, stub.MappingKey, stub.LibKey, stub.IsCopy)!;
        var shareUsers = new HashSet<Guid>();
        Assert.True(AccessPolicy.Admitted(Caller(OnlyL2), access, shareUsers));
        Assert.False(AccessPolicy.Admitted(Caller(OnlyL1), access, shareUsers)); // L1 users see the owner's stub, not this copy
        Assert.Null(LibraryFolders.AccessOfItem(state, stub.PeerKey, stub.MappingKey, stub.LibKey)); // read as a plain item it would fail closed: A does not feed L2

        // A folder the copy writer did not record has no owner.
        Directory.CreateDirectory(Path.Combine(Root, "libs", L2, "Planted"));
        Assert.True(ShoalIndex.Classify(state, Path.Combine(Root, "libs", L2, "Planted", "Planted.strm"), owners)!.OwnerUnknown);
    }

    [Fact]
    public async Task ACopy_PlaysOnlyFromMembersTheCallerMayOpen_NeverFallingBackToTheOwner()
    {
        await _writer.RunAsync();
        var registry = new PeerRegistry(new ManualTimeProvider());
        registry.SetStatus(A, PeerStatus.Online);
        registry.SetStatus(B, PeerStatus.Online);
        var playback = new GroupPlayback(_book, _catalog, _state, registry, new Ranking([A, B]));
        var owner = _catalog.Find(A, Folder)!;

        Assert.Equal([B], playback.CandidatesForCopy(A, owner, Caller(OnlyL2), L2).Select(static c => c.PeerKey));
        Assert.Equal([A, B], playback.CandidatesForCopy(A, owner, Caller(Both), L2).Select(static c => c.PeerKey));
        Assert.Equal([A], playback.Candidates(A, owner, Caller(OnlyL1))!.Select(static c => c.PeerKey)); // the owner's own stub: its library was checked with the item

        registry.SetStatus(B, PeerStatus.Offline);
        Assert.Empty(playback.CandidatesForCopy(A, owner, Caller(OnlyL2), L2)); // B down: nothing, never A
    }

    [Fact]
    public async Task ACopyWhoseLibraryNoMemberFeeds_IsRetired_AndOnlyItsRecordedFolderDeleted()
    {
        await _writer.RunAsync();
        var copyFolder = Path.Combine(Root, "libs", L2, Folder);
        var neighbour = Path.Combine(Root, "libs", L2, "Neighbour (2000)");
        Directory.CreateDirectory(neighbour);
        await _state.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == L2).Feeds.Clear());

        var result = await _writer.RunAsync();

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(copyFolder));
        Assert.True(Directory.Exists(neighbour));
        Assert.DoesNotContain(_book.All().Single().Copies, static c => c.Extra);
        Assert.Empty(_book.RetiredCopies());
    }

    [Fact]
    public async Task ARetiredCopyWhoseFolderNowHoldsSomethingElse_IsNeverDeleted()
    {
        await _writer.RunAsync();
        var copyFolder = Path.Combine(Root, "libs", L2, Folder);
        File.WriteAllText(Path.Combine(copyFolder, Folder + ".strm"), StubSentinel.Content("someoneelse00001")); // not this copy's sentinel any more
        await _state.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == L2).Feeds.Clear());

        var result = await _writer.RunAsync();

        Assert.Equal(0, result.Removed);
        Assert.True(Directory.Exists(copyFolder));
        Assert.Empty(_book.RetiredCopies()); // forgotten, untouched
    }

    [Fact]
    public async Task WhenTheGroupEnds_ItsExtraCopiesAreRetired_AndDeletedByTheNextPass()
    {
        await _writer.RunAsync();
        _book.Update(d => GroupPlanner.ApplyPeer(d, new GroupPeerInput(B, [], _ => L2, new Dictionary<string, IReadOnlyList<string>>()), _ => null, new HashSet<string> { A, B }, () => "grp0000000000009"));

        Assert.Empty(_book.All());
        Assert.Single(_book.RetiredCopies());

        await _writer.RunAsync();

        Assert.False(Directory.Exists(Path.Combine(Root, "libs", L2, Folder)));
        Assert.True(Directory.Exists(Path.Combine(Root, "libs", L1, Folder))); // the owner's own stub is the sync's
    }

    [Fact]
    public async Task TheOwnersPlannerPass_KeepsTheExtraCopies()
    {
        await _writer.RunAsync();
        var owner = _catalog.Find(A, Folder)!;

        _book.Update(d => GroupPlanner.ApplyPeer(d, new GroupPeerInput(A, [owner], _ => L1, new Dictionary<string, IReadOnlyList<string>>()), _ => null, new HashSet<string> { A, B }, () => "grp0000000000009"));

        Assert.Equal(2, _book.All().Single().Copies.Count);
        Assert.Empty(_book.RetiredCopies());
    }

    [Fact]
    public void AltTags_FollowTheOrder_AndIgnoreLibrariesItDoesNotName()
    {
        Assert.Empty(GroupCopyWriter.AltTagsFor([L1, L2], [L1, L2], L1));
        Assert.Equal(["shoal-alt-11111111"], GroupCopyWriter.AltTagsFor([L1, L2], [L1, L2], L2));
        Assert.Equal(["shoal-alt-11111111", "shoal-alt-22222222"], GroupCopyWriter.AltTagsFor([L1, L2], [L2, L1, "3333333333333333"], "3333333333333333"));
    }

    [Fact]
    public void GroupStore_RetiresASecondCopyInOneLibrary_AndAGroupWithOnlyExtraCopies()
    {
        var two = new TitleGroup
        {
            GroupKey = "grp0000000000001",
            MetaOwner = A,
            Members = [new GroupMember { PeerKey = A, PeerItemId = new string('1', 32) }, new GroupMember { PeerKey = B, PeerItemId = new string('2', 32) }],
            Copies = [new GroupCopy { StubKey = OwnerStub, LibKey = L1, FolderRel = Folder }, new GroupCopy { StubKey = "extracopy0000001", LibKey = L1, FolderRel = "x", Extra = true }],
        };
        var retired = new List<GroupCopy>();

        Assert.True(GroupStore.Validate(two, retired)); // one copy per library: the owner's stays, the extra one is retired (its folder still on record)
        Assert.Single(two.Copies);
        Assert.Equal(["extracopy0000001"], retired.Select(static c => c.StubKey));

        var noLibrary = new GroupCopy { StubKey = "extracopy0000003", LibKey = string.Empty, FolderRel = "x", Extra = true };
        two.Copies.Add(noLibrary);
        Assert.True(GroupStore.Validate(two)); // a malformed extra copy (no library) is dropped
        Assert.Single(two.Copies);

        var onlyExtra = new TitleGroup { GroupKey = "grp0000000000002", MetaOwner = A, Members = two.Members, Copies = [new GroupCopy { StubKey = "extracopy0000002", LibKey = L2, FolderRel = "x", Extra = true }] };
        Assert.False(GroupStore.Validate(onlyExtra, retired));
        Assert.Contains(retired, static c => c.StubKey == "extracopy0000002");
    }

    private static ShoalLibrary Library(string key, string feed, params Guid[] users) => new()
    {
        LibKey = key,
        Name = "L" + key[0],
        Type = "movies",
        PathRel = "libs/" + key,
        Feeds = [feed],
        Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [.. users] },
    };

    private static CallerUser Caller(Guid id) => new(id, false, false, true, true, true, false, null);

    private void Save(string peer, CatalogEntry entry)
    {
        var doc = new CatalogDocument { PeerKey = peer, CompletedOnce = true };
        doc.Entries.Add(entry);
        _catalogs.Save(doc);
    }

    private sealed class Ranking(IReadOnlyList<string> keys) : IConnectionRanking
    {
        public IReadOnlyList<string> Order() => keys;

        public double? Throughput(string peerKey) => null;
    }
}
