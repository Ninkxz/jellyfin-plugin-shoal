using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

internal sealed class FakeItemPaths : IItemPaths
{
    public Dictionary<Guid, string> Items { get; } = [];

    public int PathLookups { get; private set; }

    public event EventHandler<ItemPathEventArgs>? Added;

    public event EventHandler<ItemPathEventArgs>? Removed;

    public string? PathOf(Guid itemId)
    {
        PathLookups++;
        return Items.GetValueOrDefault(itemId);
    }

    public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) => [.. Items.Select(static p => new ItemPathEventArgs(p.Key, p.Value))];

    public void Add(Guid id, string path)
    {
        Items[id] = path;
        Added?.Invoke(this, new ItemPathEventArgs(id, path));
    }

    public void Remove(Guid id)
    {
        var path = Items[id];
        Items.Remove(id);
        Removed?.Invoke(this, new ItemPathEventArgs(id, path));
    }
}

/// <summary>
/// K12: ShoalIndex (DESIGN 3.5), StubEnricher and the sentinel (3.4, T-SEC-6).
/// </summary>
public sealed class ShoalIndexTests
{
    private static ShoalState StateWith(string root)
    {
        var state = new ShoalState { StubRoot = root };
        state.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyA, Libraries = { new LibraryMapping { MappingKey = "m1", Subfolder = "movies" } } });
        return state;
    }

    private static string StubPath(string root, string folder = "A (2000) [tmdbid-1]") => Path.Combine(root, StateSamples.PeerKeyA, "movies", folder, folder + ".strm");

    private sealed class Rig : IDisposable
    {
        public Rig(string? stubRoot = null)
        {
            Dir = new TempDirectory();
            Root = stubRoot ?? Dir.Combine("stubs");
            Store = new StateStore(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(Dir.Path), NullLogger.Instance, TimeProvider.System, null);
            Store.UpdateAsync(s =>
            {
                s.StubRoot = Root;
                s.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyA, Libraries = { new LibraryMapping { MappingKey = "m1", Subfolder = "movies" } } });
            }).GetAwaiter().GetResult();
            Index = new ShoalIndex(Items, Store, NullLogger.Instance);
        }

        public TempDirectory Dir { get; }

        public string Root { get; }

        public StateStore Store { get; }

        public FakeItemPaths Items { get; } = new();

        public ShoalIndex Index { get; }

        public void Dispose()
        {
            Index.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task TheIndex_HoldsOnlyItemsBelowAMappedLibrary()
    {
        using var rig = new Rig();
        var federated = Guid.NewGuid();
        var local = Guid.NewGuid();
        var otherPeer = Guid.NewGuid();
        var otherLibrary = Guid.NewGuid();
        rig.Items.Items[federated] = StubPath(rig.Root);
        rig.Items.Items[local] = Path.Combine(Path.GetTempPath(), "movies", "Local (2001)", "Local (2001).mkv");
        rig.Items.Items[otherPeer] = Path.Combine(rig.Root, StateSamples.PeerKeyB, "movies", "X", "X.strm");
        rig.Items.Items[otherLibrary] = Path.Combine(rig.Root, StateSamples.PeerKeyA, "tv", "X", "X.strm");

        await rig.Index.RebuildAsync();

        Assert.True(rig.Index.IsReady);
        Assert.Equal(1, rig.Index.Count);
        Assert.True(rig.Index.Contains(federated));
        Assert.False(rig.Index.Contains(local));
        Assert.False(rig.Index.Contains(otherPeer));
        Assert.False(rig.Index.Contains(otherLibrary));
        Assert.Equal(new IndexedStub(StateSamples.PeerKeyA, "m1", "A (2000) [tmdbid-1]", "A (2000) [tmdbid-1].strm"), rig.Index.Get(federated));
        Assert.Equal(new FederatedItem(StateSamples.PeerKeyA, "m1"), rig.Index.Find(federated));
    }

    [Fact]
    public async Task Contains_IsAPureLookup_AndOnlyTheFallbackAsksCore()
    {
        using var rig = new Rig();
        var late = Guid.NewGuid();
        await rig.Index.RebuildAsync();
        rig.Items.Items[late] = StubPath(rig.Root); // scanned after the last snapshot, and no event reached us

        Assert.False(rig.Index.Contains(late));
        Assert.Equal(0, rig.Items.PathLookups);

        Assert.True(rig.Index.ContainsOrClassify(late)); // the path rule finds it
        Assert.Equal(1, rig.Items.PathLookups);
        Assert.True(rig.Index.Contains(late)); // and it is remembered
        Assert.False(rig.Index.ContainsOrClassify(Guid.NewGuid()));
    }

    [Fact]
    public async Task ItemEvents_KeepTheIndexCurrent()
    {
        using var rig = new Rig();
        rig.Index.Start();
        await rig.Index.RebuildAsync();
        var id = Guid.NewGuid();

        rig.Items.Add(id, StubPath(rig.Root));
        Assert.True(rig.Index.Contains(id));

        rig.Items.Remove(id);
        Assert.False(rig.Index.Contains(id));

        var local = Guid.NewGuid();
        rig.Items.Add(local, Path.Combine(Path.GetTempPath(), "x", "y.mkv"));
        Assert.False(rig.Index.Contains(local));
    }

    [Fact]
    public async Task AnEmptyStubRoot_FederatesNothing()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        rig.Items.Items[id] = StubPath(rig.Root);
        await rig.Index.RebuildAsync();
        Assert.True(rig.Index.Contains(id));

        await rig.Store.UpdateAsync(s => s.StubRoot = string.Empty);
        await rig.Index.RebuildAsync();

        Assert.Equal(0, rig.Index.Count);
        Assert.False(rig.Index.Contains(id));
        Assert.False(rig.Index.ContainsOrClassify(id));
        Assert.Null(rig.Index.Find(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnInvalidStubRoot_ClassifiesNothing(string root)
    {
        var state = StateWith(root);

        Assert.Null(ShoalIndex.Classify(state, StubPath("/stubs")));
        Assert.Null(ShoalIndex.FromPath(state, StubPath("/stubs")));
    }

    [Fact]
    public async Task ChangingTheStubRoot_RebuildsTheIndex()
    {
        using var rig = new Rig();
        rig.Index.Start();
        var id = Guid.NewGuid();
        rig.Items.Items[id] = StubPath(rig.Root);
        await rig.Index.RebuildAsync();
        Assert.True(rig.Index.Contains(id));

        await rig.Store.UpdateAsync(s => s.StubRoot = rig.Dir.Combine("elsewhere"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.Index.Contains(id) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.False(rig.Index.Contains(id));
    }

    [Fact]
    public void ThePathRule_WinsOverAClaimedNameInTheMiddle()
    {
        var state = StateWith("/stubs");

        Assert.Null(ShoalIndex.Classify(state, "/stubs/../other/" + StateSamples.PeerKeyA + "/movies/A/A.strm"));
        Assert.Null(ShoalIndex.Classify(state, "/stubs-evil/" + StateSamples.PeerKeyA + "/movies/A/A.strm"));
        Assert.NotNull(ShoalIndex.Classify(state, "/stubs/" + StateSamples.PeerKeyA + "/movies/A/A.strm"));
    }

    // --- enricher

    private sealed class FakeStubItems : IStubItems
    {
        public List<(Guid Id, string Path, string Title, StubFacts Facts, bool Merged)> Items { get; } = [];

        public int Applied { get; private set; }

        public bool FailFirst { get; set; }

        public List<StubFacts> Written { get; } = [];

        public IReadOnlyList<StubItemView> List() => [.. Items.Select((item, index) => new StubItemView(item.Id, item.Path, item.Title, item.Facts, item.Merged, (facts, _) =>
        {
            if (FailFirst && Applied == 0)
            {
                Applied++;
                throw new InvalidOperationException("save failed");
            }

            Applied++;
            Written.Add(facts);
            Items[index] = Items[index] with { Facts = facts };
            return Task.CompletedTask;
        }))];

        public void ForceRefresh()
        {
            for (var i = 0; i < Items.Count; i++)
            {
                Items[i] = Items[i] with { Facts = new StubFacts(0, null, 0, 0, 0, 0) };
            }
        }
    }

    private static async Task<(SyncRig Rig, ShoalIndex Index, FakeStubItems Items, StubEnricher Enricher, CatalogEntry Entry)> EnricherRigAsync()
    {
        var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var entry = rig.Entries.Single();
        var items = new FakeStubItems();
        var index = new ShoalIndex(new FakeItemPaths(), rig.State, NullLogger.Instance);
        var enricher = new StubEnricher(index, items, new StubCatalog(rig.Catalogs), rig.State, NullLogger.Instance);
        items.Items.Add((Guid.NewGuid(), Path.Combine(rig.LibraryFolder, entry.StubRelPath!.Replace('/', Path.DirectorySeparatorChar)), "The Matrix", new StubFacts(0, null, 0, 0, 0, 0), false));
        return (rig, index, items, enricher, entry);
    }

    [Fact]
    public async Task TheEnricher_WritesTheCatalogsFactsOntoTheStub_OnlyWhenTheyDiffer()
    {
        var (rig, index, items, enricher, entry) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var first = await enricher.RunAsync();

                Assert.Equal(1, first.Checked);
                Assert.Equal(1, first.Updated);
                Assert.Empty(first.Merged);
                var written = Assert.Single(items.Written);
                Assert.Equal(new StubFacts(72_000_000_000, "mkv", 4_000_000, 1920, 1080, 8_000_000), written);
                Assert.Equal(StubEnricher.FactsOf(entry), written);

                var second = await enricher.RunAsync();

                Assert.Equal(1, second.Checked);
                Assert.Equal(0, second.Updated);
                Assert.Equal(1, items.Applied);
            }
        }
    }

    [Fact]
    public async Task AForcedRefresh_DropsTheDuration_AndTheNextPassPutsItBack()
    {
        var (rig, index, items, enricher, _) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                await enricher.RunAsync();
                items.ForceRefresh(); // core re-reads the stub file and finds no media: the runtime is gone

                var result = await enricher.RunAsync();

                Assert.Equal(1, result.Updated);
                Assert.Equal(72_000_000_000, items.Items[0].Facts.RunTimeTicks);
            }
        }
    }

    [Fact]
    public async Task TheEnricher_NeverWritesText_OnlyNumbersAndTheContainer()
    {
        var properties = typeof(StubFacts).GetProperties().Where(static p => p.PropertyType == typeof(string)).Select(static p => p.Name).ToList();

        Assert.Equal(["Container"], properties); // no titles, languages or stream names can be written
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheEnricher_LeavesDuplicatesUnknownStubsAndForeignItemsAlone()
    {
        var (rig, index, items, enricher, entry) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var document = rig.Catalog();
                document.Entries[0].State = EntryState.DuplicateLocal;
                rig.Catalogs.Save(document);
                items.Items.Add((Guid.NewGuid(), Path.Combine(rig.LibraryFolder, "Unknown (2000)", "Unknown (2000).strm"), "Unknown", new StubFacts(0, null, 0, 0, 0, 0), false));
                items.Items.Add((Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "local", "Local.mkv"), "Local", new StubFacts(1, "mkv", 1, 1, 1, 1), false));

                var result = await enricher.RunAsync();

                Assert.Equal(2, result.Checked); // the local file is not a stub at all
                Assert.Equal(0, result.Updated);
                Assert.Empty(items.Written);
                _ = entry;
            }
        }
    }

    [Fact]
    public async Task AMergedStub_IsReportedForTheAdmin()
    {
        var (rig, index, items, enricher, _) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                items.Items[0] = items.Items[0] with { Merged = true };

                var result = await enricher.RunAsync();

                var warning = Assert.Single(result.Merged);
                Assert.Equal("The Matrix", warning.Title);
                Assert.Equal(warning, Assert.Single(enricher.MergedVersions));

                items.Items[0] = items.Items[0] with { Merged = false };
                await enricher.RunAsync();
                Assert.Empty(enricher.MergedVersions);
            }
        }
    }

    [Fact]
    public async Task AFailedSave_DoesNotStopTheRest()
    {
        var (rig, index, items, enricher, entry) = await EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                rig.Peer.Add("Inception", 2010, tmdb: 27205);
                await rig.SyncAsync();
                var second = rig.Entries.Single(static e => e.Title == "Inception");
                items.Items.Add((Guid.NewGuid(), Path.Combine(rig.LibraryFolder, second.StubRelPath!.Replace('/', Path.DirectorySeparatorChar)), "Inception", new StubFacts(0, null, 0, 0, 0, 0), false));
                items.FailFirst = true;

                var result = await enricher.RunAsync();

                Assert.Equal(2, result.Checked);
                Assert.Equal(1, result.Updated);
                _ = entry;
            }
        }
    }

    [Fact]
    public async Task TheCatalogLookup_FollowsTheFile()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("Keeper", 2001, tmdb: 7);
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var catalog = new StubCatalog(rig.Catalogs);
        var entry = rig.Entries.Single(static e => e.Title == "The Matrix");
        var folder = entry.StubRelPath!.Split('/')[0];

        Assert.Equal(entry.StubKey, catalog.Find(SyncRig.PeerKey, folder)!.StubKey);
        Assert.Null(catalog.Find(SyncRig.PeerKey, "Nope"));
        Assert.Null(catalog.Find("0000000000000000", folder));

        rig.Peer.Movies[1].Deleted = true;
        await rig.SyncAsync(); // the stub is removed and the file is rewritten
        Assert.Null(catalog.Find(SyncRig.PeerKey, folder));
    }

    // --- sentinel (T-SEC-6)

    [Fact]
    public async Task T_SEC_6_TheStrmFilesHoldOnlyTheSentinel_AndNothingElseNamesThePeer()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603, imdb: "tt0133093");
        rig.Peer.Add("Inception", 2010, imdb: "tt1375666");
        await rig.SyncAsync();

        var strms = rig.AllStubFiles().Where(static f => f.EndsWith(".strm", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, strms.Count);
        foreach (var strm in strms)
        {
            var text = File.ReadAllText(strm);
            Assert.True(StubSentinel.TryParse(text, out var key));
            Assert.Equal(StubSentinel.Content(key), text);
            Assert.Contains(rig.Entries, e => e.StubKey == key);
        }

        foreach (var nfo in rig.AllStubFiles().Where(static f => f.EndsWith(".nfo", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(nfo);
            Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(matrix.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(rig.AllStubFiles(), f => File.ReadAllText(f).Contains("token-for-tests", StringComparison.Ordinal));
    }
}
