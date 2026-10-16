using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K35 (DESIGN v2.7): the pure TV libraries: <c>TvSanitizer</c>, <c>TvLayout</c>, <c>TvNfoWriter</c> and the series dedup keys (T-TV-2, F-TV-01 to F-TV-12).</summary>
public sealed class TvLibraryTests
{
    private static readonly TvSanitizer Sanitizer = new(new Ratings());
    private static readonly TvLayout Layout = new();
    private static readonly TvNfoWriter Nfo = new();
    private static readonly string[] Tags = ["Shoal", "Shoal: Hops"];
    private static readonly string StubKey = "abcdefgh12345678";

    private sealed class Ratings : IRatingTable
    {
        public bool IsRecognized(string rating) => rating is "TV-14" or "PG";
    }

    private static BaseItemDto Dto(FakeTvTree tree, Guid id) =>
        JsonSerializer.Deserialize<BaseItemDto>(JsonSerializer.Serialize(tree.Detail(id)), JsonDefaults.Options)!;

    private static SanitizedSeries Series(FakeTvTree tree, int index = 0) => Sanitizer.Series(Dto(tree, tree.Series[index].Id))!;

    private static IReadOnlyList<SanitizedEpisode> Episodes(FakeTvTree tree) =>
        [.. tree.Episodes.Select(e => Sanitizer.Episode(Dto(tree, e.Id))).Where(static r => r.Episode is not null).Select(static r => r.Episode!)];

    private static XmlDocument Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    // --- the sanitizer

    [Fact]
    public void ASeries_IsSanitized_FromTheDetails()
    {
        var series = Series(FakeTvFixtures.Build(1));

        Assert.Equal("Show 0", series.Title);
        Assert.Equal(["Drama"], series.Genres);
        Assert.Equal(["Network One"], series.Studios);
        Assert.Equal("TV-14", series.OfficialRating);
        Assert.Equal("Ended", series.Status);
        Assert.Equal(100_000, series.ProviderIds.Tvdb);
        Assert.Equal("p1", series.PosterTag);
        Assert.Equal("bn1", series.BannerTag);
        Assert.Equal("t1", series.LandscapeTag);
        Assert.Equal("b1", series.BackdropTag);
        Assert.Equal(32, series.ContentHash.Length);
    }

    [Fact]
    public void AnEtagChangeThatChangesNothing_KeepsTheContentHash()
    {
        var tree = FakeTvFixtures.Build(1);
        var before = Series(tree);
        var episodeBefore = Sanitizer.Episode(Dto(tree, tree.Episodes[0].Id)).Episode!;
        tree.Series[0].Etag = "s2";
        tree.Episodes[0].Etag = "e2";

        var after = Series(tree);
        var episodeAfter = Sanitizer.Episode(Dto(tree, tree.Episodes[0].Id)).Episode!;

        Assert.NotEqual(before.Etag, after.Etag);
        Assert.Equal(before.ContentHash, after.ContentHash);
        Assert.Equal(episodeBefore.ContentHash, episodeAfter.ContentHash);
        tree.Episodes[0].Name = "Renamed";
        Assert.NotEqual(episodeBefore.ContentHash, Sanitizer.Episode(Dto(tree, tree.Episodes[0].Id)).Episode!.ContentHash);
    }

    [Fact]
    public void AnEpisode_KeepsThePrimarySourceOnly_AndItsNumbers()
    {
        var tree = FakeTvTree.Generate(1, 1, 1);
        var episode = Sanitizer.Episode(Dto(tree, tree.Episodes[0].Id));

        Assert.Equal(TvSkip.None, episode.Skip);
        Assert.Equal((1, 1, null), (episode.Episode!.Season, episode.Episode.Number, episode.Episode.NumberEnd));
        Assert.Equal("mkv", episode.Episode.Source!.Container);
        Assert.Equal(27_000_000_000L, episode.Episode.RunTimeTicks);
        Assert.Equal("ep1", episode.Episode.ThumbTag);
    }

    [Fact]
    public void FTv01_AnEpisodeTitledS09E09_KeepsItsTitle_ButItsFileNameComesFromItsNumbers()
    {
        var tree = FakeTvFixtures.Build(1);
        var episode = Episodes(tree).Single(static e => e.Number == 3);

        Assert.Equal("S09E09", episode.Title);
        Assert.Equal("S01E03", Layout.EpisodeStem(episode.Season, episode.Number, episode.NumberEnd));
    }

    [Fact]
    public void FTv02_ASeriesNamed24_GetsAPlainFolder()
    {
        var folder = Layout.SeriesFolder(Series(FakeTvFixtures.Build(2)), StubKey, new HashSet<string>());

        Assert.Equal("24 (2010) [tvdbid-100000]", folder);
    }

    [Fact]
    public void FTv03_ASeriesNamedDotDot_CannotLeaveItsLibrary()
    {
        var folder = Layout.SeriesFolder(Series(FakeTvFixtures.Build(3)), StubKey, new HashSet<string>());
        using var dir = new TempDirectory();

        Assert.NotEqual("..", folder);
        Assert.DoesNotContain("/", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", folder, StringComparison.Ordinal);
        Assert.True(PathSanitizer.TryConfine(dir.Path, folder, out var full));
        Assert.StartsWith(System.IO.Path.GetFullPath(dir.Path), full, StringComparison.Ordinal);
    }

    [Fact]
    public void FTv04_NulAndBidiControls_AreGone_FromTheTextTheFolderAndTheNfo()
    {
        var tree = FakeTvFixtures.Build(4);
        var series = Series(tree);
        var episode = Episodes(tree).Single(static e => e.Number == 3);

        foreach (var text in new[] { series.Title, episode.Title, Layout.SeriesFolder(series, StubKey, new HashSet<string>()) })
        {
            Assert.DoesNotContain('\0', text);
            Assert.DoesNotContain('‮', text);
            Assert.DoesNotContain('⁦', text);
        }

        Parse(Nfo.Series(series, Tags));
        Parse(Nfo.Episode(episode, Tags));
    }

    [Fact]
    public void FTv05_ATitleOf300Characters_IsKept_ButTheFolderStaysShort()
    {
        var tree = FakeTvFixtures.Build(5);
        var series = Series(tree);
        var episode = Episodes(tree).Single(static e => e.Number == 3);

        Assert.Equal(300, series.Title.Length);
        Assert.Equal(300, episode.Title.Length);
        Assert.True(Encoding.UTF8.GetByteCount(Layout.SeriesFolder(series, StubKey, new HashSet<string>())) <= PathSanitizer.MaxTitleBytes + 60);
    }

    [Fact]
    public void FTv06_InjectionInProviderIds_IsDropped_AndTheFolderGetsTheShoalTag()
    {
        var series = Series(FakeTvFixtures.Build(6));

        Assert.False(series.ProviderIds.HasAny);
        Assert.Equal("Show 0 (2010) [shoal-abcdefgh]", Layout.SeriesFolder(series, StubKey, new HashSet<string>()));
        Assert.DoesNotContain("uniqueid", Nfo.Series(series, Tags), StringComparison.Ordinal);
    }

    [Fact]
    public void FTv07_EpisodeNumbersMinusOneAndNinetyNineThousand_AreSkippedAsNoNumber()
    {
        var tree = FakeTvFixtures.Build(7);

        var results = tree.Episodes.Where(static e => e.Number is -1 or 99999).Select(e => Sanitizer.Episode(Dto(tree, e.Id))).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, static r => Assert.Equal(TvSkip.NoNumber, r.Skip));
        Assert.All(results, static r => Assert.Null(r.Episode));
    }

    [Fact]
    public void FTv08_AnEndBeforeTheStart_OrFarPastIt_IsNotBelieved()
    {
        var tree = FakeTvFixtures.Build(8);

        var episodes = Episodes(tree).Where(static e => e.Number is 5 or 7).ToList();

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, static e => Assert.Null(e.NumberEnd));
    }

    [Theory]
    [InlineData(1, 2, 3, "S01E02-E03")]
    [InlineData(1, 2, 2, "S01E02")]
    [InlineData(0, 1, null, "S00E01")]
    [InlineData(2024, 12, null, "S2024E12")]
    [InlineData(1, 1000, null, "S01E1000")]
    [InlineData(1, 9999, null, "S01E9999")]
    public void EpisodeStems_AreBuiltFromIntegers(int season, int episode, int? end, string expected)
    {
        Assert.Equal(expected, Layout.EpisodeStem(season, episode, end));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2101)]
    public void ASeasonOutsideItsRange_HasNoFolder(int season)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout.SeasonFolder(season));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout.EpisodeStem(season, 1, null));
    }

    [Fact]
    public void SeasonFolders_UseAtLeastTwoDigits()
    {
        Assert.Equal(["Season 00", "Season 01", "Season 12", "Season 2024"], new[] { 0, 1, 12, 2024 }.Select(Layout.SeasonFolder));
    }

    [Fact]
    public void FTv09_TwoHundredFiftySeasons_AreAllNamedFromTheirNumbers()
    {
        var tree = FakeTvFixtures.Build(9);

        var seasons = tree.Seasons.Select(s => Sanitizer.Season(Dto(tree, s.Id))).ToList();

        Assert.Equal(250, seasons.Count);
        Assert.All(seasons, static s => Assert.NotNull(s));
        Assert.Equal("Season 250", Layout.SeasonFolder(seasons.Max(static s => s!.Number)));
    }

    [Fact]
    public void FTv10_ThreeThousandEpisodesInOneSeason_AllSurviveTheOverlapRule()
    {
        var set = Layout.Resolve(Episodes(FakeTvFixtures.Build(10)));

        Assert.Equal(3_000, set.Accepted.Count);
        Assert.Equal(0, set.DuplicatePeer);
    }

    [Fact]
    public void FTv11_AVirtualEpisode_IsSkipped_AndAWrongSeriesIdStillSanitizes()
    {
        var tree = FakeTvFixtures.Build(11);

        var virtualEpisode = Sanitizer.Episode(Dto(tree, tree.Episodes.Single(static e => e.LocationType == "Virtual").Id));
        var wrong = Sanitizer.Episode(Dto(tree, tree.Episodes.Single(static e => e.Name == "Wrong series").Id));

        Assert.Equal(TvSkip.Virtual, virtualEpisode.Skip);
        Assert.Equal(TvSkip.None, wrong.Skip); // the planner drops it as an orphan: the series is not in the listing
        Assert.Equal(FakeTvTree.IdOf('S', 999), wrong.Episode!.PeerSeriesId);
    }

    [Fact]
    public void FTv12_EqualNumbersAndOverlappingRanges_AreResolvedDeterministically()
    {
        var tree = FakeTvFixtures.Build(12);
        var all = Episodes(tree).ToList();

        var forward = Layout.Resolve(all);
        var backward = Layout.Resolve([.. all.AsEnumerable().Reverse()]);
        var shuffled = Layout.Resolve([.. all.OrderBy(static e => e.PeerItemId)]);

        Assert.Equal(forward.Accepted.Select(static e => e.PeerItemId), backward.Accepted.Select(static e => e.PeerItemId));
        Assert.Equal(forward.Accepted.Select(static e => e.PeerItemId), shuffled.Accepted.Select(static e => e.PeerItemId));
        Assert.Equal(2, forward.DuplicatePeer); // Twin B, and the episode inside the range
        Assert.Equal([1, 2, 3, 10], forward.Accepted.Select(static e => e.Number));
        Assert.Equal(12, forward.Accepted.Single(static e => e.Number == 10).NumberEnd);
    }

    [Fact]
    public void FTv12_AScriptInTheOverview_NeverReachesTheNfoAsMarkup()
    {
        var tree = FakeTvFixtures.Build(12);
        var series = Series(tree);

        var xml = Nfo.Series(series, Tags);
        var doc = Parse(xml);

        Assert.DoesNotContain("<script", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Null(doc.SelectSingleNode("//script"));
        Assert.Contains("second paragraph", doc.SelectSingleNode("/tvshow/plot")!.InnerText, StringComparison.Ordinal);
    }

    // --- overlaps, ranges, collisions

    [Fact]
    public void ARangeBlocksTheNumbersInsideIt_AndOnlyInItsOwnSeason()
    {
        var tree = FakeTvTree.Generate(1, 2, 0);
        FakeTvEpisode Make(int season, int number, int? end, int n) =>
            new() { Id = FakeTvTree.IdOf('X', n), SeriesId = tree.Series[0].Id, SeasonId = tree.Seasons[season - 1].Id, Season = season, Number = number, NumberEnd = end };
        tree.Episodes.AddRange([Make(1, 1, 3, 1), Make(1, 2, null, 2), Make(1, 3, null, 3), Make(1, 4, null, 4), Make(2, 2, null, 5)]);

        var set = Layout.Resolve(Episodes(tree));

        Assert.Equal([(1, 1), (1, 4), (2, 2)], set.Accepted.Select(static e => (e.Season, e.Number)));
        Assert.Equal(2, set.DuplicatePeer);
    }

    [Fact]
    public void SeriesFolders_ThatCollide_AreToldApart_IgnoringCaseAndNormalization()
    {
        var taken = new HashSet<string>();
        var tree = FakeTvTree.Generate(3, 1, 1);
        tree.Series[0].Name = "Café";
        tree.Series[1].Name = "CAFÉ"; // the same word, upper case
        tree.Series[2].Name = "Café"; // e plus a combining acute
        foreach (var series in tree.Series)
        {
            series.ProviderIds = [];
            series.Year = 2000;
        }

        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var name = Layout.SeriesFolder(Series(tree, i), "key" + i + "abcdefghijkl", taken);
            names.Add(name);
            taken.Add(PathSanitizer.CollisionKey(name));
        }

        Assert.Equal(3, names.Select(PathSanitizer.CollisionKey).Distinct().Count());
    }

    // --- the NFO files

    [Fact]
    public void TheSeriesNfo_HasTheSpecifiedFields_AndNoUrlOrPeerId()
    {
        var tree = FakeTvFixtures.Build(1);
        var series = Series(tree);

        var xml = Nfo.Series(series, Tags);
        var doc = Parse(xml);

        Assert.Equal("Show 0", doc.SelectSingleNode("/tvshow/title")!.InnerText);
        Assert.Equal("Ended", doc.SelectSingleNode("/tvshow/status")!.InnerText);
        Assert.Equal("TV-14", doc.SelectSingleNode("/tvshow/mpaa")!.InnerText);
        Assert.Equal("100000", doc.SelectSingleNode("/tvshow/uniqueid[@type='tvdb']")!.InnerText);
        Assert.Equal("true", doc.SelectSingleNode("/tvshow/uniqueid[@type='tvdb']/@default")!.Value);
        Assert.Equal(["Shoal", "Shoal: Hops"], doc.SelectNodes("/tvshow/tag")!.Cast<XmlNode>().Select(static n => n.InnerText));
        Assert.Equal("Actor One", doc.SelectSingleNode("/tvshow/actor/name")!.InnerText);
        Assert.Null(doc.SelectSingleNode("/tvshow/actor/thumb"));
        Assert.DoesNotContain(series.PeerItemId.ToString("N"), xml, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "<thumb", "<trailer", "<fileinfo", "<lockdata", "<website", "<runtime", "<director", "<credits", "http:", "https:" })
        {
            Assert.DoesNotContain(forbidden, xml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheEpisodeNfo_WritesNumbersFromIntegers_AndNeverUniqueIdOrActors()
    {
        var tree = FakeTvFixtures.Build(12);
        var episode = Episodes(tree).Single(static e => e.Number == 10);

        var xml = Nfo.Episode(episode, Tags);
        var doc = Parse(xml);

        Assert.Equal("1", doc.SelectSingleNode("/episodedetails/season")!.InnerText);
        Assert.Equal("10", doc.SelectSingleNode("/episodedetails/episode")!.InnerText);
        Assert.Equal("12", doc.SelectSingleNode("/episodedetails/episodenumberend")!.InnerText);
        Assert.Equal("2011-03-30", doc.SelectSingleNode("/episodedetails/aired")!.InnerText);
        Assert.Equal("7.9", doc.SelectSingleNode("/episodedetails/rating")!.InnerText);
        Assert.Equal(2, doc.SelectNodes("/episodedetails/tag")!.Count);
        foreach (var forbidden in new[] { "uniqueid", "<actor", "<director", "<credits", "<runtime", "<thumb", "<fileinfo", "<lockdata", "http:" })
        {
            Assert.DoesNotContain(forbidden, xml, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(episode.PeerItemId.ToString("N"), xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSeasonNfo_CarriesTheNumber_AndATitleOnlyWhenItIsNotTheDefault()
    {
        var tree = FakeTvTree.Generate(1, 2, 1);
        tree.Seasons[1].Name = "The Second One";

        var plain = Sanitizer.Season(Dto(tree, tree.Seasons[0].Id))!;
        var named = Sanitizer.Season(Dto(tree, tree.Seasons[1].Id))!;

        Assert.Equal(string.Empty, plain.Name);
        Assert.Equal("The Second One", named.Name);
        Assert.Null(Parse(Nfo.Season(plain, Tags)).SelectSingleNode("/season/title"));
        Assert.Equal("The Second One", Parse(Nfo.Season(named, Tags)).SelectSingleNode("/season/title")!.InnerText);
        Assert.Equal("2", Parse(Nfo.Season(named, Tags)).SelectSingleNode("/season/seasonnumber")!.InnerText);
    }

    // --- T-TV-2: a fuzz of 10,000 hostile titles

    [Fact]
    public void T_TV_2_TenThousandHostileTitles_StayInsideTheRoot_AndGiveValidXml()
    {
        using var dir = new TempDirectory();
        var random = new Random(20261015);
        var pieces = new[] { "..", ".", "/", "\\", "\0", "‮", "⁦", "\u0001", "\n", "\t", "CON", "NUL", "<", ">", "&", "\"", "'", "]]>", "<!--", "%00", "~", " ", "é", "😀", "\ud800", "http://x", "S01E01", "24", "漢字", "A", "z" };
        var taken = new HashSet<string>();
        for (var i = 0; i < 10_000; i++)
        {
            var title = string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => pieces[random.Next(pieces.Length)]));
            var dto = new BaseItemDto { Id = Guid.NewGuid(), Name = title, OriginalTitle = title, Overview = title, ProductionYear = random.Next(1800, 2200), Genres = [title], ProviderIds = new Dictionary<string, string> { ["Tvdb"] = title } };
            var series = Sanitizer.Series(dto)!;
            var folder = Layout.SeriesFolder(series, "k" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), taken);
            taken.Add(PathSanitizer.CollisionKey(folder));

            Assert.True(PathSanitizer.TryConfine(dir.Path, folder, out var full), "outside the root: " + folder);
            Assert.Equal(dir.Path.TrimEnd('/', '\\'), System.IO.Path.GetDirectoryName(full)!.TrimEnd('/', '\\'));
            Assert.NotEqual(".", folder);
            Assert.NotEqual("..", folder);
            Parse(Nfo.Series(series, Tags));
            var episode = Sanitizer.Episode(new BaseItemDto { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), Name = title, Overview = title, ParentIndexNumber = 1, IndexNumber = i % 50 });
            Parse(Nfo.Episode(episode.Episode!, Tags));
        }
    }

    // --- moves

    private static CatalogEntry SeriesEntry(string key, string title, int? year, Dictionary<string, string>? ids = null) =>
        new() { Kind = EntryKind.Series, StubKey = key, Title = title, Year = year, ProviderIds = ids ?? [] };

    private static SanitizedSeries NewSeries(FakeTvTree tree, int index, string? name = null, Dictionary<string, string>? ids = null, int? year = 2008)
    {
        tree.Series[index].Name = name ?? tree.Series[index].Name;
        tree.Series[index].ProviderIds = ids ?? [];
        tree.Series[index].Year = year;
        return Series(tree, index);
    }

    [Fact]
    public void AReimportedSeries_IsMatchedByItsProviderId()
    {
        var tree = FakeTvTree.Generate(2, 1, 1);
        var moved = NewSeries(tree, 0, ids: new() { ["Tvdb"] = "81189" });
        var other = NewSeries(tree, 1, ids: new() { ["Tvdb"] = "5" });

        var moves = Layout.MatchSeriesMoves([SeriesEntry("keyaaaaaaaaaaaaa", "Breaking Bad", 2008, new() { ["Tvdb"] = "81189" })], [moved, other]);

        var move = Assert.Single(moves);
        Assert.Equal(("keyaaaaaaaaaaaaa", moved.PeerItemId), (move.StubKey, move.NewPeerItemId));
    }

    [Fact]
    public void WithoutProviderIds_ATitleAndYearMatch_MustBeUniqueOnBothSides()
    {
        var tree = FakeTvTree.Generate(3, 1, 1);
        var a = NewSeries(tree, 0, "Show");
        var b = NewSeries(tree, 1, "SHOW");
        var c = NewSeries(tree, 2, "Other");

        var ambiguousNew = Layout.MatchSeriesMoves([SeriesEntry("keyaaaaaaaaaaaaa", "Show", 2008)], [a, b, c]);
        var ambiguousGone = Layout.MatchSeriesMoves([SeriesEntry("keyaaaaaaaaaaaaa", "Other", 2008), SeriesEntry("keybbbbbbbbbbbbb", "Other", 2008)], [c]);
        var unique = Layout.MatchSeriesMoves([SeriesEntry("keyaaaaaaaaaaaaa", "Other", 2008)], [a, b, c]);
        var otherYear = Layout.MatchSeriesMoves([SeriesEntry("keyaaaaaaaaaaaaa", "Other", 1999)], [c]);

        Assert.Empty(ambiguousNew);
        Assert.Empty(ambiguousGone);
        Assert.Equal(c.PeerItemId, Assert.Single(unique).NewPeerItemId);
        Assert.Empty(otherYear);
    }

    [Fact]
    public void AReimportedEpisode_IsMatchedByItsNumbers_WhenUnique()
    {
        var tree = FakeTvTree.Generate(1, 1, 3);
        var fresh = Episodes(tree);
        CatalogEntry Gone(string key, int number, int? end = null) => new() { Kind = EntryKind.Episode, StubKey = key, Season = 1, Episode = number, EpisodeEnd = end };

        var moves = Layout.MatchEpisodeMoves([Gone("keyaaaaaaaaaaaaa", 2), Gone("keybbbbbbbbbbbbb", 9), Gone("keyccccccccccccc", 3, 3)], fresh);

        Assert.Equal(["keyaaaaaaaaaaaaa", "keyccccccccccccc"], moves.Select(static m => m.StubKey));
        Assert.Equal(fresh.Single(static e => e.Number == 2).PeerItemId, moves[0].NewPeerItemId);
    }

    // --- dedup keys

    [Fact]
    public void ASeriesIsADuplicate_ByAnyStrongKey_OrByTitleAndYearWhenItHasNoId()
    {
        var tree = FakeTvTree.Generate(3, 1, 1);
        var withId = NewSeries(tree, 0, "Breaking Bad", new() { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" });
        var noId = NewSeries(tree, 1, "Breaking Bad");
        var different = NewSeries(tree, 2, "Better Call Saul", new() { ["Tvdb"] = "273181" });
        var local = new HashSet<string>(TvDedup.LocalKeys(new CanonicalIds(null, 81189, null), "Breaking Bad", 2008));
        var localNoId = new HashSet<string>(TvDedup.LocalKeys(default, "breaking bad", 2008));

        Assert.True(TvDedup.IsDuplicateLocal(withId, local));
        Assert.True(TvDedup.IsDuplicateLocal(noId, local)); // the weak key of a series without ids
        Assert.False(TvDedup.IsDuplicateLocal(different, local));
        Assert.False(TvDedup.IsDuplicateLocal(withId, localNoId)); // a series with ids is not matched by its title alone
        Assert.True(TvDedup.IsDuplicateLocal(noId, localNoId));
        Assert.Equal(["imdb:tt0903747", "tvdb:81189"], TvDedup.Keys(withId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheRegistration_ReplacesTheDefaults()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IRatingTable>(new Ratings());
        Jellyfin.Plugin.Shoal.Registrations.Tv.AddShoalTv(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TvSanitizer>(provider.GetService(typeof(ITvSanitizer)));
        Assert.IsType<TvLayout>(provider.GetService(typeof(ITvLayout)));
        Assert.IsType<TvNfoWriter>(provider.GetService(typeof(ITvNfoWriter)));
        Assert.IsType<NoTv>(provider.GetService(typeof(ITvPlanner)));
    }
}
