using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class TvRangeSafetyTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static SanitizedEpisode Episode(int start, int end) => new() { PeerItemId = Guid.NewGuid(), PeerSeriesId = SeriesId, Season = 1, Number = start, NumberEnd = end };

    [Fact]
    public void FiftyThousandCoverageQueries_UsePrecomputedUnion()
    {
        var watch = Stopwatch.StartNew();
        var episodes = Enumerable.Range(0, 50_000).Select(i => new TvOwnedEpisode(i / 5000, i % 5000, i % 5000, "source")).ToArray();
        var coverage = new TvCoverageIndex(episodes);
        var complete = true;
        foreach (var episode in episodes)
        {
            var candidate = new SanitizedEpisode { Season = episode.Season, Number = episode.Number };
            complete &= coverage.Covers(candidate) && coverage.Exact(candidate) is not null;
        }

        Assert.True(complete);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void MatchedOwnerUnion_HasCoverageBeyondFirstOwnersRange()
    {
        var identity = new LocalSeries(new CanonicalIds(null, 1, null), "Show", 2000);
        var series = new SanitizedSeries { PeerItemId = SeriesId, ProviderIds = identity.ProviderIds, Title = identity.Title, Year = identity.Year };
        var first = new TvOwnedSeries("first", "firstseries0001", true, [new TvOwnedEpisode(1, 1, 1, "firstepisode0001")], identity);
        var second = new TvOwnedSeries("second", "secondseries001", true, [new TvOwnedEpisode(1, 2, 2, "secondepisode001")], identity);
        var plan = new TvPlanner(new TvLayout()).Plan(new TvPlanInput("tv-1", [], [series], [], [Episode(1, 2)], true, true, [], new TvGuardSettings(25, 10, 500, 20), true, true, PeerOwned: [first, second]));
        Assert.Empty(plan.HeldAdditionPeerIds);
        Assert.Null(Assert.Single(plan.HiddenEpisodes!).DuplicateOf);
    }

    [Fact]
    public void HiddenLookup_SameStartDifferentRanges_SelectsExactBackup()
    {
        using var dir = new TempDirectory();
        var store = new CatalogStore(new ShoalPaths(dir.Path), NullLogger<CatalogStore>.Instance);
        var series = new CatalogEntry { Kind = EntryKind.Series, StubKey = "series0000000001", PeerItemId = SeriesId.ToString("N"), MappingKey = "tv-1", State = EntryState.DuplicatePeer };
        CatalogEntry Hidden(int end, char id) => new() { Kind = EntryKind.Episode, StubKey = "episode00000000" + id, PeerItemId = new string(id, 32), PeerSeriesId = series.PeerItemId, ParentKey = series.StubKey, MappingKey = "tv-1", Season = 1, Episode = 1, EpisodeEnd = end, State = EntryState.DuplicatePeer };
        var longFile = Hidden(2, 'a');
        var shortFile = Hidden(1, 'b');
        store.Save(new CatalogDocument { PeerKey = StateSamples.PeerKeyA, Entries = [series, longFile, shortFile] });
        var lookup = new StubCatalog(store);
        Assert.Equal(shortFile.PeerItemId, lookup.FindHiddenEpisode(StateSamples.PeerKeyA, series.PeerItemId, 1, 1)!.PeerItemId);
        Assert.Equal(longFile.PeerItemId, lookup.FindHiddenEpisode(StateSamples.PeerKeyA, series.PeerItemId, 1, 1, 2)!.PeerItemId);
        Assert.Null(lookup.FindHiddenEpisode(StateSamples.PeerKeyA, series.PeerItemId, 1, 1, 3));
    }

    [Fact]
    public void SameStart_PrefersContainingFileWithoutLosingTail()
    {
        var shortFile = Episode(1, 1);
        var longFile = Episode(1, 2);
        var result = new TvLayout().Resolve([shortFile, longFile]);
        Assert.Equal(longFile.PeerItemId, Assert.Single(result.Accepted).PeerItemId);
        Assert.Empty(result.Held!);
        Assert.Equal(1, result.DuplicatePeer);
    }

    [Fact]
    public void CrossingFiles_HoldUncoveredRangeInsteadOfDroppingTail()
    {
        var first = Episode(1, 2);
        var crossing = Episode(2, 3);
        var result = new TvLayout().Resolve([first, crossing]);
        Assert.Equal(first.PeerItemId, Assert.Single(result.Accepted).PeerItemId);
        Assert.Equal(crossing.PeerItemId, Assert.Single(result.Held!).PeerItemId);
        Assert.Equal(0, result.DuplicatePeer);
    }

    [Fact]
    public void PartialPeerCoverage_HoldsEvenWhenBothGuardsAreBypassed()
    {
        var candidate = Episode(1, 2);
        var plan = Plan(candidate, new TvOwnedEpisode(1, 1, 1, "owner00000000001"));
        Assert.Empty(plan.AddEpisodes);
        Assert.Empty(plan.HiddenEpisodes!);
        Assert.Contains(candidate.PeerItemId.ToString("N"), plan.HeldAdditionPeerIds);
        Assert.Contains(nameof(SyncAlert.EpisodeRangeConflict), plan.Alerts);
    }

    [Fact]
    public void CompleteUnion_HidesRangeButDoesNotInventInterchangeableBackup()
    {
        var plan = Plan(Episode(1, 2), new TvOwnedEpisode(1, 1, 1, "owner00000000001"), new TvOwnedEpisode(1, 2, 2, "owner00000000002"));
        Assert.Empty(plan.HeldAdditionPeerIds);
        Assert.Null(Assert.Single(plan.HiddenEpisodes!).DuplicateOf);
    }

    [Fact]
    public void RetainedCrossingStub_AlsoBlocksOverlappingNewAddition()
    {
        var retained = Episode(2, 3);
        var incoming = Episode(1, 2);
        var series = new SanitizedSeries { PeerItemId = SeriesId, Title = "Show", Year = 2000 };
        var oldSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "series0000000001", PeerItemId = SeriesId.ToString("N"), MappingKey = "tv-1", FolderRel = "Show", State = EntryState.Active };
        var oldEpisode = new CatalogEntry { Kind = EntryKind.Episode, StubKey = "episode000000001", PeerItemId = retained.PeerItemId.ToString("N"), PeerSeriesId = SeriesId.ToString("N"), ParentKey = oldSeries.StubKey, MappingKey = "tv-1", Season = 1, Episode = 2, EpisodeEnd = 3, StubRelPath = "Show/Season 01/S01E02-E03.strm", State = EntryState.Active };
        var plan = new TvPlanner(new TvLayout()).Plan(new TvPlanInput("tv-1", [oldSeries, oldEpisode], [series], [], [incoming, retained], true, false, [], new TvGuardSettings(25, 10, 500, 20), true, true));
        Assert.Empty(plan.AddEpisodes);
        Assert.DoesNotContain(oldEpisode.StubKey, plan.RemoveKeys);
        Assert.Contains(oldEpisode.StubKey, plan.HeldRemovalKeys);
        Assert.Contains(incoming.PeerItemId.ToString("N"), plan.HeldAdditionPeerIds);
    }

    [Fact]
    public async Task RuntimeCaptureFailure_PreventsOverlappingAdditionAfterPlanning()
    {
        await using var rig = await SyncRig.CreateAsync();
        var tree = FakeTvTree.Generate(1, 1, 1);
        tree.Episodes[0].Number = 2;
        tree.Episodes[0].NumberEnd = 3;
        rig.Peer.Tv = tree;
        await rig.AddTvMappingAsync();
        await rig.SyncAsync();
        var old = Assert.Single(rig.Entries, e => e.Kind == EntryKind.Episode);
        var incoming = tree.Episodes[0];
        incoming.Id = Guid.NewGuid();
        incoming.Number = 1;
        incoming.NumberEnd = 2;
        rig.Sync.KeepMyPlace = new KeepMyPlaceRetentionTests.Capture();
        var report = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Both);
        Assert.Single(Directory.GetFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories));
        Assert.Equal(old.PeerItemId, Assert.Single(rig.Entries, e => e.Kind == EntryKind.Episode).PeerItemId);
        Assert.Contains(incoming.Id.ToString("N"), rig.Catalog().TvHeldAdditions);
        Assert.Contains(SyncAlert.EpisodeRangeConflict, report.Alerts);
    }

    private static TvPlan Plan(SanitizedEpisode episode, params TvOwnedEpisode[] owned)
    {
        var identity = new LocalSeries(new CanonicalIds(null, 1, null), "Show", 2000);
        var series = new SanitizedSeries { PeerItemId = SeriesId, ProviderIds = identity.ProviderIds, Title = identity.Title, Year = identity.Year };
        return new TvPlanner(new TvLayout()).Plan(new TvPlanInput("tv-1", [], [series], [], [episode], true, true, [], new TvGuardSettings(25, 10, 500, 20), true, true, PeerOwned: [new TvOwnedSeries("owner", "ownerseries00001", true, owned, identity)]));
    }
}
