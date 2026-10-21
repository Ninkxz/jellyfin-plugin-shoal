using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 11 L3 / I2: genre and studio names are left out of the NFOs of titles in a library that is not visible to everyone (restricted profiles included), so a
/// user who cannot open it never sees them in core's lists; a library everyone opens keeps them.
/// </summary>
public sealed class NfoFacetTests
{
    private static void Seed(FakeLibraryPeer p) => p.Add("Alien", 1979, tmdb: 348);

    [Fact]
    public void OnlyALibraryEveryoneOpens_KeepsGenresAndStudios()
    {
        Assert.False(LibraryFolders.HidesFacets(new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true }));
        Assert.True(LibraryFolders.HidesFacets(new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = false }));
        Assert.True(LibraryFolders.HidesFacets(new LibraryAccess { Mode = AccessMode.Selected }));
    }

    [Fact]
    public async Task ASelectedUsersLibrary_GetsNoGenreOrStudio_AnEveryoneLibraryDoes()
    {
        await using var restricted = await SyncRig.CreateAsync(Seed);
        await restricted.State.UpdateAsync(s => s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Selected });
        await restricted.SyncAsync();
        var hidden = File.ReadAllText(Directory.EnumerateFiles(restricted.LibraryFolder, "*.nfo", SearchOption.AllDirectories).First());

        Assert.DoesNotContain("<genre>", hidden, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Studio One", hidden, System.StringComparison.Ordinal);
        Assert.Contains("<title>Alien</title>", hidden, System.StringComparison.Ordinal);

        await using var everyone = await SyncRig.CreateAsync(Seed);
        await everyone.State.UpdateAsync(s => s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true });
        await everyone.SyncAsync();
        var shown = File.ReadAllText(Directory.EnumerateFiles(everyone.LibraryFolder, "*.nfo", SearchOption.AllDirectories).First());

        Assert.Contains("<genre>Drama</genre>", shown, System.StringComparison.Ordinal);
    }

    // --- 2.9.2 K5: the friend is the studio

    private static string[] Studios(string nfo) => [.. System.Xml.Linq.XDocument.Parse(nfo).Root!.Elements("studio").Select(static e => e.Value)];

    [Fact]
    public async Task FriendStudio_ReplacesPeerStudios()
    {
        await using var rig = await SyncRig.CreateAsync(Seed);
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true });
        await rig.SyncAsync();
        var nfo = File.ReadAllText(Directory.EnumerateFiles(rig.LibraryFolder, "*.nfo", SearchOption.AllDirectories).First());

        Assert.Equal(["Shared by Hops"], Studios(nfo)); // exactly one, the friend; the peer's "Studio One" is gone
        Assert.Contains("<genre>Drama</genre>", nfo, System.StringComparison.Ordinal);

        var tv = new Jellyfin.Plugin.Shoal.Sync.TvNfoWriter();
        IReadOnlyList<string> tags = ["Shoal"];
        var series = tv.Series(new Jellyfin.Plugin.Shoal.Sync.SanitizedSeries { Title = "Show", Studios = ["Network One"] }, tags, "Pat");
        var episode = tv.Episode(new Jellyfin.Plugin.Shoal.Sync.SanitizedEpisode { Title = "Pilot", Season = 1, Number = 1 }, tags, "Pat");
        Assert.Equal(["Shared by Pat"], Studios(series));
        Assert.Equal(["Shared by Pat"], Studios(episode));
        Assert.Empty(Studios(tv.Season(new Jellyfin.Plugin.Shoal.Sync.SanitizedSeason { Name = "Season 1", Number = 1 }, tags)));
    }

    [Fact]
    public async Task FriendStudio_WithOmitFacets_PerW1()
    {
        await using var rig = await SyncRig.CreateAsync(Seed);
        await rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries[0].Access = new LibraryAccess { Mode = AccessMode.Selected });
        await rig.SyncAsync();
        var nfo = File.ReadAllText(Directory.EnumerateFiles(rig.LibraryFolder, "*.nfo", SearchOption.AllDirectories).First());

        Assert.DoesNotContain("<genre>", nfo, System.StringComparison.Ordinal);
        Assert.Equal(["Shared by Hops"], Studios(nfo)); // W1: the friend studio says what the Shoal: Hops tag says, so OmitFacets keeps it
        Assert.Contains("<tag>Shoal: Hops</tag>", nfo, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ACopysNfo_KeepsTheFriendStudio()
    {
        using var dir = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "libs", "Alien (1979)")).FullName;
        var nfo = Path.Combine(folder, "Alien (1979).nfo");
        File.WriteAllText(nfo, "<movie>\n  <title>Alien</title>\n  <genre>Horror</genre>\n  <studio>Brandywine</studio>\n  <studio>Shared by Hops &amp; Co</studio>\n  <tag>Shoal</tag>\n</movie>\n");

        Assert.Equal(1, NfoRetag.StripFacets(dir.Path, folder));

        Assert.Equal("<movie>\n  <title>Alien</title>\n  <studio>Shared by Hops &amp; Co</studio>\n  <tag>Shoal</tag>\n</movie>\n", File.ReadAllText(nfo));
    }

    [Fact]
    public void ACopysNfo_LosesOnlyItsGenreAndStudioLines()
    {
        using var dir = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "libs", "Alien (1979)")).FullName;
        var nfo = Path.Combine(folder, "Alien (1979).nfo");
        File.WriteAllText(nfo, "<movie>\n  <title>Alien</title>\n  <genre>Horror &amp; Sci-Fi</genre>\n  <studio>Brandywine</studio>\n  <tag>Shoal</tag>\n</movie>\n");

        Assert.Equal(1, NfoRetag.StripFacets(dir.Path, folder));

        Assert.Equal("<movie>\n  <title>Alien</title>\n  <tag>Shoal</tag>\n</movie>\n", File.ReadAllText(nfo));
        Assert.Equal(0, NfoRetag.StripFacets(dir.Path, folder));
    }
}
