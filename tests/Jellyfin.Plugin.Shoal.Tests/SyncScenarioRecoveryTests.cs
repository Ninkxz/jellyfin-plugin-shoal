using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed partial class SyncScenarioMatrixTests
{
    [Fact]
    public async Task CrossPeerLostCatalog_GlobalSyncNeverCreatesFiveCollisionCopies()
    {
        await using var home = await RecoveryFixtureAsync();
        var before = RecoveryFiles(home);
        home.ApplyFault(Fault.MissingCatalog, 2);

        var reports = await home.Sync.SyncAllAsync(SyncTrigger.Scheduled);

        Assert.Equal(5, Directory.EnumerateFiles(home.TvFolder, "*.strm", SearchOption.AllDirectories).Count());
        Assert.Equal(before, RecoveryFiles(home));
        Assert.All(reports, report => Assert.Equal("catalogUnreadable", report.Error));
        Assert.True(home.Catalogs.Load(home.Friends[0].Key).Unreadable);
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("manual")]
    [InlineData("additions")]
    [InlineData("removals")]
    [InlineData("both")]
    [InlineData("series")]
    public async Task CrossPeerLostCatalog_HealthyPeerCannotBypassRecovery(string action)
    {
        await using var home = await RecoveryFixtureAsync();
        var healthy = home.Friends[1];
        var seriesKey = home.Catalogs.Load(healthy.Key).Document.Entries.Single(static e => e.Kind == EntryKind.Series).StubKey;
        var catalogPath = Path.Combine(home.Paths.PeerDir(healthy.Key), "catalog.json");
        var catalogBefore = File.ReadAllBytes(catalogPath);
        var filesBefore = RecoveryFiles(home);
        var requestsBefore = healthy.Peer.Requests.Count;
        home.ApplyFault(Fault.MissingCatalog, 2);

        var report = action switch
        {
            "scheduled" => await home.Sync.SyncAsync(healthy.Key, SyncTrigger.Scheduled),
            "manual" => await home.Sync.SyncAsync(healthy.Key, SyncTrigger.Manual),
            "additions" => await home.Sync.ReleaseAsync(healthy.Key, ReleaseKind.Additions),
            "removals" => await home.Sync.ReleaseAsync(healthy.Key, ReleaseKind.Removals),
            "both" => await home.Sync.ReleaseAsync(healthy.Key, ReleaseKind.Both),
            _ => await home.Sync.ReleaseSeriesAsync(healthy.Key, removals: true, seriesKey),
        };

        Assert.Equal("catalogUnreadable", report.Error);
        Assert.Equal(filesBefore, RecoveryFiles(home));
        Assert.Equal(catalogBefore, File.ReadAllBytes(catalogPath));
        Assert.Equal(requestsBefore, healthy.Peer.Requests.Count);
    }

    [Fact]
    public async Task CrossPeerLostCatalog_DisablingUnknownOwnerDoesNotPermitTakeover()
    {
        await using var home = await RecoveryFixtureAsync();
        var before = RecoveryFiles(home);
        home.ApplyFault(Fault.MissingCatalog, 2);
        await home.State.UpdateAsync(state => state.Home.Peers[0].Enabled = false);

        var report = await home.Sync.SyncAsync(home.Friends[1].Key, SyncTrigger.Scheduled);

        Assert.Equal("catalogUnreadable", report.Error);
        Assert.Equal(before, RecoveryFiles(home));
    }

    [Fact]
    public async Task CrossPeerLostCatalog_RestoringOwnerCatalogAllowsHealthySyncAgain()
    {
        await using var home = await RecoveryFixtureAsync();
        var path = Path.Combine(home.Paths.PeerDir(home.Friends[0].Key), "catalog.json");
        var original = File.ReadAllBytes(path);
        var before = RecoveryFiles(home);
        home.ApplyFault(Fault.MissingCatalog, 2);
        Assert.Equal("catalogUnreadable", (await home.Sync.SyncAsync(home.Friends[1].Key, SyncTrigger.Scheduled)).Error);
        File.WriteAllBytes(path, original);
        File.WriteAllBytes(path + ".bak", original);

        var report = await home.Sync.SyncAsync(home.Friends[1].Key, SyncTrigger.Scheduled);

        Assert.Null(report.Error);
        Assert.Equal(SyncResultKind.Ok, report.Result);
        Assert.Equal(before, RecoveryFiles(home));
    }

    [Fact]
    public async Task CrossPeerLostCatalog_FreshUnestablishedPeerStillImportsNormally()
    {
        await using var home = await RecoveryFixtureAsync(initialSyncs: false);
        Assert.False(home.Catalogs.Load(home.Friends[1].Key).Unreadable);

        var report = await home.Sync.SyncAsync(home.Friends[0].Key, SyncTrigger.Scheduled);

        Assert.Null(report.Error);
        Assert.Equal(SyncResultKind.Ok, report.Result);
        Assert.Equal(5, Directory.EnumerateFiles(home.TvFolder, "*.strm", SearchOption.AllDirectories).Count());
    }

    private static async Task<ScenarioHome> RecoveryFixtureAsync(bool initialSyncs = true)
    {
        var home = await ScenarioHome.CreateAsync(2, Layout.SharedMerges);
        foreach (var friend in home.Friends)
        {
            friend.Peer.Movies.Clear();
            var tree = new FakeTvTree { LibraryId = friend.Peer.Tv!.LibraryId };
            var index = friend.Index * 100;
            var series = new FakeTvSeries { Id = FakeTvTree.IdOf('S', index), Name = "Recovery Fixture", Year = 2024, ProviderIds = new() { ["Tvdb"] = "424242" } };
            tree.Series.Add(series);
            foreach (var (seasonNumber, episodeCount) in new[] { (1, 3), (2, 2) })
            {
                var season = new FakeTvSeason { Id = FakeTvTree.IdOf('N', index, seasonNumber), SeriesId = series.Id, Number = seasonNumber };
                tree.Seasons.Add(season);
                for (var episodeNumber = 1; episodeNumber <= episodeCount; episodeNumber++)
                {
                    tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('E', index, seasonNumber, episodeNumber), SeriesId = series.Id, SeasonId = season.Id, Season = seasonNumber, Number = episodeNumber });
                }
            }

            friend.Peer.Tv = tree;
        }

        if (initialSyncs)
        {
            foreach (var friend in home.Friends)
            {
                Assert.Equal(SyncResultKind.Ok, (await home.Sync.SyncAsync(friend.Key, SyncTrigger.Scheduled)).Result);
            }

            Assert.Equal(5, Directory.EnumerateFiles(home.TvFolder, "*.strm", SearchOption.AllDirectories).Count());
            home.Time.Advance(TimeSpan.FromHours(6));
        }

        return home;
    }

    private static IReadOnlyList<string> RecoveryFiles(ScenarioHome home) => Directory.EnumerateFiles(home.StubRoot, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(home.StubRoot, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        .Order(StringComparer.Ordinal).ToArray();
}
