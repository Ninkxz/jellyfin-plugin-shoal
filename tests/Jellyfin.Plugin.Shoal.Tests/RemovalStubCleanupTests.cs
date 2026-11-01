using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.10.3 P2-ROLLBACK-TIMING-7DEF195 (docs/tests/pilot2-simple-removal-2.10.3.md, lab B, 7def195): after a rollback or a Restore the shared copy's
/// stub folder stayed in the Shoal stub folder, and the next move of that title rolled back at once. Cause: the restore retired the stub with one
/// try, at the first look where core listed the restored movie; core saves a new item before it probes its runtime (and a sync may hold the gate),
/// so that try was refused and nothing retried it. These tests use the real sync, catalog and stub on disk.
/// </summary>
public sealed class RemovalStubCleanupTests
{
    [Fact]
    public async Task ARestoreRetiresTheStubOnceCoreHasProbedTheRestoredMovie()
    {
        await using var f = await Fixture.Create(unprobedLooks: 3);
        await f.Restore();
        Assert.False(File.Exists(f.StubPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.StubPath)));
        var entry = f.Entry();
        Assert.Equal(EntryState.DuplicateLocal, entry.State);
        Assert.Null(entry.StubRelPath);
        Assert.True(File.Exists(f.Target.Path)); // the restored original is never touched
    }

    [Fact]
    public async Task ARestoreRetriesTheStubWhileASyncHoldsTheGate()
    {
        await using var f = await Fixture.Create(unprobedLooks: 0);
        var held = f.Rig.Sync.TryHold();
        Assert.NotNull(held);
        f.Adapter.Delay = (_, _) =>
        {
            if (++f.Delays == 4) held!.Dispose(); // the running sync ends two seconds later
            return Task.CompletedTask;
        };

        await f.Restore();
        Assert.False(File.Exists(f.StubPath));
        Assert.Null(f.Entry().StubRelPath);
    }

    [Fact]
    public async Task AStubTheRetirementChecksRefuseStaysAndTheRestoreStillSucceeds()
    {
        await using var f = await Fixture.Create(unprobedLooks: 0);
        File.WriteAllText(f.StubPath, "http://unrelated.invalid/movie"); // not Shoal's sentinel any more: never deleted
        Assert.Equal(f.RestoredId, await f.Restore());
        Assert.True(File.Exists(f.StubPath));
        Assert.Equal(RemovalPolicy.RetireLooks - 1, f.Delays); // bounded: 30 s of looks, then left to the next sync
        Assert.NotNull(f.Entry().StubRelPath);
    }

    [Fact]
    public async Task ARestoredMovieThatNeverSettlesLeavesTheStubWithinTheIndexWait()
    {
        await using var f = await Fixture.Create(unprobedLooks: int.MaxValue);
        Assert.Equal(f.RestoredId, await f.Restore());
        Assert.True(File.Exists(f.StubPath));
        Assert.Equal(RemovalPolicy.RetireLooks - 1, f.Delays); // 30 s at most, never the whole index wait again
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly SyncRig Rig;
        internal readonly RemovalTarget Target;
        internal readonly string StubPath;
        internal readonly Guid RestoredId = Guid.NewGuid();
        internal readonly JellyfinRemovalLibrary Adapter;
        internal int Delays;
        private readonly string _stubKey;
        private int _indexedLooks;

        private Fixture(SyncRig rig, int unprobedLooks)
        {
            Rig = rig;
            var entry = Assert.Single(rig.Entries);
            _stubKey = entry.StubKey;
            StubPath = Path.GetFullPath(Path.Combine(rig.LibraryFolder, entry.StubRelPath!));
            var root = rig.Dir.Combine("physical");
            Directory.CreateDirectory(Path.Combine(root, "Film (2000)"));
            var path = Path.Combine(root, "Film (2000)", "Film (2000).mkv");
            File.WriteAllBytes(path, [1, 2, 3]);
            rig.Local.Movies.Add(new(new(1, null, null), "Film", 2000, 3)); // the restored original, as the dedup sees it
            Target = new() { PeerKey = SyncRig.PeerKey, MappingKey = SyncRig.MappingKey, PeerItemId = Guid.Parse(entry.PeerItemId), LocalItemId = Guid.NewGuid(), LibraryId = Guid.NewGuid(), Title = "Film", Root = root, Path = path, RecoveryRoot = rig.Dir.Combine(".shoal-recovery"), ProviderIds = new Dictionary<string, string>(entry.ProviderIds), RuntimeTicks = 72_000_000_000, Bytes = 3, Generation = "fixture" };
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, args) => m.Name switch
            {
                // Core lists the restored movie at once but probes its runtime only after the given number of looks by id (lab: new item, then ffprobe).
                "GetItemList" when ((InternalItemsQuery)args[0]!).ItemIds.Length > 0 => new List<BaseItem> { Restored(++_indexedLooks > unprobedLooks) },
                "GetItemList" => new List<BaseItem> { Restored(false) },
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            Adapter = new(library, null!, RemoveServerGhostTests.CoreProxy<IUserManager>.Create(), RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create(), RemoveServerGhostTests.CoreProxy<ISessionManager>.Create(), RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create(), RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), rig.State, rig.Catalogs, rig.Sync, rig.Refresh, new GroupBook(new MemoryGroupStore(), NullLogger.Instance))
            {
                Delay = (_, _) =>
                {
                    Delays++;
                    return Task.CompletedTask;
                },
            };
        }

        internal static async Task<Fixture> Create(int unprobedLooks)
        {
            var rig = await SyncRig.CreateAsync(p => p.Add("Film", 2000, tmdb: 1));
            await rig.SyncAsync();
            return new(rig, unprobedLooks);
        }

        internal Task<Guid> Restore() => Adapter.RestoreAsync(Target, [], [], default);

        internal CatalogEntry Entry() => Rig.Entries.Single(e => e.StubKey == _stubKey);

        public async ValueTask DisposeAsync()
        {
            Adapter.Dispose();
            await Rig.DisposeAsync();
        }

        private Movie Restored(bool probed) => new SingleFileMovie { Id = RestoredId, Path = Target.Path, RunTimeTicks = probed ? Target.RuntimeTicks : null, ProviderIds = new(Target.ProviderIds) };
    }

    private sealed class SingleFileMovie : Movie
    {
        public override bool HasLocalAlternateVersions => false;
    }
}
