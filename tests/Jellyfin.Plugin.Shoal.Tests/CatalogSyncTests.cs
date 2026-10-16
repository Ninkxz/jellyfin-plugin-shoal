using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K11 acceptance (DESIGN 5.1 to 5.3, 5.8 to 5.10): T-SYNC-1 to T-SYNC-8, T-SYNC-10 to T-SYNC-12 (the large ones scaled down where the
/// number only exists to trip a guard), the partial-run rule, one global sync, and that only the Shoal library is refreshed.
/// </summary>
public sealed class CatalogSyncTests
{
    private static string Text(string path) => File.ReadAllText(path);

    private static string FolderOf(SyncRig rig, CatalogEntry entry) => Path.Combine(rig.LibraryFolder, entry.StubRelPath!.Split('/')[0]);

    private static async Task<SyncRig> RigWithAsync(int count, Action<PeerMovie>? configure = null)
    {
        var rig = await SyncRig.CreateAsync();
        for (var i = 0; i < count; i++)
        {
            rig.Peer.Add("Movie " + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), 1990 + (i % 30), tmdb: 1000 + i, configure: m =>
            {
                m.PosterTag = null;
                m.BackdropTag = null;
                configure?.Invoke(m);
            });
        }

        return rig;
    }

    [Fact]
    public async Task W6_M1_AnUnexpectedException_InOnePeersSync_IsAFailedReport_NeverAThrow()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Local.Throw = true;

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Failed, report.Result);
        Assert.Equal("internal", report.Error);
        rig.Local.Throw = false;
        rig.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result); // the gate was released: the next cycle runs
    }

    // --- T-SYNC-1

    [Fact]
    public async Task T_SYNC_1_TheInitialSync_ImportsTwoMovies_AndCountsTheOneHomeAlreadyHas()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603, imdb: "tt0133093", configure: m => { m.Actors = ["Keanu Reeves", "Carrie-Anne Moss", "A", "B", "C", "D", "E"]; m.Rating = "R"; });
        rig.Peer.Add("Inception", 2010, imdb: "tt1375666");
        rig.Peer.Add("Big Buck Bunny", 2008, tmdb: 10378);
        rig.Local.Movies.Add(new LocalMovie(new CanonicalIds(10378, null, null), "Big Buck Bunny", 2008, 123_456));

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, report.Result);
        Assert.True(report.Complete);
        Assert.Equal(2, report.Added);
        Assert.Equal(1, report.Duplicates);
        var entries = rig.Entries.ToDictionary(static e => e.Title);
        Assert.Equal(EntryState.Active, entries["The Matrix"].State);
        Assert.Equal(EntryState.Active, entries["Inception"].State);
        var bunny = entries["Big Buck Bunny"];
        Assert.Equal(EntryState.DuplicateLocal, bunny.State);
        Assert.Null(bunny.StubRelPath);
        Assert.Equal(123_456, bunny.LocalSizeBytes);
        Assert.Equal(4_000_000, bunny.SizeBytes);

        var matrixEntry = entries["The Matrix"];
        Assert.Equal("The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm", matrixEntry.StubRelPath);
        var folder = FolderOf(rig, matrixEntry);
        Assert.Equal(["The Matrix (1999) [tmdbid-603].nfo", "The Matrix (1999) [tmdbid-603].strm", "backdrop.jpg", "poster.jpg"], Directory.GetFiles(folder).Select(static f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal($"http://shoal.invalid/v1/{matrixEntry.StubKey}\n", Text(Path.Combine(folder, "The Matrix (1999) [tmdbid-603].strm")));
        Assert.True(Directory.Exists(Path.Combine(rig.LibraryFolder, "Inception (2010) [imdbid-tt1375666]")));
        Assert.False(Directory.Exists(Path.Combine(rig.LibraryFolder, "Big Buck Bunny (2008) [tmdbid-10378]")));

        var nfo = XDocument.Parse(Text(Path.Combine(folder, "The Matrix (1999) [tmdbid-603].nfo"))).Root!;
        Assert.Equal(5, nfo.Elements("actor").Count());
        Assert.Equal("R", (string?)nfo.Element("mpaa"));
        Assert.Equal(["Shoal", "Shoal: Hops"], nfo.Elements("tag").Select(static e => e.Value));

        var catalog = rig.Catalog();
        Assert.True(catalog.CompletedOnce);
        Assert.Equal(SyncResultKind.Ok, catalog.LastSync!.Result);
        Assert.Equal(2, catalog.LastSync.Added);
        Assert.Equal(rig.Peer.LibraryId.ToString("N"), catalog.Libraries.Single().PeerLibraryId);
        Assert.Equal(1, catalog.Report!.OverlapCount);
        Assert.Equal(123_456, catalog.Report.OverlapLocalBytes);
        Assert.Equal(4_000_000, catalog.Report.OverlapPeerBytes);
        Assert.Equal(2, catalog.Report.GainCount);
        Assert.Equal(8_000_000, catalog.Report.GainBytes);
        Assert.True(catalog.Usage.DiskBytes > 0);
        _ = matrix;
    }

    [Fact]
    public async Task NoPeerIdAndNoToken_AppearsInAnyStubFile_OrTheLog()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();

        var secrets = new[] { matrix.Id.ToString("N"), rig.Peer.LibraryId.ToString("N"), rig.Peer.ServerId.ToString("N"), "token-for-tests", rig.Peer.Url };
        var files = rig.AllStubFiles().ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = Text(file);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret.Replace("-", string.Empty, StringComparison.Ordinal), file, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var secret in secrets.Take(4))
        {
            Assert.DoesNotContain(secret, rig.Log.All, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(matrix.Id.ToString("N"), Text(Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog.json")), StringComparison.Ordinal); // the one place they live
    }

    [Fact]
    public async Task ASecondSyncWithNothingNew_WritesNothing_AndRefreshesNothing()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var reported = rig.Refresh.Reported.Count;
        var stamps = rig.AllStubFiles().ToDictionary(static f => f, static f => File.GetLastWriteTimeUtc(f));

        var second = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, second.Result);
        Assert.Equal(0, second.Added + second.Updated + second.Removed);
        Assert.Equal(reported, rig.Refresh.Reported.Count);
        Assert.Equal(stamps, rig.AllStubFiles().ToDictionary(static f => f, static f => File.GetLastWriteTimeUtc(f)));
    }

    [Fact]
    public async Task ThePeersFirstSyncWithStubs_QueuesOneScan_AndLaterSyncsQueueNone()
    {
        await using var rig = await SyncRig.CreateAsync();
        var first = await rig.SyncAsync();
        Assert.Equal(0, first.Added);
        Assert.Equal(0, rig.Refresh.Scans); // nothing written, nothing to scan

        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        Assert.Equal(1, rig.Refresh.Scans);

        rig.Peer.Add("Alien", 1979, tmdb: 348);
        await rig.SyncAsync();
        Assert.Equal(1, rig.Refresh.Scans); // the library already had stubs: core's folder report is enough
    }

    // --- T-SYNC-2, T-SYNC-3

    [Fact]
    public async Task T_SYNC_2_AMetadataEditOnThePeer_UpdatesTheNfo_AndKeepsTheStub()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var before = rig.Entries.Single();
        var strm = Path.Combine(FolderOf(rig, before), "The Matrix (1999) [tmdbid-603].strm");
        var strmBefore = Text(strm);
        rig.Refresh.Reported.Clear();

        matrix.Overview = "A new plot.";
        matrix.Etag = "e2";
        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Updated);
        var after = rig.Entries.Single();
        Assert.Equal(before.StubKey, after.StubKey);
        Assert.Equal(before.StubRelPath, after.StubRelPath);
        Assert.Equal("e2", after.PeerEtag);
        Assert.Contains("A new plot.", Text(Path.Combine(FolderOf(rig, after), "The Matrix (1999) [tmdbid-603].nfo")), StringComparison.Ordinal);
        Assert.Equal(strmBefore, Text(strm)); // the .strm is untouched: same item, same local id
        Assert.Equal([FolderOf(rig, after)], rig.Refresh.Reported);
    }

    [Fact]
    public async Task Warden07_L5_ANfoThatGrowsPastTheQuota_KeepsTheOldNfo_AndRaisesTheAlert()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var folder = FolderOf(rig, rig.Entries.Single());
        var nfoPath = Path.Combine(folder, "The Matrix (1999) [tmdbid-603].nfo");
        var old = Text(nfoPath);
        rig.Settings = SyncSettings.Default with { QuotaBytes = rig.Catalog().Usage.DiskBytes + 200 };
        rig.Time.Advance(TimeSpan.FromHours(7));

        matrix.Overview = new string('x', 5000);
        matrix.Etag = "e2";
        var report = await rig.SyncAsync();

        Assert.Equal(old, Text(nfoPath));
        Assert.Contains(SyncAlert.QuotaReached, report.Alerts);
        Assert.True(rig.Catalog().Usage.DiskBytes <= rig.Settings.QuotaBytes);
    }

    [Fact]
    public async Task T_SYNC_3_ARenameOnThePeer_NeverRenamesTheFolder()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();

        matrix.Name = "The Matrix Reloaded";
        matrix.Etag = "e2";
        await rig.SyncAsync();

        var entry = rig.Entries.Single();
        Assert.Equal("The Matrix Reloaded", entry.Title);
        Assert.True(Directory.Exists(Path.Combine(rig.LibraryFolder, "The Matrix (1999) [tmdbid-603]")));
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
        Assert.Contains("The Matrix Reloaded", Text(Path.Combine(rig.LibraryFolder, "The Matrix (1999) [tmdbid-603]", "The Matrix (1999) [tmdbid-603].nfo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_SYNC_3_AMovedItem_KeepsItsStub_AndOnlyThePeerIdChanges()
    {
        await using var rig = await SyncRig.CreateAsync();
        var matrix = rig.Peer.Add("The Matrix", 1999, tmdb: 603, imdb: "tt0133093");
        await rig.SyncAsync();
        var before = rig.Entries.Single();
        var strm = Path.Combine(FolderOf(rig, before), "The Matrix (1999) [tmdbid-603].strm");
        var strmBefore = Text(strm);

        // The file was moved on the peer: it is a new item there, with the same provider ids.
        matrix.Deleted = true;
        var moved = rig.Peer.Add("The Matrix", 1999, tmdb: 603, imdb: "tt0133093", configure: m => m.Etag = "e9");
        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Moved);
        Assert.Equal(0, report.Removed);
        Assert.Equal(0, report.Added);
        var after = rig.Entries.Single();
        Assert.Equal(before.StubKey, after.StubKey);
        Assert.Equal(moved.Id.ToString("N"), after.PeerItemId);
        Assert.Equal(before.StubRelPath, after.StubRelPath);
        Assert.Equal(strmBefore, Text(strm));
        Assert.Equal("e9", after.PeerEtag);
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
    }

    // --- T-SYNC-4, T-SYNC-5

    [Fact]
    public async Task T_SYNC_4_AMovieDeletedOnThePeer_LosesItsStub_AtTheNextSync()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("Keep", 2000, tmdb: 1);
        var gone = rig.Peer.Add("Gone", 2001, tmdb: 2);
        rig.Peer.Add("Also Keep", 2002, tmdb: 3);
        await rig.SyncAsync();
        Assert.Equal(3, Directory.GetDirectories(rig.LibraryFolder).Length);
        rig.Refresh.Reported.Clear();

        gone.Deleted = true;
        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Removed);
        Assert.Equal(["Also Keep (2002) [tmdbid-3]", "Keep (2000) [tmdbid-1]"], Directory.GetDirectories(rig.LibraryFolder).Select(static f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Equal(2, rig.Entries.Count());
        Assert.Equal([Path.Combine(rig.LibraryFolder, "Gone (2001) [tmdbid-2]")], rig.Refresh.Reported);
    }

    [Fact]
    public async Task T_SYNC_5_AnEmptiedPeerLibrary_HoldsEveryRemoval_AndDeletesNothing()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        rig.Peer.Add("Two", 2001, tmdb: 2);
        rig.Peer.Add("Three", 2002, tmdb: 3);
        await rig.SyncAsync();

        foreach (var movie in rig.Peer.Movies)
        {
            movie.Deleted = true; // the NAS is unmounted: the library is empty
        }

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, report.Result);
        Assert.Equal(3, report.HeldRemovals);
        Assert.Contains(SyncAlert.HeldRemovals, report.Alerts);
        Assert.Equal(0, report.Removed);
        Assert.Equal(3, Directory.GetDirectories(rig.LibraryFolder).Length);
        Assert.All(rig.Entries, static e => Assert.Equal(EntryState.HeldRemoval, e.State));
        Assert.Equal(3, rig.Catalog().HeldRemovals.Count);

        // The share comes back: the entries are active again and nothing was lost.
        foreach (var movie in rig.Peer.Movies)
        {
            movie.Deleted = false;
        }

        var back = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, back.Result);
        Assert.All(rig.Entries, static e => Assert.Equal(EntryState.Active, e.State));
        Assert.Empty(rig.Catalog().HeldRemovals);
        Assert.Equal(3, Directory.GetDirectories(rig.LibraryFolder).Length);
    }

    [Fact]
    public async Task TheMassDeletionGuard_HoldsMoreThanTheThreshold_AndAllowsLess()
    {
        await using var rig = await RigWithAsync(100);
        await rig.SyncAsync();
        Assert.Equal(100, Directory.GetDirectories(rig.LibraryFolder).Length);

        foreach (var movie in rig.Peer.Movies.Take(25))
        {
            movie.Deleted = true; // 25 is not more than max(25, 10%)
        }

        var allowed = await rig.SyncAsync();
        Assert.Equal(25, allowed.Removed);
        Assert.Equal(75, Directory.GetDirectories(rig.LibraryFolder).Length);

        foreach (var movie in rig.Peer.Movies.Skip(25).Take(26))
        {
            movie.Deleted = true; // 26 is more than max(25, 10% of 75)
        }

        var held = await rig.SyncAsync();
        Assert.Equal(0, held.Removed);
        Assert.Equal(26, held.HeldRemovals);
        Assert.Equal(75, Directory.GetDirectories(rig.LibraryFolder).Length);
        Assert.Equal(SyncResultKind.Held, held.Result);
    }

    [Fact]
    public async Task ReleasingHeldRemovals_AppliesThem()
    {
        await using var rig = await RigWithAsync(40);
        await rig.SyncAsync();
        foreach (var movie in rig.Peer.Movies.Take(30))
        {
            movie.Deleted = true;
        }

        var held = await rig.SyncAsync();
        Assert.Equal(30, held.HeldRemovals);

        var released = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Removals);

        Assert.Equal(30, released.Removed);
        Assert.Equal(10, Directory.GetDirectories(rig.LibraryFolder).Length);
        Assert.Empty(rig.Catalog().HeldRemovals);
        Assert.Equal(SyncResultKind.Ok, released.Result);
    }

    [Fact]
    public async Task TheSameRemovals_StillGoneAfterThreeCompleteSyncsOver24Hours_AreReleasedAutomatically()
    {
        await using var rig = await RigWithAsync(40);
        await rig.SyncAsync();
        foreach (var movie in rig.Peer.Movies.Take(30))
        {
            movie.Deleted = true;
        }

        var first = await rig.SyncAsync();
        rig.Time.Advance(TimeSpan.FromHours(13));
        var second = await rig.SyncAsync();
        Assert.Equal(30, first.HeldRemovals);
        Assert.Equal(30, second.HeldRemovals);
        Assert.Equal(40, Directory.GetDirectories(rig.LibraryFolder).Length);

        rig.Time.Advance(TimeSpan.FromHours(13));
        var third = await rig.SyncAsync();

        Assert.Equal(30, third.Removed);
        Assert.Equal(10, Directory.GetDirectories(rig.LibraryFolder).Length);
    }

    [Fact]
    public async Task AnItemTheListingMisses_ButThePeerStillHas_IsNotRemoved()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        var shifted = rig.Peer.Add("Two", 2001, tmdb: 2);
        rig.Peer.Add("Three", 2002, tmdb: 3);
        await rig.SyncAsync();

        shifted.HideFromListing = true; // a page boundary shifted while paging: the item is skipped, not deleted
        var report = await rig.SyncAsync();

        Assert.Equal(0, report.Removed);
        Assert.Equal(3, rig.Entries.Count(static e => e.State == EntryState.Active));
        Assert.Equal(3, Directory.GetDirectories(rig.LibraryFolder).Length);
    }

    // --- the partial-run rule

    [Fact]
    public async Task APageThatFails_MakesTheRunPartial_AddsAndUpdatesAreKept_NothingIsRemoved()
    {
        await using var rig = await SyncRig.CreateAsync();
        var doomed = rig.Peer.Add("Doomed", 2000, tmdb: 1);
        rig.Peer.Add("Stays", 2001, tmdb: 2);
        await rig.SyncAsync();

        rig.Peer.GeneratedCount = 450; // three pages
        rig.Peer.ListingOverride = start => start >= 200 ? (500, "{}"u8.ToArray()) : null;
        doomed.Deleted = true;
        rig.Peer.Add("Fresh", 2005, tmdb: 5);
        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Partial, report.Result);
        Assert.False(report.Complete);
        Assert.Equal(1, report.Added); // the new title still arrives
        Assert.Equal(0, report.Removed);
        Assert.True(Directory.Exists(Path.Combine(rig.LibraryFolder, "Doomed (2000) [tmdbid-1]")));
    }

    [Fact]
    public async Task APeerThatAnnouncesMoreThanItListed_IsNotACompleteListing()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        var two = rig.Peer.Add("Two", 2001, tmdb: 2);
        await rig.SyncAsync();

        two.Deleted = true;
        rig.Peer.TotalOverride = 500; // announces 500, lists 1
        var report = await rig.SyncAsync();

        Assert.False(report.Complete);
        Assert.Equal(0, report.Removed);
        Assert.Equal(2, Directory.GetDirectories(rig.LibraryFolder).Length);
    }

    // --- guards on additions

    [Fact]
    public async Task TheFirstSync_IsExemptFromTheNewItemsGuard()
    {
        await using var rig = await RigWithAsync(30);
        rig.Settings = SyncSettings.Default with { NewItemsMinCount = 3, NewItemsPercent = 20 };

        var report = await rig.SyncAsync();

        Assert.Equal(30, report.Added);
        Assert.Equal(0, report.HeldAdditions);
    }

    [Fact]
    public async Task T_SYNC_12_AnAvalancheOfNewTitles_IsHeld_AndReleaseImportsThem()
    {
        await using var rig = await RigWithAsync(5);
        rig.Settings = SyncSettings.Default with { NewItemsMinCount = 10, NewItemsPercent = 20 };
        await rig.SyncAsync();
        for (var i = 0; i < 40; i++)
        {
            rig.Peer.Add("Flood " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), 2020, tmdb: 5000 + i, configure: m => { m.PosterTag = null; m.BackdropTag = null; });
        }

        var held = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, held.Result);
        Assert.Equal(40, held.HeldAdditions);
        Assert.Equal(0, held.Added);
        Assert.Contains(SyncAlert.HeldAdditions, held.Alerts);
        Assert.Equal(5, Directory.GetDirectories(rig.LibraryFolder).Length);
        Assert.Equal(40, rig.Catalog().HeldAdditions.Count);

        var again = await rig.SyncAsync();
        Assert.Equal(0, again.Added); // held items are not re-counted as new, and still not imported
        Assert.Equal(40, again.HeldAdditions);

        var released = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Additions);

        Assert.Equal(40, released.Added);
        Assert.Equal(45, Directory.GetDirectories(rig.LibraryFolder).Length);
        Assert.Empty(rig.Catalog().HeldAdditions);
    }

    [Fact]
    public async Task T_SYNC_12_FortyNineThousandNewTitles_AreAllHeld_WithoutWritingAnything()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync();

        rig.Peer.GeneratedCount = 49_000;
        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Held, report.Result);
        Assert.Equal(49_000, report.HeldAdditions);
        Assert.Equal(0, report.Added);
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
        Assert.Equal(49_000, rig.Catalog().HeldAdditions.Count);
    }

    [Fact]
    public async Task APeerWithMoreThan50000Titles_StopsTheSync_WithAnAlert_AndChangesNothing()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync();
        var before = rig.Catalog().Entries.Count;

        rig.Peer.TotalOverride = 1_000_000;
        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Failed, report.Result);
        Assert.Equal("entryCap", report.Error);
        Assert.Contains(SyncAlert.EntryCap, report.Alerts);
        Assert.Equal(before, rig.Catalog().Entries.Count);
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
    }

    // --- dedup transitions

    [Fact]
    public async Task ALocalCopyThatAppearsLater_TurnsTheStubIntoADuplicate_AndRemovesIt()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var key = rig.Entries.Single().StubKey;

        rig.Local.Movies.Add(new LocalMovie(new CanonicalIds(603, null, null), "The Matrix", 1999, 999));
        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Removed);
        var entry = rig.Entries.Single();
        Assert.Equal(EntryState.DuplicateLocal, entry.State);
        Assert.Equal(key, entry.StubKey);
        Assert.Null(entry.StubRelPath);
        Assert.Empty(Directory.GetDirectories(rig.LibraryFolder));

        rig.Local.Movies.Clear(); // and when the local copy goes away, the stub comes back
        var back = await rig.SyncAsync();

        Assert.Equal(1, back.Added);
        var restored = rig.Entries.Single();
        Assert.Equal(EntryState.Active, restored.State);
        Assert.Equal(key, restored.StubKey);
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
    }

    [Fact]
    public async Task ABetterRankedPeersTitle_WinsOverThisPeers()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Peer.Add("Inception", 2010, tmdb: 27205);
        var other = "ffffffffffffffffffffffffffffffff";
        await rig.AddPeerAsync(other, "Better", priority: 50, paired: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = new CatalogDocument { PeerKey = other, CompletedOnce = true };
        catalog.Entries.Add(new CatalogEntry
        {
            StubKey = "bbbbbbbbbbbbbbbb",
            PeerItemId = "11111111111111111111111111111111",
            PeerMediaSourceId = "11111111111111111111111111111111",
            MappingKey = "movies-1",
            Title = "The Matrix",
            Year = 1999,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
            State = EntryState.Active,
            StubRelPath = "The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm",
        });
        rig.Catalogs.Save(catalog);

        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added);
        var entries = rig.Entries.ToDictionary(static e => e.Title);
        Assert.Equal(EntryState.DuplicatePeer, entries["The Matrix"].State);
        Assert.Equal("bbbbbbbbbbbbbbbb", entries["The Matrix"].DuplicateOf);
        Assert.Equal(EntryState.Active, entries["Inception"].State);
    }

    private static async Task AddBetterPeerWithTheMatrixAsync(SyncRig rig, string other)
    {
        await rig.AddPeerAsync(other, "Better", priority: 50, paired: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = new CatalogDocument { PeerKey = other, CompletedOnce = true };
        catalog.Entries.Add(new CatalogEntry
        {
            StubKey = "bbbbbbbbbbbbbbbb",
            PeerItemId = "11111111111111111111111111111111",
            PeerMediaSourceId = "11111111111111111111111111111111",
            MappingKey = "movies-1",
            Title = "The Matrix",
            Year = 1999,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
            State = EntryState.Active,
            StubRelPath = "The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm",
        });
        rig.Catalogs.Save(catalog);
    }

    [Fact]
    public async Task AnOfflineOrPausedBetterPeer_DoesNotKeepThisPeersTitlesOut_AndTheTieIsRankedAgainWhenItIsBack()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Peer.Add("Inception", 2010, tmdb: 27205);
        var other = "ffffffffffffffffffffffffffffffff";
        await AddBetterPeerWithTheMatrixAsync(rig, other);
        rig.DownPeers.Add(other); // the old server is offline: the owner's case, a new server for the same friend with the same titles

        var report = await rig.SyncAsync();

        Assert.Equal(2, report.Added);
        Assert.All(rig.Entries, e => Assert.Equal(EntryState.Active, e.State));
        Assert.Equal(2, Directory.GetDirectories(rig.LibraryFolder).Length); // both stubs were written

        rig.DownPeers.Clear(); // the old one is back: rank decides again at the next sync, and it is better
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        await rig.SyncAsync(SyncTrigger.Scheduled);

        var entries = rig.Entries.ToDictionary(static e => e.Title);
        Assert.Equal(EntryState.DuplicatePeer, entries["The Matrix"].State);
        Assert.Equal(EntryState.Active, entries["Inception"].State);
    }

    [Fact]
    public async Task WhenABetterServerLetsGo_TheTitlesItHeldBackGetTheirStubs_AndOneScanIsQueued()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Peer.Add("Inception", 2010, tmdb: 27205);
        var other = "ffffffffffffffffffffffffffffffff";
        await AddBetterPeerWithTheMatrixAsync(rig, other);
        var better = rig.Catalogs.Load(other).Document;
        better.Entries.Add(new CatalogEntry
        {
            StubKey = "cccccccccccccccc",
            PeerItemId = "22222222222222222222222222222222",
            PeerMediaSourceId = "22222222222222222222222222222222",
            MappingKey = "movies-1",
            Title = "Inception",
            Year = 2010,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "27205" },
            State = EntryState.Active,
            StubRelPath = "Inception (2010) [tmdbid-27205]/Inception (2010) [tmdbid-27205].strm",
        });
        rig.Catalogs.Save(better);

        await rig.SyncAsync(); // the better server holds both: this server has no stub at all
        Assert.All(rig.Entries, e => Assert.Equal(EntryState.DuplicatePeer, e.State));
        Assert.Equal(0, rig.Refresh.Scans);

        rig.DownPeers.Add(other); // the better server is removed, paused or gone
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        var report = await rig.SyncAsync(SyncTrigger.Scheduled);

        Assert.Equal(2, report.Added);
        Assert.All(rig.Entries, e => Assert.Equal(EntryState.Active, e.State));
        Assert.Equal(1, rig.Refresh.Scans); // the new stubs show without a manual scan

        rig.Time.Advance(TimeSpan.FromMinutes(11));
        await rig.SyncAsync(SyncTrigger.Scheduled);
        Assert.Equal(1, rig.Refresh.Scans); // and a sync that promotes nothing queues none
    }

    [Fact]
    public void PromotionScans_AreOneEveryFiveMinutes_AcrossAllServers_AndNeverWhileAScanRuns()
    {
        var refresh = new FakeRefresh();
        var d = new CatalogSyncService.Dependencies(null!, null!, null!, null!, null!, refresh, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ => true);
        var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(d.TryStartScan("a", t0, promotion: true));
        Assert.False(d.TryStartScan("b", t0.AddMinutes(1), promotion: true)); // another server's promotion, within five minutes of any
        Assert.True(d.TryStartScan("b", t0.AddMinutes(1))); // the first stubs of a library are not held back by that
        Assert.False(d.TryStartScan("a", t0.AddMinutes(2))); // but one server asks at most once in five minutes
        Assert.True(d.TryStartScan("b", t0.AddMinutes(6), promotion: true));
        refresh.IsScanRunning = true;
        Assert.False(d.TryStartScan("c", t0.AddMinutes(30), promotion: true)); // a scan is running: none is queued behind it
    }

    [Fact]
    public async Task AWorseRankedPeersTitle_DoesNotBlockThisPeers()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        var other = "ffffffffffffffffffffffffffffffff";
        await rig.AddPeerAsync(other, "Worse", priority: 200, paired: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = new CatalogDocument { PeerKey = other };
        catalog.Entries.Add(new CatalogEntry
        {
            StubKey = "bbbbbbbbbbbbbbbb",
            PeerItemId = "11111111111111111111111111111111",
            MappingKey = "movies-1",
            Title = "The Matrix",
            Year = 1999,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
            State = EntryState.Active,
            StubRelPath = "x/x.strm",
        });
        rig.Catalogs.Save(catalog);

        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added);
        Assert.Equal(EntryState.Active, rig.Entries.Single().State);
    }

    [Fact]
    public void ThePeerRank_IsPriorityThenPairingTimeThenKey()
    {
        PeerState Peer(string key, int priority, DateTime? paired) => new() { PeerKey = key, Priority = priority, Pairing = paired is null ? null : new PairingInfo { PairedUtc = paired.Value } };
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(CatalogSyncService.Outranks(Peer("b", 10, late), Peer("a", 20, early)));
        Assert.True(CatalogSyncService.Outranks(Peer("b", 10, early), Peer("a", 10, late)));
        Assert.False(CatalogSyncService.Outranks(Peer("a", 10, late), Peer("b", 10, early)));
        Assert.True(CatalogSyncService.Outranks(Peer("a", 10, early), Peer("b", 10, early)));
        Assert.False(CatalogSyncService.Outranks(Peer("a", 10, early), Peer("a", 10, early)));
        Assert.True(CatalogSyncService.Outranks(Peer("a", 10, early), Peer("z", 10, null)));
    }

    // --- T-SYNC-6, -7, -8 at the sync level

    [Fact]
    public async Task T_SYNC_6_HostileTitles_StayInsideTheLibrary_WithValidNfo()
    {
        await using var rig = await SyncRig.CreateAsync();
        var titles = new[] { "../../etc/x", "..\\..\\Windows", "CON", "com1.txt", "</title><thumb>http://evil</thumb>", "\u202Eevil\u202C", "a\u0000b", new string('x', 20_000) };
        for (var i = 0; i < titles.Length; i++)
        {
            rig.Peer.Add(titles[i], 2000 + i, tmdb: 100 + i);
        }

        var report = await rig.SyncAsync();

        Assert.Equal(titles.Length, report.Added);
        var libraryFull = Path.GetFullPath(rig.LibraryFolder);
        foreach (var folder in Directory.GetDirectories(rig.LibraryFolder))
        {
            Assert.Equal(libraryFull, Path.GetDirectoryName(Path.GetFullPath(folder)));
            var nfo = Directory.GetFiles(folder, "*.nfo").Single();
            var document = XDocument.Parse(Text(nfo));
            Assert.Empty(document.Descendants("thumb"));
            Assert.Empty(document.Descendants("fanart"));
        }

        Assert.False(Directory.Exists(Path.Combine(rig.Dir.Path, "etc")));
        Assert.DoesNotContain(Directory.GetDirectories(rig.StubRoot, "*", SearchOption.AllDirectories), static d => d.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_SYNC_7_HostileImages_AreRejected_AndTheTitlesStillImport()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("Html", 2000, tmdb: 1);
        rig.Peer.Add("Webp", 2001, tmdb: 2);
        rig.Peer.Add("Huge", 2002, tmdb: 3);
        rig.Peer.Add("Bomb", 2003, tmdb: 4);
        rig.Peer.ImageOverride = (movie, _) => movie.Name switch
        {
            "Html" => (200, "image/jpeg", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")),
            "Webp" => (200, "image/webp", ImageBuilder.WebP()),
            "Huge" => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 50 * 1024 * 1024)),
            _ => (200, "image/png", ImageBuilder.Png(8000, 8000, 4000)),
        };

        var report = await rig.SyncAsync();

        Assert.Equal(4, report.Added);
        Assert.Equal(0, report.ImagesStored);
        Assert.DoesNotContain(rig.AllStubFiles(), static f => Path.GetFileName(f).StartsWith("poster.", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("backdrop.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_SYNC_8_AListingThatIsTooLarge_OrTooDeep_FailsTheSync_AndChangesNothing()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync();

        rig.Peer.ListingOverride = _ => (200, new byte[12 * 1024 * 1024]);
        var big = await rig.SyncAsync();
        rig.Peer.ListingOverride = _ => (200, Encoding.ASCII.GetBytes(new string('[', 10_000)));
        var deep = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Failed, big.Result);
        Assert.Equal(SyncResultKind.Failed, deep.Result);
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
        Assert.Equal(EntryState.Active, rig.Entries.Single().State);
    }

    [Fact]
    public async Task T_SYNC_8_AnItemWithAnUnknownEnum_IsSkippedAndCounted_NotFatal()
    {
        await using var rig = await SyncRig.CreateAsync();
        var one = rig.Peer.Add("One", 2000, tmdb: 1);
        var two = rig.Peer.Add("Two", 2001, tmdb: 2);
        var body = $"{{\"Items\":[{{\"Id\":\"{one.Id:N}\",\"Name\":\"One\",\"Etag\":\"e1\",\"Type\":\"Movie\",\"ProviderIds\":{{\"Tmdb\":\"1\"}},\"ProductionYear\":2000}},"
            + $"{{\"Id\":\"{Guid.NewGuid():N}\",\"Name\":\"Future\",\"Type\":\"FutureItemKind\"}},"
            + $"{{\"Id\":\"{two.Id:N}\",\"Name\":\"Two\",\"Etag\":\"e1\",\"Type\":\"Movie\",\"ProviderIds\":{{\"Tmdb\":\"2\"}},\"ProductionYear\":2001}}],\"TotalRecordCount\":3,\"StartIndex\":0}}";
        rig.Peer.ListingOverride = _ => (200, Encoding.UTF8.GetBytes(body));

        var report = await rig.SyncAsync();

        Assert.Equal(2, report.Added);
        Assert.Equal(1, report.SkippedItems);
        Assert.Contains(SyncAlert.ItemsSkipped, report.Alerts);
    }

    // --- T-SYNC-10, T-SYNC-11

    [Fact]
    public async Task T_SYNC_10_TitlesStopAtTheQuota_WithAnAlert_AndTheRestImportOnceThereIsRoom()
    {
        await using var rig = await SyncRig.CreateAsync();
        for (var i = 0; i < 40; i++)
        {
            rig.Peer.Add("Movie " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), 2000, tmdb: 100 + i);
        }

        rig.Settings = SyncSettings.Default with { QuotaBytes = 100_000 };
        var report = await rig.SyncAsync();

        Assert.InRange(report.Added, 1, 39); // the NFO and .strm count against the budget too (W6 L-8): the first sync stops adding titles
        Assert.Contains(SyncAlert.QuotaReached, report.Alerts);
        Assert.True(rig.Catalog().Usage.DiskBytes <= 100_000);
        Assert.Equal(report.Added, Directory.GetDirectories(rig.LibraryFolder).Length);
        var first = report.Added;

        rig.Settings = SyncSettings.Default;
        rig.Time.Advance(TimeSpan.FromHours(7));
        var second = await rig.SyncAsync();
        Assert.Equal(40 - first, second.Added);
        Assert.Equal(40, Directory.GetDirectories(rig.LibraryFolder).Length);
    }

    [Fact]
    public async Task T_SYNC_11_ActorsPerTitleAndDistinctNamesAreBounded()
    {
        await using var rig = await SyncRig.CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            var index = i;
            rig.Peer.Add("Movie " + i, 2000 + i, tmdb: 100 + i, configure: m =>
            {
                m.PosterTag = null;
                m.BackdropTag = null;
                m.Actors = [.. Enumerable.Range(0, 15).Select(a => $"Actor {index}-{a}")];
                m.Genres = [$"Genre {index}"];
            });
        }

        rig.Settings = SyncSettings.Default with { NameCap = 12 };
        var report = await rig.SyncAsync();

        Assert.Equal(6, report.Added);
        Assert.Contains(SyncAlert.NameCap, report.Alerts);
        var catalog = rig.Catalog();
        Assert.Equal(12, catalog.Names.People.Count);
        Assert.Equal(6, catalog.Names.Genres.Count);
        var names = new HashSet<string>();
        foreach (var nfo in rig.AllStubFiles().Where(static f => f.EndsWith(".nfo", StringComparison.Ordinal)))
        {
            var actors = XDocument.Parse(Text(nfo)).Root!.Elements("actor").Select(static a => (string)a.Element("name")!).ToList();
            Assert.True(actors.Count <= 5);
            names.UnionWith(actors);
        }

        Assert.True(names.Count <= 12);
    }

    // --- one sync, refresh scope, preconditions

    [Fact]
    public async Task OnlyOneSyncRunsAtATime()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        rig.Peer.DelayMs = 300;

        var first = rig.SyncAsync();
        await Task.Delay(100);
        var second = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Skipped, second.Result);
        Assert.Equal("busy", second.Error);
        Assert.Equal(SyncResultKind.Ok, (await first).Result);
    }

    [Fact]
    public async Task OnlyTheShoalLibraryFoldersAreRefreshed_AndALargeChangeReportsTheLibraryRoot()
    {
        await using var small = await RigWithAsync(5);
        await small.SyncAsync();
        Assert.Equal(5, small.Refresh.Reported.Count);
        Assert.All(small.Refresh.Reported, p => Assert.StartsWith(small.LibraryFolder + Path.DirectorySeparatorChar, p, StringComparison.Ordinal));

        await using var large = await RigWithAsync(60);
        await large.SyncAsync();
        Assert.Equal([large.LibraryFolder], large.Refresh.Reported);
    }


    [Fact]
    public async Task AMappingWithNoStubs_IsNeverThrottled_SoAReAddedLibraryFillsAtOnce()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync(SyncTrigger.Manual)).Result);

        // The library is removed and added again within five minutes: its catalog part is gone.
        var load = rig.Catalogs.Load(SyncRig.PeerKey);
        load.Document.Entries.Clear();
        load.Document.Libraries.Clear();
        rig.Catalogs.Save(load.Document);

        var again = await rig.SyncAsync(SyncTrigger.Manual);

        Assert.Equal(SyncResultKind.Ok, again.Result);
        Assert.Equal(1, again.Added);
    }
    [Fact]
    public async Task ManualSyncs_NeedFiveMinutesBetweenThem_AScanDefersTheSync_AndPreconditionsAreChecked()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync(SyncTrigger.Manual)).Result);

        var soon = await rig.SyncAsync(SyncTrigger.Manual);
        Assert.Equal("tooSoon", soon.Error);
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync(SyncTrigger.Manual)).Result);

        rig.Refresh.IsScanRunning = true;
        Assert.Equal("scanRunning", (await rig.SyncAsync()).Error);
        rig.Refresh.IsScanRunning = false;

        rig.HasCredentials = false;
        Assert.Equal("noCredentials", (await rig.SyncAsync()).Error);
        rig.HasCredentials = true;

        Assert.Equal("unknownPeer", (await rig.Sync.SyncAsync("0000000000000000", SyncTrigger.Scheduled)).Error);
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = false);
        Assert.Equal("disabled", (await rig.SyncAsync()).Error);
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = true);
        await rig.State.UpdateAsync(s => s.StubRoot = string.Empty);
        Assert.Equal("noStubRoot", (await rig.SyncAsync()).Error);
    }

    [Fact]
    public async Task AnotherServerAtTheAddress_StopsTheSync_BeforeAnythingIsAsked()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        rig.Peer.ServerId = Guid.Parse("ffffffffffffffffffffffffffffffff"); // a reset server or a reused name

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Failed, report.Result);
        Assert.Equal("identityMismatch", report.Error);
        Assert.Equal(0, rig.Peer.ItemHits);
        Assert.Empty(rig.Entries);
    }

    [Fact]
    public async Task ADamagedCatalog_IsNeverReplacedByAnEmptyOne()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("One", 2000, tmdb: 1);
        await rig.SyncAsync();
        var path = Path.Combine(rig.Paths.PeerDir(SyncRig.PeerKey), "catalog.json");
        File.WriteAllText(path, "{ not json");
        File.Delete(path + ".bak");

        var report = await rig.SyncAsync();

        Assert.Equal(SyncResultKind.Failed, report.Result);
        Assert.Equal("catalogUnreadable", report.Error);
        Assert.Equal("{ not json", Text(path));
        Assert.Single(Directory.GetDirectories(rig.LibraryFolder));
    }

    [Fact]
    public async Task StopAsync_EndsASyncThatWaitsOnASlowOrUnreachablePeer_AtOnce_AndNothingThrows()
    {
        await using var rig = await RigWithAsync(30);
        rig.Peer.DelayMs = 20_000; // every answer takes far longer than the test
        var running = rig.SyncAsync();
        await Task.Delay(300);
        Assert.True(rig.Sync.IsSyncing(SyncRig.PeerKey));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await rig.Sync.StopAsync(SyncRig.PeerKey, TimeSpan.FromSeconds(10));
        watch.Stop();

        Assert.False(rig.Sync.IsSyncing(SyncRig.PeerKey));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "took " + watch.Elapsed);
        await running; // the run ended without an exception
    }

    [Fact]
    public async Task WhenTheBudgetRunsOut_WhatIsDoneIsSaved_AndNothingThrows()
    {
        await using var rig = await RigWithAsync(30);
        rig.Peer.DelayMs = 150;
        rig.Sync.SyncBudget = TimeSpan.FromMilliseconds(500);

        var report = await rig.SyncAsync();

        Assert.True(report.Result is SyncResultKind.Partial or SyncResultKind.Failed);
        Assert.NotNull(rig.Catalog().LastSync);
    }
}
