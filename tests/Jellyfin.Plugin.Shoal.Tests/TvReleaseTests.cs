using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Release by series (DESIGN v2.7.2: "Release works by series group"): the guards of a TV library hold episodes, the admin sees the held series and lets one through,
/// and the others stay held.
/// </summary>
public sealed class TvReleaseTests
{
    private static async Task<SyncRig> RigAsync(FakeTvTree tree)
    {
        var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = tree;
        await rig.AddTvMappingAsync();
        return rig;
    }

    private static int Strms(SyncRig rig) => Directory.Exists(rig.TvFolder) ? Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count() : 0;

    private static AdminHoldsController Controller(SyncRig rig) => new(rig.State, rig.Catalogs);

    private static JsonElement Groups(SyncRig rig, string kind) => AdminTestSupport.Body(Controller(rig).Holds(SyncRig.PeerKey)).GetProperty(kind);

    private static void AddBigSeries(FakeTvTree tree, int count, int episodes)
    {
        for (var s = 0; s < count; s++)
        {
            var show = new FakeTvSeries
            {
                Id = FakeTvTree.IdOf('S', 100 + s),
                Name = "Big " + s.ToString(CultureInfo.InvariantCulture),
                ProviderIds = new Dictionary<string, string> { ["Tvdb"] = (900_000 + s).ToString(CultureInfo.InvariantCulture) },
            };
            tree.Series.Add(show);
            var season = new FakeTvSeason { Id = FakeTvTree.IdOf('N', 100 + s, 1), SeriesId = show.Id, Number = 1 };
            tree.Seasons.Add(season);
            for (var e = 1; e <= episodes; e++)
            {
                tree.Episodes.Add(new FakeTvEpisode { Id = FakeTvTree.IdOf('E', 100 + s, 1, e), SeriesId = show.Id, SeasonId = season.Id, Season = 1, Number = e, Name = "Bulk " + e.ToString(CultureInfo.InvariantCulture) });
            }
        }
    }

    [Fact]
    public async Task HeldRemovals_AreListedBySeries_AndOneSeriesCanBeReleasedAlone()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(10, 3, 10)); // 300 episodes
        await rig.SyncAsync();
        var tree = rig.Peer.Tv!;
        var goneSeries = tree.Series.Take(5).Select(static s => s.Id).ToHashSet();
        tree.Episodes.RemoveAll(e => goneSeries.Contains(e.SeriesId)); // 150 episodes of 5 series
        var held = await rig.SyncAsync();
        Assert.Equal(SyncResultKind.Held, held.Result);
        Assert.Equal(150, held.HeldRemovals);

        var groups = Groups(rig, "removals").EnumerateArray().ToList();
        Assert.Equal(5, groups.Count);
        Assert.All(groups, g => Assert.Equal(30, g.GetProperty("episodes").GetInt32()));
        var text = Groups(rig, "removals").GetRawText();
        Assert.DoesNotContain(tree.Series[0].Id.ToString("N"), text, StringComparison.Ordinal); // no peer id in the answer

        var first = groups[0].GetProperty("series").GetString()!;
        var released = await rig.Sync.ReleaseSeriesAsync(SyncRig.PeerKey, removals: true, first);

        Assert.Equal(30, released.Removed);
        Assert.Equal(270, Strms(rig));
        Assert.Equal(120, rig.Catalog().Entries.Count(static e => e.State == EntryState.HeldRemoval));
        Assert.Equal(4, Groups(rig, "removals").GetArrayLength());
        Assert.Equal(SyncResultKind.Held, released.Result); // the rest is still held
    }

    [Fact]
    public async Task HeldAdditions_AreListedBySeries_AndOneSeriesCanBeReleasedAlone()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 5));
        await rig.SyncAsync();
        AddBigSeries(rig.Peer.Tv!, 2, 1_001);
        var held = await rig.SyncAsync();
        Assert.Equal(SyncResultKind.Held, held.Result);
        Assert.Equal(5, Strms(rig));

        var groups = Groups(rig, "additions").EnumerateArray().OrderBy(static g => g.GetProperty("title").GetString(), StringComparer.Ordinal).ToList();
        Assert.Equal(["Big 0", "Big 1"], groups.Select(static g => g.GetProperty("title").GetString()!).ToArray());
        Assert.All(groups, g => Assert.Equal(1_001, g.GetProperty("episodes").GetInt32()));

        var released = await rig.Sync.ReleaseSeriesAsync(SyncRig.PeerKey, removals: false, groups[0].GetProperty("series").GetString()!);

        Assert.Equal(1_001, released.Added);
        Assert.Equal(1_006, Strms(rig));
        Assert.Equal(["Big 1"], Groups(rig, "additions").EnumerateArray().Select(static g => g.GetProperty("title").GetString()!).ToArray());
        Assert.Contains(rig.Entries, static e => e.Kind == EntryKind.Series && e.Title == "Big 0");
        Assert.DoesNotContain(rig.Entries, static e => e.Kind == EntryKind.Series && e.Title == "Big 1");

        var rest = await rig.Sync.ReleaseSeriesAsync(SyncRig.PeerKey, removals: false, Groups(rig, "additions")[0].GetProperty("series").GetString()!);

        Assert.Equal(1_001, rest.Added);
        Assert.Equal(2_007, Strms(rig));
        Assert.Equal(0, Groups(rig, "additions").GetArrayLength());
        Assert.Empty(rig.Catalog().HeldAdditions);
    }

    [Fact]
    public async Task AKeyThatIsNotAHeldGroup_ReleasesNothing()
    {
        await using var rig = await RigAsync(FakeTvTree.Generate(1, 1, 5));
        await rig.SyncAsync();

        var report = await rig.Sync.ReleaseSeriesAsync(SyncRig.PeerKey, removals: false, "abcdefgh12345678");

        Assert.Equal("notFound", report.Error);
        Assert.Equal(5, Strms(rig));
    }
}
