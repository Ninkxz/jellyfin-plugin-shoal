using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Guard;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K38 (DESIGN v2.7.6): the TV library, the index, the catalog lookup of an episode, the enricher, the picker (T-TV-7, T-TV-11).</summary>
public sealed class TvAccessTests : IAsyncLifetime
{
    private SyncRig _rig = null!;
    private FakeLibraries _libraries = null!;
    private FakeAccounts _accounts = null!;
    private AccountInfo _admin = null!;
    private bool _tvEnabled = true;

    public async Task InitializeAsync()
    {
        _rig = await SyncRig.CreateAsync(peer =>
        {
            peer.CollectionType = "tvshows";
            peer.SeriesCount = 12;
            peer.EpisodeCount = 345;
        });
        await _rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries.Clear());
        _libraries = new FakeLibraries();
        _accounts = new FakeAccounts();
        _admin = _accounts.Add("admin", admin: true);
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private sealed class Ratings : IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }

    private sealed class Endpoints(PeerEndpoint endpoint) : IPeerEndpoints
    {
        public PeerEndpoint? Find(string peerKey) => peerKey == endpoint.PeerKey ? endpoint : null;
    }

    private sealed class Settings(Func<bool> tv) : ISettingsAccessor
    {
        public PluginConfiguration Current => new() { TvShowsEnabled = tv() };

        public void Save(PluginConfiguration configuration)
        {
        }
    }

    private sealed class NoLive : IPeerLiveStatus
    {
        public PeerLiveInfo ForPeer(string peerKey) => new();

        public ShareLiveInfo ForShare(string shareId) => new();

        public int StartSyncAll() => 0;

        public bool StartSync(string peerKey) => true;
    }

    private AdminPeerLibrariesController Controller()
    {
        var trust = new PeerTrust(_rig.Client, _rig.Registry, _rig.Time);
        var reader = new PeerCatalogReader(_rig.Client, trust, _rig.Registry, new MetadataSanitizer(new Ratings()), _rig.Log);
        var visibility = new VisibilityService(_rig.State, _libraries, _accounts, _rig.Time, _rig.Log);
        var access = new AccessService(_rig.State, _accounts, visibility, _rig.Time);
        var service = new ShoalLibraryService(_rig.State, _libraries, visibility, _rig.Time, NullLogger<ShoalLibraryService>.Instance);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", _admin.Id.ToString("N"))], "test")) };
        return new AdminPeerLibrariesController(_rig.State, new Endpoints(_rig.Endpoint), reader, service, access, _rig.Catalogs, new NoLive(), NullLogger<AdminPeerLibrariesController>.Instance, null, new Settings(() => _tvEnabled))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private string Key => AdminPeerLibrariesController.KeyOf(_rig.Peer.LibraryId);

    // --- the locked options

    [Fact]
    public void ATvLibrary_LocksSeriesSeasonAndEpisode_WithNoFetchers()
    {
        var tv = LibraryOptionsFactory.Create("/stubs/peer/lib", ["OpenSubtitles"], ["Intro"], tvShows: true);
        var movies = LibraryOptionsFactory.Create("/stubs/peer/lib", ["OpenSubtitles"], ["Intro"]);

        Assert.Equal(["Series", "Season", "Episode", "Video"], tv.TypeOptions.Select(static t => t.Type));
        Assert.All(tv.TypeOptions, static t =>
        {
            Assert.Empty(t.MetadataFetchers);
            Assert.Empty(t.ImageFetchers);
        });
        Assert.True(tv.EnableAutomaticSeriesGrouping); // K43: the union of two friends' episodes shows as one series
        Assert.False(movies.EnableAutomaticSeriesGrouping);
        Assert.False(tv.SaveTrickplayWithMedia);
        Assert.False(tv.EnableRealtimeMonitor);
        Assert.False(tv.SaveLocalMetadata);
        Assert.Equal(["OpenSubtitles"], tv.DisabledSubtitleFetchers);
        Assert.Equal(["Intro"], tv.DisabledMediaSegmentProviders);
        Assert.DoesNotContain(movies.TypeOptions, static t => t.Type is "Series" or "Season" or "Episode");
        Assert.Contains(movies.TypeOptions, static t => t.Type == "Movie");
    }

    // --- the picker

    [Fact]
    public async Task ATvLibrary_IsListedWithItsCounts_AndSelectableOnlyWhenTvShowsAreOn()
    {
        _tvEnabled = false;
        var off = AdminTestSupport.Render(await Controller().RemoteLibraries(SyncRig.PeerKey, default)).Json[0];
        _tvEnabled = true;
        var on = AdminTestSupport.Render(await Controller().RemoteLibraries(SyncRig.PeerKey, default)).Json[0];

        Assert.Equal("tvshows", off.GetProperty("type").GetString());
        Assert.False(off.GetProperty("selectable").GetBoolean());
        Assert.True(on.GetProperty("selectable").GetBoolean());
        Assert.Equal((12, 345, 345), (on.GetProperty("seriesCount").GetInt32(), on.GetProperty("episodeCount").GetInt32(), on.GetProperty("count").GetInt32()));
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("music")]
    [InlineData("")]
    public async Task AMixedOrOtherView_IsNeverSelectable(string type)
    {
        _rig.Peer.CollectionType = type;

        var views = AdminTestSupport.Render(await Controller().RemoteLibraries(SyncRig.PeerKey, default)).Json;

        Assert.All(views.EnumerateArray(), static v => Assert.False(v.GetProperty("selectable").GetBoolean()));
        var add = await Add(Controller());
        Assert.Equal(400, AdminTestSupport.Render(add).Status);
    }

    private async Task<IActionResult> Add(AdminPeerLibrariesController controller, string? name = null)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("[{\"mappingKey\":\"" + Key + "\",\"localLibraryName\":" + (name is null ? "null" : "\"" + name + "\"") + ",\"selectedUserIds\":[],\"ratingsAcknowledged\":false}]"));
        return await controller.AddLibraries(SyncRig.PeerKey, default);
    }

    [Fact]
    public async Task AddingATvLibrary_StoresItsType_AndCreatesATvShowsLibraryNamedForThePeer()
    {
        var result = AdminTestSupport.Render(await Add(Controller()));

        Assert.Equal(200, result.Status);
        var mapping = _rig.State.Current.Home.Peers.Single().Libraries.Single();
        Assert.Equal("tvshows", mapping.CollectionType);
        Assert.Equal("tvshows", _libraries.LastCollectionType);
        Assert.Equal("Hops TV shows", mapping.LocalLibraryName);
        Assert.Contains(_libraries.List(), static l => l.Name == "Hops TV shows");
    }

    [Fact]
    public async Task AddingATvLibrary_WithTvShowsOff_IsRefused_AndNothingIsCreated()
    {
        _tvEnabled = false;

        var result = AdminTestSupport.Render(await Add(Controller()));

        Assert.Equal(400, result.Status);
        Assert.Empty(_rig.State.Current.Home.Peers.Single().Libraries);
        Assert.Null(_libraries.LastCollectionType);
    }

    // --- the index

    private static ShoalState StateWith(string collectionType)
    {
        var state = new ShoalState { StubRoot = Path.Combine(Path.GetTempPath(), "stubroot-tv") };
        state.Home.Peers.Add(new PeerState
        {
            PeerKey = "0123456789abcdef0123456789abcdef",
            Libraries = { new LibraryMapping { MappingKey = "r1", Subfolder = "r1", CollectionType = collectionType } },
        });
        return state;
    }

    private static string Pathed(string state, params string[] parts) => Path.Combine([Path.Combine(Path.GetTempPath(), "stubroot-tv"), "0123456789abcdef0123456789abcdef", "r1", .. parts]);

    [Fact]
    public void AnEpisode_IsPlayable_AndNamedByItsPathBelowTheLibrary_ASeriesAndASeasonAreContainers()
    {
        var state = StateWith("tvshows");

        var episode = ShoalIndex.Classify(state, Pathed("tv", "Breaking Bad (2008) [tvdbid-81189]", "Season 01", "S01E02-E03.strm"))!;
        var season = ShoalIndex.Classify(state, Pathed("tv", "Breaking Bad (2008) [tvdbid-81189]", "Season 01"))!;
        var series = ShoalIndex.Classify(state, Pathed("tv", "Breaking Bad (2008) [tvdbid-81189]"))!;
        var art = ShoalIndex.Classify(state, Pathed("tv", "Breaking Bad (2008) [tvdbid-81189]", "Season 01", "S01E02-E03-thumb.jpg"))!;

        Assert.Equal((IndexKind.Playable, "Breaking Bad (2008) [tvdbid-81189]/Season 01/S01E02-E03.strm", "S01E02-E03.strm"), (episode.Kind, episode.FolderName, episode.FileName));
        Assert.Equal(IndexKind.Container, season.Kind);
        Assert.Equal(IndexKind.Container, series.Kind);
        Assert.Equal("Breaking Bad (2008) [tvdbid-81189]", series.FolderName);
        Assert.Equal(IndexKind.Container, art.Kind); // a picture is no item to play
        Assert.All(new[] { episode, season, series }, s => Assert.Equal("r1", s.MappingKey));
    }

    [Fact]
    public void AMovieLibrary_IsClassifiedExactlyAsBefore()
    {
        var state = StateWith("movies");

        var movie = ShoalIndex.Classify(state, Pathed("m", "The Matrix (1999) [tmdbid-603]", "The Matrix (1999) [tmdbid-603].strm"))!;
        var odd = ShoalIndex.Classify(state, Pathed("m", "A", "Season 01", "S01E01.strm"))!;

        Assert.Equal(("The Matrix (1999) [tmdbid-603]", IndexKind.Playable), (movie.FolderName, movie.Kind));
        Assert.Equal(("A", IndexKind.Playable), (odd.FolderName, odd.Kind));
    }

    [Fact]
    public async Task TheIndex_TellsContainersFromEpisodes_ByKind()
    {
        await _rig.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].Libraries.Add(new LibraryMapping { MappingKey = SyncRig.MappingKey, Subfolder = "movies", CollectionType = "tvshows" });
        });
        var episode = Guid.NewGuid();
        var season = Guid.NewGuid();
        var series = Guid.NewGuid();
        var paths = new Dictionary<Guid, string>
        {
            [episode] = Path.Combine(_rig.LibraryFolder, "Show", "Season 01", "S01E01.strm"),
            [season] = Path.Combine(_rig.LibraryFolder, "Show", "Season 01"),
            [series] = Path.Combine(_rig.LibraryFolder, "Show"),
        };
        using var index = new ShoalIndex(new Paths(paths), _rig.State, NullLogger<ShoalIndex>.Instance);
        await index.RebuildAsync();

        Assert.Equal(IndexKind.Playable, index.KindOf(episode));
        Assert.True(index.IsContainer(season));
        Assert.True(index.IsContainer(series));
        Assert.Null(index.KindOf(Guid.NewGuid()));
    }

    private sealed class Paths(IReadOnlyDictionary<Guid, string> paths) : IItemPaths
    {
#pragma warning disable CS0067
        public event EventHandler<ItemPathEventArgs>? Added;

        public event EventHandler<ItemPathEventArgs>? Removed;
#pragma warning restore CS0067

        public string? PathOf(Guid itemId) => paths.GetValueOrDefault(itemId);

        public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) => [.. paths.Select(static p => new ItemPathEventArgs(p.Key, p.Value))];
    }

    // --- the catalog lookup and the enricher

    private async Task<CatalogEntry[]> SeedEpisodesAsync()
    {
        await _rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries.Add(new LibraryMapping { MappingKey = SyncRig.MappingKey, Subfolder = "movies", CollectionType = "tvshows" }));
        var doc = new CatalogDocument { PeerKey = SyncRig.PeerKey, CompletedOnce = true };
        CatalogEntry Episode(string key, int season, int number, string stem, long ticks) => new()
        {
            Kind = EntryKind.Episode,
            ParentKey = "keyseriesseriess",
            PeerSeriesId = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a",
            StubKey = key,
            PeerItemId = Guid.NewGuid().ToString("N"),
            PeerMediaSourceId = Guid.NewGuid().ToString("N"),
            MappingKey = SyncRig.MappingKey,
            Title = "Episode",
            State = EntryState.Active,
            Season = season,
            Episode = number,
            RuntimeTicks = ticks,
            SizeBytes = 100,
            StubRelPath = "Show (2010) [tvdbid-1]/Season " + season.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "/" + stem + ".strm",
        };
        CatalogEntry[] entries = [Episode("keyaaaaaaaaaaaaa", 1, 1, "S01E01", 1_000), Episode("keybbbbbbbbbbbbb", 1, 2, "S01E02", 2_000), Episode("keyccccccccccccc", 2, 1, "S02E01", 3_000)];
        doc.Entries.AddRange(entries);
        doc.Entries.Add(new CatalogEntry { Kind = EntryKind.Series, StubKey = "keyseriesseriess", PeerItemId = Guid.NewGuid().ToString("N"), Title = "Show", State = EntryState.Active, MappingKey = SyncRig.MappingKey, FolderRel = "Show (2010) [tvdbid-1]" });
        _rig.Catalogs.Save(doc);
        return entries;
    }

    [Fact]
    public async Task EveryEpisodeOfASeries_IsFoundByItsOwnPath_AndAMovieStillByItsFolder()
    {
        var entries = await SeedEpisodesAsync();
        var catalog = new StubCatalog(_rig.Catalogs);

        var found = new[] { "S01E01", "S01E02" }.Select(stem => catalog.Find(SyncRig.PeerKey, "Show (2010) [tvdbid-1]/Season 01/" + stem + ".strm")).ToList();

        Assert.Equal(["keyaaaaaaaaaaaaa", "keybbbbbbbbbbbbb"], found.Select(static e => e!.StubKey));
        Assert.Equal("keyccccccccccccc", catalog.Find(SyncRig.PeerKey, "Show (2010) [tvdbid-1]/Season 02/S02E01.strm")!.StubKey);
        Assert.Null(catalog.Find(SyncRig.PeerKey, "Show (2010) [tvdbid-1]")); // the series is a container: no stub entry answers for it
        Assert.Equal(3, entries.Length);
    }

    [Fact]
    public async Task TheEnricher_GivesEachEpisodeItsOwnRunTime_SoTheResumeRowsAndThePlayedMarkWork()
    {
        var entries = await SeedEpisodesAsync();
        var ids = entries.ToDictionary(static e => e.StubKey, static _ => Guid.NewGuid());
        var items = new Items([.. entries.Select(e => (ids[e.StubKey], Path.Combine(_rig.LibraryFolder, e.StubRelPath!.Replace('/', Path.DirectorySeparatorChar))))]);
        var enricher = new StubEnricher(new ShoalIndex(new Paths(new Dictionary<Guid, string>()), _rig.State, NullLogger<ShoalIndex>.Instance), items, new StubCatalog(_rig.Catalogs), _rig.State, NullLogger<StubEnricher>.Instance);

        var result = await enricher.RunAsync();
        var again = await enricher.RunAsync();

        Assert.Equal((3, 3), (result.Checked, result.Updated));
        Assert.Equal([1_000L, 2_000L, 3_000L], entries.Select(e => items.Applied[ids[e.StubKey]].RunTimeTicks));
        Assert.Equal(0, again.Updated); // a second pass writes nothing
    }

    [Fact]
    public async Task TheTrickplayWriter_SavesTheLevelsOfEachEpisode()
    {
        var entries = await SeedEpisodesAsync();
        var doc = _rig.Catalogs.Load(SyncRig.PeerKey).Document;
        foreach (var entry in doc.Entries.Where(static e => e.Kind == EntryKind.Episode))
        {
            entry.Sources = [new CatalogSource { StubKey = entry.StubKey, PeerMediaSourceId = entry.PeerMediaSourceId, StubRelPath = entry.StubRelPath, Trickplay = [new TrickplayLevel { Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = 60, IntervalMs = 10_000, Bandwidth = 1 }] }];
        }

        doc.Entries.First(static e => e.Season == 2).Sources[0].Trickplay.Clear();
        _rig.Catalogs.Save(doc);
        var ids = entries.ToDictionary(static e => e.StubKey, static _ => Guid.NewGuid());
        var paths = entries.ToDictionary(e => ids[e.StubKey], e => Path.Combine(_rig.LibraryFolder, e.StubRelPath!.Replace('/', Path.DirectorySeparatorChar)));
        var items = new Items([.. paths.Select(static p => (p.Key, p.Value))]);
        var catalog = new StubCatalog(_rig.Catalogs);
        var index = new ShoalIndex(new Paths(paths), _rig.State, NullLogger<ShoalIndex>.Instance);
        using var temp = new TempDirectory();
        var cache = new Jellyfin.Plugin.Shoal.Playback.TrickplayCache(temp.Combine("tp"), () => 1 << 30, TimeProvider.System, NullLogger<Jellyfin.Plugin.Shoal.Playback.TrickplayCache>.Instance);
        var manager = TrickplayManagerFake.Create(out var rows);
        var writer = new TrickplayInfoWriter(items, catalog, new Jellyfin.Plugin.Shoal.Playback.OwnSourceVersionResolver(index, catalog), manager, cache, _rig.State, () => true, NullLogger.Instance);

        var (written, _) = await writer.RunAsync(CancellationToken.None);

        Assert.Equal(2, written); // the episode without levels gets none
        Assert.Equal(new HashSet<Guid> { ids["keyaaaaaaaaaaaaa"], ids["keybbbbbbbbbbbbb"] }, rows.Keys.ToHashSet());
        Assert.Equal(320, rows[ids["keyaaaaaaaaaaaaa"]].Keys.Single());
    }

    private sealed class Items(IReadOnlyList<(Guid Id, string Path)> paths) : IStubItems
    {
        public Dictionary<Guid, StubFacts> Applied { get; } = [];

        public IReadOnlyList<StubItemView> List() =>
            [.. paths.Select(p => new StubItemView(p.Id, p.Path, "t", Applied.GetValueOrDefault(p.Id, new StubFacts(0, null, 0, 0, 0, 0)), false, (facts, _) =>
            {
                Applied[p.Id] = facts;
                return Task.CompletedTask;
            }))];
    }

    // --- T-TV-11: the Peer guard

    [Fact]
    public void T_TV_11_BannerAndThumbImages_AreOnTheSameGuardRouteAsPosters()
    {
        // One template serves every image type (the type is a route value), so the guard's route table needs no entry per type.
        var images = GuardRoutes.Expected.Where(static t => t.Contains("images", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(images);
        Assert.DoesNotContain(images, static t => t.Contains("banner", StringComparison.OrdinalIgnoreCase) || t.Contains("thumb", StringComparison.OrdinalIgnoreCase) || t.Contains("primary", StringComparison.OrdinalIgnoreCase));
    }
}
