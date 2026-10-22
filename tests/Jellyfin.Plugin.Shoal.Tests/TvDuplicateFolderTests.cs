using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.3 friend report: in a shared TV library a few series got a brand new <c>[shoal-xxxxxxxx]</c> folder every sync and the old ones stayed. A series a
/// higher-ranked server also shows in the same library folder (K43) first had only a hidden record (no folder); when the peer then listed episodes the other
/// server lacks, the plain folder name was that server's, the hidden record was dropped as the "promoted" entry and a second series entry for the same peer
/// series was written; the next catalog load kept the first (the record) and dropped the new entry and its episodes, so every sync wrote another copy.
/// </summary>
public sealed class TvDuplicateFolderTests
{
    internal const string Other = "cccccccccccccccccccccccccccccccc";
    internal const string LibKey = "0123456789abcdef";
    internal const string Plain = "Show 0 (2010) [tvdbid-100000]";

    internal static async Task<(SyncRig Rig, string Folder)> SharedLibraryRigAsync()
    {
        var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = Other, DisplayName = "Barley", BaseUrl = "http://barley.tail1234.ts.net:8096", Priority = 10, Libraries = { new LibraryMapping { MappingKey = "tv-9", RemoteName = "TV", LocalLibraryName = "Barley TV", Subfolder = "tv", CollectionType = "tvshows" } } });
            s.Home.Libraries.Add(new ShoalLibrary { LibKey = LibKey, Name = "Shows", Type = "tvshows", PathRel = ShoalLibrary.LibsFolder + "/" + LibKey, Feeds = [SyncRig.PeerKey, Other] });
        });
        var folder = LibraryFolders.PathOf(rig.StubRoot, rig.State.Current.Home.Libraries.Single(static l => l.LibKey == LibKey))!;

        // Barley (ranked higher) shows season 1 of the same series, in the same library folder, under the plain name.
        var otherSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "barleyseries0001", PeerItemId = new string('9', 32), MappingKey = "tv-9", State = EntryState.Active, FolderRel = Plain, Title = "Show 0", Year = 2010, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "100000" } };
        var otherDoc = new CatalogDocument { PeerKey = Other, CompletedOnce = true };
        otherDoc.Entries.Add(otherSeries);
        Directory.CreateDirectory(Path.Combine(folder, Plain, "Season 01"));
        File.WriteAllText(Path.Combine(folder, Plain, "tvshow.nfo"), "<tvshow />");
        for (var n = 1; n <= 3; n++)
        {
            var key = "barleyep0000000" + n;
            otherDoc.Entries.Add(new CatalogEntry { Kind = EntryKind.Episode, StubKey = key, ParentKey = otherSeries.StubKey, PeerItemId = new string((char)('a' + n), 32), PeerSeriesId = otherSeries.PeerItemId, MappingKey = "tv-9", Season = 1, Episode = n, State = EntryState.Active, StubRelPath = $"{Plain}/Season 01/S01E0{n}.strm" });
            File.WriteAllText(Path.Combine(folder, Plain, "Season 01", $"S01E0{n}.strm"), StubSentinel.Content(key));
        }

        rig.Catalogs.Save(otherDoc);
        return (rig, folder);
    }

    internal static void AddSeason2(SyncRig rig)
    {
        var tree = rig.Peer.Tv!;
        var show = tree.Series[0];
        var season = new FakeTvSeason { Id = System.Guid.Parse("5e000000000000000000000000000002"), SeriesId = show.Id, Number = 2 };
        tree.Seasons.Add(season);
        for (var e = 1; e <= 3; e++)
        {
            tree.Episodes.Add(new FakeTvEpisode { Id = System.Guid.Parse("5e00000000000000000000000000020" + e), SeriesId = show.Id, SeasonId = season.Id, Season = 2, Number = e, Name = "Episode " + e });
        }
    }

    internal static List<string> SeriesFolders(string folder) =>
        [.. Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(static n => n!.StartsWith("Show 0", System.StringComparison.Ordinal)).Select(static n => n!).Order(System.StringComparer.Ordinal)];

    [Fact]
    public async Task ASeriesGroupedWithAHigherRankedServer_GetsOneFolderAndOneStubKey_OverManySyncs()
    {
        var (rig, folder) = await SharedLibraryRigAsync();
        await using var _ = rig;

        await rig.SyncAsync(); // every episode overlaps Barley's: a hidden record, no folder
        var record = rig.Entries.Single(static e => e.Kind == EntryKind.Series);
        Assert.Equal(EntryState.DuplicatePeer, record.State);
        Assert.Null(record.FolderRel);
        Assert.Equal([Plain], SeriesFolders(folder));

        AddSeason2(rig); // the peer now lists episodes Barley does not have: they join the series
        var keys = new List<string>();
        for (var run = 0; run < 3; run++)
        {
            rig.Time.Advance(System.TimeSpan.FromHours(6));
            await rig.SyncAsync();
            var series = rig.Entries.Where(static e => e.Kind == EntryKind.Series).ToList();
            var mine = Assert.Single(series);
            Assert.Equal(EntryState.Active, mine.State);
            Assert.NotNull(mine.FolderRel);
            keys.Add(mine.StubKey);
            Assert.Equal(3, rig.Entries.Count(e => e.Kind == EntryKind.Episode && e.State == EntryState.Active && e.ParentKey == mine.StubKey));
            Assert.Equal(2, SeriesFolders(folder).Count); // Barley's and ours, never a third
        }

        Assert.Single(keys.Distinct());
    }

    private static void Copy(string folder, string name, string keyPrefix, string? extraFile = null)
    {
        var season = Path.Combine(folder, name, "Season 02");
        Directory.CreateDirectory(season);
        File.WriteAllText(Path.Combine(folder, name, "tvshow.nfo"), "<tvshow />");
        File.WriteAllBytes(Path.Combine(folder, name, "poster.jpg"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(season, "season.nfo"), "<season />");
        for (var n = 1; n <= 3; n++)
        {
            File.WriteAllText(Path.Combine(season, $"S02E0{n}.strm"), StubSentinel.Content(keyPrefix + n));
            File.WriteAllText(Path.Combine(season, $"S02E0{n}.nfo"), "<episodedetails />");
            File.WriteAllBytes(Path.Combine(season, $"S02E0{n}-thumb.jpg"), [1]);
        }

        if (extraFile is not null)
        {
            File.WriteAllText(Path.Combine(folder, name, extraFile), "mine");
        }
    }

    [Fact]
    public async Task TheLeftoverCopiesOfOlderBuilds_GoOnTheNextSync_AndNothingElseDoes()
    {
        var (rig, folder) = await SharedLibraryRigAsync();
        await using var _ = rig;
        Copy(folder, Plain + " [shoal-aaaaaaaa]", "leftovera000000");
        Copy(folder, Plain + " [shoal-bbbbbbbb]", "leftoverb000000");
        Copy(folder, Plain + " [shoal-cccccccc]", "leftoverc000000", extraFile: "notes.txt"); // a file Shoal never writes: kept
        Copy(folder, Plain + " [shoal-dddddddd]", "barleyep0000000"); // a stub key another catalog knows: kept
        Copy(folder, "Show 1 (2010) [tvdbid-100001] [shoal-eeeeeeee]", "leftovere000000"); // another series: kept

        await rig.SyncAsync();
        Assert.Equal(5, SeriesFolders(folder).Count); // no live series of ours yet: nothing is swept

        AddSeason2(rig);
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();

        var mine = rig.Entries.Single(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active).FolderRel!;
        Assert.Equal(
            new[] { Plain, Plain + " [shoal-cccccccc]", Plain + " [shoal-dddddddd]", mine }.Order(System.StringComparer.Ordinal),
            SeriesFolders(folder));
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(folder, Plain), "*.strm", SearchOption.AllDirectories).Count()); // Barley's own copy is untouched
        Assert.True(Directory.Exists(Path.Combine(folder, "Show 1 (2010) [tvdbid-100001] [shoal-eeeeeeee]")));

        // Warden 19 L2: idempotent, a second sweep deletes nothing more
        var after = SeriesFolders(folder);
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();
        Assert.Equal(after, SeriesFolders(folder));
        Assert.Equal(mine, rig.Entries.Single(static e => e.Kind == EntryKind.Series && e.State == EntryState.Active).FolderRel);
        Assert.Equal(3, rig.Entries.Count(static e => e.Kind == EntryKind.Episode && e.State == EntryState.Active));
    }

    [Fact]
    public void ACatalogWithTwoEntriesForOnePeerSeries_KeepsTheOneWithAFolder_AndItsEpisodes()
    {
        using var dir = new TempDirectory();
        var store = new CatalogStore(new ShoalPaths(dir.Path), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var peerSeries = new string('1', 32);
        var record = new CatalogEntry { Kind = EntryKind.Series, StubKey = "record0000000001", PeerItemId = peerSeries, MappingKey = "tv-1", State = EntryState.DuplicatePeer, DuplicateOf = "barleyseries0001" };
        var hidden = new CatalogEntry { Kind = EntryKind.Episode, StubKey = "hidden0000000001", ParentKey = record.StubKey, PeerItemId = new string('2', 32), PeerSeriesId = peerSeries, MappingKey = "tv-1", Season = 1, Episode = 1, State = EntryState.DuplicatePeer };
        var copy = new CatalogEntry { Kind = EntryKind.Series, StubKey = "copy000000000001", PeerItemId = peerSeries, MappingKey = "tv-1", State = EntryState.Active, FolderRel = Plain + " [shoal-copy0000]" };
        var season = new CatalogEntry { Kind = EntryKind.Season, StubKey = "season0000000001", ParentKey = copy.StubKey, PeerItemId = new string('3', 32), PeerSeriesId = peerSeries, MappingKey = "tv-1", Season = 2, State = EntryState.Active, FolderRel = Plain + " [shoal-copy0000]/Season 02" };
        var episode = new CatalogEntry { Kind = EntryKind.Episode, StubKey = "episode000000001", ParentKey = copy.StubKey, PeerItemId = new string('4', 32), PeerSeriesId = peerSeries, MappingKey = "tv-1", Season = 2, Episode = 1, State = EntryState.Active, StubRelPath = Plain + " [shoal-copy0000]/Season 02/S02E01.strm" };
        var document = new CatalogDocument { PeerKey = SyncRig.PeerKey };
        document.Entries.AddRange([record, hidden, copy, season, episode]);
        store.Save(document);

        var keys = store.Load(SyncRig.PeerKey).Document.Entries.Select(static e => e.StubKey).Order(System.StringComparer.Ordinal);

        Assert.Equal(new[] { "copy000000000001", "episode000000001", "season0000000001" }, keys); // the hidden record's sources are re-planned on the next sync
    }

    [Theory]
    [InlineData(Plain, true)]
    [InlineData(Plain + " [shoal-abcdefgh]", true)]
    [InlineData(Plain + " [shoal-abcdefgh12345678]", true)]
    [InlineData(Plain + " [shoal-ABCDEFGH]", false)]
    [InlineData(Plain + " [shoal-abc]", false)]
    [InlineData(Plain + " (1)", false)]
    [InlineData(Plain + " [shoal-abcdefgh] [shoal-abcdefgh]", false)]
    [InlineData("Show 0 (2010) [tvdbid-1000001]", false)]
    public void ACopyName_IsTheSeriesOwnNameWithAtMostOneShoalTag(string name, bool copy) =>
        Assert.Equal(copy, TvDuplicateSweep.IsCopyName(name, TvDuplicateSweep.BaseOf(Plain + " [shoal-12345678]")!));

    [Fact]
    public void AFolderWithoutAProviderIdTag_IsNeverSwept() =>
        Assert.Null(TvDuplicateSweep.BaseOf("Show 0 (2010) [shoal-12345678]"));

    // A directory link: a symbolic link where allowed, else (Windows without the privilege) a junction.
    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (System.Exception ex) when (System.OperatingSystem.IsWindows() && ex is IOException or System.UnauthorizedAccessException)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            process.WaitForExit();
        }

        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void ACopyWithALinkInIt_OrThatIsALink_IsNeverALeftover()
    {
        using var dir = new TempDirectory();
        var keys = new HashSet<string>(System.StringComparer.Ordinal);
        Copy(dir.Path, "copy", "leftovera000000");
        Assert.True(TvDuplicateSweep.IsLeftover(Path.Combine(dir.Path, "copy"), keys, out _)); // the plain copy is one

        var outside = dir.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.nfo"), "user data");
        LinkDirectory(Path.Combine(dir.Path, "copy", "Season 03"), outside);
        Assert.False(TvDuplicateSweep.IsLeftover(Path.Combine(dir.Path, "copy"), keys, out _));

        Copy(dir.Path, "real", "leftoverb000000");
        LinkDirectory(Path.Combine(dir.Path, "linked"), Path.Combine(dir.Path, "real"));
        Assert.False(TvDuplicateSweep.IsLeftover(Path.Combine(dir.Path, "linked"), keys, out _));
        Assert.True(File.Exists(Path.Combine(outside, "precious.nfo")));
    }

    private static string OtherCatalog(SyncRig rig) => Path.Combine(rig.Paths.PeerDir(Other), "catalog.json");

    // Warden 19 M1: the other friend's catalog says nothing true about its folder; neither its folder nor any leftover may go.
    private static async Task AssertNothingSweptAsync(System.Action<SyncRig> breakOther)
    {
        var (rig, folder) = await SharedLibraryRigAsync();
        await using var _ = rig;
        Copy(folder, Plain + " [shoal-aaaaaaaa]", "leftovera000000");
        await rig.SyncAsync();
        AddSeason2(rig);
        breakOther(rig);
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();

        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(folder, Plain), "*.strm", SearchOption.AllDirectories).Count()); // Barley's copy stays
        Assert.True(Directory.Exists(Path.Combine(folder, Plain + " [shoal-aaaaaaaa]"))); // and so does the leftover, until every catalog is whole
    }

    [Fact]
    public async Task AnotherFriendsMissingCatalog_DeletesNothing() => await AssertNothingSweptAsync(rig =>
    {
        File.Delete(OtherCatalog(rig));
        File.Delete(OtherCatalog(rig) + ".bak");
    });

    [Fact]
    public async Task AnotherFriendsCatalogThatDropsEntries_DeletesNothing() => await AssertNothingSweptAsync(rig =>
    {
        var document = rig.Catalogs.Load(Other).Document;
        document.Entries.Single(static e => e.Kind == EntryKind.Series).FolderRel = "a/b"; // fails validation: the series and its episodes drop
        rig.Catalogs.Save(document);
    });

    [Fact]
    public async Task AnotherFriendsUnreadableCatalog_DeletesNothing() => await AssertNothingSweptAsync(rig =>
    {
        File.WriteAllText(OtherCatalog(rig), "{ not json");
        File.Delete(OtherCatalog(rig) + ".bak");
    });

    [Fact]
    public async Task AnIncompleteSync_DeletesNothing() => await AssertNothingSweptAsync(rig =>
        rig.Peer.Tv!.TotalOverride = kind => kind == "Episode" ? 500 : kind == "Series" ? rig.Peer.Tv.Series.Count : rig.Peer.Tv.Seasons.Count); // the listing says more episodes exist

    [Fact]
    public async Task AHalfWrittenOwnCatalog_DeletesNothing_UntilAWholeOneIsRead()
    {
        var (rig, folder) = await SharedLibraryRigAsync();
        await using var _ = rig;
        await rig.SyncAsync();
        AddSeason2(rig);
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync(); // our copy is written
        Copy(folder, Plain + " [shoal-aaaaaaaa]", "leftovera000000");
        var own = Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog.json");
        Assert.True(File.Exists(own + ".bak"));
        var bytes = File.ReadAllBytes(own);
        File.WriteAllBytes(own, bytes[..(bytes.Length / 2)]); // a torn write: the load falls back to the .bak

        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();
        Assert.True(Directory.Exists(Path.Combine(folder, Plain + " [shoal-aaaaaaaa]")));

        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync(); // the catalog was saved whole: now it goes
        Assert.False(Directory.Exists(Path.Combine(folder, Plain + " [shoal-aaaaaaaa]")));
        Assert.True(Directory.Exists(Path.Combine(folder, Plain)));
    }
}
