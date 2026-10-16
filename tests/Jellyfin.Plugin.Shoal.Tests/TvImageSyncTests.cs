using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The TV image stage inside the TV sync (DESIGN v2.7.3; wiring from Warden 12): images follow the stubs, only for what is new or changed, from the entry's own
/// folder, and what the peer could not deliver waits in the catalog for the follow-up run.
/// </summary>
public sealed class TvImageSyncTests
{
    private static async Task<SyncRig> RigAsync(int episodes = 2)
    {
        var rig = await SyncRig.CreateAsync(null, realTv: true);
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: episodes);
        await rig.AddTvMappingAsync();
        return rig;
    }

    private static string Show(SyncRig rig) => Directory.EnumerateDirectories(rig.TvFolder).Single();

    private static int ImageRequests(SyncRig rig) => rig.Peer.Requests.Count(static r => r.Contains("/Images/", StringComparison.Ordinal));

    [Fact]
    public async Task TheImagesOfASeriesItsSeasonsAndItsEpisodes_FollowTheStubs_InTheirOwnFolders()
    {
        await using var rig = await RigAsync();

        var report = await rig.SyncAsync();

        var show = Show(rig);
        foreach (var name in new[] { "poster", "backdrop", "banner", "landscape" })
        {
            Assert.True(File.Exists(Path.Combine(show, name + ".jpg")), name);
        }

        var season = Path.Combine(show, "Season 01");
        Assert.True(File.Exists(Path.Combine(season, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(season, "S01E01-thumb.jpg")));
        Assert.True(File.Exists(Path.Combine(season, "S01E02-thumb.jpg")));
        Assert.Equal(7, report.ImagesStored);
        Assert.False(report.ImagesPending);
        Assert.Empty(rig.Catalog().ImagesPending);
        Assert.All(rig.AllStubFiles().Where(static f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)), f => Assert.StartsWith(rig.TvFolder, f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnchangedLibrary_FetchesNoImageAgain()
    {
        await using var rig = await RigAsync();
        await rig.SyncAsync();
        var before = ImageRequests(rig);
        rig.Time.Advance(TimeSpan.FromHours(1));

        var report = await rig.SyncAsync();

        Assert.Equal(before, ImageRequests(rig));
        Assert.Equal(0, report.ImagesStored);
    }

    [Fact]
    public async Task APeerThatCannotDeliverImages_LeavesThemPending_AndTheFollowUpFetchesThem()
    {
        await using var rig = await RigAsync();
        rig.Peer.TvImageOverride = static (_, _) => (-1, "text/plain", []); // the connection drops

        var failed = await rig.SyncAsync();

        Assert.True(failed.ImagesPending);
        Assert.NotEmpty(rig.Catalog().ImagesPending);
        Assert.DoesNotContain(rig.AllStubFiles(), static f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        Assert.True(SyncFollowUp.Next([failed], TimeSpan.FromHours(6), 0).Wait == SyncFollowUp.Delay);

        rig.Peer.TvImageOverride = null;
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        var followUp = await rig.SyncAsync();

        Assert.False(followUp.ImagesPending);
        Assert.Equal(7, followUp.ImagesStored);
        Assert.Empty(rig.Catalog().ImagesPending);
    }

    [Fact]
    public async Task AnImageTheSandboxRefuses_IsNotRetried()
    {
        await using var rig = await RigAsync(1);
        rig.Peer.TvImageOverride = static (_, _) => (200, "image/jpeg", System.Text.Encoding.ASCII.GetBytes("not an image at all"));

        var report = await rig.SyncAsync();

        Assert.False(report.ImagesPending);
        Assert.Empty(rig.Catalog().ImagesPending);
        Assert.DoesNotContain(rig.AllStubFiles(), static f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithEpisodeThumbnailsOff_OnlySeriesAndSeasonArtIsFetched()
    {
        await using var rig = await RigAsync();
        rig.Tv.EpisodeThumbs = static () => false;

        await rig.SyncAsync();

        Assert.DoesNotContain(rig.AllStubFiles(), static f => f.EndsWith("-thumb.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(Show(rig), "poster.jpg")));
    }
}
