using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot 14 backend defects.
/// </summary>
public sealed class Pilot14FixTests
{
    [Fact]
    public void INBOX_Moonbase240Ids_WithoutHyphens_AreRead_AndAnUnreadableItemIsSkipped()
    {
        // The shape Pilot 13/14 saw from Moonbase 2.4.0: PascalCase, ids as 32 hex digits.
        const string List = """
            {"items":[
              {"Id":"3f2504e04f8941d39a0c0305e82c3301","Title":"Why might a title not work?","Body":"**Why**","Color":null,"Delivery":"inbox","Audience":"users","TargetUserIds":["a0000000000000000000000000000001","b0000000-0000-0000-0000-000000000002"],"CreatedUtc":"2026-10-18T06:00:00Z","CreatedByUserId":"00000000000000000000000000000000"},
              {"Id":"not-a-guid","Title":"Broken"},
              {"Id":"4f2504e0-4f89-41d3-9a0c-0305e82c3302","Title":"Other","TargetUserIds":[]}
            ]}
            """;

        var (outcome, items) = Jellyfin.Plugin.Shoal.Api.Admin.LoopbackMoonbaseClient.ParseList(System.Text.Encoding.UTF8.GetBytes(List));

        Assert.Equal(Jellyfin.Plugin.Shoal.Api.Admin.MoonbaseOutcome.Ok, outcome);
        Assert.Equal(2, items.Count);
        Assert.Equal(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"), items[0].Id);
        Assert.Equal([Guid.Parse("a0000000-0000-0000-0000-000000000001"), Guid.Parse("b0000000-0000-0000-0000-000000000002")], items[0].TargetUserIds);
        Assert.Equal("Other", items[1].Title);
    }

    [Fact]
    public void INBOX_ThePostAnswersId_IsReadInEitherForm()
    {
        var hyphenless = """{"success":true,"item":{"Id":"3f2504e04f8941d39a0c0305e82c3301","Title":"Why might a title not work?"}}""";

        Assert.Equal(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"), Jellyfin.Plugin.Shoal.Api.Admin.LoopbackMoonbaseClient.ParsePostId(System.Text.Encoding.UTF8.GetBytes(hyphenless), null));
        var sent = Guid.NewGuid();
        Assert.Equal(sent, Jellyfin.Plugin.Shoal.Api.Admin.LoopbackMoonbaseClient.ParsePostId("""{"success":true}"""u8.ToArray(), sent));
    }

    [Fact]
    public async Task LBL3_ANextSync_HealsAStubAnNfoSaverWiped_NfoLabelArtAndStrayMovieNfo()
    {
        await using var rig = await SyncRig.CreateAsync(static p => p.Add("Alien", 1979, tmdb: 348));
        await rig.SyncAsync();
        var folder = System.IO.Directory.EnumerateDirectories(rig.LibraryFolder).Single();
        var top = System.IO.Path.GetFileName(folder);
        var nfo = System.IO.Path.Combine(folder, top + ".nfo");
        var hadPoster = System.IO.File.Exists(System.IO.Path.Combine(folder, "poster.jpg"));
        Assert.True(hadPoster);

        // What the saver plus "Replace all metadata" left: our NFO without the label and synopsis, a movie.nfo, the art gone.
        System.IO.File.WriteAllText(nfo, "<movie><title>Alien</title><plot></plot></movie>");
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "movie.nfo"), "<movie><plot>label only</plot></movie>");
        System.IO.File.Delete(System.IO.Path.Combine(folder, "poster.jpg"));
        Assert.NotEqual(StubDamage.None, StubInspector.Of(rig.LibraryFolder, rig.Catalog().Entries.Single(static e => e.Title == "Alien")));

        rig.Time.Advance(TimeSpan.FromHours(7));
        await rig.SyncAsync();

        Assert.Contains("From Hops's server. " + FriendLabel.Tail, System.IO.File.ReadAllText(nfo), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(folder, "movie.nfo")));
        Assert.Equal(hadPoster, System.IO.File.Exists(System.IO.Path.Combine(folder, "poster.jpg")));
        Assert.Equal(StubDamage.None, StubInspector.Of(rig.LibraryFolder, rig.Catalog().Entries.Single(static e => e.Title == "Alien")));
    }

    [Fact]
    public void LBL3_TheLock_TurnsSaversFetchersAndLocalSavingOffAgain_AndAFreshShoalLibraryIsAlreadyLocked()
    {
        var fresh = Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsFactory.Create("/stubs/libs/0123456789abcdef", ["OpenSubtitles"], [], tvShows: false);
        Assert.Empty(fresh.MetadataSavers ?? []);
        Assert.False(Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsFactory.Lock(fresh)); // created locked (also for the v2.9 layouts: one factory)

        fresh.MetadataSavers = ["Nfo"];
        fresh.SaveLocalMetadata = true;
        fresh.TypeOptions[0].MetadataFetchers = ["TheMovieDb"];
        fresh.TypeOptions[0].ImageFetchers = ["TheMovieDb"];
        fresh.EnableRealtimeMonitor = true;

        Assert.True(Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsFactory.Lock(fresh));
        Assert.Empty(fresh.MetadataSavers);
        Assert.False(fresh.SaveLocalMetadata);
        Assert.Empty(fresh.TypeOptions[0].MetadataFetchers);
        Assert.Empty(fresh.TypeOptions[0].ImageFetchers);
        Assert.False(fresh.EnableRealtimeMonitor);
        Assert.Equal(["OpenSubtitles"], fresh.DisabledSubtitleFetchers); // what the admin may set stays
    }

    [Fact]
    public async Task LBL3_TheGuard_LocksShoalPlacementLibraries_AndNamesMainLibrariesWithTheNfoSaverOn()
    {
        using var rig = new LayoutRig();
        var shoal = (await rig.Layout.CreateAsync(new Jellyfin.Plugin.Shoal.Visibility.LibraryCreateRequest("Friends' movies", "movies", Jellyfin.Plugin.Shoal.State.LibraryPlacement.Shoal), rig.Admin.Id)).LibKey!;
        var main = (await rig.Layout.CreateAsync(new Jellyfin.Plugin.Shoal.Visibility.LibraryCreateRequest("Friends in Movies", "movies", Jellyfin.Plugin.Shoal.State.LibraryPlacement.Main, rig.Main.Id), rig.Admin.Id)).LibKey!;
        rig.Libraries.Unlocked.Add("Friends' movies");
        rig.Libraries.SetSavesNfo(rig.Main.Id, true);
        var guard = new Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsGuard(rig.State, rig.Libraries, NullLogger.Instance);

        Assert.Equal(1, guard.Enforce());
        Assert.Contains("Friends' movies", rig.Libraries.Locked);
        Assert.DoesNotContain(rig.Main.Name, rig.Libraries.Locked); // the admin's main library is never changed
        Assert.Equal([rig.Main.Name], guard.MainLibrariesSavingNfo());
        Assert.NotNull(shoal);
        Assert.NotNull(main);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-peer")]
    public void POSTSCAN_AnItemWithNoKnownOwner_HasNoCatalog_AndNothingThrows(string peerKey)
    {
        using var dir = new TempDirectory();
        var catalog = new StubCatalog(new CatalogStore(new ShoalPaths(dir.Path), NullLogger.Instance));

        Assert.Null(catalog.Find(peerKey, "Alien (1979)"));
        Assert.Null(catalog.FindMovie(peerKey, new string('1', 32)));
        Assert.Null(catalog.FindSeriesByTop(peerKey, "Show"));
        Assert.Null(catalog.FindHiddenEpisode(peerKey, new string('1', 32), 1, 1));
    }
}
