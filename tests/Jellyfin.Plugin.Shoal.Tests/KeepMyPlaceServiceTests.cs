using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Progress;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class KeepMyPlaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shoal-progress-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibrary _library = new();

    public KeepMyPlaceServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RemovalAwaitsDurableCaptureAndRestartRetainsIt()
    {
        var store = Store();
        using (var service = new KeepMyPlaceService(_library, store, () => true))
        {
            Assert.True(await service.RetainBeforeRemovalAsync("owned.strm", CancellationToken.None));
        }

        var restored = Store();
        Assert.True(restored.Healthy);
        Assert.Single(restored.Read());
        _library.Captures = []; // source no longer indexed; restart still reconciles
        using var restarted = new KeepMyPlaceService(_library, restored, () => true);
        await restarted.ReconcileAsync([], CancellationToken.None);
        Assert.Equal(1, _library.Applied);
    }

    [Fact]
    public async Task CorruptionOrUnwritableStoreHoldsRemovalAndDoesNotApply()
    {
        File.WriteAllText(Path.Combine(_root, "progress.json"), "bad json");
        using var corrupt = new KeepMyPlaceService(_library, Store(), () => true);
        Assert.False(await corrupt.RetainBeforeRemovalAsync("owned.strm", CancellationToken.None));
        await corrupt.ReconcileAsync([], CancellationToken.None);
        Assert.Equal(0, _library.Applied);

        using var unavailable = new KeepMyPlaceService(_library, new KeepMyPlaceStore(Path.Combine(_root, "missing", "progress.json"), TimeProvider.System), () => true);
        Assert.False(await unavailable.RetainBeforeRemovalAsync("owned.strm", CancellationToken.None));
    }

    [Fact]
    public async Task DisabledFeatureDoesNotCaptureOrWrite()
    {
        using var service = new KeepMyPlaceService(_library, Store(), () => false);
        Assert.True(await service.RetainBeforeRemovalAsync("owned.strm", CancellationToken.None));
        await service.ReconcileAsync([Guid.NewGuid()], CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_root, "progress.json")));
        Assert.Equal(0, _library.Applied);
    }

    [Fact]
    public async Task ResetOverwritesPreviousCaptureAndDoesNotReplay()
    {
        var store = Store();
        using var service = new KeepMyPlaceService(_library, store, () => true);
        Assert.True(await service.RetainBeforeRemovalAsync("owned.strm", CancellationToken.None));
        _library.Captures = [_library.Snapshot with { Source = _library.Snapshot.Source with { History = _library.Snapshot.Source.History with { PositionTicks = 0 } } }];
        await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
        Assert.Equal(0, Assert.Single(store.Read()).Source.History.PositionTicks);
        Assert.Equal(0, _library.Applied);
    }

    [Fact]
    public async Task RepeatedEventsCoalesceAndFreshAccessRaceDeniesWrite()
    {
        using var service = new KeepMyPlaceService(_library, Store(), () => true);
        _library.AllowAtSave = false; // access revoked or manual destination update after candidate discovery
        await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
        Assert.Equal(0, _library.Applied);
        _library.AllowAtSave = true;
        await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
        await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
        Assert.Equal(1, _library.Applied); // destination now has history
    }

    [Fact]
    public async Task AmbiguousDestinationAndDeletedUserNeverApply()
    {
        var store = Store();
        using var service = new KeepMyPlaceService(_library, store, () => true);
        _library.Ambiguous = true;
        await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
        Assert.Equal(0, _library.Applied);
        _library.UserPresent = false;
        _library.Ambiguous = false;
        await service.ReconcileAsync([], CancellationToken.None);
        Assert.Empty(store.Read());
        Assert.Equal(0, _library.Applied);
    }

    [Fact]
    public async Task ConflictingNewestSourcesSkipInsteadOfPickingPeerOrder()
    {
        var snapshot = _library.Snapshot;
        _library.Captures = [snapshot, snapshot with { Source = snapshot.Source with { Media = snapshot.Source.Media with { ItemId = Guid.NewGuid() }, History = snapshot.Source.History with { PositionTicks = 200 } } }];
        using var service = new KeepMyPlaceService(_library, Store(), () => true);
        await service.ReconcileAsync([snapshot.Source.Media.ItemId], CancellationToken.None);
        Assert.Equal(0, _library.Applied);
    }

    [Fact]
    public async Task DurableReceiptPreventsReplayAfterDestinationResetAndRestart()
    {
        using (var service = new KeepMyPlaceService(_library, Store(), () => true))
        {
            await service.ReconcileAsync([_library.Snapshot.Source.Media.ItemId], CancellationToken.None);
            Assert.Equal(1, _library.Applied);
        }

        _library.ResetDestination();
        using var restarted = new KeepMyPlaceService(_library, Store(), () => true);
        await restarted.ReconcileAsync([], CancellationToken.None);
        Assert.Equal(0, _library.Applied);
    }

    [Fact]
    public void UnchangedCaptureKeepsOriginalTimestampAndDoesNotRewriteFile()
    {
        var store = Store();
        Assert.True(store.Save([_library.Snapshot], _library.UserExists));
        var path = Path.Combine(_root, "progress.json");
        var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, marker);
        Assert.True(store.Save([_library.Snapshot with { CapturedUtc = DateTime.UtcNow }], _library.UserExists));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
        Assert.Equal(_library.Snapshot.CapturedUtc, Assert.Single(store.Read()).CapturedUtc);
    }

    [Fact]
    public async Task DisabledMaintenancePrunesExpiredAndDeletedUsersWithoutCapturingMedia()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new KeepMyPlaceStore(Path.Combine(_root, "progress.json"), clock);
        Assert.True(store.Save([_library.Snapshot], _library.UserExists));
        clock.Advance(TimeSpan.FromDays(91));
        using var service = new KeepMyPlaceService(_library, store, () => false);
        await service.PruneAsync(default);
        Assert.Empty(new KeepMyPlaceStore(Path.Combine(_root, "progress.json"), clock).Read());
        var current = _library.Snapshot with { CapturedUtc = clock.GetUtcNow().UtcDateTime };
        Assert.True(store.Save([current], _library.UserExists));
        _library.UserPresent = false;
        await service.PruneAsync(default);
        Assert.Empty(new KeepMyPlaceStore(Path.Combine(_root, "progress.json"), clock).Read());
        Assert.Equal(0, _library.CaptureCalls);
        Assert.Equal(0, _library.DestinationCalls);
    }

    [Fact]
    public async Task CandidateDiscoveryIsBoundedAndContinuesOnNextPass()
    {
        var store = Store();
        var snapshots = new List<KeepMyPlaceSnapshot>();
        for (var i = 0; i < 129; i++)
        {
            snapshots.Add(_library.Snapshot with { Source = _library.Snapshot.Source with { Media = _library.Snapshot.Source.Media with { ItemId = Guid.NewGuid() } } });
        }

        Assert.True(store.Save(snapshots, _library.UserExists));
        using var service = new KeepMyPlaceService(_library, store, () => true);
        await service.ReconcileAsync([], default);
        Assert.Equal(128, _library.DestinationCalls);
        await service.ReconcileAsync([], default);
        Assert.Equal(129, _library.DestinationCalls);
        Assert.Equal(0, _library.Applied); // too many conflicting candidates is held, never first-match selected
    }

    [Fact]
    public void FullStoreRefusesNewCaptureInsteadOfEvictingHistory()
    {
        var entries = new List<KeepMyPlaceSnapshot>();
        for (var i = 0; i < 10001; i++)
        {
            entries.Add(_library.Snapshot with { Source = _library.Snapshot.Source with { Media = _library.Snapshot.Source.Media with { ItemId = Guid.NewGuid() } } });
        }

        Assert.False(Store().Save(entries, _library.UserExists));
        Assert.False(File.Exists(Path.Combine(_root, "progress.json")));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private KeepMyPlaceStore Store() => new(Path.Combine(_root, "progress.json"), TimeProvider.System);

    private sealed class FakeLibrary : IKeepMyPlaceLibrary
    {
        private readonly Guid _destination = Guid.NewGuid();

        public FakeLibrary()
        {
            Snapshot = new(new(new(Guid.NewGuid(), new(KeepMyPlaceKind.Movie, new Dictionary<string, string> { ["Tmdb"] = "123" }), TimeSpan.TicksPerHour), new(Guid.NewGuid(), PositionTicks: 100)), new FederatedItem(new string('a', 16), "mapping"), DateTime.UtcNow);
            Captures = [Snapshot];
        }

        public KeepMyPlaceSnapshot Snapshot { get; }

        public IReadOnlyList<KeepMyPlaceSnapshot>? Captures { get; set; }

        public bool UserPresent { get; set; } = true;

        public bool AllowAtSave { get; set; } = true;

        public bool Ambiguous { get; set; }

        public int Applied { get; private set; }

        public int CaptureCalls { get; private set; }

        public int DestinationCalls { get; private set; }

        public void ResetDestination() => Applied = 0;

        public bool UserExists(Guid userId) => UserPresent;

        public IReadOnlyList<KeepMyPlaceSnapshot>? Capture(Guid itemId)
        {
            CaptureCalls++;
            return Captures;
        }

        public IReadOnlyList<KeepMyPlaceSnapshot>? CapturePath(string path) => Captures;

        public IReadOnlyList<Guid> SourceItems(int offset, int limit) => [];

        public IReadOnlyList<KeepMyPlaceMedia> Destinations(KeepMyPlaceSnapshot snapshot)
        {
            DestinationCalls++;
            return Ambiguous ? [snapshot.Source.Media with { ItemId = _destination }, snapshot.Source.Media with { ItemId = Guid.NewGuid() }] : [snapshot.Source.Media with { ItemId = _destination }];
        }

        public bool ApplyFresh(KeepMyPlaceSnapshot snapshot, Guid destinationItemId, CancellationToken cancellationToken)
        {
            if (!AllowAtSave || Applied > 0)
            {
                return false;
            }

            Applied++;
            return true;
        }
    }
}
