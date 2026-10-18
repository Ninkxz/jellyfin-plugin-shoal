using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 12 M1: the planner and the move matching are linear in what a peer sends, so a hostile peer (or a very large library) cannot hold the global sync lock
/// with quadratic loops.
/// </summary>
public sealed class TvScaleTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(8); // generous: the old loops took minutes at these sizes

    private static Guid Id(int kind, int n) => new((kind << 24) | 0x1000000, 0, 0, 0, 0, 0, 0, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);

    private static TvPlanner Planner() => new(new TvLayout());

    private static TvPlanInput Input(IReadOnlyList<CatalogEntry> existing, IReadOnlyList<SanitizedSeries> series, IReadOnlyList<SanitizedEpisode> episodes) =>
        new("tv-1", existing, series, [], episodes, true, false, new HashSet<string>(), new TvGuardSettings(25, 10, 500, 20), true, true);

    [Fact]
    public void FiftyThousandEpisodes_WithRotatingEtags_PlanInSeconds()
    {
        const int seriesCount = 5;
        const int seasons = 5;
        const int perSeason = 2000;
        var series = new List<SanitizedSeries>();
        var entries = new List<CatalogEntry>();
        var episodes = new List<SanitizedEpisode>();
        for (var s = 0; s < seriesCount; s++)
        {
            var seriesId = Id(1, s);
            series.Add(new SanitizedSeries { PeerItemId = seriesId, Title = "Show " + s.ToString(CultureInfo.InvariantCulture), Year = 2000 + s, Etag = "e0", ContentHash = "h" });
            entries.Add(new CatalogEntry { Kind = EntryKind.Series, StubKey = "series" + s.ToString("0000000000", CultureInfo.InvariantCulture), PeerItemId = seriesId.ToString("N"), State = EntryState.Active, PeerEtag = "e0", ContentHash = "h", LabelVersion = FriendLabel.Version, FolderRel = "Show " + s.ToString(CultureInfo.InvariantCulture) });
            for (var season = 1; season <= seasons; season++)
            {
                for (var number = 1; number <= perSeason; number++)
                {
                    var n = (((s * seasons) + season) * 10_000) + number;
                    episodes.Add(new SanitizedEpisode { PeerItemId = Id(2, n), PeerSeriesId = seriesId, Season = season, Number = number, Etag = "rotated-" + n.ToString(CultureInfo.InvariantCulture), ContentHash = "h" });
                    entries.Add(new CatalogEntry
                    {
                        Kind = EntryKind.Episode,
                        StubKey = "ep" + n.ToString("00000000000000", CultureInfo.InvariantCulture),
                        ParentKey = "series" + s.ToString("0000000000", CultureInfo.InvariantCulture),
                        PeerItemId = Id(2, n).ToString("N"),
                        Season = season,
                        Episode = number,
                        State = EntryState.Active,
                        PeerEtag = "old",
                        ContentHash = "h",
                    });
                }
            }
        }

        var clock = Stopwatch.StartNew();
        var plan = Planner().Plan(Input(entries, series, episodes));

        Assert.True(clock.Elapsed < Fast, "planning took " + clock.Elapsed);
        Assert.Equal(seriesCount * seasons * perSeason, plan.UpdateKeys.Count);
        Assert.Empty(plan.RemoveKeys);
    }

    [Fact]
    public void FiveThousandSeries_ReIdentified_AreMatchedInSeconds()
    {
        const int count = 5000;
        var gone = new List<CatalogEntry>();
        var fresh = new List<SanitizedSeries>();
        for (var i = 0; i < count; i++)
        {
            var title = "Title number " + i.ToString(CultureInfo.InvariantCulture);
            gone.Add(new CatalogEntry { Kind = EntryKind.Series, StubKey = "old" + i.ToString("0000000000000", CultureInfo.InvariantCulture), PeerItemId = Id(3, i).ToString("N"), Title = title, Year = 2000, State = EntryState.Active });
            fresh.Add(new SanitizedSeries { PeerItemId = Id(4, i), Title = title, Year = 2000 });
        }

        var clock = Stopwatch.StartNew();
        var moves = new TvLayout().MatchSeriesMoves(gone, fresh);
        var plan = Planner().Plan(Input(gone, fresh, []));

        Assert.True(clock.Elapsed < Fast, "matching took " + clock.Elapsed);
        Assert.Equal(count, moves.Count);
        Assert.Equal(count, plan.Moves.Count);
    }

    [Fact]
    public void TwoGoneSeriesThatWantTheSameNewSeries_AreNotMoved()
    {
        var gone = new[]
        {
            new CatalogEntry { Kind = EntryKind.Series, StubKey = "olda0000000000", PeerItemId = Id(3, 1).ToString("N"), Title = "Same", Year = 2000, State = EntryState.Active },
            new CatalogEntry { Kind = EntryKind.Series, StubKey = "oldb0000000000", PeerItemId = Id(3, 2).ToString("N"), Title = "Same", Year = 2000, State = EntryState.Active },
        };

        var moves = new TvLayout().MatchSeriesMoves(gone, [new SanitizedSeries { PeerItemId = Id(4, 1), Title = "Same", Year = 2000 }]);

        Assert.Empty(moves);
    }

    [Fact]
    public void AnEpisodeThatIsRenumbered_IsMovedOnlyWhenBothSidesAreUnique()
    {
        var series = Id(1, 1);
        CatalogEntry Old(string key, int number) => new() { Kind = EntryKind.Episode, StubKey = key, PeerItemId = Id(5, number).ToString("N"), Season = 1, Episode = number, State = EntryState.Active };
        SanitizedEpisode New(int id, int number) => new() { PeerItemId = Id(6, id), PeerSeriesId = series, Season = 1, Number = number };

        var moves = new TvLayout().MatchEpisodeMoves([Old("oneoneoneone1", 1), Old("twotwotwotwo2", 2)], [New(1, 1), New(2, 1), New(3, 2)]);

        Assert.Equal(["twotwotwotwo2"], moves.Select(static m => m.StubKey));
        Assert.Equal(Id(6, 3), moves.Single().NewPeerItemId);
    }

    // ---- Warden 12 L3: the deferred refresh

    private static async System.Threading.Tasks.Task<bool> WaitAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !done())
        {
            await System.Threading.Tasks.Task.Delay(10);
        }

        return done();
    }

    [Fact]
    public async System.Threading.Tasks.Task ADeferredRefresh_IsReportedOnce_AfterTheStreamsWait_ByOneTimer()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 2, seasonsPerSeries: 1, episodesPerSeason: 2);
        await rig.AddTvMappingAsync();
        rig.Tv.ActiveStreams = static _ => 1;
        rig.Tv.RefreshDeferral = TimeSpan.FromMilliseconds(300);

        await rig.SyncAsync();
        Assert.Empty(rig.Refresh.Reported); // streams are running: the refresh waits
        Assert.True(await WaitAsync(() => rig.Refresh.Reported.Count > 0));
        var reported = rig.Refresh.Reported.Count;
        await System.Threading.Tasks.Task.Delay(700);

        Assert.Equal(reported, rig.Refresh.Reported.Count); // reported once, not once per timer
    }

    [Fact]
    public async System.Threading.Tasks.Task AFailingDeferredRefresh_IsLogged_NotLostSilently()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 2);
        await rig.AddTvMappingAsync();
        rig.Tv.ActiveStreams = static _ => 1;
        rig.Tv.RefreshDeferral = TimeSpan.FromMilliseconds(100);
        rig.Refresh.Throw = true;

        await rig.SyncAsync();

        Assert.True(await WaitAsync(() => rig.Log.All.Contains("deferred library refresh", StringComparison.Ordinal)));
        Assert.DoesNotContain(SyncRig.PeerKey, rig.Log.All, StringComparison.Ordinal); // the peer key is redacted
    }

    // ---- Warden 12 M2: the playable cap holds across the mappings of a catalog

    [Fact]
    public void ThePlanner_LeavesRoomOnlyForWhatTheOtherMappingsDidNotTake()
    {
        var seriesId = Id(1, 1);
        var series = new[] { new SanitizedSeries { PeerItemId = seriesId, Title = "Show", Year = 2000 } };
        var episodes = Enumerable.Range(1, 100).Select(n => new SanitizedEpisode { PeerItemId = Id(2, n), PeerSeriesId = seriesId, Season = 1, Number = n }).ToList();
        var input = Input([], series, episodes) with { OtherPlayable = CatalogDocument.MaxEntries - 40, FirstSyncOfMapping = true };

        var plan = Planner().Plan(input);

        Assert.Equal(40, plan.AddEpisodes.Count);
        Assert.Contains(nameof(SyncAlert.EntryCap), plan.Alerts);
        Assert.Contains(nameof(SyncAlert.TvCaps), plan.Alerts);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheEngine_StopsAtTheCatalogCap_RaisesEntryCap_AndWritesNothingTheNextLoadWouldDrop()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 5);
        await rig.AddTvMappingAsync();
        var document = rig.Catalog();
        for (var i = 0; i < CatalogDocument.MaxEntries - 2; i++)
        {
            var folder = "Movie " + i.ToString(CultureInfo.InvariantCulture);
            document.Entries.Add(new CatalogEntry
            {
                StubKey = "m" + i.ToString("000000000000000", CultureInfo.InvariantCulture),
                PeerItemId = Id(7, i).ToString("N"),
                MappingKey = "movies-1",
                State = EntryState.Active,
                Title = folder,
                StubRelPath = folder + "/" + folder + ".strm",
            });
        }

        rig.Catalogs.Save(document);

        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.EntryCap, report.Alerts);
        var reloaded = rig.Catalogs.Load(SyncRig.PeerKey);
        Assert.Equal(0, reloaded.Dropped);
        Assert.Equal(2, reloaded.Document.Entries.Count(static e => e.Kind == EntryKind.Episode));
        Assert.Equal(CatalogDocument.MaxEntries, reloaded.Document.Entries.Count(static e => e.Kind is EntryKind.Movie or EntryKind.Episode));
    }
}
