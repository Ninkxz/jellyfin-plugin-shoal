using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K36, the TV sync engine (DESIGN v2.7.2). T-TV-3 (add, change, remove, move, renumber, rebind, partial listing, wrong SeriesId, virtual episode, caps), T-TV-4 (the guards
/// count episodes) and T-TV-5 (a 5,000 episode tree). The sanitizer, layout and NFO writer are the stand-ins of <c>TestTv.cs</c>; the peer is <see cref="FakeLibraryPeer"/> with a
/// <see cref="FakeTvTree"/>.
/// </summary>
public sealed class TvSyncTests
{
    private static async Task<SyncRig> RigAsync(FakeTvTree tree)
    {
        var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = tree;
        await rig.AddTvMappingAsync();
        return rig;
    }

    private static IEnumerable<CatalogEntry> Kind(SyncRig rig, EntryKind kind) => rig.Entries.Where(e => e.Kind == kind);

    private static string SeriesFolder(SyncRig rig, int series = 0) => Path.Combine(rig.TvFolder, Kind(rig, EntryKind.Series).First(s => s.Title == "Show " + series.ToString(CultureInfo.InvariantCulture)).FolderRel!);

    private static int Strms(SyncRig rig) => Directory.Exists(rig.TvFolder) ? Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count() : 0;

    // ---------------- T-TV-3: the sync ----------------

    [Fact]
    public async Task ATvLibrary_IsAddedAsSeriesSeasonsAndEpisodes_InTheWrittenOrder_WithOneScanForTheFirstSyncOnly()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(series: 2, seasonsPerSeries: 2, episodesPerSeason: 3));

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, report.Result);
        Assert.Equal(12, report.Added);
        Assert.Equal((2, 4, 12), (Kind(rig, EntryKind.Series).Count(), Kind(rig, EntryKind.Season).Count(), Kind(rig, EntryKind.Episode).Count()));
        var show = SeriesFolder(rig, 0);
        Assert.True(File.Exists(Path.Combine(show, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(show, "Season 01", "season.nfo")));
        Assert.True(File.Exists(Path.Combine(show, "Season 02", "S02E03.nfo")));
        var strm = File.ReadAllText(Path.Combine(show, "Season 01", "S01E02.strm"));
        Assert.True(StubSentinel.TryParse(strm, out var key));
        var episode = Kind(rig, EntryKind.Episode).Single(e => e.StubKey == key);
        Assert.Equal((1, 2), (episode.Season!.Value, episode.Episode!.Value));
        Assert.EndsWith("/Season 01/S01E02.strm", episode.StubRelPath, StringComparison.Ordinal);
        Assert.DoesNotContain("http", File.ReadAllText(Path.Combine(show, "Season 01", "S01E02.nfo")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rig.Refresh.Scans); // the first stubs of a library need one scan to show up (as for movies)
        Assert.All(rig.Refresh.Reported, p => Assert.StartsWith(rig.TvFolder, p, StringComparison.Ordinal)); // only the TV library refreshes
        Assert.NotEmpty(rig.Refresh.Reported);
        Assert.Equal(12, rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).LastCount);
        Assert.True(rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).CompletedOnce);
        await rig.SyncAsync();
        Assert.Equal(1, rig.Refresh.Scans); // a later sync queues none
    }

    [Fact]
    public async Task WithTvShowsOff_TheTvMappingIsLeftAlone()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 2));
        rig.TvEnabled = false;

        var report = await rig.SyncAsync();

        Assert.Equal(0, report.Added);
        Assert.Empty(Kind(rig, EntryKind.Episode));
        Assert.False(Directory.Exists(rig.TvFolder));
    }

    [Fact]
    public async Task AChangedEpisode_RewritesItsNfo_ButAnEtagStormRewritesNothing()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 3));
        await rig.SyncAsync();
        var nfo = Path.Combine(SeriesFolder(rig), "Season 01", "S01E02.nfo");
        var before = File.GetLastWriteTimeUtc(nfo);
        await Task.Delay(30);

        foreach (var e in rig.Peer.Tv!.Episodes)
        {
            e.Etag = "storm"; // the peer bumped every etag; nothing changed in the content
        }

        var storm = await rig.SyncAsync();
        Assert.Equal(0, storm.Updated);
        Assert.Equal(before, File.GetLastWriteTimeUtc(nfo));
        Assert.All(Kind(rig, EntryKind.Episode), e => Assert.Equal("storm", e.PeerEtag));

        rig.Peer.Tv.Episodes[1].Name = "A new title";
        rig.Peer.Tv.Episodes[1].Etag = "changed";
        var changed = await rig.SyncAsync();

        Assert.Equal(1, changed.Updated);
        Assert.Contains("A new title", File.ReadAllText(nfo), StringComparison.Ordinal);
        Assert.Equal("A new title", Kind(rig, EntryKind.Episode).Single(e => e.Episode == 2).Title);
    }

    [Fact]
    public async Task ARemovedEpisode_IsDeleted_AndAnEmptySeasonAndSeriesGoWithTheirLastEpisode()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(2, 2, 2));
        await rig.SyncAsync();
        var s1 = SeriesFolder(rig, 1);

        rig.Peer.Tv!.Episodes.RemoveAll(e => e.SeriesId == rig.Peer.Tv.Series[1].Id && e.Season == 2);
        rig.Peer.Tv.Seasons.RemoveAll(s => s.SeriesId == rig.Peer.Tv.Series[1].Id && s.Number == 2);
        var seasonGone = await rig.SyncAsync();
        Assert.Equal(2, seasonGone.Removed);
        Assert.False(Directory.Exists(Path.Combine(s1, "Season 02")));
        Assert.True(Directory.Exists(Path.Combine(s1, "Season 01")));

        rig.Peer.Tv.Episodes.RemoveAll(e => e.SeriesId == rig.Peer.Tv.Series[1].Id);
        rig.Peer.Tv.Seasons.RemoveAll(s => s.SeriesId == rig.Peer.Tv.Series[1].Id);
        rig.Peer.Tv.Series.RemoveAt(1);
        await rig.SyncAsync();

        Assert.False(Directory.Exists(s1));
        Assert.Single(Kind(rig, EntryKind.Series));
        Assert.Equal(4, Kind(rig, EntryKind.Episode).Count());
        Assert.True(Directory.Exists(SeriesFolder(rig, 0)));
    }

    [Fact]
    public async Task AReimportedLibrary_RebindsInPlace_TheStubsTheKeysAndTheFoldersStay()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(3, 2, 4));
        await rig.SyncAsync();
        var keys = Kind(rig, EntryKind.Episode).ToDictionary(e => (e.Season, e.Episode, e.ParentKey), e => e.StubKey);
        var files = rig.AllStubFiles().Where(f => f.EndsWith(".strm", StringComparison.Ordinal)).Select(File.ReadAllText).OrderBy(static s => s, StringComparer.Ordinal).ToArray();

        // The peer re-imported everything: every id is new, the numbers and provider ids are the same.
        var tree = rig.Peer.Tv!;
        var seriesMap = tree.Series.ToDictionary(s => s.Id, s => Guid.NewGuid());
        foreach (var e in tree.Episodes)
        {
            e.Id = Guid.NewGuid();
            e.SeriesId = seriesMap[e.SeriesId];
        }

        foreach (var s in tree.Seasons)
        {
            s.Id = Guid.NewGuid();
            s.SeriesId = seriesMap[s.SeriesId];
        }

        foreach (var s in tree.Series)
        {
            s.Id = seriesMap[s.Id];
        }

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, report.Result); // no guard tripped
        Assert.Equal((0, 0), (report.Added, report.Removed));
        Assert.True(report.Moved >= 3);
        Assert.Equal(keys, Kind(rig, EntryKind.Episode).ToDictionary(e => (e.Season, e.Episode, e.ParentKey), e => e.StubKey));
        Assert.Equal(files, rig.AllStubFiles().Where(f => f.EndsWith(".strm", StringComparison.Ordinal)).Select(File.ReadAllText).OrderBy(static s => s, StringComparer.Ordinal).ToArray());
        Assert.All(Kind(rig, EntryKind.Episode), e => Assert.Contains(tree.Episodes, x => x.Id.ToString("N") == e.PeerItemId));
        Assert.All(Kind(rig, EntryKind.Episode), e => Assert.Contains(tree.Series, x => x.Id.ToString("N") == e.PeerSeriesId));
    }

    [Fact]
    public async Task ARenumberedEpisode_IsOneGoneAndOneNew_WithANewStub()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 3));
        await rig.SyncAsync();
        var old = Kind(rig, EntryKind.Episode).Single(e => e.Episode == 3);
        var oldPath = Path.Combine(rig.TvFolder, old.StubRelPath!);

        rig.Peer.Tv!.Episodes[2].Number = 7;
        rig.Peer.Tv.Episodes[2].Etag = "renumbered";
        var report = await rig.SyncAsync();

        Assert.False(File.Exists(oldPath));
        var now = Kind(rig, EntryKind.Episode).Single(e => e.PeerItemId == old.PeerItemId);
        Assert.Equal(7, now.Episode);
        Assert.NotEqual(old.StubKey, now.StubKey);
        Assert.True(File.Exists(Path.Combine(rig.TvFolder, now.StubRelPath!)));
        Assert.Contains("S01E07", now.StubRelPath, StringComparison.Ordinal);
        Assert.Equal((1, 1), (report.Added, report.Removed));
        Assert.Equal(3, Kind(rig, EntryKind.Episode).Count());
    }

    [Fact]
    public async Task APartialEpisodeListing_RemovesNothingAnywhere_ButAddsWhatItSaw()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 5));
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        tree.Episodes.RemoveRange(0, 2); // two gone, and a total that says there are many more
        tree.TotalOverride = kind => kind == "Episode" ? 400 : tree.Series.Count + tree.Seasons.Count;

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Partial, report.Result);
        Assert.Equal(0, report.Removed);
        Assert.Equal(5, Kind(rig, EntryKind.Episode).Count());
        Assert.Equal(5, Strms(rig));
    }

    [Fact]
    public async Task AnEpisodeOfAnUnlistedSeries_AndAVirtualEpisode_AreSkippedAndCounted()
    {
        await using var rig = await RigAsync(FakeTvFixtures.Build(11));

        var report = await rig.SyncAsync();

        Assert.Equal(2, Kind(rig, EntryKind.Episode).Count()); // the two plain episodes of Show 0
        var library = rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey);
        Assert.Equal(1, library.DroppedOrphans);
        Assert.Contains(SyncAlert.TvSkipped, report.Alerts);
        Assert.Equal(2, Strms(rig));
    }

    [Fact]
    public async Task AnEpisodeWithoutAUsableNumber_IsSkippedAndCounted_WhateverThePeerSays()
    {
        await using var rig = await RigAsync(FakeTvFixtures.Build(7));

        await rig.SyncAsync();

        Assert.Equal(2, Kind(rig, EntryKind.Episode).Count());
        Assert.Equal(2, rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).SkippedNoNumber);
    }

    [Fact]
    public async Task EqualAndOverlappingNumbers_KeepOneStub_AndCountTheRest()
    {
        await using var rig = await RigAsync(FakeTvFixtures.Build(12));

        await rig.SyncAsync();

        var numbers = Kind(rig, EntryKind.Episode).Select(e => (e.Season!.Value, e.Episode!.Value, e.EpisodeEnd)).OrderBy(static t => t.Item2).ToArray();
        Assert.Equal(new (int, int, int?)[] { (1, 1, null), (1, 2, null), (1, 3, null), (1, 10, 12) }, numbers); // "Inside the range" (11) lost to 10-12; the second 3 lost to the first
        Assert.Equal(2, rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).SkippedDuplicate);
        Assert.Contains(rig.AllStubFiles(), f => f.EndsWith("S01E10-E12.strm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCaps_StopTheListing_BeforeTheStore()
    {
        await using var rig = await RigAsync(FakeTvFixtures.Build(9)); // 250 seasons of one episode
        var report = await rig.SyncAsync();

        Assert.Equal(TvLimits.MaxSeasonsPerSeries, Kind(rig, EntryKind.Season).Count());
        Assert.Equal(1, report.Alerts.Count(static a => a == SyncAlert.TvCaps));
        Assert.Equal(250 - TvLimits.MaxSeasonsPerSeries + 1, rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).DroppedOverCap + 1); // 50 seasons dropped, one episode each
    }

    [Fact]
    public async Task ASeasonOf3000Episodes_KeepsTheFirst2000()
    {
        await using var rig = await RigAsync(FakeTvFixtures.Build(10));

        await rig.SyncAsync();

        Assert.Equal(TvLimits.MaxEpisodesPerSeason, Kind(rig, EntryKind.Episode).Count(static e => e.Season == 1));
    }

    [Fact]
    public async Task ASeriesHomeHas_IsHiddenAsAWhole_AndComesBackWhenHomeLosesIt()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(2, 1, 3));
        rig.LocalSeries.Series.Add(new LocalSeries(new CanonicalIds(null, 100_000, null), "Show 0", 2010));

        var first = await rig.SyncAsync();

        Assert.Equal(3, first.Added); // only Show 1
        var hidden = Kind(rig, EntryKind.Series).Single(s => s.State == EntryState.DuplicateLocal);
        Assert.Equal("Show 0", hidden.Title);
        Assert.Null(hidden.FolderRel);
        Assert.Equal(3, Strms(rig));

        rig.LocalSeries.Series.Clear();
        var second = await rig.SyncAsync();

        Assert.Equal(3, second.Added);
        Assert.Equal(6, Strms(rig));
        Assert.DoesNotContain(Kind(rig, EntryKind.Series), static s => s.State == EntryState.DuplicateLocal);

        rig.LocalSeries.Series.Add(new LocalSeries(new CanonicalIds(null, 100_000, null), "Show 0", 2010));
        await rig.SyncAsync();
        Assert.Equal(3, Strms(rig)); // Home has it again: the peer's copy is demoted like a movie
    }

    [Fact]
    public async Task ACrashInTheMiddleOfAnEpisode_LeavesNoStrmWithoutItsNfo()
    {
        await using var rig = await SyncRig.CreateAsync();
        var writer = new TvStubWriter(rig.Log);
        var target = new StubTarget(rig.StubRoot, rig.TvFolder, rig.Endpoint);
        Directory.CreateDirectory(rig.TvFolder);
        var quota = new DiskQuota(1 << 20);
        Assert.Equal(StubWriteOutcome.Written, writer.WriteSeries(target, "Show", "<tvshow/>", quota).Outcome);
        Assert.Equal(StubWriteOutcome.Written, writer.WriteSeason(target, "Show", "Season 01", "<season/>", quota).Outcome);
        Directory.CreateDirectory(Path.Combine(rig.TvFolder, "Show", "Season 01", "S01E01.nfo")); // the NFO cannot be written: a directory sits in its way
        var used = quota.UsedBytes;

        var result = writer.WriteEpisode(target, "Show", "Season 01", "S01E01", "<episodedetails/>", "abcdefghijklmnop", quota);

        Assert.Equal(StubWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(rig.TvFolder, "Show", "Season 01", "S01E01.strm"))); // the .strm comes last
        Assert.Equal(used, quota.UsedBytes); // the reserved bytes were given back
        Assert.Equal(StubWriteOutcome.Written, writer.WriteEpisode(target, "Show", "Season 01", "S01E02", "<episodedetails/>", "abcdefghijklmnoq", quota).Outcome);
        foreach (var strm in Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories))
        {
            Assert.True(File.Exists(Path.ChangeExtension(strm, ".nfo")));
        }
    }

    [Fact]
    public async Task TheWriter_RefusesAPathThatLeavesTheLibrary_OrIsNotMadeOfIntegers()
    {
        await using var rig = await SyncRig.CreateAsync();
        var writer = new TvStubWriter(rig.Log);
        var target = new StubTarget(rig.StubRoot, rig.TvFolder, rig.Endpoint);
        Directory.CreateDirectory(rig.TvFolder);
        var quota = new DiskQuota(1 << 20);

        Assert.Equal(StubWriteOutcome.Unsafe, writer.WriteSeries(target, "..", "<x/>", quota).Outcome);
        Assert.Equal(StubWriteOutcome.Unsafe, writer.WriteSeries(target, "a/b", "<x/>", quota).Outcome);
        Assert.Equal(StubWriteOutcome.Written, writer.WriteSeries(target, "Show", "<x/>", quota).Outcome);
        Assert.Equal(StubWriteOutcome.Unsafe, writer.WriteEpisode(target, "Show", "..", "S01E01", "<x/>", "abcdefghijklmnop", quota).Outcome);
        Assert.Equal(StubWriteOutcome.Unsafe, writer.WriteEpisode(target, "Show", "Season 01", "evil name", "<x/>", "abcdefghijklmnop", quota).Outcome);
        Assert.False(writer.RemoveEpisode(target, "Show/../../x.strm", quota));
        Assert.False(writer.RemoveContainerIfEmpty(target, "..", isSeries: true, quota));
    }

    [Fact]
    public async Task EveryStubKeyAndPath_StaysInsideTheLibraryFolder_ForHostileFixtures()
    {
        foreach (var number in new[] { 2, 3, 4, 5, 6 })
        {
            await using var rig = await RigAsync(FakeTvFixtures.Build(number));
            await rig.SyncAsync();
            var root = Path.GetFullPath(rig.StubRoot);
            Assert.All(rig.AllStubFiles(), f => Assert.StartsWith(root, Path.GetFullPath(f), StringComparison.Ordinal));
            Assert.All(Kind(rig, EntryKind.Episode), e => Assert.True(CatalogStore.IsSafeEpisodePath(e.StubRelPath)));
        }
    }

    // ---------------- T-TV-4: the guards count episodes ----------------

    [Fact]
    public async Task OneHundredFiftyGoneEpisodes_AreHeld_AndReleaseRemovesThem()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(10, 3, 10)); // 300 episodes
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        var gone = tree.Episodes.Take(150).Select(static e => e.Id).ToHashSet();
        tree.Episodes.RemoveAll(e => gone.Contains(e.Id));

        var held = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, held.Result);
        Assert.Equal(0, held.Removed);
        Assert.Equal(150, held.HeldRemovals);
        Assert.Contains(SyncAlert.HeldRemovals, held.Alerts);
        Assert.Equal(300, Strms(rig));
        Assert.Equal(150, rig.Catalog().Entries.Count(static e => e.State == EntryState.HeldRemoval));

        var released = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Removals);

        Assert.Equal(150, released.Removed);
        Assert.Equal(150, Strms(rig));
        Assert.DoesNotContain(rig.Catalog().Entries, static e => e.State == EntryState.HeldRemoval);
    }

    [Fact]
    public async Task ASmallRemoval_BelowTheEpisodeFloor_IsNotHeld()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(10, 3, 10));
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        var gone = tree.Episodes.Take(90).Select(static e => e.Id).ToHashSet(); // under 4 x 25 = 100 and under 10% of 300 = 30? no: the larger of the two counts, 100
        tree.Episodes.RemoveAll(e => gone.Contains(e.Id));

        var report = await rig.SyncAsync();

        Assert.Equal(90, report.Removed);
        Assert.Equal(SyncResultKind.Ok, report.Result);
    }

    [Fact]
    public async Task FourGoneSeriesOfTwenty_AreHeld_EvenWhenEachHadFewEpisodes()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(20, 1, 2));
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        foreach (var series in tree.Series.Take(4).ToList())
        {
            tree.Episodes.RemoveAll(e => e.SeriesId == series.Id);
            tree.Seasons.RemoveAll(s => s.SeriesId == series.Id);
            tree.Series.Remove(series);
        }

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, report.Result);
        Assert.Equal(0, report.Removed);
        Assert.Equal(40, Strms(rig));
    }

    [Fact]
    public async Task AMappingThatReturnsNoEpisodes_WhileTheCatalogHasMany_HoldsEverything()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(3, 1, 4));
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        tree.Episodes.Clear();
        tree.Seasons.Clear();
        tree.Series.Clear();

        var report = await rig.SyncAsync();

        Assert.Equal(0, report.Removed);
        Assert.Equal(12, Strms(rig));
        Assert.Equal(SyncResultKind.Held, report.Result);
    }

    [Fact]
    public async Task AdditionsAbove2000Episodes_AreHeld_AndTheFirstSyncOfAMappingIsExempt()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 5));
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        var big = FakeTvTree.Generate(2, 1, 1_001); // 2,002 new episodes in 2 new series (the ids differ from the ones above by the series number)
        foreach (var s in big.Series)
        {
            s.Id = FakeTvTree.IdOf('S', 100 + big.Series.IndexOf(s));
            s.Name = "Big " + big.Series.IndexOf(s).ToString(CultureInfo.InvariantCulture);
            s.ProviderIds = new Dictionary<string, string> { ["Tvdb"] = (900_000 + big.Series.IndexOf(s)).ToString(CultureInfo.InvariantCulture) };
        }

        for (var s = 0; s < 2; s++)
        {
            var show = big.Series[s];
            var season = new FakeTvSeason { Id = FakeTvTree.IdOf('N', 100 + s, 1), SeriesId = show.Id, Number = 1 };
            tree.Seasons.Add(season);
            for (var e = 1; e <= 1_001; e++)
            {
                tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('E', 100 + s, 1, e), SeriesId = show.Id, SeasonId = season.Id, Season = 1, Number = e, Name = "Bulk " + e.ToString(CultureInfo.InvariantCulture) });
            }

            tree.Series.Add(show);
        }

        var held = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, held.Result);
        Assert.Equal(0, held.Added);
        Assert.Contains(SyncAlert.HeldAdditions, held.Alerts);
        Assert.Equal(5, Strms(rig));
        Assert.True(held.HeldAdditions >= 2002);

        var released = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Additions);

        Assert.Equal(2_002, released.Added);
        Assert.Equal(2_007, Strms(rig));
        Assert.Empty(rig.Catalog().HeldAdditions);
    }

    [Fact]
    public async Task ANewMappingOfAPeerThatAlreadySyncedMovies_IsExemptFromTheAdditionsGuardOnItsFirstSync()
    {
        await using var rig = await SyncRig.CreateAsync(peer => peer.Add("Heat", 1995, tmdb: 949));
        await rig.SyncAsync(); // the peer has synced once; the document says so
        rig.Peer.Tv = FakeTvTree.Generate(1, 2, 1_050); // 2,100 episodes: would trip the guard if it were not the first sync of its mapping
        await rig.AddTvMappingAsync();

        var report = await rig.SyncAsync();

        Assert.NotEqual(SyncResultKind.Held, report.Result);
        Assert.Equal(2_100, report.Added);
        Assert.Equal(2_100, Strms(rig));
    }

    [Fact]
    public async Task TheMovieSync_IsUntouched_BesideATvLibrary()
    {
        await using var rig = await SyncRig.CreateAsync(peer =>
        {
            peer.Add("Heat", 1995, tmdb: 949);
            peer.Add("Alien", 1979, tmdb: 348);
        });
        rig.Peer.Tv = FakeTvTree.Generate(1, 1, 3);
        await rig.AddTvMappingAsync();

        var report = await rig.SyncAsync();

        Assert.Equal(5, report.Added);
        Assert.Equal(2, rig.Entries.Count(static e => e.Kind == EntryKind.Movie));
        Assert.Equal(3, rig.Entries.Count(static e => e.Kind == EntryKind.Episode));
        Assert.Equal(2, Directory.EnumerateFiles(rig.LibraryFolder, "*.strm", SearchOption.AllDirectories).Count());
        Assert.Equal(2, rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.MappingKey).LastCount);
    }

    // ---------------- T-TV-5: scale ----------------

    [Fact]
    public async Task A5000EpisodeLibrary_SyncsWithinFourMinutes_AndTheCatalogStaysSmall()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(25, 10, 20)); // 5,000 episodes
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var clock = Stopwatch.StartNew();

        var report = await rig.SyncAsync();

        clock.Stop();
        Assert.Equal(5_000, report.Added);
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(4), "the first sync took " + clock.Elapsed);
        var path = Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog.json");
        var size = new FileInfo(path).Length;
        var saveClock = Stopwatch.StartNew();
        rig.Catalogs.Save(rig.Catalog());
        saveClock.Stop();
        var grown = GC.GetTotalMemory(forceFullCollection: true) - before;

        // Measured, not guessed (DESIGN v2.7.2): a save over 1 s or a file over 15 MB would call for one file per mapping.
        Assert.True(size < 15 * 1024 * 1024, "catalog.json is " + size + " bytes");
        Assert.True(saveClock.Elapsed < TimeSpan.FromSeconds(1), "a save took " + saveClock.Elapsed);
        Assert.True(grown < 200L * 1024 * 1024, "memory grew by " + grown);
        var again = await rig.SyncAsync();
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed)); // nothing changed, nothing is rewritten
        Assert.Equal(5_000, Strms(rig));
    }
}
