using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.2 K1: a mapping follows the peer library it was made for (its key, else the stored id), never another library with the same name.
/// </summary>
public sealed class SyncRunTests
{
    private static readonly Guid A = Guid.Parse("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");
    private static readonly Guid B = Guid.Parse("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2");

    private static LibraryMapping MappingFor(Guid id, string type = "movies") =>
        new() { MappingKey = SyncRun.MappingKeyOf(id), RemoteName = "Movies", CollectionType = type, LocalLibraryName = "Hops Movies", Subfolder = "movies" };

    [Fact]
    public void ResolveLibraryId_SameNameOtherLibrary_IsMissing()
    {
        var mapping = MappingFor(A);
        var known = new CatalogLibrary { MappingKey = mapping.MappingKey, PeerLibraryId = A.ToString("N") };

        Assert.Null(SyncRun.ResolveLibraryId(known, mapping, [new PeerView(B, "Movies", "movies")]));
        Assert.Null(SyncRun.ResolveLibraryId(null, mapping, [new PeerView(B, "Movies", "movies")]));
        Assert.Null(SyncRun.ResolveLibraryId(null, mapping, [new PeerView(A, "Movies", "tvshows")])); // same id, other type: not this mapping
    }

    [Fact]
    public void ResolveLibraryId_FirstSync_UsesKey()
    {
        var mapping = MappingFor(A);

        // No catalog record yet (the very first sync) and a namesake listed first: the key decides.
        Assert.Equal(A, SyncRun.ResolveLibraryId(null, mapping, [new PeerView(B, "Movies", "movies"), new PeerView(A, "Movies", "movies")]));
        Assert.Equal(A, SyncRun.ResolveLibraryId(null, mapping, [new PeerView(A, "Renamed by the friend", "movies")]));
    }

    [Fact]
    public void ResolveLibraryId_LegacyKey_UsesStoredIdOnly()
    {
        var mapping = new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", Subfolder = "movies" };

        Assert.Equal(A, SyncRun.ResolveLibraryId(new CatalogLibrary { MappingKey = "movies-1", PeerLibraryId = A.ToString("N") }, mapping, [new PeerView(B, "Movies", "movies"), new PeerView(A, "Films", "movies")]));
        Assert.Null(SyncRun.ResolveLibraryId(null, mapping, [new PeerView(B, "Movies", "movies")]));
    }

    [Fact]
    public async Task ASameNameLibrary_AfterThePickedOneLeaves_NeverEntersTheCatalog_AndTheTitlesAreHeld()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.LibraryId = A);
        rig.Peer.Add("Picked One", 2001, tmdb: 11);
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result);
        var stubs = rig.AllStubFiles().Count();
        Assert.True(stubs > 0);

        rig.Peer.LibraryId = B; // A left the share; B has the same name and type
        rig.Peer.Add("Only In B", 2002, tmdb: 22);
        rig.Time.Advance(TimeSpan.FromHours(1));
        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.LibraryMissing, report.Alerts);
        Assert.DoesNotContain(rig.Entries, e => e.Title == "Only In B");
        Assert.Contains(rig.Entries, e => e.Title == "Picked One" && e.State == EntryState.Active); // held, never deleted
        Assert.Equal(stubs, rig.AllStubFiles().Count());
        Assert.DoesNotContain(rig.Peer.Requests, r => r.Contains(B.ToString("N"), StringComparison.OrdinalIgnoreCase)); // B was never listed
        Assert.Equal(A.ToString("N"), rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.MappingKey).PeerLibraryId);
    }

    // 2.9.2 K2: the picked library is still listed by /UserViews, but the share no longer includes it (Pilot 21 G1: EnabledFolders in N form,
    // with a stale id of a deleted library). The mapping is missing and its titles are held; the library is never listed.
    [Fact]
    public async Task MappingOutOfScope_HeldAsMissing()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.LibraryId = A);
        rig.Peer.Add("Picked One", 2001, tmdb: 11);
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result);
        var stubs = rig.AllStubFiles().Count();
        Assert.True(stubs > 0);

        rig.Peer.EnabledFolders = ["3fb79a0c5d2e4f6a8b9c0d1e2f3a4b5c"]; // only a stale id: the friend took Movies out of the share
        rig.Peer.Add("Added Later", 2002, tmdb: 22);
        rig.Time.Advance(TimeSpan.FromHours(1));
        var listedBefore = rig.Peer.Requests.Count(r => r.Contains("ParentId=" + A.ToString("N"), StringComparison.OrdinalIgnoreCase));
        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.LibraryMissing, report.Alerts);
        Assert.DoesNotContain(rig.Entries, e => e.Title == "Added Later");
        Assert.Contains(rig.Entries, e => e.Title == "Picked One" && e.State == EntryState.Active); // held, never deleted
        Assert.Equal(stubs, rig.AllStubFiles().Count());
        Assert.Equal(listedBefore, rig.Peer.Requests.Count(r => r.Contains("ParentId=" + A.ToString("N"), StringComparison.OrdinalIgnoreCase))); // not listed
    }

    [Theory]
    [InlineData("allFolders")]
    [InlineData("noPolicy")]
    [InlineData("noEnabledFolders")]
    [InlineData("401")]
    [InlineData("notJson")]
    public async Task UnknownScope_HoldsEveryLibrary(string answer)
    {
        await using var rig = await SyncRig.CreateAsync(p => p.LibraryId = A);
        rig.Peer.Add("Picked One", 2001, tmdb: 11);
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result);
        var entries = rig.Entries.Count();

        switch (answer)
        {
            case "allFolders":
                rig.Peer.EnableAllFolders = true; // EnabledFolders still names Movies: fails closed all the same
                break;
            case "noPolicy":
                rig.Peer.MeOverride = static () => (200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Name\":\"shoal-home\"}");
                break;
            case "noEnabledFolders":
                rig.Peer.MeOverride = static () => (200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Policy\":{\"IsAdministrator\":false,\"EnableAllFolders\":false}}");
                break;
            case "401":
                rig.Peer.MeOverride = static () => (401, "{}");
                break;
            default:
                rig.Peer.MeOverride = static () => (200, "<html>");
                break;
        }

        rig.Peer.Add("Added Later", 2002, tmdb: 22);
        rig.Time.Advance(TimeSpan.FromHours(1));
        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.LibraryMissing, report.Alerts);
        Assert.Equal(entries, rig.Entries.Count());
        Assert.DoesNotContain(rig.Entries, e => e.Title == "Added Later");
    }

    /// <summary>
    /// 2.9.2 K7: an alias rename makes every NFO label stale. The next sync rewrites the movie, series and episode NFOs once (plot and "Shared by"
    /// studio name the new alias), stamps the catalog with it, and the sync after that writes nothing.
    /// </summary>
    [Fact]
    public async Task AliasChanged_RewritesNfosOnce()
    {
        await using var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 2);
        await rig.AddTvMappingAsync();
        await rig.SyncAsync();

        // Movie, tvshow and both episode NFOs (a season NFO carries no label).
        string[] Labelled() => [.. rig.AllStubFiles().Where(static f => f.EndsWith(".nfo", StringComparison.Ordinal) && !f.EndsWith("season.nfo", StringComparison.Ordinal))];
        var nfos = Labelled();
        Assert.Equal(4, nfos.Length);
        Assert.All(nfos, f => Assert.Contains("From Hops's server.", System.IO.File.ReadAllText(f), StringComparison.Ordinal));
        Assert.All(nfos, f => Assert.Contains("<studio>Shared by Hops</studio>", System.IO.File.ReadAllText(f), StringComparison.Ordinal));
        Assert.Equal("Hops", rig.Catalog().LabelName);

        await rig.State.UpdateAsync(static s => s.Home.Peers[0].Alias = "Brew");
        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.SyncAsync();

        Assert.Equal(nfos.Order(StringComparer.Ordinal), Labelled().Order(StringComparer.Ordinal)); // same paths: same items
        foreach (var f in nfos)
        {
            var text = System.IO.File.ReadAllText(f);
            Assert.Contains("From Brew's server.", text, StringComparison.Ordinal);
            Assert.Contains("<studio>Shared by Brew</studio>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Hops's", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Shared by Hops", text, StringComparison.Ordinal);
        }

        Assert.Equal("Brew", rig.Catalog().LabelName);
        Assert.All(rig.Entries.Where(static e => e.Kind != EntryKind.Season), static e => Assert.Equal(FriendLabel.Version, e.LabelVersion));

        var written = nfos.ToDictionary(static f => f, System.IO.File.GetLastWriteTimeUtc);
        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.SyncAsync();
        Assert.All(nfos, f => Assert.Equal(written[f], System.IO.File.GetLastWriteTimeUtc(f))); // once
    }

    /// <summary>
    /// 2.9.3 K6: the stamp holds the cleaned name. A catalog stamped by 2.9.2 with a name that held a link ("Hops evil.example") is rewritten once: the NFOs
    /// name "Hops" with no link, the stamp becomes "Hops", and the next sync writes nothing.
    /// </summary>
    [Fact]
    public async Task NameCleaned_RewritesOnce()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.State.UpdateAsync(static s => s.Home.Peers[0].Alias = "Brew");
        await rig.SyncAsync();
        var nfo = rig.AllStubFiles().Single(static f => f.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.Contains("From Brew's server.", System.IO.File.ReadAllText(nfo), StringComparison.Ordinal);

        // What 2.9.2 left behind after the alias became "Hops evil.example": the raw name in the stamp.
        await rig.State.UpdateAsync(static s => s.Home.Peers[0].Alias = "Hops evil.example");
        var doc = rig.Catalog();
        doc.LabelName = "Hops evil.example";
        rig.Catalogs.Save(doc);
        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.SyncAsync();

        var text = System.IO.File.ReadAllText(nfo);
        Assert.Contains("From Hops's server.", text, StringComparison.Ordinal);
        Assert.Contains("<studio>Shared by Hops</studio>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", text, StringComparison.Ordinal);
        Assert.Equal("Hops", rig.Catalog().LabelName);

        var written = System.IO.File.GetLastWriteTimeUtc(nfo);
        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.SyncAsync();
        Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(nfo)); // once
        Assert.Equal("Hops", rig.Catalog().LabelName);
    }

    /// <summary>2.9.2 K7: a catalog from before the stamp (no LabelName) is only stamped; its up-to-date NFOs are not rewritten for it.</summary>
    [Fact]
    public async Task NoStampYet_IsStamped_WithoutARewrite()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var doc = rig.Catalog();
        doc.LabelName = null;
        rig.Catalogs.Save(doc);
        var nfo = rig.AllStubFiles().Single(static f => f.EndsWith(".nfo", StringComparison.Ordinal));
        var before = System.IO.File.GetLastWriteTimeUtc(nfo);

        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.SyncAsync();

        Assert.Equal("Hops", rig.Catalog().LabelName);
        Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(nfo));
    }
}
