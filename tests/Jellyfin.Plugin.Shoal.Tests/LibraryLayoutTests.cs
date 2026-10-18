using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K44, the folder layout: a connection that feeds a shared library writes into <c>&lt;StubRoot&gt;/libs/&lt;libKey&gt;</c> with the library tag in its NFOs;
/// a legacy mapping keeps its per-friend folder and its exact NFO bytes; a title folder in a shared library is found by its owner's catalog, and an unknown owner
/// fails closed; deletion touches only recorded title folders.
/// </summary>
public sealed class LibraryLayoutTests
{
    private const string LibKey = "0123456789abcdef";
    private const string OtherPeer = "ffffffffffffffffffffffffffffffff";

    private static ShoalLibrary Shared(string libKey = LibKey, string type = "movies", params string[] feeds) => new()
    {
        LibKey = libKey,
        Name = "Friends' " + type,
        Type = type,
        PathRel = ShoalLibrary.LibsFolder + "/" + libKey,
        Feeds = [.. feeds],
        Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [StateSamples.AdminUser] },
    };

    private static ShoalState StateWith(string stubRoot, params ShoalLibrary[] libraries)
    {
        var state = new ShoalState();
        StateSamples.Fill(state);
        state.StubRoot = stubRoot;
        state.Home.Peers[0].Libraries.Add(new LibraryMapping { MappingKey = "tv-1", RemoteName = "TV", CollectionType = "tvshows", LocalLibraryName = "Hops TV", Subfolder = "tv" });
        state.Home.Libraries.AddRange(libraries);
        return state;
    }

    [Fact]
    public void FolderOf_ALegacyMapping_IsItsPerFriendFolder_AndAFedType_IsTheSharedLibraryFolder()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var state = StateWith(root, Shared(feeds: StateSamples.PeerKeyA));
        var peer = state.Home.Peers[0];

        Assert.Equal(Path.Combine(root, ShoalLibrary.LibsFolder, LibKey), LibraryFolders.FolderOf(state, peer.PeerKey, peer.Libraries[0]));
        Assert.Equal(Path.Combine(root, peer.PeerKey, "tv"), LibraryFolders.FolderOf(state, peer.PeerKey, peer.Libraries[1])); // no TV library fed: legacy
        Assert.Equal([LibraryFolders.LibTag(LibKey)], LibraryFolders.NfoTags(state, peer.PeerKey, peer.Libraries[0]));
        Assert.Empty(LibraryFolders.NfoTags(state, peer.PeerKey, peer.Libraries[1]));
    }

    [Fact]
    public void Tags_UseTheFirstEightCharactersOfTheKey_AndAreRecognizedAsShoalTags()
    {
        Assert.Equal("shoal-lib-01234567", LibraryFolders.LibTag(LibKey));
        Assert.Equal("shoal-alt-01234567", LibraryFolders.AltTag(LibKey));
        Assert.True(LibraryFolders.IsShoalTag("Shoal"));
        Assert.True(LibraryFolders.IsShoalTag("shoal-lib-01234567"));
        Assert.False(LibraryFolders.IsShoalTag("Kids"));
        Assert.False(LibraryFolders.IsShoalTag("Shoal: Hops"));
    }

    [Fact]
    public void PathOf_RefusesAFolderThatIsNotTheLibrarysOwnKey_OrARelativeRoot()
    {
        using var dir = new TempDirectory();
        var library = Shared();
        library.PathRel = ShoalLibrary.LibsFolder + "/fedcba9876543210";
        Assert.Null(LibraryFolders.PathOf(dir.Combine("stubs"), library));
        Assert.Null(LibraryFolders.PathOf("relative/stubs", Shared()));
        Assert.NotNull(LibraryFolders.PathOf(dir.Combine("stubs"), Shared()));
    }

    [Fact]
    public void AccessOfItem_FailsClosed_ForAnUnknownOwner_AMissingFeed_AWrongType_OrAnUnknownLibrary()
    {
        using var dir = new TempDirectory();
        var state = StateWith(dir.Combine("stubs"), Shared(feeds: StateSamples.PeerKeyA), Shared("1111111111111111", "tvshows", StateSamples.PeerKeyA), Shared("2222222222222222"));

        Assert.Same(state.Home.Libraries[0].Access, LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "movies-1", LibKey));
        Assert.Same(state.Home.Peers[0].Libraries[0].Access, LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "movies-1", string.Empty)); // legacy folder
        Assert.Null(LibraryFolders.AccessOfItem(state, string.Empty, string.Empty, LibKey));
        Assert.Null(LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "movies-1", "2222222222222222")); // not fed by this connection
        Assert.Null(LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "movies-1", "1111111111111111")); // a movie in a TV library
        Assert.Null(LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "movies-1", "3333333333333333"));
        Assert.Null(LibraryFolders.AccessOfItem(state, StateSamples.PeerKeyA, "gone", LibKey));
    }

    [Fact]
    public void Classify_ATitleFolderOfASharedLibrary_FindsItsOwnerInTheCatalog()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var state = StateWith(root, Shared(feeds: [StateSamples.PeerKeyA, OtherPeer]));
        state.Home.Peers.Add(new PeerState { PeerKey = OtherPeer, DisplayName = "Other", Libraries = { new LibraryMapping { MappingKey = "m2", RemoteName = "Films", Subfolder = "films" } } });
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        Save(store, OtherPeer, "m2", "Heat (1995) [tmdbid-949]");
        Save(store, StateSamples.PeerKeyA, "movies-1", "Alien (1979) [tmdbid-348]");
        var owners = new StubCatalog(store);

        var heat = ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey, "Heat (1995) [tmdbid-949]", "Heat (1995) [tmdbid-949].strm"), owners);
        var alien = ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey, "Alien (1979) [tmdbid-348]", "Alien (1979) [tmdbid-348].strm"), owners);

        Assert.Equal(new IndexedStub(OtherPeer, "m2", "Heat (1995) [tmdbid-949]", "Heat (1995) [tmdbid-949].strm", IndexKind.Playable, LibKey), heat);
        Assert.Equal(StateSamples.PeerKeyA, alien!.PeerKey);
        Assert.False(alien.OwnerUnknown);
    }

    [Fact]
    public void Classify_AFolderNoConnectionRecorded_IsFederatedButRefusing_AndAnUnknownLibraryIsNotFederated()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var state = StateWith(root, Shared(feeds: StateSamples.PeerKeyA));
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        Save(store, StateSamples.PeerKeyA, "movies-1", "Alien (1979) [tmdbid-348]");
        var owners = new StubCatalog(store);

        var stray = ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey, "Planted", "Planted.strm"), owners);
        Assert.NotNull(stray);
        Assert.True(stray!.OwnerUnknown);
        Assert.Equal(LibKey, stray.LibKey);
        Assert.True(ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey, "Alien (1979) [tmdbid-348]", "x.strm"))!.OwnerUnknown); // no lookup given
        Assert.Null(ShoalIndex.Classify(state, Path.Combine(root, "libs", "fedcba9876543210", "Alien", "Alien.strm"), owners));
        Assert.Null(ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey), owners)); // the library folder itself
    }

    [Fact]
    public void Classify_AFolderOfAConnectionThatNoLongerFeedsTheLibrary_IsRefusing()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var state = StateWith(root, Shared(feeds: []));
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        Save(store, StateSamples.PeerKeyA, "movies-1", "Alien (1979) [tmdbid-348]");

        var stub = ShoalIndex.Classify(state, Path.Combine(root, "libs", LibKey, "Alien (1979) [tmdbid-348]", "Alien.strm"), new StubCatalog(store));

        Assert.True(stub!.OwnerUnknown);
    }

    [Fact]
    public void Classify_ASharedTvLibrary_NamesSeriesAndSeasonsAsContainers_AndEpisodesByTheirPath()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var state = StateWith(root, Shared("1111111111111111", "tvshows", StateSamples.PeerKeyA));
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance);
        var doc = new CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        doc.Entries.Add(new CatalogEntry { Kind = EntryKind.Series, StubKey = "series0000000001", PeerItemId = new string('1', 32), MappingKey = "tv-1", State = EntryState.Active, FolderRel = "Show (2015)" });
        store.Save(doc);
        var owners = new StubCatalog(store);
        var library = Path.Combine(root, "libs", "1111111111111111");

        var series = ShoalIndex.Classify(state, Path.Combine(library, "Show (2015)"), owners);
        var season = ShoalIndex.Classify(state, Path.Combine(library, "Show (2015)", "Season 01"), owners);
        var episode = ShoalIndex.Classify(state, Path.Combine(library, "Show (2015)", "Season 01", "S01E01.strm"), owners);

        Assert.Equal(IndexKind.Container, series!.Kind);
        Assert.Equal(IndexKind.Container, season!.Kind);
        Assert.Equal(new IndexedStub(StateSamples.PeerKeyA, "tv-1", "Show (2015)/Season 01/S01E01.strm", "S01E01.strm", IndexKind.Playable, "1111111111111111"), episode);
    }

    [Fact]
    public async Task Sync_AConnectionThatFeedsASharedLibrary_WritesIntoItsFolder_WithTheLibraryTag()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("Alien", 1979, tmdb: 348));
        await rig.State.UpdateAsync(s => s.Home.Libraries.Add(Shared(feeds: SyncRig.PeerKey)));

        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added);
        var folder = Path.Combine(rig.StubRoot, "libs", LibKey);
        var nfo = Directory.EnumerateFiles(folder, "*.nfo", SearchOption.AllDirectories).Single();
        Assert.Contains("<tag>shoal-lib-01234567</tag>", File.ReadAllText(nfo), StringComparison.Ordinal);
        Assert.Contains("<tag>Shoal</tag>", File.ReadAllText(nfo), StringComparison.Ordinal);
        Assert.False(Directory.Exists(rig.LibraryFolder)); // nothing in the per-friend folder
        var entry = rig.Entries.Single();
        Assert.StartsWith("Alien (1979) [tmdbid-348]/", entry.StubRelPath, StringComparison.Ordinal);
        Assert.Equal(new TreeOwner(SyncRig.PeerKey, SyncRig.MappingKey), new StubCatalog(rig.Catalogs).OwnerOf(rig.State.Current, rig.State.Current.Home.Libraries[0], "Alien (1979) [tmdbid-348]"));
    }

    [Fact]
    public async Task Sync_ANameAnotherConnectionAlreadyUsesInTheSharedFolder_GetsItsOwnName()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("Alien", 1979, tmdb: 348));
        await rig.State.UpdateAsync(s => s.Home.Libraries.Add(Shared(feeds: SyncRig.PeerKey)));
        var foreign = Path.Combine(rig.StubRoot, "libs", LibKey, "Alien (1979) [tmdbid-348]");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "theirs.strm"), "another connection's stub");

        await rig.SyncAsync();

        var entry = rig.Entries.Single(e => e.State == EntryState.Active);
        Assert.NotEqual("Alien (1979) [tmdbid-348]", entry.StubRelPath!.Split('/')[0]);
        Assert.Equal("another connection's stub", File.ReadAllText(Path.Combine(foreign, "theirs.strm"))); // untouched
    }

    [Fact]
    public async Task Upgrade_LegacyLibrariesFromSchema2_ChangeNoFile_OnTheNextSync()
    {
        await using var rig = await SyncRig.CreateAsync(p =>
        {
            p.Add("Alien", 1979, tmdb: 348);
            p.Add("Heat", 1995, tmdb: 949);
        });
        await rig.SyncAsync();
        var before = Snapshot(rig.StubRoot);
        Assert.NotEmpty(before);

        // What StateUpgrade does to a schema 2 file: one legacy library per mapping, layout PerFriend.
        await rig.State.UpdateAsync(s =>
        {
            var upgraded = new ShoalState { Schema = StateUpgrade.LegacySchema, StubRoot = s.StubRoot, Home = s.Home };
            StateUpgrade.Upgrade(upgraded);
        });
        Assert.Single(rig.State.Current.Home.Libraries);
        Assert.True(rig.State.Current.Home.Libraries[0].IsLegacy);
        rig.Time.Advance(TimeSpan.FromHours(1));

        await rig.SyncAsync();

        Assert.Equal(before, Snapshot(rig.StubRoot));
    }

    [Fact]
    public void Enricher_ReassertsTheLibraryTag_OfASharedFolder_AndDropsATagLeftFromAnotherLibrary()
    {
        IReadOnlyList<string> legacy = ["Shoal", "Shoal: Hops"];
        Assert.Same(legacy, StubEnricher.WantedTags(legacy, "Hops", null));

        var moved = StubEnricher.WantedTags(["Shoal", "Shoal: Hops", "shoal-lib-fedcba98", "shoal-alt-11111111", "Kids"], "Hops", LibKey);
        Assert.Equal(["Shoal", "Shoal: Hops", "shoal-alt-11111111", "Kids", "shoal-lib-01234567"], moved);

        IReadOnlyList<string> right = ["Shoal", "Shoal: Hops", "shoal-lib-01234567"];
        Assert.Same(right, StubEnricher.WantedTags(right, "Hops", LibKey)); // no-op guard
    }

    [Fact]
    public void Enricher_GivesACopyExactlyItsAltTags()
    {
        var tags = StubEnricher.WantedTags(["Shoal", "Shoal: Hops", "shoal-alt-99999999"], "Hops", null, ["shoal-alt-11111111"]);
        Assert.Equal(["Shoal", "Shoal: Hops", "shoal-alt-11111111"], tags);

        IReadOnlyList<string> plain = ["Shoal", "Shoal: Hops"];
        Assert.Same(plain, StubEnricher.WantedTags(plain, "Hops", null, [])); // a title with no group, nothing to do
    }

    [Fact]
    public void DeleteRecorded_RemovesOnlyTheTitleFoldersTheEntriesRecorded()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var library = Path.Combine(root, "libs", LibKey);
        Directory.CreateDirectory(Path.Combine(library, "Mine (2000)"));
        Directory.CreateDirectory(Path.Combine(library, "Theirs (2001)"));
        File.WriteAllText(Path.Combine(library, "Mine (2000)", "Mine.strm"), "x");
        File.WriteAllText(Path.Combine(library, "Theirs (2001)", "Theirs.strm"), "y");
        var entries = new[]
        {
            new CatalogEntry { State = EntryState.Active, StubRelPath = "Mine (2000)/Mine.strm" },
            new CatalogEntry { State = EntryState.Active, StubRelPath = "../escape/x.strm" },
            new CatalogEntry { State = EntryState.DuplicatePeer, StubRelPath = null },
            new CatalogEntry { State = EntryState.Active, Kind = EntryKind.Series, FolderRel = "Not There" },
        };

        var deleted = LibraryTree.DeleteRecorded(root, library, entries, NullLogger.Instance);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(library, "Mine (2000)")));
        Assert.True(File.Exists(Path.Combine(library, "Theirs (2001)", "Theirs.strm")));
        Assert.True(Directory.Exists(library));
    }

    [Fact]
    public void DeleteIfEmpty_RemovesOnlyAnEmptyRecordedLibsFolder()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("stubs");
        var full = Path.Combine(root, "libs", LibKey);
        Directory.CreateDirectory(Path.Combine(full, "Left (1999)"));

        Assert.False(LibraryTree.DeleteIfEmpty(root, Shared(), NullLogger.Instance));
        Directory.Delete(Path.Combine(full, "Left (1999)"));
        Assert.True(LibraryTree.DeleteIfEmpty(root, Shared(), NullLogger.Instance));
        Assert.False(Directory.Exists(full));

        var legacy = new ShoalLibrary { LibKey = "aaaaaaaaaaaaaaaa", PathRel = StateSamples.PeerKeyA + "/movies" };
        Directory.CreateDirectory(Path.Combine(root, StateSamples.PeerKeyA, "movies"));
        Assert.False(LibraryTree.DeleteIfEmpty(root, legacy, NullLogger.Instance)); // never a legacy folder
        Assert.True(Directory.Exists(Path.Combine(root, StateSamples.PeerKeyA, "movies")));
    }

    private static void Save(CatalogStore store, string peerKey, string mappingKey, string folder)
    {
        var doc = new CatalogDocument { PeerKey = peerKey };
        doc.Entries.Add(new CatalogEntry
        {
            StubKey = "k" + Math.Abs(folder.GetHashCode(StringComparison.Ordinal)).ToString("D15", System.Globalization.CultureInfo.InvariantCulture)[..15],
            PeerItemId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..32],
            PeerMediaSourceId = new string('a', 32),
            MappingKey = mappingKey,
            State = EntryState.Active,
            StubRelPath = folder + "/" + folder + ".strm",
        });
        store.Save(doc);
    }

    private static Dictionary<string, (string Hash, DateTime Written)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith("groups.json", StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                f => (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)));
}
