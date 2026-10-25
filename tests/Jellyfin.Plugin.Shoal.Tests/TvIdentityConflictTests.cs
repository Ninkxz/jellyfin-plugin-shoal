using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class TvIdentityConflictTests
{
    [Fact]
    public void WeakSource_CannotBridgeConflictingProviderBundles()
    {
        var records = new[] { new LocalSeries(new CanonicalIds(10, 1, null), "Show", 2000), new LocalSeries(new CanonicalIds(10, 2, null), "Show", 2000) };
        var index = new TvIdentityIndex<LocalSeries>(records, static s => s);
        Assert.Empty(index.Match(default, "Show", 2000));
        Assert.Empty(index.Match(new CanonicalIds(10, null, null), "Show", 2000));
    }

    [Fact]
    public void MissingIds_KeepTitleYearPolicy_AndStrongMatchesIgnoreTitle()
    {
        var index = new TvIdentityIndex<LocalSeries>([new LocalSeries(new CanonicalIds(null, 1, null), "Show", 2000)], static s => s);
        Assert.Single(index.Match(default, "Show", 2000));
        Assert.Single(index.Match(new CanonicalIds(null, 1, null), "Renamed", 2001));
        Assert.Empty(index.Match(new CanonicalIds(null, 2, null), "Show", 2000));
    }

    [Fact]
    public void WeakSource_DisjointProviderNamespacesAreAmbiguous()
    {
        var index = new TvIdentityIndex<LocalSeries>([new LocalSeries(new CanonicalIds(null, 1, null), "Show", 2000), new LocalSeries(new CanonicalIds(20, null, null), "Show", 2000)], static s => s);
        Assert.Empty(index.Match(default, "Show", 2000));
    }
    [Fact]
    public void Move_SameTmdbConflictingTvdb_DoesNotRetargetStub()
    {
        var old = new CatalogEntry { Kind = EntryKind.Series, StubKey = "series0000000001", Title = "Show", Year = 2000, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "10", ["Tvdb"] = "1" } };
        var next = new SanitizedSeries { PeerItemId = Guid.NewGuid(), Title = "Show", Year = 2000, ProviderIds = new CanonicalIds(10, 2, null) };
        Assert.Empty(new TvLayout().MatchSeriesMoves([old], [next]));
    }

    [Fact]
    public async Task Local_SameTitleYearConflictingTvdb_DoesNotHidePeerSeries()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(1, 1, 1);
        await rig.AddTvMappingAsync();
        rig.LocalSeries.Series.Add(new LocalSeries(new CanonicalIds(null, 200_000, null), "Show 0", 2010));
        await rig.SyncAsync();
        Assert.Single(rig.Entries, e => e.Kind == EntryKind.Episode && e.State == EntryState.Active);
        Assert.DoesNotContain(rig.Entries, e => e.Kind == EntryKind.Series && e.State == EntryState.DuplicateLocal);
    }
}
