using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K40, the TV lines of the overlap report (DESIGN v2.7 decision 3): folders are not titles, and a series Home already has reports the episodes it hides.
/// </summary>
public sealed class TvOverlapTests
{
    private static CatalogEntry Entry(EntryKind kind, int n, EntryState state, long size = 0, int? peerEpisodes = null) => new()
    {
        Kind = kind,
        StubKey = "tv" + n.ToString("x14", CultureInfo.InvariantCulture),
        PeerItemId = n.ToString("x32", CultureInfo.InvariantCulture),
        MappingKey = "tv-1",
        State = state,
        SizeBytes = size,
        Width = 1920,
        Height = 1080,
        PeerEpisodeCount = peerEpisodes,
        StubRelPath = kind == EntryKind.Movie ? "Movie " + n.ToString(CultureInfo.InvariantCulture) + "/Movie.strm" : null,
    };

    [Fact]
    public void SeriesAndSeasons_AreNotTitles_AndTheHiddenEpisodesAreCountedApart()
    {
        var document = new CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        document.Entries.AddRange(
        [
            Entry(EntryKind.Movie, 1, EntryState.Active, 1000),
            Entry(EntryKind.Series, 2, EntryState.Active),
            Entry(EntryKind.Season, 3, EntryState.Active),
            Entry(EntryKind.Episode, 4, EntryState.Active, 200),
            Entry(EntryKind.Episode, 5, EntryState.Active, 300),
            Entry(EntryKind.Series, 6, EntryState.DuplicateLocal, peerEpisodes: 62),
            Entry(EntryKind.Series, 7, EntryState.DuplicateLocal, peerEpisodes: 8),
        ]);

        var report = OverlapReports.Compute(document, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, report.GainCount); // one movie and two episodes; the series and the season are folders
        Assert.Equal(1500, report.GainBytes);
        Assert.Equal(2, report.GainEpisodes);
        Assert.Equal(1, report.GainSeries);
        Assert.Equal(0, report.OverlapCount); // the movie overlap counts movies and episodes only
        Assert.Equal(2, report.SharedSeries);
        Assert.Equal(70, report.SharedSeriesEpisodes);
    }

    [Fact]
    public async Task TheOverlapAnswer_CarriesTheTvLines_OnlyWhenTheServerHasTv()
    {
        using var rig = new PairingRig();
        await rig.State.UpdateAsync(StateSamples.Fill);
        var catalogs = new CatalogStore(rig.Paths, NullLogger<CatalogStore>.Instance);
        var controller = new AdminOverlapController(rig.State, catalogs);
        var load = catalogs.Load(StateSamples.PeerKeyA);
        load.Document.Entries.Add(Entry(EntryKind.Movie, 1, EntryState.Active, 5));
        load.Document.Report = OverlapReports.Compute(load.Document, DateTime.UtcNow);
        catalogs.Save(load.Document);

        var movies = AdminTestSupport.Body(controller.Overlap(StateSamples.PeerKeyA));
        Assert.True(!movies.TryGetProperty("tv", out var none) || none.ValueKind == System.Text.Json.JsonValueKind.Null);

        load = catalogs.Load(StateSamples.PeerKeyA);
        load.Document.Entries.Add(Entry(EntryKind.Series, 2, EntryState.DuplicateLocal, peerEpisodes: 41));
        load.Document.Report = OverlapReports.Compute(load.Document, DateTime.UtcNow);
        catalogs.Save(load.Document);

        var shows = AdminTestSupport.Body(controller.Overlap(StateSamples.PeerKeyA));
        var tv = shows.GetProperty("tv");
        Assert.Equal(1, tv.GetProperty("sharedSeries").GetInt32());
        Assert.Equal(41, tv.GetProperty("sharedSeriesEpisodes").GetInt32());
        Assert.Equal(1, shows.GetProperty("gain").GetProperty("count").GetInt32()); // the movie only
    }

    [Fact]
    public async Task TheSync_ReportsTheEpisodesAHomeSeriesHides()
    {
        await using var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Tv = FakeTvTree.Generate(series: 2, seasonsPerSeries: 2, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        rig.LocalSeries.Series.Add(new LocalSeries(new CanonicalIds(null, 100_000, null), "Show 0", 2010));

        await rig.SyncAsync();

        var report = rig.Catalog().Report!;
        Assert.Equal(1, report.SharedSeries);
        Assert.Equal(6, report.SharedSeriesEpisodes); // 2 seasons of 3, none of them imported
        Assert.Equal(1, report.GainSeries);
        Assert.Equal(6, report.GainEpisodes);
        Assert.Equal(6, report.GainCount);
        Assert.Equal(0, rig.Entries.Count(static e => e.Kind == EntryKind.Episode && e.PeerSeriesId.Length > 0 && e.State == EntryState.DuplicateLocal));
    }
}
