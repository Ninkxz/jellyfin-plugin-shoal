using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.10.3 P2-SIMPLE-SELF-ROLLBACK-COMMON and P2-UI2103-RESTORE-FIRST-ATTEMPT (docs/plans/kiro2-rollback-diagnosis-2.10.3.md): in the lab, core indexed the
/// shared copy (and the restored original) about 60 to 75 s after the move, past the old fixed 60 s wait (120 looks of 500 ms), so moves rolled back and
/// first Restores reported needsRecovery with the file back. The real adapter now waits RemovalPolicy.IndexWait and asks for the one changed folder.
/// </summary>
public sealed class RemovalIndexWaitTests
{
    // Lab timing: the copy appeared on the 150th look (about 75 s), after the old limit of 120.
    private const int LabLookWhenIndexed = 150;

    [Fact]
    public void TheWaitIsANamedThreeMinutesOfHalfSecondLooks()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), RemovalPolicy.IndexWait);
        Assert.Equal(TimeSpan.FromMilliseconds(500), RemovalPolicy.IndexPoll);
        Assert.Equal(360, RemovalPolicy.IndexPolls);
        Assert.True(RemovalPolicy.IndexPolls > LabLookWhenIndexed);
    }

    [Fact]
    public async Task AMoveFindsASharedCopyIndexedAfterTheOldSixtySecondsAndAsksForItsFolder()
    {
        using var f = new Fixture(sharedAppearsAt: LabLookWhenIndexed);
        var shared = await f.Adapter.ImportAsync(f.Target, [], default);
        Assert.Equal(f.Stub.Id, shared);
        Assert.Equal(LabLookWhenIndexed, f.Looks);
        // The shared copy's own folder is asked for, not a library scan.
        Assert.Contains(f.StubFolder, f.Refresh.Nearest);
        Assert.Empty(f.Refresh.LibraryScans);
        Assert.Equal(0, f.Refresh.Scans);
    }

    [Fact]
    public async Task AMoveStillGivesUpAfterTheWaitSoTheCoordinatorRollsBackSafely()
    {
        using var f = new Fixture(sharedAppearsAt: int.MaxValue);
        Assert.Equal("replacementUnavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Adapter.ImportAsync(f.Target, [], default))).Message);
        Assert.Equal(RemovalPolicy.IndexPolls, f.Looks);
    }

    [Fact]
    public async Task ARestoreAsksForTheRestoredMoviesFolderOnlyAndWaitsTheFullIndexWait()
    {
        using var f = new Fixture(sharedAppearsAt: int.MaxValue);
        Assert.Equal("restoredItemNotIndexed", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Adapter.RestoreAsync(f.Target, [], [], default))).Message);
        Assert.Equal(RemovalPolicy.IndexPolls, f.Looks);
        Assert.Equal([Path.GetDirectoryName(f.Target.Path)!], f.Refresh.Nearest);
        Assert.Empty(f.Refresh.LibraryScans); // was a refresh of the whole local library
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Peer = "11111111111111111111111111111111";
        private readonly TempDirectory _dir = new();
        internal readonly ShoalState State = new();
        internal readonly LibraryMapping Mapping = new() { MappingKey = "movies-1", Subfolder = "movies", CollectionType = "movies", Import = true };
        internal readonly ShoalLibrary Library = new() { LibKey = "aaaaaaaaaaaaaaaa", PathRel = "libs/aaaaaaaaaaaaaaaa", Type = "movies", Feeds = [Peer] };
        internal readonly RemovalTarget Target;
        internal readonly Movie Stub;
        internal readonly string StubFolder;
        internal readonly NearestRefresh Refresh = new() { IsScanRunning = true }; // a scan runs, as in the lab: no catalog sync is attempted
        internal readonly JellyfinRemovalLibrary Adapter;
        internal int Looks;

        internal Fixture(int sharedAppearsAt)
        {
            State.StubRoot = _dir.Combine("stubs");
            State.Home.Peers.Add(new() { PeerKey = Peer, Enabled = true, Pairing = new() { Method = PairingMethod.Invite }, Libraries = [Mapping] });
            State.Home.Libraries.Add(Library);
            var root = _dir.Combine("media");
            Directory.CreateDirectory(Path.Combine(root, "Fixture (2024)"));
            var path = Path.Combine(root, "Fixture (2024)", "movie.mp4");
            var ids = new Dictionary<string, string> { ["Tmdb"] = "123" };
            Target = new() { PeerKey = Peer, MappingKey = Mapping.MappingKey, PeerItemId = Guid.NewGuid(), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Fixture", Root = root, Path = path, RecoveryRoot = _dir.Combine(".shoal-recovery"), ProviderIds = ids, RuntimeTicks = 200_000_000, Bytes = 3, Generation = "fixture" };
            var folder = LibraryFolders.FolderOf(State, Peer, Mapping)!;
            StubFolder = Path.Combine(folder, "Fixture (2024)");
            Directory.CreateDirectory(StubFolder);
            var stubPath = Path.Combine(StubFolder, "Fixture (2024).strm");
            File.WriteAllText(stubPath, StubSentinel.Content("bbbbbbbbbbbbbbbb"));
            Stub = new Movie { Id = Guid.NewGuid(), Name = "Fixture", Path = stubPath, RunTimeTicks = 200_000_000, ProviderIds = new(ids) };
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, _) => m.Name switch
            {
                // Core indexes the shared copy only on the given look; the restored original never shows (the Restore test waits it out).
                "GetItemList" => ++Looks >= sharedAppearsAt ? new List<BaseItem> { Stub } : new List<BaseItem>(),
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : m.Name == "get_Current" ? State : null);
            var catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger<CatalogStore>.Instance);
            catalogs.Save(new() { PeerKey = Peer, Entries = [new() { Kind = EntryKind.Movie, State = EntryState.Active, MappingKey = Mapping.MappingKey, PeerItemId = Target.PeerItemId.ToString("N"), StubKey = "bbbbbbbbbbbbbbbb", StubRelPath = "Fixture (2024)/Fixture (2024).strm", Title = "Fixture", ProviderIds = ids }] });
            var federated = RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create((m, args) => m.Name == "Find" && (Guid)args[0]! == Stub.Id ? new FederatedItem(Peer, Mapping.MappingKey) : null);
            Adapter = new(library, null!, RemoveServerGhostTests.CoreProxy<IUserManager>.Create(), RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create(), RemoveServerGhostTests.CoreProxy<ISessionManager>.Create(), federated, RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), state, catalogs, null!, Refresh, new GroupBook(new MemoryGroupStore(), NullLogger.Instance))
            {
                Delay = (_, _) => Task.CompletedTask,
            };
        }

        public void Dispose()
        {
            Adapter.Dispose();
            _dir.Dispose();
        }
    }

    private sealed class NearestRefresh : ILibraryRefresh
    {
        public bool IsScanRunning { get; set; }

        public List<string> Nearest { get; } = [];

        public List<string> LibraryScans { get; } = [];

        public int Scans { get; private set; }

        public void ReportChanged(string path)
        {
        }

        public void QueueScan() => Scans++;

        public void ScanLibrariesAt(IReadOnlyCollection<string> folders) => LibraryScans.AddRange(folders);

        public void RefreshNearest(string path) => Nearest.Add(path);
    }
}
