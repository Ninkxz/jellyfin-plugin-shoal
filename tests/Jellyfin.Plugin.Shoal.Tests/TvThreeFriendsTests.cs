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
/// 2.9.4 friend report: three friends feed one folder (placement Main). Friend C's episodes were only checked against the highest-ranked owner of the
/// series (Friend A), so episodes A lacks but Friend B has were added by C too. Now C checks against every reachable higher-ranked owner, in rank order.
/// </summary>
public sealed class TvThreeFriendsTests
{
    private const string FriendA = TvDuplicateFolderTests.Other;
    private const string FriendB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Plain = TvDuplicateFolderTests.Plain;

    private static int ActiveEpisodes(SyncRig rig, string seriesKey) =>
        rig.Entries.Count(e => e.Kind == EntryKind.Episode && e.State == EntryState.Active && e.ParentKey == seriesKey);

    // Friend A (ranked first) has S01E01; Friend B (second) has S01E02 and S01E03 in its own copy; the rig's peer (Friend C, last) lists S01E01 to E03.
    private static async Task<(SyncRig Rig, string Folder)> ThreeFriendsRigAsync()
    {
        var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        const string libKey = TvDuplicateFolderTests.LibKey;
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = FriendA, DisplayName = "Friend A", BaseUrl = "http://a.tail1234.ts.net:8096", Priority = 1, Libraries = { new LibraryMapping { MappingKey = "tv-9", RemoteName = "TV", LocalLibraryName = "A TV", Subfolder = "tv", CollectionType = "tvshows" } } });
            s.Home.Peers.Add(new PeerState { PeerKey = FriendB, DisplayName = "Friend B", BaseUrl = "http://b.tail1234.ts.net:8096", Priority = 5, Libraries = { new LibraryMapping { MappingKey = "tv-8", RemoteName = "TV", LocalLibraryName = "B TV", Subfolder = "tv", CollectionType = "tvshows" } } });
            s.Home.Libraries.Add(new ShoalLibrary { LibKey = libKey, Name = "Shows", Type = "tvshows", PathRel = ShoalLibrary.LibsFolder + "/" + libKey, Feeds = [SyncRig.PeerKey, FriendA, FriendB] });
        });
        var folder = LibraryFolders.PathOf(rig.StubRoot, rig.State.Current.Home.Libraries.Single(static l => l.LibKey == libKey))!;
        var ids = new Dictionary<string, string> { ["Tvdb"] = "100000" };

        var a = new CatalogDocument { PeerKey = FriendA, CompletedOnce = true };
        var aSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "aseries000000001", PeerItemId = new string('9', 32), MappingKey = "tv-9", State = EntryState.Active, FolderRel = Plain, Title = "Show 0", Year = 2010, ProviderIds = ids };
        a.Entries.Add(aSeries);
        a.Entries.Add(new CatalogEntry { Kind = EntryKind.Episode, StubKey = "aepisode00000001", ParentKey = aSeries.StubKey, PeerItemId = new string('a', 32), PeerSeriesId = aSeries.PeerItemId, MappingKey = "tv-9", Season = 1, Episode = 1, State = EntryState.Active, StubRelPath = $"{Plain}/Season 01/S01E01.strm" });
        Directory.CreateDirectory(Path.Combine(folder, Plain, "Season 01"));
        File.WriteAllText(Path.Combine(folder, Plain, "tvshow.nfo"), "<tvshow />");
        File.WriteAllText(Path.Combine(folder, Plain, "Season 01", "S01E01.strm"), StubSentinel.Content("aepisode00000001"));
        rig.Catalogs.Save(a);

        const string bFolder = Plain + " [shoal-bbbbbbbb]";
        var b = new CatalogDocument { PeerKey = FriendB, CompletedOnce = true };
        var bSeries = new CatalogEntry { Kind = EntryKind.Series, StubKey = "bseries000000001", PeerItemId = new string('8', 32), MappingKey = "tv-8", State = EntryState.Active, FolderRel = bFolder, Title = "Show 0", Year = 2010, ProviderIds = ids };
        b.Entries.Add(bSeries);
        for (var n = 2; n <= 3; n++)
        {
            b.Entries.Add(new CatalogEntry { Kind = EntryKind.Episode, StubKey = "bepisode0000000" + n, ParentKey = bSeries.StubKey, PeerItemId = new string((char)('a' + n), 32), PeerSeriesId = bSeries.PeerItemId, MappingKey = "tv-8", Season = 1, Episode = n, State = EntryState.Active, StubRelPath = $"{bFolder}/Season 01/S01E0{n}.strm" });
        }

        rig.Catalogs.Save(b);
        return (rig, folder);
    }

    [Fact]
    public async Task TheThirdFriend_DoesNotAddEpisodesTheSecondHas_AndFailsOverWhenTheSecondIsOffline()
    {
        var (rig, _) = await ThreeFriendsRigAsync();
        await using var __ = rig;

        await rig.SyncAsync();

        var series = Assert.Single(rig.Entries, static e => e.Kind == EntryKind.Series);
        Assert.Equal(EntryState.DuplicatePeer, series.State); // every episode is another friend's: nothing of Friend C's shows
        Assert.Equal(0, ActiveEpisodes(rig, series.StubKey));
        var hidden = rig.Entries.Where(static e => e.Kind == EntryKind.Episode && e.State == EntryState.DuplicatePeer).OrderBy(static e => e.Episode).ToList();
        Assert.Equal(["aepisode00000001", "bepisode00000002", "bepisode00000003"], hidden.Select(static e => e.DuplicateOf));

        await rig.State.UpdateAsync(s => s.Home.Peers.Single(static p => p.PeerKey == FriendB).Enabled = false); // Friend B is offline
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();

        var mine = Assert.Single(rig.Entries, static e => e.Kind == EntryKind.Series);
        Assert.Equal(EntryState.Active, mine.State);
        Assert.Equal(2, ActiveEpisodes(rig, mine.StubKey)); // Friend C now shows the two Friend A lacks
        Assert.Equal("aepisode00000001", Assert.Single(rig.Entries, static e => e.Kind == EntryKind.Episode && e.State == EntryState.DuplicatePeer).DuplicateOf);
    }
}
