using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K28 (DESIGN v2.6.4): alternate versions on the sync side. T-ALT-2 (the selector), T-ALT-3 (sync), T-ALT-4 (guards count titles) and T-ALT-5 (the
/// resolver). The switch is off by default, and then the sync is v1.
/// </summary>
public sealed class AlternateVersionsTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid D = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static SanitizedSource Src(Guid id, int height, int bitrate = 8_000_000, long size = 4_000_000, string codec = "h264", int? width = null) =>
        new(id, size, bitrate, "mkv", codec, width ?? (height * 16 / 9), height, 72_000_000_000L);

    // --- T-ALT-2: the selector

    [Fact]
    public void T_ALT_2_OneSource_IsTheSelection_AndThereIsNothingToSelectFromNone()
    {
        Assert.Equal([A], SourceSelector.Select([Src(A, 1080)], A).Select(static s => s.Id));
        Assert.Empty(SourceSelector.Select([], null));
    }

    [Fact]
    public void T_ALT_2_FourSources_AreAllKept_WithThePrimaryFirst()
    {
        var sources = new[] { Src(A, 720), Src(B, 1080), Src(C, 2160), Src(D, 480) };

        var selected = SourceSelector.Select(sources, A);

        Assert.Equal([A, C, B, D], selected.Select(static s => s.Id)); // the primary, then by height
    }

    [Fact]
    public void T_ALT_2_FiveAndSixtyFourSources_KeepThePrimaryAndTheBestThree_AndOnlyTheFirstSixtyFourAreLookedAt()
    {
        var five = new[] { Src(A, 480), Src(B, 1080), Src(C, 2160), Src(D, 720), Src(Guid.NewGuid(), 576) };
        Assert.Equal([A, C, B, D], SourceSelector.Select(five, A).Select(static s => s.Id));

        var many = Enumerable.Range(0, 70).Select(i => Src(Guid.NewGuid(), 100 + i, size: 1000 + i)).ToList();
        var best = Src(Guid.NewGuid(), 9999, size: 1);
        many.Add(best); // the 71st: beyond the 64 that are looked at
        var selected = SourceSelector.Select(many, many[0].Id);

        Assert.Equal(4, selected.Count);
        Assert.Equal(many[0].Id, selected[0].Id);
        Assert.DoesNotContain(best, selected);
        Assert.Equal([many[0].Id, many[63].Id, many[62].Id, many[61].Id], selected.Select(static s => s.Id));
    }

    [Fact]
    public void T_ALT_2_TiesBreakByBitrateThenSize_AndTwoIdenticalFilesCollapse()
    {
        var sources = new[]
        {
            Src(A, 1080, bitrate: 5_000_000, size: 9),
            Src(B, 1080, bitrate: 6_000_000, size: 1),
            Src(C, 1080, bitrate: 6_000_000, size: 2, codec: "hevc"),
            Src(D, 1080, bitrate: 6_000_000, size: 2, codec: "hevc"), // the same width, height, codec and size as C
        };

        var selected = SourceSelector.Select(sources, A);

        Assert.Equal([A, C, B], selected.Select(static s => s.Id)); // C before B on size; D collapsed into C
    }

    [Fact]
    public void T_ALT_2_ThePrimarySurvivesACollapse_AndWhenItsSourceIsGoneTheBestTakesOver()
    {
        var twin = Src(D, 1080);
        var sources = new[] { Src(B, 1080), twin, Src(C, 480) };

        Assert.Equal(D, SourceSelector.Select(sources, D)[0].Id); // D is the title's own primary: it, not its earlier twin, is kept
        Assert.Equal(B, SourceSelector.Select(sources, A)[0].Id); // A is gone: the best of what is left (the first of the twins)
    }

    [Theory]
    [InlineData(2160, "2160p")]
    [InlineData(2000, "2160p")]
    [InlineData(1999, "1080p")]
    [InlineData(1080, "1080p")]
    [InlineData(1000, "1080p")]
    [InlineData(999, "720p")]
    [InlineData(720, "720p")]
    [InlineData(700, "720p")]
    [InlineData(699, "480p")]
    [InlineData(480, "480p")]
    [InlineData(450, "480p")]
    [InlineData(449, "SD")]
    [InlineData(0, "SD")]
    public void T_ALT_2_TheLabelComesFromTheHeightAlone(int height, string expected)
    {
        Assert.Equal(expected, SourceSelector.Label(Src(A, height), new HashSet<string>()));
        Assert.Equal("SD", SourceSelector.Label(new SanitizedSource(A, null, null, null, null, null, null, null), new HashSet<string>()));
    }

    [Fact]
    public void T_ALT_2_AClashAddsTheCodec_ThenANumber_AndEveryLabelIsLettersDigitsAndSpaces()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();
        foreach (var codec in new[] { "h264", "hevc", "hevc", "hevc", "av1" })
        {
            var label = SourceSelector.Label(Src(Guid.NewGuid(), 1080, codec: codec), taken);
            taken.Add(label);
            labels.Add(label);
        }

        Assert.Equal(["1080p", "1080p hevc", "1080p hevc 2", "1080p hevc 3", "1080p av1"], labels);
        Assert.All(labels, static l => Assert.Matches("^[A-Za-z0-9 ]{1,40}$", l));
        Assert.Equal("1080p", SourceSelector.Label(Src(A, 1080, codec: "h.26/4‮"), new HashSet<string>())); // a codec is never used unless it is plain
    }

    // --- T-ALT-3: sync

    private static PeerMovie Movie(FakeLibraryPeer peer, params PeerSource[] sources) => peer.Add("The Matrix", 1999, tmdb: 603, configure: m => m.Sources = [.. sources]);

    private static PeerSource Ps(Guid id, int height, string codec = "h264", int bitrate = 8_000_000, long size = 4_000_000) =>
        new() { Id = id, Height = height, Width = height * 16 / 9, Codec = codec, Bitrate = bitrate, Size = size };

    private static async Task<SyncRig> RigOnAsync(Action<FakeLibraryPeer>? seed = null)
    {
        var rig = await SyncRig.CreateAsync(seed);
        rig.Settings = new SyncSettings(25, 10, 500, 20, 2, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard) { AlternateVersions = true };
        return rig;
    }

    private static IEnumerable<string> Strms(SyncRig rig) => rig.AllStubFiles().Where(static f => f.EndsWith(".strm", StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    [Fact]
    public async Task T_ALT_3_WithTheSwitchOff_AThreeSourceTitle_IsOneStub_ExactlyAsInV1()
    {
        await using var rig = await SyncRig.CreateAsync(p => Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160)));

        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added);
        Assert.Single(Strms(rig));
        var entry = Assert.Single(rig.Entries);
        Assert.Single(entry.Sources);
        Assert.Equal(A.ToString("N"), entry.PeerMediaSourceId); // the peer's first source, as before
        Assert.False(rig.Catalog().AlternateVersionsApplied);
    }

    [Fact]
    public async Task T_ALT_3_WithTheSwitchOn_ATitleGetsOneStubPerSource_WithTheNfoAndTheImagesOnce()
    {
        await using var rig = await RigOnAsync(p => Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160)));

        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added); // guards and counts are titles
        var entry = Assert.Single(rig.Entries);
        var folder = entry.StubRelPath!.Split('/')[0];
        Assert.Equal([folder + " - 2160p.strm", folder + " - 720p.strm", folder + ".strm"], Strms(rig));
        Assert.Single(rig.AllStubFiles(), static f => f.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.Equal(["2160p", "720p"], entry.Sources.Skip(1).Select(static s => s.Label));
        Assert.Equal(entry.StubKey, entry.Sources[0].StubKey);
        Assert.Equal(entry.StubRelPath, entry.Sources[0].StubRelPath);
        Assert.Equal(3, entry.Sources.Select(static s => s.StubKey).Distinct().Count());
        foreach (var source in entry.Sources)
        {
            var content = File.ReadAllText(Path.Combine(rig.LibraryFolder, source.StubRelPath!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(StubSentinel.TryParse(content, out var key));
            Assert.Equal(source.StubKey, key);
        }

        Assert.Equal(C.ToString("N"), entry.Sources[1].PeerMediaSourceId);
        Assert.Equal(2160, entry.Sources[1].Height);
        Assert.True(rig.Catalog().AlternateVersionsApplied);
    }

    [Fact]
    public async Task T_ALT_3_NoPeerTextAndNoIdIsInAnyFileName()
    {
        await using var rig = await RigOnAsync(p => Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160, "hevc")));

        await rig.SyncAsync();

        Assert.All(rig.AllStubFiles().Select(Path.GetFileName), static f => Assert.Matches(@"^[A-Za-z0-9 ()\[\]._-]+$", f!));
        Assert.DoesNotContain(rig.AllStubFiles(), f => f.Contains(A.ToString("N"), StringComparison.OrdinalIgnoreCase) || f.Contains(B.ToString("N"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T_ALT_3_MoreThanFourSources_GiveFourStubs()
    {
        var ids = Enumerable.Range(0, 6).Select(static i => Guid.NewGuid()).ToArray();
        await using var rig = await RigOnAsync(p => Movie(p, [.. ids.Select((id, i) => Ps(id, 480 + (i * 100)))]));

        await rig.SyncAsync();

        var entry = Assert.Single(rig.Entries);
        Assert.Equal(4, entry.Sources.Count);
        Assert.Equal(4, Strms(rig).Count());
        Assert.Equal([ids[0].ToString("N"), ids[5].ToString("N"), ids[4].ToString("N"), ids[3].ToString("N")], entry.Sources.Select(static s => s.PeerMediaSourceId)); // the peer's first, then the tallest three
    }

    [Fact]
    public async Task T_ALT_3_ADroppedExtraVersion_DeletesOnlyThatFile_AndANewOneIsWritten()
    {
        PeerMovie movie = null!;
        await using var rig = await RigOnAsync(p => movie = Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160)));
        await rig.SyncAsync();
        var before = rig.Entries.Single();
        var folder = before.StubRelPath!.Split('/')[0];

        movie.Sources = [Ps(A, 1080), Ps(C, 2160)];
        movie.Etag = "e2";
        await rig.SyncAsync();

        Assert.Equal([folder + " - 2160p.strm", folder + ".strm"], Strms(rig));
        var after = rig.Entries.Single();
        Assert.Equal(before.StubKey, after.StubKey);
        Assert.Equal(before.StubRelPath, after.StubRelPath);
        Assert.Equal(before.Sources[1].StubKey, after.Sources[1].StubKey); // the 2160p stub was not rewritten

        var d = Ps(D, 480);
        movie.Sources = [Ps(A, 1080), Ps(C, 2160), d];
        movie.Etag = "e3";
        await rig.SyncAsync();

        Assert.Equal([folder + " - 2160p.strm", folder + " - 480p.strm", folder + ".strm"], Strms(rig));
    }

    [Fact]
    public async Task T_ALT_3_WhenThePrimarysSourceDisappears_ThePrimaryIsReboundInPlace_AndTheFileOfThatSourceGoes()
    {
        PeerMovie movie = null!;
        await using var rig = await RigOnAsync(p => movie = Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160)));
        await rig.SyncAsync();
        var before = rig.Entries.Single();
        var folder = before.StubRelPath!.Split('/')[0];
        var primaryFile = Path.Combine(rig.LibraryFolder, before.StubRelPath.Replace('/', Path.DirectorySeparatorChar));
        var written = File.GetLastWriteTimeUtc(primaryFile);

        movie.Sources = [Ps(B, 720), Ps(C, 2160)]; // A, the primary's source, is gone
        movie.Etag = "e2";
        await rig.SyncAsync();

        var after = rig.Entries.Single();
        Assert.Equal(before.StubKey, after.StubKey); // key, path and watch state of the primary do not change
        Assert.Equal(before.StubRelPath, after.StubRelPath);
        Assert.Equal(written, File.GetLastWriteTimeUtc(primaryFile));
        Assert.Equal(C.ToString("N"), after.PeerMediaSourceId); // the best of what is left
        Assert.Equal(C.ToString("N"), after.Sources[0].PeerMediaSourceId);
        Assert.Equal(2160, after.Height);
        Assert.Equal([folder + " - 720p.strm", folder + ".strm"], Strms(rig)); // the 2160p file of the source that became the primary is deleted
        Assert.Equal([B.ToString("N")], after.Sources.Skip(1).Select(static s => s.PeerMediaSourceId));
    }

    [Fact]
    public async Task T_ALT_3_FlippingTheSwitch_RemovesTheExtras_ThroughTheNormalPath_AndWritesThemBackWithoutAnEtagChange()
    {
        await using var rig = await RigOnAsync(p => Movie(p, Ps(A, 1080), Ps(B, 720)));
        await rig.SyncAsync();
        var before = rig.Entries.Single();
        Assert.Equal(2, Strms(rig).Count());

        rig.Settings = SyncSettings.Default;
        var off = await rig.SyncAsync();

        Assert.Single(Strms(rig));
        Assert.Equal(0, off.Removed + off.Added); // titles untouched
        var offEntry = rig.Entries.Single();
        Assert.Equal(before.StubKey, offEntry.StubKey);
        Assert.Equal(before.StubRelPath, offEntry.StubRelPath);
        Assert.Single(offEntry.Sources);
        Assert.False(rig.Catalog().AlternateVersionsApplied);

        rig.Settings = new SyncSettings(25, 10, 500, 20, 2, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard) { AlternateVersions = true };
        await rig.SyncAsync();

        Assert.Equal(2, Strms(rig).Count()); // the etag did not change; the switch alone brought the extras back
        Assert.True(rig.Catalog().AlternateVersionsApplied);
        Assert.Equal(before.StubKey, rig.Entries.Single().StubKey);
    }

    [Fact]
    public async Task T_ALT_3_ATitleThatIsRemoved_TakesAllItsVersionsWithIt()
    {
        PeerMovie movie = null!;
        await using var rig = await RigOnAsync(p =>
        {
            movie = Movie(p, Ps(A, 1080), Ps(B, 720));
            p.Add("Alien", 1979, tmdb: 348);
        });
        await rig.SyncAsync();

        movie.Deleted = true;
        await rig.SyncAsync();

        Assert.Single(Strms(rig)); // Alien's; every version of The Matrix went with the title
        Assert.Equal(["Alien"], rig.Entries.Select(static e => e.Title));
    }

    // --- T-ALT-4: the guards count titles

    [Fact]
    public async Task T_ALT_4_TheMassDeletionGuard_CountsTitlesNotStubs()
    {
        var movies = new List<PeerMovie>();
        await using var rig = await RigOnAsync(p =>
        {
            for (var i = 0; i < 30; i++)
            {
                movies.Add(p.Add("Title " + i, 2000, tmdb: 1000 + i, configure: m => m.Sources = [Ps(Guid.NewGuid(), 1080), Ps(Guid.NewGuid(), 720), Ps(Guid.NewGuid(), 2160)]));
            }
        });
        await rig.SyncAsync();
        Assert.Equal(90, Strms(rig).Count());

        foreach (var movie in movies.Take(26))
        {
            movie.Deleted = true; // 26 titles = 78 stubs; the limit is 25 titles
        }

        var report = await rig.SyncAsync();

        Assert.Equal(26, report.HeldRemovals);
        Assert.Equal(90, Strms(rig).Count());
    }

    // --- T-ALT-5: the resolver

    [Fact]
    public async Task T_ALT_5_TheResolver_MapsEachVersionToItsOwnPeerSource_AndRefusesEverythingOutsideTheSet()
    {
        PeerMovie other = null!;
        await using var rig = await RigOnAsync(p =>
        {
            Movie(p, Ps(A, 1080), Ps(B, 720), Ps(C, 2160));
            other = p.Add("Alien", 1979, tmdb: 348, configure: m => m.Sources = [Ps(D, 1080)]);
        });
        await rig.SyncAsync();
        var entry = rig.Entries.Single(static e => e.Title == "The Matrix");
        var otherEntry = rig.Entries.Single(static e => e.Title == "Alien");
        var ids = new Dictionary<string, Guid>();
        var paths = new Dictionary<Guid, string>();
        foreach (var file in rig.AllStubFiles().Where(static f => f.EndsWith(".strm", StringComparison.Ordinal)))
        {
            var id = Guid.NewGuid();
            ids[Path.GetFileName(file)] = id;
            paths[id] = file;
        }

        var folder = entry.StubRelPath!.Split('/')[0];
        var primary = ids[folder + ".strm"];
        var hd = ids[folder + " - 2160p.strm"];
        var sd = ids[folder + " - 720p.strm"];
        var stranger = ids[otherEntry.StubRelPath!.Split('/')[1]];
        var index = new ShoalIndex(new FileItemPaths(paths), rig.State, NullLogger<ShoalIndex>.Instance);
        await index.RebuildAsync();
        var resolver = new ShoalVersionResolver(index, new StubCatalog(rig.Catalogs));
        var peerItem = Guid.ParseExact(entry.PeerItemId, "N");

        var own = resolver.Resolve(primary, null)!;
        Assert.Equal(new ResolvedVersion(primary, entry.StubKey, SyncRig.PeerKey, peerItem, A, entry.StubKey), own);
        Assert.Equal(own, resolver.Resolve(primary, primary.ToString("N")));
        Assert.Equal(own, resolver.Resolve(sd, primary.ToString("D"))); // asked from another version's route: the named version wins
        var second = resolver.Resolve(primary, hd.ToString("B"))!; // the braced form
        Assert.Equal(hd, second.VersionItemId);
        Assert.Equal(C, second.PeerMediaSourceId);
        Assert.Equal(entry.StubKey, second.SetId);
        Assert.Equal(entry.Sources[1].StubKey, second.StubKey);
        Assert.Equal(B, resolver.Resolve(sd, null)!.PeerMediaSourceId);
        Assert.Equal(peerItem, resolver.Resolve(sd, null)!.PeerItemId);

        Assert.Null(resolver.Resolve(primary, stranger.ToString("N"))); // another title's version
        Assert.Null(resolver.Resolve(primary, Guid.NewGuid().ToString("N"))); // not a stub at all
        Assert.Null(resolver.Resolve(primary, "not-a-guid"));
        Assert.Null(resolver.Resolve(primary, other.Id.ToString("N"))); // a Peer id is not a local one
        Assert.Null(resolver.Resolve(Guid.NewGuid(), null));
        Assert.Equal(otherEntry.StubKey, resolver.Resolve(stranger, null)!.SetId);
    }

    [Fact]
    public async Task TheEnricher_GivesEachVersionItsOwnFacts_AndDoesNotWarnAboutTheSetShoalWrote()
    {
        await using var rig = await RigOnAsync(p => Movie(p, Ps(A, 1080, size: 4_000_000), Ps(B, 720, size: 1_000_000), Ps(C, 2160, size: 9_000_000)));
        await rig.SyncAsync();
        var entry = rig.Entries.Single();
        var folder = entry.StubRelPath!.Split('/')[0];

        var primary = StubEnricher.FactsOfStub(entry, new IndexedStub(SyncRig.PeerKey, SyncRig.MappingKey, folder, folder + ".strm"));
        var hd = StubEnricher.FactsOfStub(entry, new IndexedStub(SyncRig.PeerKey, SyncRig.MappingKey, folder, folder + " - 2160p.strm"));
        var sd = StubEnricher.FactsOfStub(entry, new IndexedStub(SyncRig.PeerKey, SyncRig.MappingKey, folder, folder + " - 720p.strm"));
        var unknown = StubEnricher.FactsOfStub(entry, new IndexedStub(SyncRig.PeerKey, SyncRig.MappingKey, folder, "something else.strm"));

        Assert.Equal((4_000_000L, 1080), (primary.Size, primary.Height));
        Assert.Equal((9_000_000L, 2160), (hd.Size, hd.Height));
        Assert.Equal((1_000_000L, 720), (sd.Size, sd.Height));
        Assert.Equal(primary, unknown);

        var items = new SetItems([.. entry.Sources.Select(s => (Guid.NewGuid(), Path.Combine(rig.LibraryFolder, s.StubRelPath!.Replace('/', Path.DirectorySeparatorChar))))], merged: true, linked: false);
        var enricher = new StubEnricher(new ShoalIndex(new FileItemPaths(new Dictionary<Guid, string>()), rig.State, NullLogger<ShoalIndex>.Instance), items, new StubCatalog(rig.Catalogs), rig.State, NullLogger<StubEnricher>.Instance);
        var clean = await enricher.RunAsync();
        Assert.Empty(clean.Merged); // exactly the catalog's set
        Assert.Equal(3, clean.Checked);
        Assert.Equal(3, items.Applied.Count);
        Assert.Equal(9_000_000L, items.Applied[items.Paths.Single(p => p.Path.EndsWith("2160p.strm", StringComparison.Ordinal)).Id].Size);

        var linked = new SetItems(items.Paths, merged: true, linked: true);
        var warned = await new StubEnricher(new ShoalIndex(new FileItemPaths(new Dictionary<Guid, string>()), rig.State, NullLogger<ShoalIndex>.Instance), linked, new StubCatalog(rig.Catalogs), rig.State, NullLogger<StubEnricher>.Instance).RunAsync();
        Assert.Equal(3, warned.Merged.Count); // a linked (manual) merge is still a warning

        var foreign = new SetItems([.. items.Paths, (Guid.NewGuid(), Path.Combine(rig.LibraryFolder, folder, "Some other file.strm"))], merged: true, linked: false);
        var other = await new StubEnricher(new ShoalIndex(new FileItemPaths(new Dictionary<Guid, string>()), rig.State, NullLogger<ShoalIndex>.Instance), foreign, new StubCatalog(rig.Catalogs), rig.State, NullLogger<StubEnricher>.Instance).RunAsync();
        Assert.NotEmpty(other.Merged); // a set with a file that is not one of the catalog's versions is not Shoal's
    }

    private sealed class FileItemPaths : IItemPaths
    {
        private readonly IReadOnlyDictionary<Guid, string> _paths;

        public FileItemPaths(IReadOnlyDictionary<Guid, string> paths) => _paths = paths;

#pragma warning disable CS0067
        public event EventHandler<ItemPathEventArgs>? Added;

        public event EventHandler<ItemPathEventArgs>? Removed;
#pragma warning restore CS0067

        public string? PathOf(Guid itemId) => _paths.GetValueOrDefault(itemId);

        public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) => [.. _paths.Select(static p => new ItemPathEventArgs(p.Key, p.Value))];
    }

    private sealed class SetItems : IStubItems
    {
        private readonly bool _merged;
        private readonly bool _linked;

        public SetItems(IReadOnlyList<(Guid Id, string Path)> paths, bool merged, bool linked)
        {
            Paths = paths;
            _merged = merged;
            _linked = linked;
        }

        public IReadOnlyList<(Guid Id, string Path)> Paths { get; }

        public Dictionary<Guid, StubFacts> Applied { get; } = [];

        public IReadOnlyList<StubItemView> List() => [.. Paths.Select(p => new StubItemView(
            p.Id,
            p.Path,
            "The Matrix",
            new StubFacts(0, null, 0, 0, 0, 0),
            _merged,
            (facts, _) =>
            {
                Applied[p.Id] = facts;
                return Task.CompletedTask;
            },
            [.. Paths.Select(static q => q.Path)],
            _linked))];
    }
}
