using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.3 fixture: two friends. Hops feeds a shared "Friends' movies" (placement Shoal, Everyone) with movies-1, and "Friends' TV" (placement Main, inside
/// "Main Shows", admin only) with tv-1 and tv-2. Reef's movies-1 is legacy. Every mapping's own access is Selected with nobody ("Hidden from everyone").
/// </summary>
internal sealed class LibraryViewFixture : IDisposable
{
    public const string Hops = VisibilityRig.PeerKey;
    public const string Reef = "5c1d2e3f405162738495a6b7c8d9e0f1";
    public const string FedMovies = "a1b2c3d4e5f60718";
    public const string FedTv = "b2c3d4e5f6071829";

    private readonly bool _ownsState;

    public LibraryViewFixture(StateStore? store = null)
    {
        Dir = new TempDirectory();
        StubRoot = Dir.Combine("stubs");
        _ownsState = store is null;
        State = store ?? new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, TimeProvider.System, null);
        Catalogs = new CatalogStore(new ShoalPaths(Dir.Path), NullLogger<CatalogStore>.Instance);
        Visibility = new VisibilityService(State, Libraries, Accounts, TimeProvider.System, NullLogger.Instance);
        View = new AdminLibraryView(State, Catalogs, Libraries, Accounts, Visibility);
        Admin = Accounts.Add("admin", admin: true);
    }

    public TempDirectory Dir { get; }

    public string StubRoot { get; }

    public StateStore State { get; }

    public CatalogStore Catalogs { get; }

    public FakeLibraries Libraries { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public VisibilityService Visibility { get; }

    public AdminLibraryView View { get; }

    public AccountInfo Admin { get; }

    public LibraryInfo? MainShows { get; private set; }

    public LibraryInfo? FriendsMovies { get; private set; }

    public Guid ShareUser { get; } = Guid.NewGuid();

    public AdminLibrariesController Controller() => new(State, View, new AccessService(State, Accounts, Visibility, TimeProvider.System), Visibility)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    public async Task SeedAsync(bool feed = true)
    {
        static LibraryMapping Mapping(string key, string remote, string local, string type, string sub) =>
            new() { MappingKey = key, RemoteName = remote, LocalLibraryName = local, CollectionType = type, Subfolder = sub, Access = new LibraryAccess { Mode = AccessMode.Selected } };

        await State.UpdateAsync(s =>
        {
            s.StubRoot = StubRoot;
            s.Home.Peers.Add(new PeerState
            {
                PeerKey = Hops,
                DisplayName = "Hops",
                BaseUrl = "http://hops.tail1234.ts.net:8096",
                Libraries = { Mapping("movies-1", "Movies", "Hops Movies", "movies", "movies"), Mapping("tv-1", "TV", "Hops TV", "tvshows", "tv"), Mapping("tv-2", "Anime", "Hops Anime", "tvshows", "anime") },
            });
            s.Home.Peers.Add(new PeerState
            {
                PeerKey = Reef,
                DisplayName = "Reef",
                BaseUrl = "http://reef.tail1234.ts.net:8096",
                Libraries = { Mapping("movies-1", "Movies", "Reef Movies", "movies", "movies") },
            });
            s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = ShareUser });
            if (feed)
            {
                s.Home.Libraries.Add(new LayoutLibrary { LibKey = FedMovies, Name = "Friends' movies", Type = "movies", Placement = LibraryPlacement.Shoal, PathRel = "libs/" + FedMovies, Feeds = [Hops], Access = new LibraryAccess { Mode = AccessMode.Everyone } });
                s.Home.Libraries.Add(new LayoutLibrary { LibKey = FedTv, Name = "Friends' TV", Type = "tvshows", Placement = LibraryPlacement.Main, PathRel = "libs/" + FedTv, Feeds = [Hops], Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [Admin.Id, ShareUser] } });
            }
        });

        if (feed)
        {
            var home = State.Current.Home.Libraries;
            FriendsMovies = Libraries.AddMain("Friends' movies", "movies");
            Libraries.Change(FriendsMovies.Id, l => l with { Locations = [LibraryFolders.PathOf(StubRoot, home.Single(x => x.LibKey == FedMovies))!] });
            FriendsMovies = Libraries.All.Single(l => l.Id == FriendsMovies.Id);
            MainShows = Libraries.AddMain("Main Shows", "tvshows");
            Libraries.Change(MainShows.Id, l => l with { Locations = [.. l.Locations, LibraryFolders.PathOf(StubRoot, home.Single(x => x.LibKey == FedTv))!] });
        }
    }

    public JsonElement Mapping(string peerKey, string mappingKey) =>
        AdminTestSupport.Body(Controller().GetLibraries()).GetProperty("mappings").EnumerateArray().Single(m => m.GetProperty("mappingKey").GetString() == AdminLibraryView.Compose(peerKey, mappingKey));

    public void Dispose()
    {
        if (_ownsState)
        {
            State.Dispose();
        }

        Dir.Dispose();
    }
}

/// <summary>2.9.3 K3: a library row says its type and the library that really decides who sees its titles.</summary>
public sealed class AdminLibraryViewTests
{
    private static Guid[] Ids(JsonElement access) => [.. access.GetProperty("selectedUserIds").EnumerateArray().Select(static e => e.GetGuid())];

    [Fact]
    public async Task FedMapping_EffectiveAccessIsSharedLibrarys()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();

        var row = f.Mapping(LibraryViewFixture.Hops, "movies-1");

        Assert.Equal("Selected", row.GetProperty("access").GetProperty("mode").GetString()); // the mapping's own (dead) setting is unchanged for older pages
        Assert.Empty(Ids(row.GetProperty("access")));
        Assert.Equal("Everyone", row.GetProperty("effectiveAccess").GetProperty("mode").GetString());
        Assert.Equal(LibraryViewFixture.FedMovies, row.GetProperty("libKey").GetString());
        Assert.Equal("Friends' movies", row.GetProperty("libraryName").GetString());
        Assert.Equal("Shoal", row.GetProperty("placement").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("mainLibraryName").ValueKind);

        // The shared library's selection never lists a sharing account (same filter as access).
        var tv = f.Mapping(LibraryViewFixture.Hops, "tv-1");
        Assert.Equal([f.Admin.Id], Ids(tv.GetProperty("effectiveAccess")));
        Assert.DoesNotContain(f.ShareUser.ToString(), AdminTestSupport.Text(f.Controller().GetLibraries()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyMapping_EffectiveAccessIsMappings()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();

        var row = f.Mapping(LibraryViewFixture.Reef, "movies-1");

        Assert.Equal("Selected", row.GetProperty("effectiveAccess").GetProperty("mode").GetString());
        Assert.Empty(Ids(row.GetProperty("effectiveAccess")));
        Assert.Equal("Legacy", row.GetProperty("placement").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("libKey").ValueKind);
        Assert.Equal("Reef Movies", row.GetProperty("libraryName").GetString());

        // With a recorded legacy library, its key and name are given; the access is still the mapping's.
        var legacyKey = StateUpgrade.LegacyLibKey(LibraryViewFixture.Reef, "movies-1");
        await f.State.UpdateAsync(s => s.Home.Libraries.Add(new LayoutLibrary { LibKey = legacyKey, Name = "Reef old movies", Type = "movies", PathRel = LibraryViewFixture.Reef + "/movies", Feeds = [LibraryViewFixture.Reef], Access = new LibraryAccess { Mode = AccessMode.Everyone } }));

        row = f.Mapping(LibraryViewFixture.Reef, "movies-1");
        Assert.Equal(legacyKey, row.GetProperty("libKey").GetString());
        Assert.Equal("Reef old movies", row.GetProperty("libraryName").GetString());
        Assert.Equal("Legacy", row.GetProperty("placement").GetString());
        Assert.Equal("Selected", row.GetProperty("effectiveAccess").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task MainPlacement_NamesMainLibrary()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();

        foreach (var key in new[] { "tv-1", "tv-2" })
        {
            var row = f.Mapping(LibraryViewFixture.Hops, key);
            Assert.Equal("Main", row.GetProperty("placement").GetString());
            Assert.Equal("Main Shows", row.GetProperty("mainLibraryName").GetString());
            Assert.Equal("Friends' TV", row.GetProperty("libraryName").GetString());
            Assert.Equal(LibraryViewFixture.FedTv, row.GetProperty("libKey").GetString());
        }

        // No main library holds the folder any more: the titles show nowhere, and no main library is named.
        f.Libraries.Change(f.MainShows!.Id, l => l with { Locations = [l.Locations[0]] });
        Assert.Equal(JsonValueKind.Null, f.Mapping(LibraryViewFixture.Hops, "tv-1").GetProperty("mainLibraryName").ValueKind);
    }

    [Fact]
    public async Task Type_FromCollectionType()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();

        Assert.Equal("movies", f.Mapping(LibraryViewFixture.Hops, "movies-1").GetProperty("type").GetString());
        Assert.Equal("tvshows", f.Mapping(LibraryViewFixture.Hops, "tv-1").GetProperty("type").GetString());
        Assert.Equal("tvshows", f.Mapping(LibraryViewFixture.Hops, "tv-2").GetProperty("type").GetString());
        Assert.Equal("movies", f.Mapping(LibraryViewFixture.Reef, "movies-1").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Contract_Libraries_MatchesTheGoldenJson()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();

        var rendered = AdminTestSupport.Render(f.Controller().GetLibraries());

        Assert.Equal(200, rendered.Status);
        ContractGolden.Match("libraries.get", rendered.Text);
    }
}

/// <summary>2.9.3 K3: the server page's total (the sum of its libraries) equals the Overview row's count.</summary>
public sealed class AdminStatusTests
{
    private sealed class NoPairing : IPairingGate
    {
        public bool IsLive(string peerKey) => false;
    }

    [Fact]
    public async Task TitleCount_EqualsSumOfLibraryLocalCounts()
    {
        using var f = new LibraryViewFixture();
        await f.SeedAsync();
        var n = 0;
        string Key() => (++n).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        CatalogEntry Movie(string mapping, EntryState state = EntryState.Active)
        {
            var key = Key();
            return new CatalogEntry { StubKey = key, PeerItemId = Guid.NewGuid().ToString("N"), MappingKey = mapping, Title = "M" + key, State = state, StubRelPath = state == EntryState.Active ? key + "/" + key + ".strm" : null };
        }

        CatalogEntry Series(string mapping, EntryState state = EntryState.Active)
        {
            var key = Key();
            return new CatalogEntry { StubKey = key, PeerItemId = Guid.NewGuid().ToString("N"), MappingKey = mapping, Title = "S" + key, Kind = EntryKind.Series, State = state, FolderRel = "s" + key };
        }

        CatalogEntry Episode(CatalogEntry series, int number)
        {
            var key = Key();
            return new CatalogEntry { StubKey = key, PeerItemId = Guid.NewGuid().ToString("N"), MappingKey = series.MappingKey, Title = "E" + key, Kind = EntryKind.Episode, State = EntryState.Active, ParentKey = series.StubKey, PeerSeriesId = series.PeerItemId, Season = 1, Episode = number, StubRelPath = series.FolderRel + "/Season 01/e" + key + ".strm" };
        }

        var show = Series("tv-1");
        var document = new CatalogDocument { PeerKey = LibraryViewFixture.Hops, CompletedOnce = true };
        document.Entries.AddRange(
        [
            Movie("movies-1"), Movie("movies-1"), Movie("movies-1", EntryState.DuplicatePeer),
            show, Episode(show, 1), Episode(show, 2),
            Series("tv-2"), Series("tv-2", EntryState.HeldRemoval),
        ]);
        f.Catalogs.Save(document);
        var live = new StateBackedLiveStatus(new PeerHealthStore(new ShoalPaths(f.Dir.Path), NullLogger<PeerHealthStore>.Instance), new NoPairing(), f.Catalogs);

        var (libraries, _) = f.View.ForPeer(LibraryViewFixture.Hops, null);
        var sum = libraries.Sum(l => JsonSerializer.SerializeToElement(l, l.GetType()).GetProperty("LocalCount").GetInt32());

        Assert.Equal(3, libraries.Count);
        Assert.Equal(5, sum); // 2 movies, 1 series, 2 series (one held for removal still has its stub)
        Assert.Equal(live.ForPeer(LibraryViewFixture.Hops).TitleCount, sum);
    }
}
