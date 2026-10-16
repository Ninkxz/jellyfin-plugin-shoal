using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K11: the dedup rule (DESIGN 5.3), the catalog file (3.3) and the stub files' lifecycle (5.2 phase D).
/// </summary>
public sealed class CatalogUnitTests
{
    private const string PeerKey = "4b0c1d2e3f405162738495a6b7c8d9e0";

    private static DedupIndex Index(params LocalMovie[] movies) => new(movies);

    private static LocalMovie Local(string title, int? year, long? tmdb = null, string? imdb = null, long size = 100) =>
        new(new CanonicalIds(tmdb, null, imdb), title, year, size);

    // --- dedup

    [Theory]
    [InlineData("The Matrix", "the matrix")]
    [InlineData("The Matrix!", "the matrix")]
    [InlineData("  The   Matrix  ", "thematrix")]
    [InlineData("Ｔｈｅ Matrix", "the matrix")]
    [InlineData("Amélie", "AMÉLIE")]
    public void TitlesAreComparedByLettersAndDigitsOnly_AfterNfkcAndCaseFolding(string a, string b)
    {
        Assert.Equal(TitleKey.Normalize(b), TitleKey.Normalize(a));
        Assert.NotNull(TitleKey.Normalize(a));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("   ")]
    public void ATitleWithoutLettersOrDigits_HasNoKey(string? title)
    {
        Assert.Null(TitleKey.Normalize(title));
        Assert.Null(TitleKey.Weak(title, 2000));
    }

    [Fact]
    public void AWeakKey_NeedsAYear()
    {
        Assert.Null(TitleKey.Weak("The Matrix", null));
        Assert.Equal("thematrix|1999", TitleKey.Weak("The Matrix", 1999));
    }

    [Fact]
    public void AnySharedProviderId_IsADuplicateOfHomesCopy_WithItsSize()
    {
        var index = Index(Local("Whatever", 1999, tmdb: 603, imdb: "tt0133093", size: 777));

        Assert.Equal(new DedupVerdict(EntryState.DuplicateLocal, null, 777), index.Judge(new CanonicalIds(603, null, null), "Different", 2020));
        Assert.Equal(new DedupVerdict(EntryState.DuplicateLocal, null, 777), index.Judge(new CanonicalIds(null, null, "tt0133093"), "Different", 2020));
        Assert.Equal(EntryState.Active, index.Judge(new CanonicalIds(604, null, "tt0000001"), "Whatever", 1999).State); // other ids: not the same film, whatever the title says
    }

    [Fact]
    public void WithNoProviderIdOnEitherSide_AnExactTitleAndYear_Match()
    {
        var index = Index(Local("The Matrix!", 1999));

        Assert.Equal(EntryState.DuplicateLocal, index.Judge(default, "the matrix", 1999).State);
        Assert.Equal(EntryState.Active, index.Judge(default, "the matrix", 2000).State);
        Assert.Equal(EntryState.Active, index.Judge(default, "the matrix", null).State); // without a year it never matches
        Assert.Equal(EntryState.Active, index.Judge(default, "the matrix reloaded", 1999).State);
    }

    [Fact]
    public void ATitleWithoutAYearOnHomesSide_NeverMatches()
    {
        var index = Index(Local("The Matrix", null));

        Assert.Equal(EntryState.Active, index.Judge(default, "The Matrix", 1999).State);
        Assert.Equal(EntryState.Active, index.Judge(default, "The Matrix", null).State);
    }

    [Fact]
    public void IdsOnOneSideOnly_DoNotFallBackToTheTitle()
    {
        var withIds = Index(Local("The Matrix", 1999, tmdb: 603));
        var withoutIds = Index(Local("The Matrix", 1999));

        Assert.Equal(EntryState.Active, withIds.Judge(default, "The Matrix", 1999).State);
        Assert.Equal(EntryState.Active, withoutIds.Judge(new CanonicalIds(603, null, null), "The Matrix", 1999).State);
    }

    [Fact]
    public void AClaimedTitle_IsADuplicateOfTheWinner_ButHomesOwnCopyWinsOverAClaim()
    {
        var index = Index(Local("Mine", 2001, tmdb: 5, size: 9));
        index.Claim(new CanonicalIds(5, null, null), "Mine", 2001, "winnerkey1234567");
        index.Claim(new CanonicalIds(6, null, null), "Theirs", 2002, "winnerkey7654321");
        index.Claim(default, "No Ids", 2003, "winnerkeyabcdefg");

        Assert.Equal(EntryState.DuplicateLocal, index.Judge(new CanonicalIds(5, null, null), "Mine", 2001).State);
        Assert.Equal(new DedupVerdict(EntryState.DuplicatePeer, "winnerkey7654321", 0), index.Judge(new CanonicalIds(6, null, null), "Anything", 1999));
        Assert.Equal(new DedupVerdict(EntryState.DuplicatePeer, "winnerkeyabcdefg", 0), index.Judge(default, "no ids", 2003));
        Assert.Equal(EntryState.Active, index.Judge(new CanonicalIds(7, null, null), "Fresh", 2004).State);
    }

    // --- sentinel

    [Fact]
    public void TheSentinel_HoldsOnlyTheStubKey()
    {
        var content = StubSentinel.Content("k3m9q2x7v1tz8c4n");

        Assert.Equal("http://shoal.invalid/v1/k3m9q2x7v1tz8c4n\n", content);
        Assert.True(StubSentinel.TryParse(content, out var key));
        Assert.Equal("k3m9q2x7v1tz8c4n", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com/v1/k3m9q2x7v1tz8c4n\n")]
    [InlineData("http://shoal.invalid/v1/\n")]
    [InlineData("http://shoal.invalid/v1/SHORT\n")]
    [InlineData("http://shoal.invalid/v1/k3m9q2x7v1tz8c4n/extra\n")]
    [InlineData("http://shoal.invalid/v1/k3m9q2x7v1tz8c4n?token=1\n")]
    public void ASentinelThatIsNotOurs_IsRejected(string content)
    {
        Assert.False(StubSentinel.TryParse(content, out _));
    }

    // --- catalog store

    private static CatalogEntry Entry(string key = "k3m9q2x7v1tz8c4n", string id = "0123456789abcdef0123456789abcdef", string? path = "A (2000) [tmdbid-1]/A (2000) [tmdbid-1].strm", EntryState state = EntryState.Active) => new()
    {
        StubKey = key,
        PeerItemId = id,
        PeerMediaSourceId = id,
        MappingKey = "movies-1",
        Title = "A",
        Year = 2000,
        State = state,
        StubRelPath = path,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" },
        ImageTags = new Dictionary<string, string> { ["Primary"] = "t1" },
    };

    private static CatalogStore Store(TempDirectory dir) => new(new ShoalPaths(dir.Path), NullLogger.Instance);

    [Fact]
    public void ACatalog_RoundTrips_WithTheStatesAsTextOfDesign33()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        var document = new CatalogDocument { PeerKey = PeerKey, CompletedOnce = true };
        document.Entries.Add(Entry());
        document.Entries.Add(Entry("duplicatekey00001", "11111111111111111111111111111111", null, EntryState.DuplicateLocal));
        document.Libraries.Add(new CatalogLibrary { MappingKey = "movies-1", PeerLibraryId = "aaaaaaaabbbbccccddddeeeeeeeeeeee", LastCount = 2 });
        document.Names.People.Add("Someone");

        store.Save(document);
        var text = File.ReadAllText(Path.Combine(new ShoalPaths(dir.Path).PeerDir(PeerKey), "catalog.json"));
        var load = store.Load(PeerKey);

        Assert.Contains("\"state\":\"duplicate-local\"", text, StringComparison.Ordinal);
        Assert.Contains("\"stubRelPath\"", text, StringComparison.Ordinal);
        Assert.False(load.Unreadable);
        Assert.Equal(0, load.Dropped);
        Assert.Equal(2, load.Document.Entries.Count);
        Assert.Equal(EntryState.DuplicateLocal, load.Document.Entries[1].State);
        Assert.Equal("t1", load.Document.Entries[0].ImageTags["Primary"]);
        Assert.Equal(1, load.Document.Usage.People);
        Assert.True(load.Document.CompletedOnce);
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheCatalogFile_IsPrivate()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        store.Save(new CatalogDocument { PeerKey = PeerKey });

        Assert.Equal(AtomicFile.PrivateFileMode, File.GetUnixFileMode(Path.Combine(new ShoalPaths(dir.Path).PeerDir(PeerKey), "catalog.json")));
    }

    [Fact]
    public void NoFile_IsAnEmptyCatalog_NotAnError()
    {
        using var dir = new TempDirectory();

        var load = Store(dir).Load(PeerKey);

        Assert.False(load.Unreadable);
        Assert.Empty(load.Document.Entries);
        Assert.Equal(PeerKey, load.Document.PeerKey);
    }

    [Fact]
    public void EntriesThatCannotBeTrusted_AreDroppedOnLoad()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        var document = new CatalogDocument { PeerKey = PeerKey };
        document.Entries.Add(Entry());
        document.Entries.Add(Entry("goodkey000000002", "22222222222222222222222222222222", "../../etc/x.strm"));
        document.Entries.Add(Entry("goodkey000000003", "33333333333333333333333333333333", "a/b/c.strm"));
        document.Entries.Add(Entry("goodkey000000004", "44444444444444444444444444444444", "/abs/x.strm"));
        document.Entries.Add(Entry("goodkey000000005", "55555555555555555555555555555555", "dir/file.txt"));
        document.Entries.Add(Entry("BAD KEY", "66666666666666666666666666666666"));
        document.Entries.Add(Entry("goodkey000000007", "not-a-guid"));
        document.Entries.Add(Entry("goodkey000000008", "88888888888888888888888888888888", null, EntryState.Active)); // active without a stub path
        document.Entries.Add(Entry("goodkey000000009", "99999999999999999999999999999999", null, EntryState.Removed));
        document.Entries.Add(Entry("k3m9q2x7v1tz8c4n", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")); // duplicate stub key
        store.Save(document);

        var load = store.Load(PeerKey);

        Assert.Equal(["k3m9q2x7v1tz8c4n"], load.Document.Entries.Select(static e => e.StubKey));
        Assert.Equal(9, load.Dropped);
    }

    [Theory]
    [InlineData("A/A.strm", true)]
    [InlineData("The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm", true)]
    [InlineData("../A.strm", false)]
    [InlineData("./A.strm", false)]
    [InlineData("A/../B.strm", false)]
    [InlineData("A.strm", false)]
    [InlineData("A/B/C.strm", false)]
    [InlineData("A\\B.strm", false)]
    [InlineData("/A/B.strm", false)]
    [InlineData("A/B.nfo", false)]
    [InlineData("A/B:.strm", false)]
    [InlineData("A /B.strm", false)]
    [InlineData("A/ .strm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTwoPlainSegmentsEndingInStrm_AreASafeStubPath(string? path, bool safe)
    {
        Assert.Equal(safe, CatalogStore.IsSafeStubPath(path));
    }

    [Fact]
    public void ADamagedCatalog_FallsBackToTheBackup_AndWhenBothAreDamaged_IsUnreadable()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        var document = new CatalogDocument { PeerKey = PeerKey };
        document.Entries.Add(Entry());
        store.Save(document);
        document.Entries.Add(Entry("goodkey000000002", "22222222222222222222222222222222", "B (2001) [tmdbid-2]/B (2001) [tmdbid-2].strm"));
        store.Save(document); // the first version becomes the .bak
        var path = Path.Combine(new ShoalPaths(dir.Path).PeerDir(PeerKey), "catalog.json");

        File.WriteAllText(path, "{ damaged");
        var fallback = store.Load(PeerKey);
        Assert.False(fallback.Unreadable);
        Assert.Single(fallback.Document.Entries);

        File.WriteAllText(path + ".bak", "{ damaged too");
        var lost = store.Load(PeerKey);
        Assert.True(lost.Unreadable);
        Assert.Equal("{ damaged", File.ReadAllText(path));
    }

    [Fact]
    public void ACatalogOfAnotherPeer_OrANewerSchema_IsUnreadable()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        store.Save(new CatalogDocument { PeerKey = PeerKey });
        var path = Path.Combine(new ShoalPaths(dir.Path).PeerDir(PeerKey), "catalog.json");
        var text = File.ReadAllText(path);

        File.WriteAllText(path, text.Replace("\"schema\":4", "\"schema\":5", StringComparison.Ordinal));
        File.Delete(path + ".bak");
        Assert.True(store.Load(PeerKey).Unreadable);

        File.WriteAllText(path, text.Replace(PeerKey, "0000000000000000", StringComparison.Ordinal));
        Assert.True(store.Load(PeerKey).Unreadable);
    }

    // --- T-ALT-1: catalog schema 3

    [Fact]
    public void T_ALT_1_ASchema2Catalog_ReadsEveryEntryAsOneSource_AndIsWrittenAsSchema3_WithNothingRenamed()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        var document = new CatalogDocument { PeerKey = PeerKey, CompletedOnce = true };
        var entry = Entry();
        entry.Container = "mkv";
        entry.VideoCodec = "h264";
        entry.Width = 1920;
        entry.Height = 1080;
        entry.SizeBytes = 1234;
        document.Entries.Add(entry);
        store.Save(document);
        var path = Path.Combine(new ShoalPaths(dir.Path).PeerDir(PeerKey), "catalog.json");

        // What a v1 build wrote: schema 2, and no "sources" at all.
        var v2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        v2["schema"] = 2;
        foreach (var e in v2["entries"]!.AsArray())
        {
            e!.AsObject().Remove("sources");
        }

        File.WriteAllText(path, v2.ToJsonString());
        File.Delete(path + ".bak");

        var loaded = store.Load(PeerKey);

        Assert.False(loaded.Unreadable);
        var read = Assert.Single(loaded.Document.Entries);
        var source = Assert.Single(read.Sources);
        Assert.Equal(entry.StubKey, source.StubKey);
        Assert.Equal(entry.PeerMediaSourceId, source.PeerMediaSourceId);
        Assert.Equal(entry.StubRelPath, source.StubRelPath);
        Assert.Equal((entry.Container, entry.VideoCodec, entry.Width, entry.Height, entry.SizeBytes), (source.Container, source.VideoCodec, source.Width, source.Height, source.SizeBytes));
        Assert.Equal(entry.StubRelPath, read.StubRelPath); // the entry itself is unchanged: no file is renamed

        store.Save(loaded.Document);
        Assert.Equal(4, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["schema"]!.GetValue<int>());
        var again = Assert.Single(store.Load(PeerKey).Document.Entries);
        Assert.Equal(entry.StubKey, Assert.Single(again.Sources).StubKey);
    }

    [Fact]
    public void T_ALT_1_Sources_AreCappedAtFour_AndHostileOnesAreDropped()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);
        var document = new CatalogDocument { PeerKey = PeerKey };
        var entry = Entry();
        entry.Sources =
        [
            new CatalogSource { StubKey = entry.StubKey, StubRelPath = entry.StubRelPath, PeerMediaSourceId = entry.PeerMediaSourceId },
            new CatalogSource { StubKey = "UPPER-CASE-KEY!", StubRelPath = "a/b.strm" },
            new CatalogSource { StubKey = "k3m9q2x7v1tz8c4n", StubRelPath = "a/b.strm" }, // the same key twice
            new CatalogSource { StubKey = "evilpath0000001x", StubRelPath = "../x/b.strm" },
            new CatalogSource { StubKey = "badsourceid0001x", PeerMediaSourceId = "not-hex" },
            new CatalogSource { StubKey = "extra0000000002x", StubRelPath = "A (2000) [tmdbid-1]/A (2000) [tmdbid-1] - 720p.strm", Label = "720p" },
            new CatalogSource { StubKey = "extra0000000003x" },
            new CatalogSource { StubKey = "extra0000000004x" },
            new CatalogSource { StubKey = "extra0000000005x" },
        ];
        document.Entries.Add(entry);
        store.Save(document);

        var sources = Assert.Single(store.Load(PeerKey).Document.Entries).Sources;

        Assert.Equal(["k3m9q2x7v1tz8c4n", "extra0000000002x", "extra0000000003x", "extra0000000004x"], sources.Select(static s => s.StubKey));
    }

    [Theory]
    [InlineData(320, 180, 10, 10, 100, 10_000, 1_000_000, true)]
    [InlineData(1, 1, 1, 1, 1, 1, 0, true)]
    [InlineData(1024, 1024, 3, 3, 100_000, 3_600_000, 100_000_000, true)]
    [InlineData(0, 180, 10, 10, 100, 10_000, 0, false)]
    [InlineData(1025, 180, 10, 10, 100, 10_000, 0, false)]
    [InlineData(320, 180, 33, 10, 100, 10_000, 0, false)]
    [InlineData(320, 180, 10, 0, 100, 10_000, 0, false)]
    [InlineData(320, 180, 10, 10, 0, 10_000, 0, false)]
    [InlineData(320, 180, 10, 10, 100_001, 10_000, 0, false)]
    [InlineData(320, 180, 10, 10, 100, 0, 0, false)]
    [InlineData(320, 180, 10, 10, 100, 3_600_001, 0, false)]
    [InlineData(320, 180, 10, 10, 100, 10_000, 100_000_001, false)]
    [InlineData(1024, 1024, 32, 32, 100, 10_000, 0, false)] // 32768 pixels a side
    [InlineData(512, 512, 16, 16, 100, 10_000, 0, false)] // 8192 a side, 67 MP
    public void T_TRK_1_ATrickplayLevel_IsKeptOnlyInsideItsRanges(int width, int height, int tileW, int tileH, int count, int interval, int bandwidth, bool valid)
    {
        var level = new TrickplayLevel { Width = width, Height = height, TileWidth = tileW, TileHeight = tileH, ThumbnailCount = count, IntervalMs = interval, Bandwidth = bandwidth };

        Assert.Equal(valid, level.IsValid());
    }

    // --- stub files

    private static (StubWriter Writer, StubTarget Target, string Root, string Library) Writer(TempDirectory dir)
    {
        var root = dir.Combine("stubs");
        var library = Path.Combine(root, PeerKey, "movies");
        Directory.CreateDirectory(library);
        var peer = new Jellyfin.Plugin.Shoal.Peers.PeerEndpoint(PeerKey, "Hops", new Uri("http://127.0.0.1:1"), "dev", "token");
        using var client = new Jellyfin.Plugin.Shoal.Peers.PeerClient(() => "Home", () => "0", TimeProvider.System);
        var writer = new StubWriter(new ImageFetcher(client, NullLogger<ImageFetcher>.Instance), NullLogger.Instance);
        return (writer, new StubTarget(root, library, peer), root, library);
    }

    [Fact]
    public void RemovingAStub_DeletesOnlyTheFilesShoalCreates_AndKeepsAFolderThatHoldsAnythingElse()
    {
        using var dir = new TempDirectory();
        var (writer, target, _, library) = Writer(dir);
        var folder = Path.Combine(library, "A (2000) [tmdbid-1]");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "A (2000) [tmdbid-1].strm", "A (2000) [tmdbid-1].nfo", "poster.jpg", "backdrop.png" })
        {
            File.WriteAllText(Path.Combine(folder, name), "x");
        }

        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
        var quota = new DiskQuota(1000, 100);

        Assert.True(writer.Remove(target, "A (2000) [tmdbid-1]/A (2000) [tmdbid-1].strm", quota));

        Assert.Equal(["notes.txt"], Directory.GetFiles(folder).Select(static f => Path.GetFileName(f)!));
        Assert.Equal(96, quota.UsedBytes);

        File.Delete(Path.Combine(folder, "notes.txt"));
        Assert.True(writer.Remove(target, "A (2000) [tmdbid-1]/A (2000) [tmdbid-1].strm", quota));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void RemovingAStubThatIsAlreadyGone_Succeeds()
    {
        using var dir = new TempDirectory();
        var (writer, target, _, _) = Writer(dir);

        Assert.True(writer.Remove(target, "Gone/Gone.strm", new DiskQuota(1000)));
    }

    [Theory]
    [InlineData("../A/A.strm")]
    [InlineData("A/../../B.strm")]
    [InlineData("/etc/x.strm")]
    [InlineData("A.strm")]
    public void ARemovalOutsideTheLibrary_IsRefused_AndNothingIsDeleted(string path)
    {
        using var dir = new TempDirectory();
        var (writer, target, root, _) = Writer(dir);
        var outside = Path.Combine(root, "keep.strm");
        File.WriteAllText(outside, "keep");

        Assert.False(writer.Remove(target, path, new DiskQuota(1000)));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void AFolderThatIsALink_IsNeverFollowed()
    {
        using var dir = new TempDirectory();
        var (writer, target, _, library) = Writer(dir);
        var elsewhere = Directory.CreateDirectory(dir.Combine("elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "victim.strm"), "keep");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(library, "Linked"), elsewhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // symbolic links need a privilege on this machine; the Linux run covers it
        }

        Assert.False(writer.Remove(target, "Linked/victim.strm", new DiskQuota(1000)));
        Assert.True(File.Exists(Path.Combine(elsewhere, "victim.strm")));
    }

    [Fact]
    public async System.Threading.Tasks.Task AnExistingFolder_IsNeverOverwritten_AtCreation()
    {
        using var dir = new TempDirectory();
        var (writer, target, _, library) = Writer(dir);
        var folder = Path.Combine(library, "A (2000) [tmdbid-1]");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mine.txt"), "mine");
        var movie = new MetadataSanitizer(new Rate()).Sanitize(new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid(), Name = "A" })!;

        var result = await writer.AddAsync(target, "A (2000) [tmdbid-1]", "k3m9q2x7v1tz8c4n", movie, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard, new DiskQuota(1 << 20), default);

        Assert.Equal(StubWriteOutcome.Exists, result.Outcome);
        Assert.Equal(["mine.txt"], Directory.GetFiles(folder).Select(static f => Path.GetFileName(f)!));
    }

    private sealed class Rate : IRatingTable
    {
        public bool IsRecognized(string rating) => false;
    }

    [Fact]
    public async System.Threading.Tasks.Task ANewStub_IsAddedWithNfoThenStrm_AndCountedInTheQuota()
    {
        using var dir = new TempDirectory();
        var (writer, target, _, library) = Writer(dir);
        var movie = new MetadataSanitizer(new Rate()).Sanitize(new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid(), Name = "A" })!;
        var quota = new DiskQuota(1 << 20);

        var result = await writer.AddAsync(target, "A (2000) [tmdbid-1]", "k3m9q2x7v1tz8c4n", movie, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard, quota, default);

        Assert.Equal(StubWriteOutcome.Written, result.Outcome);
        Assert.Equal("A (2000) [tmdbid-1]/A (2000) [tmdbid-1].strm", result.RelPath);
        var folder = Path.Combine(library, "A (2000) [tmdbid-1]");
        Assert.Equal(["A (2000) [tmdbid-1].nfo", "A (2000) [tmdbid-1].strm"], Directory.GetFiles(folder).Select(static f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
        Assert.Equal(result.FileBytes, quota.UsedBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(StubSentinel.Content("k3m9q2x7v1tz8c4n")) + new FileInfo(Path.Combine(folder, "A (2000) [tmdbid-1].nfo")).Length, result.FileBytes);
    }
}
