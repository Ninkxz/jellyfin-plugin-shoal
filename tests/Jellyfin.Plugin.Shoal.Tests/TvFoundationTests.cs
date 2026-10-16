using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K34 (DESIGN v2.7.9): the foundation of TV shows with no behavior change. Catalog schema 4 reads an older file unchanged, keeps series and seasons outside
/// the 50,000 cap and never truncates; the settings default to off; the index can tell an episode from a series folder; every TV interface resolves to a
/// do-nothing default; and the fake peer serves a 5,000-episode tree.
/// </summary>
public sealed class TvFoundationTests
{
    private const string Peer = "4b0c1d2e3f405162738495a6b7c8d9e0";
    private const string Mapping = "tv1";

    private static CatalogStore Store(TempDirectory dir) => new(new ShoalPaths(dir.Path), NullLogger.Instance);

    private static string Hex32(int n) => n.ToString("x32", CultureInfo.InvariantCulture);

    private static string Key(string prefix, int n) => prefix + n.ToString("x8", CultureInfo.InvariantCulture);

    private static CatalogEntry Movie(int n) => new()
    {
        StubKey = Key("m", n),
        PeerItemId = Hex32(n),
        MappingKey = Mapping,
        Title = "Movie " + n.ToString(CultureInfo.InvariantCulture),
        State = EntryState.Active,
        StubRelPath = "Movie " + n.ToString(CultureInfo.InvariantCulture) + "/Movie " + n.ToString(CultureInfo.InvariantCulture) + ".strm",
    };

    private static CatalogEntry Series(int n) => new()
    {
        Kind = EntryKind.Series,
        StubKey = Key("s", n),
        PeerItemId = Hex32(1_000_000 + n),
        MappingKey = Mapping,
        Title = "Show " + n.ToString(CultureInfo.InvariantCulture),
        State = EntryState.Active,
        FolderRel = "Show " + n.ToString(CultureInfo.InvariantCulture) + " (2010) [tvdbid-" + n.ToString(CultureInfo.InvariantCulture) + "]",
    };

    private static CatalogEntry Season(CatalogEntry series, int number) => new()
    {
        Kind = EntryKind.Season,
        StubKey = Key("n", (int.Parse(series.StubKey[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 100) + number),
        PeerItemId = Hex32(2_000_000 + (int.Parse(series.StubKey[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 100) + number),
        MappingKey = Mapping,
        ParentKey = series.StubKey,
        Season = number,
        State = EntryState.Active,
        FolderRel = series.FolderRel + "/Season " + number.ToString("00", CultureInfo.InvariantCulture),
    };

    private static CatalogEntry Episode(CatalogEntry series, int season, int number, int? end = null, int id = 0)
    {
        var folder = series.FolderRel + "/Season " + season.ToString("00", CultureInfo.InvariantCulture);
        var stem = "S" + season.ToString("00", CultureInfo.InvariantCulture) + "E" + number.ToString("00", CultureInfo.InvariantCulture);
        return new CatalogEntry
        {
            Kind = EntryKind.Episode,
            StubKey = Key("e", id == 0 ? (season * 10_000) + number : id),
            PeerItemId = Hex32(3_000_000 + (id == 0 ? (season * 10_000) + number : id)),
            MappingKey = Mapping,
            ParentKey = series.StubKey,
            PeerSeriesId = series.PeerItemId,
            Season = season,
            Episode = number,
            EpisodeEnd = end,
            State = EntryState.Active,
            StubRelPath = folder + "/" + stem + ".strm",
        };
    }

    private static CatalogDocument Doc(params CatalogEntry[] entries)
    {
        var document = new CatalogDocument { PeerKey = Peer };
        document.Entries.AddRange(entries);
        return document;
    }

    private static CatalogDocument RoundTrip(CatalogStore store, CatalogDocument document)
    {
        store.Save(document);
        return store.Load(Peer).Document;
    }

    // ---------------- T-TV-1: catalog schema 4 ----------------

    [Fact]
    public void ASchema3Catalog_ReadsAsAllMovies_AndIsWrittenAsSchema4_WithNothingRenamed()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        store.Save(Doc(Movie(1), Movie(2)));
        var path = Path.Combine(new ShoalPaths(dir.Path).PeerDir(Peer), "catalog.json");
        var v3 = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        v3["schema"] = 3;
        foreach (var e in v3["entries"]!.AsArray())
        {
            foreach (var tv in new[] { "kind", "parentKey", "season", "episode", "episodeEnd", "peerSeriesId", "folderRel", "contentHash" })
            {
                e!.AsObject().Remove(tv);
            }
        }

        foreach (var tv in new[] { "pendingDetail", "imagesPending", "imageFollowUps" })
        {
            v3.Remove(tv);
        }

        File.WriteAllText(path, v3.ToJsonString());
        File.Delete(path + ".bak");

        var loaded = store.Load(Peer);

        Assert.False(loaded.Unreadable);
        Assert.All(loaded.Document.Entries, e => Assert.Equal(EntryKind.Movie, e.Kind));
        Assert.Equal(new string?[] { "Movie 1/Movie 1.strm", "Movie 2/Movie 2.strm" }, loaded.Document.Entries.Select(static e => e.StubRelPath).ToArray());
        store.Save(loaded.Document);
        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(4, written["schema"]!.GetValue<int>());
        Assert.Equal("movie", written["entries"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(CatalogDocument.CurrentSchema, 4);
    }

    [Fact]
    public void ANewerSchema_IsUnreadable_SoTheSyncWaitsForTheAdmin()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        store.Save(Doc(Movie(1)));
        var path = Path.Combine(new ShoalPaths(dir.Path).PeerDir(Peer), "catalog.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\":4", "\"schema\":5", StringComparison.Ordinal));
        File.Delete(path + ".bak");

        Assert.True(store.Load(Peer).Unreadable);
    }

    [Fact]
    public void ASeriesWithSeasonsAndEpisodes_RoundTripsWithEveryTvField()
    {
        using var dir = new TempDirectory();
        var series = Series(1);
        var season = Season(series, 1);
        var one = Episode(series, 1, 1);
        var multi = Episode(series, 1, 2, end: 3);
        multi.ContentHash = "abc123";
        var document = Doc(series, season, one, multi);
        document.PendingDetail.Add(Hex32(77));
        document.ImagesPending.Add(multi.StubKey);
        document.ImageFollowUps = 3;
        document.Libraries.Add(new CatalogLibrary { MappingKey = Mapping, PeerLibraryId = Hex32(5), CompletedOnce = true, SkippedNoNumber = 2, SkippedDuplicate = 1, DroppedOrphans = 4, DroppedOverCap = 5 });

        var read = RoundTrip(Store(dir), document);

        Assert.Equal([EntryKind.Series, EntryKind.Season, EntryKind.Episode, EntryKind.Episode], read.Entries.Select(static e => e.Kind).ToArray());
        var back = read.Entries[3];
        Assert.Equal(series.StubKey, back.ParentKey);
        Assert.Equal((1, 2, 3), (back.Season!.Value, back.Episode!.Value, back.EpisodeEnd!.Value));
        Assert.Equal("abc123", back.ContentHash);
        Assert.Equal(multi.StubRelPath, back.StubRelPath);
        Assert.Equal(series.FolderRel, read.Entries[0].FolderRel);
        Assert.Null(read.Entries[0].StubRelPath);
        Assert.Equal([Hex32(77)], read.PendingDetail);
        Assert.Equal([multi.StubKey], read.ImagesPending);
        Assert.Equal(3, read.ImageFollowUps);
        Assert.True(read.Libraries[0].CompletedOnce);
        Assert.Equal((2, 1, 4, 5), (read.Libraries[0].SkippedNoNumber, read.Libraries[0].SkippedDuplicate, read.Libraries[0].DroppedOrphans, read.Libraries[0].DroppedOverCap));
    }

    [Fact]
    public void SeriesAndSeasons_AreOutsideThe50000Cap_AndTheStoreNeverTruncatesThem()
    {
        using var dir = new TempDirectory();
        var entries = new List<CatalogEntry>();
        for (var s = 0; s < 40; s++)
        {
            var series = Series(s);
            entries.Add(series);
            for (var n = 1; n <= 3; n++)
            {
                entries.Add(Season(series, n));
            }
        }

        entries.AddRange(Enumerable.Range(1, CatalogDocument.MaxEntries + 1).Select(Movie));
        var document = Doc([.. entries]);

        var read = RoundTrip(Store(dir), document);

        Assert.Equal(CatalogDocument.MaxEntries, read.Entries.Count(static e => e.Kind == EntryKind.Movie));
        Assert.Equal(40, read.Entries.Count(static e => e.Kind == EntryKind.Series));
        Assert.Equal(120, read.Entries.Count(static e => e.Kind == EntryKind.Season));
    }

    [Fact]
    public void EpisodesCountTowardThe50000_AndOnlyThePlayableOnesAreCut()
    {
        using var dir = new TempDirectory();
        var series = Series(1);
        var entries = new List<CatalogEntry> { series, Season(series, 1) };
        entries.AddRange(Enumerable.Range(1, 30).Select(n => Episode(series, 1, n)));
        entries.AddRange(Enumerable.Range(1, CatalogDocument.MaxEntries).Select(Movie));

        var read = RoundTrip(Store(dir), Doc([.. entries]));

        Assert.Equal(CatalogDocument.MaxEntries, read.Entries.Count(static e => e.Kind is EntryKind.Movie or EntryKind.Episode));
        Assert.Equal(30, read.Entries.Count(static e => e.Kind == EntryKind.Episode)); // they came first, so they stay
        Assert.Equal(2, read.Entries.Count(static e => e.Kind is EntryKind.Series or EntryKind.Season));
    }

    [Fact]
    public void AnEntryThatBreaksItsKindsRules_IsDropped_AndNothingElseIsTouched()
    {
        using var dir = new TempDirectory();
        var series = Series(1);
        var good = Episode(series, 1, 1);
        var bad = new List<CatalogEntry>
        {
            Episode(series, 1, 2, id: 20).Tweak(e => e.StubRelPath = "../escape/S01E02.strm"),
            Episode(series, 1, 3, id: 21).Tweak(e => e.StubRelPath = "Show/S01E03.strm"),
            Episode(series, 1, 4, id: 22).Tweak(e => e.StubRelPath = series.FolderRel + "/Season 01/a:b.strm"),
            Episode(series, 1, 5, id: 23).Tweak(e => e.ParentKey = "ghost000"),
            Episode(series, 1, 6, id: 24).Tweak(e => e.ParentKey = null),
            Episode(series, 1, 7, id: 25).Tweak(e => e.Season = 2101),
            Episode(series, 1, 8, id: 26).Tweak(e => e.Season = -1),
            Episode(series, 1, 9, id: 27).Tweak(e => e.Episode = 10_000),
            Episode(series, 1, 10, id: 28).Tweak(e => e.EpisodeEnd = 9),
            Episode(series, 1, 11, id: 29).Tweak(e => e.EpisodeEnd = 111),
            Episode(series, 1, 12, id: 30).Tweak(e => e.PeerSeriesId = "zz"),
            Episode(series, 1, 13, id: 31).Tweak(e => e.StubRelPath = null),
            Season(series, 1).Tweak(e => e.FolderRel = "Show"),
            Season(series, 2).Tweak(e => e.StubRelPath = "Show/Season 02/x.strm"),
            Season(series, 3).Tweak(e => e.ParentKey = "ghost000"),
            Series(2).Tweak(e => e.FolderRel = "a/b"),
            Series(3).Tweak(e => e.StubRelPath = "x/y.strm"),
            Series(4).Tweak(e => e.FolderRel = ".."),
        };

        var read = RoundTrip(Store(dir), Doc([series, good, .. bad]));

        Assert.Equal([series.StubKey, good.StubKey], read.Entries.Select(static e => e.StubKey).ToArray());
    }

    [Fact]
    public void ASeasonOrEpisode_WhoseSeriesIsGone_IsDroppedWithIt()
    {
        using var dir = new TempDirectory();
        var series = Series(1);
        var kept = Doc(Season(series, 1), Episode(series, 1, 1));

        var read = RoundTrip(Store(dir), kept);

        Assert.Empty(read.Entries); // relationship fields are re-derived from the entries that are valid: no series entry, no children
    }

    [Fact]
    public void AMovieKeepsItsTwoSegmentPath_AndAnEpisodeNeedsThreeSegments()
    {
        Assert.True(CatalogStore.IsSafeStubPath("Movie (2020)/Movie (2020).strm"));
        Assert.False(CatalogStore.IsSafeStubPath("Show/Season 01/S01E01.strm"));
        Assert.True(CatalogStore.IsSafeEpisodePath("Show (2008) [tvdbid-1]/Season 01/S01E01-E02.strm"));
        Assert.False(CatalogStore.IsSafeEpisodePath("Movie/Movie.strm"));
        Assert.False(CatalogStore.IsSafeEpisodePath("Show/../S01E01.strm"));
        Assert.False(CatalogStore.IsSafeEpisodePath("Show/Season 01/S01E01.mkv"));
        Assert.True(CatalogStore.IsSafeFolder("Show", 1));
        Assert.True(CatalogStore.IsSafeFolder("Show/Season 00", 2));
        Assert.False(CatalogStore.IsSafeFolder("Show/Season 00", 1));
        Assert.False(CatalogStore.IsSafeFolder("Show/..", 2));
    }

    // ---------------- settings ----------------

    [Fact]
    public void TheTvSettings_DefaultToOffAndThumbsOn_AndSurviveACopyAndTheXml()
    {
        var configuration = new PluginConfiguration();
        Assert.False(configuration.TvShowsEnabled);
        Assert.True(configuration.TvEpisodeThumbs);
        Assert.Empty(SettingsValidator.Apply(configuration));

        configuration.TvShowsEnabled = true;
        configuration.TvEpisodeThumbs = false;
        var copy = PluginSettingsAccessor.Copy(configuration);

        Assert.True(copy.TvShowsEnabled);
        Assert.False(copy.TvEpisodeThumbs);
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        xml.Serialize(writer, configuration);
        var back = (PluginConfiguration)xml.Deserialize(new StringReader(writer.ToString()))!;
        Assert.True(back.TvShowsEnabled);
        Assert.False(back.TvEpisodeThumbs);
        Assert.Empty(SettingsValidator.Apply(back));
    }

    // ---------------- ShoalIndex ----------------

    private static ShoalState StateWith(string collectionType) => new()
    {
        StubRoot = Path.Combine(Path.GetTempPath(), "shoal-stubs"),
        Home = new HomeState
        {
            Peers = [new PeerState { PeerKey = Peer, Libraries = [new LibraryMapping { MappingKey = Mapping, Subfolder = "tv", CollectionType = collectionType }] }],
        },
    };

    [Fact]
    public void InATvLibrary_AFolderIsAContainer_AndAStubFileIsPlayable()
    {
        var state = StateWith("tvshows");
        var root = state.StubRoot;

        var seriesFolder = ShoalIndex.Classify(state, Path.Combine(root, Peer, "tv", "Show (2008) [tvdbid-1]"))!;
        var seasonFolder = ShoalIndex.Classify(state, Path.Combine(root, Peer, "tv", "Show (2008) [tvdbid-1]", "Season 01"))!;
        var episode = ShoalIndex.Classify(state, Path.Combine(root, Peer, "tv", "Show (2008) [tvdbid-1]", "Season 01", "S01E01.strm"))!;

        Assert.Equal(IndexKind.Container, seriesFolder.Kind);
        Assert.Equal(IndexKind.Container, seasonFolder.Kind);
        Assert.Equal(IndexKind.Playable, episode.Kind);
        Assert.Equal(Peer, episode.PeerKey);
    }

    [Fact]
    public void InAMovieLibrary_EveryFederatedItemStaysPlayable_WhateverThePathNames()
    {
        var state = StateWith("movies");
        var root = state.StubRoot;

        Assert.Equal(IndexKind.Playable, ShoalIndex.Classify(state, Path.Combine(root, Peer, "tv", "Movie (2020)", "Movie (2020).strm"))!.Kind);
        Assert.Equal(IndexKind.Playable, ShoalIndex.Classify(state, Path.Combine(root, Peer, "tv", "Movie (2020)"))!.Kind); // v1 behavior
    }

    // ---------------- registrations ----------------

    [Fact]
    public async Task EveryTvInterface_ResolvesToADoNothingDefault_AndASpecificServiceIsNotOverwritten()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Jellyfin.Plugin.Shoal.Sync.IRatingTable>(new AnyRating());
        services.AddShoalTv();
        using var provider = services.BuildServiceProvider();

        var planner = provider.GetRequiredService<ITvPlanner>();
        Assert.Same(TvPlan.Empty, planner.Plan(new TvPlanInput(Mapping, [], [], [], [], true, true, new HashSet<string>(), new TvGuardSettings(25, 10, 500, 20), false, false)));
        Assert.Empty(provider.GetRequiredService<ITvImageWork>().Queue([], [], true));
        Assert.Equal("S01E02-E03", provider.GetRequiredService<ITvLayout>().EpisodeStem(1, 2, 3));
        Assert.Equal("Season 00", provider.GetRequiredService<ITvLayout>().SeasonFolder(0));
        Assert.Equal(TvSkip.BadId, provider.GetRequiredService<ITvSanitizer>().Episode(new MediaBrowser.Model.Dto.BaseItemDto()).Skip); // K35 replaced the sanitizer
        Assert.IsType<TvNfoWriter>(provider.GetRequiredService<ITvNfoWriter>()); // K35 replaced the NFO writer
        Assert.NotNull(provider.GetRequiredService<IPeerTvReader>());
        Assert.False((await provider.GetRequiredService<IPeerTvReader>().ListAsync(null!, Guid.Empty, default)).Complete);

        var replaced = new ServiceCollection();
        replaced.AddSingleton<ITvPlanner, OtherPlanner>();
        replaced.AddShoalTv();
        using var second = replaced.BuildServiceProvider();
        Assert.IsType<OtherPlanner>(second.GetRequiredService<ITvPlanner>());
    }

    private sealed class AnyRating : Jellyfin.Plugin.Shoal.Sync.IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }

    private sealed class OtherPlanner : ITvPlanner
    {
        public TvPlan Plan(TvPlanInput input) => TvPlan.Empty;
    }

    [Fact]
    public void TheLimits_AreTheOnesOfTheDesign()
    {
        Assert.Equal((5_000, 200, 25_000, 2_000, 10_000, 5), (TvLimits.MaxSeriesPerPeer, TvLimits.MaxSeasonsPerSeries, TvLimits.MaxSeasonsPerPeer, TvLimits.MaxEpisodesPerSeason, TvLimits.MaxEpisodesPerSeries, TvLimits.MaxActorsPerSeries));
        Assert.Equal((2_100, 9_999, 99, 200, 50, 4), (TvLimits.MaxSeasonNumber, TvLimits.MaxEpisodeNumber, TvLimits.MaxEpisodeSpan, TvLimits.PageSize, TvLimits.DetailBatch, TvLimits.TvFactor));
        Assert.Equal(30_000, TvLimits.MaxContainerEntries);
        Assert.Equal(TimeSpan.FromMinutes(10), TvLimits.ImageBudget);
        Assert.Equal(12, TvLimits.MaxImageFollowUps);
    }

    // ---------------- the fake peer ----------------

    private static async Task<JsonElement> GetAsync(HttpClient http, string url) =>
        JsonDocument.Parse(await http.GetStringAsync(url)).RootElement;

    [Fact]
    public async Task TheFakePeer_ServesA5000EpisodeTree_InPagesOf200_AndTheDetailsByIds()
    {
        await using var peer = new FakeLibraryPeer { Tv = FakeTvTree.Generate(series: 25, seasonsPerSeries: 10, episodesPerSeason: 20) };
        await peer.StartAsync();
        using var http = new HttpClient();

        var views = await GetAsync(http, peer.Url + "/UserViews");
        Assert.Contains(views.GetProperty("Items").EnumerateArray(), v => v.GetProperty("CollectionType").GetString() == "tvshows");
        var counts = new Dictionary<string, int>();
        foreach (var type in new[] { "Series", "Season", "Episode" })
        {
            var total = 0;
            var pages = 0;
            for (var start = 0; ; start += TvLimits.PageSize)
            {
                var page = await GetAsync(http, $"{peer.Url}/Items?IncludeItemTypes={type}&Recursive=true&StartIndex={start}&Limit={TvLimits.PageSize}");
                var items = page.GetProperty("Items").GetArrayLength();
                total += items;
                pages++;
                if (items < TvLimits.PageSize)
                {
                    Assert.Equal(page.GetProperty("TotalRecordCount").GetInt32(), start + items);
                    break;
                }
            }

            counts[type] = total;
            Assert.True(pages >= 1);
        }

        Assert.Equal((25, 250, 5_000), (counts["Series"], counts["Season"], counts["Episode"]));
        var firstEpisode = peer.Tv!.Episodes[0];
        var detail = await GetAsync(http, $"{peer.Url}/Items?Ids={firstEpisode.Id:N}");
        var item = detail.GetProperty("Items")[0];
        Assert.Equal(firstEpisode.Id.ToString("N"), item.GetProperty("Id").GetString());
        Assert.Equal(firstEpisode.SeriesId.ToString("N"), item.GetProperty("SeriesId").GetString());
        Assert.Equal(1, item.GetProperty("ParentIndexNumber").GetInt32());
        Assert.Equal(1, item.GetProperty("IndexNumber").GetInt32());
        Assert.Equal(1, item.GetProperty("MediaSources").GetArrayLength());
    }

    [Fact]
    public async Task APartialListing_CanBeStaged_ByLyingAboutTheTotal()
    {
        var tree = FakeTvTree.Generate(2, 2, 5);
        tree.TotalOverride = kind => kind == "Episode" ? 500 : tree.Series.Count;
        await using var peer = new FakeLibraryPeer { Tv = tree };
        await peer.StartAsync();
        using var http = new HttpClient();

        var page = await GetAsync(http, $"{peer.Url}/Items?IncludeItemTypes=Episode&StartIndex=0&Limit=200");

        Assert.Equal(20, page.GetProperty("Items").GetArrayLength());
        Assert.Equal(500, page.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task EveryHostileFixture_IsServed_WithTheShapeItIsNamedFor(int number)
    {
        var tree = FakeTvFixtures.Build(number);
        await using var peer = new FakeLibraryPeer { Tv = tree };
        await peer.StartAsync();
        using var http = new HttpClient();
        var episodes = new List<JsonElement>();
        for (var start = 0; ; start += TvLimits.PageSize)
        {
            var page = await GetAsync(http, $"{peer.Url}/Items?IncludeItemTypes=Episode&StartIndex={start}&Limit={TvLimits.PageSize}");
            var items = page.GetProperty("Items").EnumerateArray().ToList();
            episodes.AddRange(items);
            if (items.Count < TvLimits.PageSize)
            {
                break;
            }
        }

        Assert.Equal(tree.Episodes.Count, episodes.Count);
        Assert.StartsWith("F-TV-" + number.ToString("00", CultureInfo.InvariantCulture), FakeTvFixtures.Names[number - 1], StringComparison.Ordinal);
        switch (number)
        {
            case 1:
                Assert.Contains(tree.Episodes, e => e.Name == "S09E09");
                break;
            case 2:
                Assert.Equal("24", tree.Series[0].Name);
                break;
            case 3:
                Assert.Equal("..", tree.Series[0].Name);
                break;
            case 4:
                Assert.Contains('\0', tree.Series[0].Name);
                Assert.Contains('‮', tree.Series[0].Name);
                break;
            case 5:
                Assert.Equal(300, tree.Series[0].Name.Length);
                break;
            case 6:
                Assert.Contains(";", tree.Series[0].ProviderIds["Tvdb"], StringComparison.Ordinal);
                Assert.Contains("<script>", tree.Series[0].ProviderIds["Imdb"], StringComparison.Ordinal);
                break;
            case 7:
                Assert.Contains(tree.Episodes, e => e.Number == -1);
                Assert.Contains(tree.Episodes, e => e.Number == 99999);
                break;
            case 8:
                Assert.Contains(tree.Episodes, e => e.NumberEnd < e.Number);
                Assert.Contains(tree.Episodes, e => e.NumberEnd - e.Number > TvLimits.MaxEpisodeSpan);
                break;
            case 9:
                Assert.Equal(250, tree.Seasons.Count);
                break;
            case 10:
                Assert.True(tree.Episodes.Count(static e => e.Season == 1) >= 3_000);
                break;
            case 11:
                Assert.Contains(tree.Episodes, e => e.SeriesId != tree.Series[0].Id);
                Assert.Contains(episodes, e => e.GetProperty("LocationType").GetString() == "Virtual");
                break;
            default:
                Assert.Equal(2, tree.Episodes.Count(static e => e.Season == 1 && e.Number == 3));
                Assert.Contains(tree.Episodes, e => e.Number == 10 && e.NumberEnd == 12);
                Assert.Contains("<script>", tree.Series[0].Overview, StringComparison.Ordinal);
                break;
        }
    }
}

internal static class EntryExtensions
{
    /// <summary>Changes an entry and returns it (the tests build a bad copy of a good entry in one expression).</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="change">The change.</param>
    /// <returns>The same entry.</returns>
    public static CatalogEntry Tweak(this CatalogEntry entry, Action<CatalogEntry> change)
    {
        change(entry);
        return entry;
    }
}
