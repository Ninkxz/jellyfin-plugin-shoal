using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The Overview must show a running pairing as Pairing and a stopped one as PairingAborted (with its kind) from the real stores, and
/// must never show "online" for something nobody measured (Warden W5, merge notes).
/// </summary>
public sealed class StateBackedLiveStatusTests
{
    private sealed class Gate : IPairingGate
    {
        public bool Live { get; set; }

        public bool IsLive(string peerKey) => Live;
    }

    [Fact]
    public void ARunningPairing_IsPairing()
    {
        using var dir = new TempDirectory();
        var live = new StateBackedLiveStatus(new PeerHealthStore(new ShoalPaths(dir.Path), NullLogger<PeerHealthStore>.Instance), new Gate { Live = true });

        Assert.Equal("Pairing", live.ForPeer(StateSamples.PeerKeyA).Status);
    }

    [Fact]
    public void AStoppedPairing_IsPairingAborted_WithItsKindPhaseAndTime()
    {
        using var dir = new TempDirectory();
        var health = new PeerHealthStore(new ShoalPaths(dir.Path), NullLogger<PeerHealthStore>.Instance);
        var at = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
        health.SetAbort(StateSamples.PeerKeyA, new PairingAbortRecord { Kind = PairingErrorKind.RequestReplaced, Phase = Jellyfin.Plugin.Shoal.State.PairingPhase.AwaitingConfirm, AtUtc = at });
        var live = new StateBackedLiveStatus(health, new Gate());

        var info = live.ForPeer(StateSamples.PeerKeyA);

        Assert.Equal("PairingAborted", info.Status);
        Assert.Equal("requestReplaced", info.PairingError);
        Assert.Equal("awaitingConfirm", info.PairingPhase);
        Assert.Equal(at, info.PairingStoppedUtc);
    }

    [Fact]
    public void NothingKnown_IsNotMeasured_NotOnline()
    {
        using var dir = new TempDirectory();
        var live = new StateBackedLiveStatus(new PeerHealthStore(new ShoalPaths(dir.Path), NullLogger<PeerHealthStore>.Instance), new Gate());

        var info = live.ForPeer(StateSamples.PeerKeyA);

        Assert.Null(info.Status);
        Assert.Null(info.Mbps);
        Assert.Equal(0, live.StartSyncAll());
    }

    [Fact]
    public async Task TitlesWithoutAStubBecauseAnotherServerCarriesThem_AreCountedWithTheWinnersName()
    {
        var card = await WinnerCardAsync(null);

        Assert.Equal(2, card.HiddenByDedup);
        Assert.Equal("Hop_Media", card.DedupWinner);
    }

    [Fact]
    public async Task WhileTheWinnerIsPaused_TheCardDoesNotNameItAsTheWinner()
    {
        var card = await WinnerCardAsync(Jellyfin.Plugin.Shoal.Peers.PeerStatus.Disabled);

        Assert.Null(card.DedupWinner); // F11: stale text on the other servers' cards
        var online = await WinnerCardAsync(Jellyfin.Plugin.Shoal.Peers.PeerStatus.Online);
        Assert.Equal("Hop_Media", online.DedupWinner);
    }

    private static async Task<Jellyfin.Plugin.Shoal.Api.Admin.ServerDto> WinnerCardAsync(Jellyfin.Plugin.Shoal.Peers.PeerStatus? winnerStatus)
    {
        using var rig = new PairingRig();
        await rig.State.UpdateAsync(StateSamples.Fill);
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new Jellyfin.Plugin.Shoal.State.PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Hop_Media", BaseUrl = "http://other.tail1234.ts.net:8096" }));
        var catalogs = new Jellyfin.Plugin.Shoal.Sync.CatalogStore(rig.Paths, NullLogger<Jellyfin.Plugin.Shoal.Sync.CatalogStore>.Instance);
        var mine = new Jellyfin.Plugin.Shoal.Sync.CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        foreach (var (title, key) in new[] { ("One", "aaaaaaaaaaaaaaa1"), ("Two", "aaaaaaaaaaaaaaa2") })
        {
            mine.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = key, PeerItemId = new string(key[^1], 32), MappingKey = "m", Title = title, State = Jellyfin.Plugin.Shoal.Sync.EntryState.DuplicatePeer, DuplicateOf = "bbbbbbbbbbbbbbb" + key[^1] });
        }

        mine.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = "aaaaaaaaaaaaaaa3", PeerItemId = new string('3', 32), MappingKey = "m", Title = "Three", State = Jellyfin.Plugin.Shoal.Sync.EntryState.Active, StubRelPath = "m/three.strm" });
        catalogs.Save(mine);
        var winner = new Jellyfin.Plugin.Shoal.Sync.CatalogDocument { PeerKey = StateSamples.PeerKeyB };
        winner.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = "bbbbbbbbbbbbbbb1", PeerItemId = new string('2', 32), MappingKey = "m", Title = "One", State = Jellyfin.Plugin.Shoal.Sync.EntryState.Active, StubRelPath = "m/one.strm" });
        winner.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = "bbbbbbbbbbbbbbb2", PeerItemId = new string('2', 32), MappingKey = "m", Title = "Two", State = Jellyfin.Plugin.Shoal.Sync.EntryState.Active, StubRelPath = "m/two.strm" });
        catalogs.Save(winner);
        Jellyfin.Plugin.Shoal.Peers.PeerRegistry? registry = null;
        if (winnerStatus is { } status)
        {
            registry = new Jellyfin.Plugin.Shoal.Peers.PeerRegistry(new ManualTimeProvider());
            registry.SetStatus(StateSamples.PeerKeyB, status);
        }

        var live = new StateBackedLiveStatus(rig.Health, new Gate(), catalogs, registry: registry, state: rig.State);

        var info = live.ForPeer(StateSamples.PeerKeyA);

        Assert.Equal(1, info.TitleCount);
        return Jellyfin.Plugin.Shoal.Api.Admin.AdminStatusBuilder.Server(rig.State.Current.Home.Peers.First(p => p.PeerKey == StateSamples.PeerKeyA), info);
    }

    [Fact]
    public void TheCatalog_GivesTheLastSyncTheCountsTheStorageAndTheAlerts()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var catalogs = new Jellyfin.Plugin.Shoal.Sync.CatalogStore(paths, NullLogger<Jellyfin.Plugin.Shoal.Sync.CatalogStore>.Instance);
        var document = new Jellyfin.Plugin.Shoal.Sync.CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        document.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = "aaaaaaaaaaaaaaaa", PeerItemId = new string('1', 32), MappingKey = "m", Title = "Sintel", State = Jellyfin.Plugin.Shoal.Sync.EntryState.Active, StubRelPath = "movies/a.strm" });
        document.Entries.Add(new Jellyfin.Plugin.Shoal.Sync.CatalogEntry { StubKey = "bbbbbbbbbbbbbbbb", PeerItemId = new string('2', 32), MappingKey = "m", Title = "Spring", State = Jellyfin.Plugin.Shoal.Sync.EntryState.HeldRemoval, StubRelPath = "movies/b.strm" });
        document.HeldRemovals.Add("bbbbbbbbbbbbbbbb");
        document.Usage.DiskBytes = 1234;
        document.LastSync = new Jellyfin.Plugin.Shoal.Sync.LastSyncInfo
        {
            EndedUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
            Result = Jellyfin.Plugin.Shoal.Sync.SyncResultKind.Partial,
            Added = 3,
            Removed = 1,
            Alerts = ["HeldRemovals", "QuotaReached", "EntryCap"]
        };
        catalogs.Save(document);
        var live = new StateBackedLiveStatus(new PeerHealthStore(paths, NullLogger<PeerHealthStore>.Instance), new Gate(), catalogs);

        var info = live.ForPeer(StateSamples.PeerKeyA);

        Assert.Equal(2, info.TitleCount);
        Assert.Equal(1, info.HeldRemovals);
        Assert.Equal("partial", info.LastSync!.Result);
        Assert.Equal(3, info.LastSync.Added);
        Assert.Equal(1234, info.Usage!.DiskBytes);
        Assert.Equal(2_000_000_000, info.Usage.QuotaBytes);
        Assert.Equal(["heldRemovals", "diskQuota", "entryCap"], info.Alerts.Select(a => a.Kind));
        Assert.Equal(["Spring"], info.Alerts[0].Examples);
        Assert.Equal("bad", info.Alerts[2].Severity);
        Assert.Equal(3, info.AlertCount);
    }

    [Fact]
    public async Task ASyncThatWasSkipped_ShowsAsSkipped_NotAsTheOldOk()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync(SyncTrigger.Manual);
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State);

        Assert.True(live.StartSync(SyncRig.PeerKey)); // within five minutes, so the service skips it
        await WaitIdleAsync(live);
        Assert.Equal("skipped", live.ForPeer(SyncRig.PeerKey).LastSync!.Result);

        rig.Time.Advance(TimeSpan.FromMinutes(6));
        live.StartSync(SyncRig.PeerKey);
        await WaitIdleAsync(live);
        Assert.Equal("ok", live.ForPeer(SyncRig.PeerKey).LastSync!.Result);
    }

    private static async Task WaitIdleAsync(StateBackedLiveStatus live)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        await Task.Delay(20);
        while (live.ForPeer(SyncRig.PeerKey).Syncing && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.False(live.ForPeer(SyncRig.PeerKey).Syncing);
    }

    private sealed class NoPairing : IPairingGate
    {
        public bool IsLive(string peerKey) => false;
    }

    [Fact]
    public async Task AFirstSyncThatAScanBlocked_RunsAsSoonAsTheScanEnds()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        rig.Refresh.IsScanRunning = true;
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State) { ScanRetryDelay = TimeSpan.FromMilliseconds(20) };

        live.StartSync(SyncRig.PeerKey);
        await Task.Delay(150);
        Assert.Empty(rig.Catalog().Entries); // still blocked
        Assert.True(live.ForPeer(SyncRig.PeerKey).Syncing);

        rig.Refresh.IsScanRunning = false;
        await WaitIdleAsync(live);

        Assert.Single(rig.Catalog().Entries);
        Assert.Equal("ok", live.ForPeer(SyncRig.PeerKey).LastSync!.Result);
    }
}
