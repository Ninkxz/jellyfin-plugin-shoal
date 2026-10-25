using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class CatalogRecoveryTests
{
    [Fact]
    public void ValidOlderBackup_UnexplainedNewerStub_HoldsBeforeAdding()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var store = NewStore(paths);
        var doc = new CatalogDocument { PeerKey = StateSamples.PeerKeyA };
        store.Save(doc);
        store.Save(doc);
        var path = Path.Combine(paths.PeerDir(StateSamples.PeerKeyA), "catalog.json");
        File.Delete(path);
        var folder = Path.Combine(paths.DefaultStubRoot, StateSamples.PeerKeyA, "movies", "Newer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Newer.strm"), StubSentinel.Content("newerstub0000001"));
        var loaded = NewStore(paths).Load(StateSamples.PeerKeyA);
        Assert.True(loaded.Unreadable);
        Assert.True(loaded.Document.LoadedWithLoss);
        Assert.Throws<InvalidOperationException>(() => store.Save(loaded.Document));
    }
    [Fact]
    public void MissingPrimary_UsesValidBackup()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var store = NewStore(paths);
        store.Save(new CatalogDocument { PeerKey = StateSamples.PeerKeyA, CompletedOnce = true });
        var path = Path.Combine(paths.PeerDir(StateSamples.PeerKeyA), "catalog.json");
        File.Move(path, path + ".bak");
        var result = NewStore(paths).Load(StateSamples.PeerKeyA);
        Assert.False(result.Unreadable);
        Assert.True(result.Document.CompletedOnce);
        Assert.True(result.Document.LoadedWithLoss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostCatalog_HoldsAcrossRestartAndRefusesSave(bool corrupt)
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        NewStore(paths).Save(new CatalogDocument { PeerKey = StateSamples.PeerKeyA });
        var path = Path.Combine(paths.PeerDir(StateSamples.PeerKeyA), "catalog.json");
        if (corrupt)
        {
            File.WriteAllText(path, "broken");
        }
        else
        {
            File.Delete(path);
        }

        var restarted = NewStore(paths);
        var held = restarted.Load(StateSamples.PeerKeyA);
        Assert.True(held.Unreadable);
        Assert.Throws<InvalidOperationException>(() => restarted.Save(held.Document));
        Assert.Equal(corrupt, File.Exists(path));
        Assert.True(NewStore(paths).Load(StateSamples.PeerKeyA).Unreadable);
    }

    [Fact]
    public void LegacyArtifactWithoutMarker_HoldsAndPreservesSentinel()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var folder = Path.Combine(paths.DefaultStubRoot, StateSamples.PeerKeyA, "Movies", "Film");
        Directory.CreateDirectory(folder);
        var sentinel = Path.Combine(folder, "Film.strm");
        var content = StubSentinel.Content("abcdefghijklmnop");
        File.WriteAllText(sentinel, content);
        Assert.True(NewStore(paths).Load(StateSamples.PeerKeyA).Unreadable);
        Assert.Equal(content, File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2048)]
    public void AmbiguousLegacyStub_Holds(int size)
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var folder = Path.Combine(paths.DefaultStubRoot, StateSamples.PeerKeyA);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "unknown.strm"), new string('x', size));
        Assert.True(NewStore(paths).Load(StateSamples.PeerKeyA).Unreadable);
    }

    [Fact]
    public void FirstImport_EmptyCatalogIsWritable()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var store = NewStore(paths);
        var first = store.Load(StateSamples.PeerKeyA);
        Assert.False(first.Unreadable);
        store.Save(first.Document);
        Assert.False(NewStore(paths).Load(StateSamples.PeerKeyA).Unreadable);
    }

    private static CatalogStore NewStore(ShoalPaths paths) => new(paths, NullLogger<CatalogStore>.Instance);

    [Fact]
    public async Task LayoutAndMigration_LostCatalogHoldBeforeAnyMutation()
    {
        await using var rig = await JobRig.CreateAsync(p => p.Add("Film", 2000, tmdb: 1));
        var path = Path.Combine(rig.Sync.Paths.PeerDir(SyncRig.PeerKey), "catalog.json");
        File.Delete(path);
        File.Delete(path + ".bak");
        var before = Directory.GetFiles(rig.Sync.StubRoot, "*", SearchOption.AllDirectories).Order().ToArray();
        var count = rig.State.Current.Home.Libraries.Count;
        var error = await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", null, new LibraryCreateRequest("New", "movies", LibraryPlacement.Shoal))], rig.Admin.Id);
        Assert.Equal(LayoutJobError.RecoveryMode, error);
        var migration = new GroupMigration(rig.State, rig.Sync.Catalogs, rig.Groups);
        Assert.Equal("catalogUnreadable", migration.Preview().Refused);
        Assert.Equal("catalogUnreadable", (await migration.RunAsync()).Refused);
        Assert.Equal(count, rig.State.Current.Home.Libraries.Count);
        Assert.Equal(before, Directory.GetFiles(rig.Sync.StubRoot, "*", SearchOption.AllDirectories).Order().ToArray());
    }
}
