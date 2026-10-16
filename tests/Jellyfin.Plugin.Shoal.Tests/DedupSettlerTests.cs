using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Pilot 09 F11: when the better-ranked server is back (or lets go), the others are settled in one pass, not at the next scheduled sync.</summary>
public sealed class DedupSettlerTests
{
    private const string Better = "ffffffffffffffffffffffffffffffff";

    private static CatalogEntry MatrixOf(string stubKey) => new()
    {
        StubKey = stubKey,
        PeerItemId = "11111111111111111111111111111111",
        PeerMediaSourceId = "11111111111111111111111111111111",
        MappingKey = "movies-1",
        Title = "The Matrix",
        Year = 1999,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
        State = EntryState.Active,
        StubRelPath = "The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm",
    };

    private static async Task<(SyncRig Rig, PeerRegistry Registry, DedupSettler Settler)> StartedAsync()
    {
        var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Peer.Add("Inception", 2010, tmdb: 27205);
        await rig.AddPeerAsync(Better, "Better", priority: 50, paired: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        rig.Catalogs.Save(new CatalogDocument { PeerKey = Better, CompletedOnce = true, Entries = { MatrixOf("bbbbbbbbbbbbbbbb") } });
        var registry = new PeerRegistry(rig.Time);
        registry.SetStatus(SyncRig.PeerKey, PeerStatus.Online);
        registry.SetStatus(Better, PeerStatus.Online);
        return (rig, registry, new DedupSettler(registry, rig.Sync, rig.State, rig.Time, NullLogger<DedupSettler>.Instance));
    }

    [Fact]
    public async Task WhenAPausedBetterServerIsResumed_TheOthersStubsForItsTitlesAreRemovedInTheSamePass_AndOneScanIsQueued()
    {
        var (rig, registry, settler) = await StartedAsync();
        await using (rig)
        using (settler)
        {
            registry.SetStatus(Better, PeerStatus.Disabled); // paused: lets go at once
            rig.DownPeers.Add(Better);
            await rig.SyncAsync(); // the other server gets the stubs
            Assert.All(rig.Entries, e => Assert.Equal(EntryState.Active, e.State));
            var scans = rig.Refresh.Scans;

            registry.SetStatus(Better, PeerStatus.Online); // resumed
            rig.DownPeers.Clear();
            Assert.Equal(0, await settler.RunDueAsync()); // not before it settled
            rig.Time.Advance(DedupSettler.Settle + TimeSpan.FromSeconds(1));
            rig.Time.Advance(TimeSpan.FromMinutes(6)); // the scan gap since the first stubs

            Assert.Equal(1, await settler.RunDueAsync());

            var entries = rig.Entries.ToDictionary(static e => e.Title);
            Assert.Equal(EntryState.DuplicatePeer, entries["The Matrix"].State); // no second copy
            Assert.Equal(EntryState.Active, entries["Inception"].State);
            Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
            Assert.Equal(scans + 1, rig.Refresh.Scans); // one scan, so core drops the removed stub
        }
    }

    [Fact]
    public async Task ABriefOutage_OfTheBetterServer_SettlesNothing()
    {
        var (rig, registry, settler) = await StartedAsync();
        await using (rig)
        using (settler)
        {
            await rig.SyncAsync();
            registry.SetStatus(Better, PeerStatus.Offline);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            registry.SetStatus(Better, PeerStatus.Online); // it held its titles all along (grace): nobody wrote stubs in its place
            rig.Time.Advance(DedupReachability.Grace + DedupSettler.Settle);

            Assert.Equal(0, await settler.RunDueAsync());
        }
    }

    [Fact]
    public async Task WhenTheBetterServerIsPaused_TheOthersAreSyncedAfterTheSettleTime_SoTheirTitlesGetStubs()
    {
        var (rig, registry, settler) = await StartedAsync();
        await using (rig)
        using (settler)
        {
            await rig.SyncAsync();
            Assert.Equal(EntryState.DuplicatePeer, rig.Entries.Single(static e => e.Title == "The Matrix").State);

            registry.SetStatus(Better, PeerStatus.Disabled);
            rig.DownPeers.Add(Better);
            Assert.Equal(0, await settler.RunDueAsync());
            rig.Time.Advance(DedupSettler.Settle + TimeSpan.FromSeconds(1));

            Assert.Equal(1, await settler.RunDueAsync());
            Assert.All(rig.Entries, e => Assert.Equal(EntryState.Active, e.State));
        }
    }

    [Fact]
    public async Task AnOfflineBetterServer_IsLookedAtOnlyWhenTheGraceHasPassed_AndNotIfItIsBackByThen()
    {
        var (rig, registry, settler) = await StartedAsync();
        await using (rig)
        using (settler)
        {
            registry.SetStatus(Better, PeerStatus.Offline);
            rig.Time.Advance(DedupSettler.Settle + TimeSpan.FromMinutes(1));
            Assert.Equal(0, await settler.RunDueAsync()); // it still holds its titles for ten minutes

            rig.Time.Advance(DedupReachability.Grace - TimeSpan.FromMinutes(1) - DedupSettler.Settle);
            registry.SetStatus(Better, PeerStatus.Online); // back before the grace ended
            rig.Time.Advance(DedupReachability.Grace);
            Assert.Equal(0, await settler.RunDueAsync());
        }
    }
}
