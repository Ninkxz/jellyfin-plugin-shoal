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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCatalog_StillShowsThePendingAdminSync(bool allPeers)
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Catalogs.Save(new CatalogDocument { PeerKey = SyncRig.PeerKey });
        rig.Catalogs.Save(new CatalogDocument { PeerKey = SyncRig.PeerKey });
        rig.Refresh.IsScanRunning = true;
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State) { ScanRetryDelay = TimeSpan.FromMilliseconds(250) };

        if (allPeers)
        {
            Assert.Equal(1, live.StartSyncAll());
        }
        else
        {
            Assert.True(live.StartSync(SyncRig.PeerKey));
        }

        await WaitSkippedAsync(rig, "scanRunning");
        var catalog = System.IO.Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog.json");
        System.IO.File.Delete(catalog);
        System.IO.File.Delete(catalog + ".bak");
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog-established")));
        Assert.True(rig.Catalogs.Load(SyncRig.PeerKey).Unreadable);

        try
        {
            var info = live.ForPeer(SyncRig.PeerKey);
            Assert.True(info.Syncing);
            var alert = Assert.Single(info.Alerts);
            Assert.Equal("catalogRecovery", alert.Kind);
            Assert.Equal("bad", alert.Severity);
        }
        finally
        {
            rig.Refresh.IsScanRunning = false;
            await WaitIdleAsync(live);
        }

        Assert.False(System.IO.File.Exists(catalog));
        Assert.False(System.IO.File.Exists(catalog + ".bak"));
        Assert.Equal("catalogRecovery", Assert.Single(live.ForPeer(SyncRig.PeerKey).Alerts).Kind);
    }

    [Fact]
    public async Task ASecondAdminRequestFinishing_DoesNotHideTheFirstPendingSync()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Refresh.IsScanRunning = true;
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State) { ScanRetryDelay = TimeSpan.FromSeconds(1) };
        Assert.True(live.StartSync(SyncRig.PeerKey));
        await WaitSkippedAsync(rig, "scanRunning");

        try
        {
            using var hold = rig.Sync.TryHold();
            Assert.NotNull(hold);
            Assert.True(live.StartSync(SyncRig.PeerKey));
            await WaitSkippedAsync(rig, "busy");
            // The first job is still waiting for its scan retry. Observe the status after the
            // short busy request has unwound, not just while its request is being dispatched.
            var until = DateTime.UtcNow.AddMilliseconds(200);
            while (DateTime.UtcNow < until)
            {
                Assert.True(live.ForPeer(SyncRig.PeerKey).Syncing);
                await Task.Delay(10);
            }
        }
        finally
        {
            rig.Refresh.IsScanRunning = false;
            await WaitIdleAsync(live);
        }
    }

    private static async Task WaitSkippedAsync(SyncRig rig, string reason)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (rig.Catalogs.LastSkipped(SyncRig.PeerKey)?.Reason != reason && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.Equal(reason, rig.Catalogs.LastSkipped(SyncRig.PeerKey)?.Reason);
    }

    [Fact]
    public void LostCatalog_HasFixedRecoveryAlert()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var catalogs = new Jellyfin.Plugin.Shoal.Sync.CatalogStore(paths, NullLogger<Jellyfin.Plugin.Shoal.Sync.CatalogStore>.Instance);
        catalogs.Save(new Jellyfin.Plugin.Shoal.Sync.CatalogDocument { PeerKey = StateSamples.PeerKeyA });
        System.IO.File.Delete(System.IO.Path.Combine(paths.PeerDir(StateSamples.PeerKeyA), "catalog.json"));
        var live = new StateBackedLiveStatus(new PeerHealthStore(paths, NullLogger<PeerHealthStore>.Instance), new Gate(), catalogs);
        var info = live.ForPeer(StateSamples.PeerKeyA);
        Assert.Equal(1, info.AlertCount);
        var alert = Assert.Single(info.Alerts);
        Assert.Equal("catalogRecovery", alert.Kind);
        Assert.Equal("bad", alert.Severity);
    }

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
    public async Task HeldAdditions_ExamplesAreTitles()
    {
        // 2.9.2 K3 acceptance: a held batch of 30 shows 3 real titles ("Title (Year)"), and the count stays 30.
        await using var rig = await SyncRig.CreateAsync();
        rig.Settings = SyncSettings.Default with { NewItemsMinCount = 10, NewItemsPercent = 20 };
        for (var i = 0; i < 5; i++)
        {
            rig.Peer.Add("Base " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 2000, tmdb: 100 + i, configure: m => { m.PosterTag = null; m.BackdropTag = null; });
        }

        await rig.SyncAsync();
        for (var i = 0; i < 30; i++)
        {
            rig.Peer.Add("Flood " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), 2020, tmdb: 5000 + i, configure: m => { m.PosterTag = null; m.BackdropTag = null; });
        }

        Assert.Equal(SyncResultKind.Held, (await rig.SyncAsync()).Result);
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State);

        var alert = live.ForPeer(SyncRig.PeerKey).Alerts.Single(a => a.Kind == "heldAdditions");

        Assert.Equal(30, alert.Count);
        Assert.Equal(["Flood 00 (2020)", "Flood 01 (2020)", "Flood 02 (2020)"], alert.Examples);
        Assert.Equal(30, rig.Catalog().HeldAdditionInfo.Count(h => h.MappingKey == SyncRig.MappingKey));

        await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Additions);
        Assert.Empty(rig.Catalog().HeldAdditionInfo); // a release clears them
    }

    [Fact]
    public async Task ASyncThatWasSkipped_ShowsTheSkip_NextToTheLastRealSync()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync(SyncTrigger.Manual);
        var real = rig.Catalog().LastSync!.EndedUtc;
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State);

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(live.StartSync(SyncRig.PeerKey)); // within five minutes, so the service skips it
        await WaitIdleAsync(live);
        var shown = live.ForPeer(SyncRig.PeerKey).LastSync!;
        Assert.Equal("ok", shown.Result); // the real result stays
        Assert.Equal(real, shown.AtUtc); // and its time: the Overview said "just now" (Pilot 24)
        Assert.Equal(real.AddMinutes(3), shown.SkippedUtc);
        Assert.Equal("tooSoon", shown.SkipReason);

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        live.StartSync(SyncRig.PeerKey);
        await WaitIdleAsync(live);
        shown = live.ForPeer(SyncRig.PeerKey).LastSync!;
        Assert.Equal("ok", shown.Result);
        Assert.Null(shown.SkippedUtc); // a request that ran clears the skip
    }

    [Fact]
    public async Task ASkip_IsForgotten_WhenAScheduledSyncRunsAfterIt()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync();
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State);
        live.StartSync(SyncRig.PeerKey);
        await WaitIdleAsync(live);
        Assert.NotNull(live.ForPeer(SyncRig.PeerKey).LastSync!.SkippedUtc);

        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.SyncAsync(); // the schedule, not the page: the skip is older than this sync

        var shown = live.ForPeer(SyncRig.PeerKey).LastSync!;
        Assert.Equal(rig.Catalog().LastSync!.EndedUtc, shown.AtUtc);
        Assert.Null(shown.SkippedUtc);
    }

    [Fact]
    public async Task Pilot24_ASkippedSyncNow_ReachesTheLibraryRows_AndKeepsTheirResult()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync(); // the scheduled sync before the click
        var real = rig.Catalog().LastSync!;
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State);
        var libraries = new FakeLibraries();
        var accounts = new FakeAccounts();
        var visibility = new Jellyfin.Plugin.Shoal.Visibility.VisibilityService(rig.State, libraries, accounts, rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.VisibilityService>.Instance);
        var view = new AdminLibraryView(rig.State, rig.Catalogs, libraries, accounts, visibility);
        var controller = new AdminLibrariesController(rig.State, view, new Jellyfin.Plugin.Shoal.Visibility.AccessService(rig.State, accounts, visibility, rig.Time), visibility)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        live.StartSync(SyncRig.PeerKey); // "Sync now" 3 minutes later: core logs "skipped (tooSoon)"
        await WaitIdleAsync(live);

        var row = Assert.Single(AdminTestSupport.Body(controller.GetLibraries()).GetProperty("mappings").EnumerateArray()).GetProperty("lastSync");
        Assert.Equal(real.Result.ToString().ToLowerInvariant(), row.GetProperty("result").GetString());
        Assert.Equal(real.EndedUtc, row.GetProperty("atUtc").GetDateTime());
        Assert.Equal(real.EndedUtc.AddMinutes(3), row.GetProperty("skippedUtc").GetDateTime());
        Assert.Equal("tooSoon", row.GetProperty("skipReason").GetString());
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
        var live = new StateBackedLiveStatus(new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance), new NoPairing(), rig.Catalogs, sync: rig.Sync, state: rig.State) { ScanRetryDelay = TimeSpan.FromMilliseconds(100) };

        live.StartSync(SyncRig.PeerKey);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (rig.Catalogs.LastSkipped(SyncRig.PeerKey)?.Reason != "scanRunning" && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.Equal("scanRunning", rig.Catalogs.LastSkipped(SyncRig.PeerKey)?.Reason);
        Assert.Empty(rig.Catalog().Entries); // still blocked
        Assert.True(live.ForPeer(SyncRig.PeerKey).Syncing);

        rig.Refresh.IsScanRunning = false;
        await WaitIdleAsync(live);

        Assert.Single(rig.Catalog().Entries);
        Assert.Equal("ok", live.ForPeer(SyncRig.PeerKey).LastSync!.Result);
    }
}
