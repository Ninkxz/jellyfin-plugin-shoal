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
/// 2.9.4 friend report: placement Main ("Combine into my libraries"), friends feeding one folder of the admin's own TV library. That library had
/// series merging (<c>EnableAutomaticSeriesGrouping</c>) off, so a lower-ranked friend's extra episodes of a series (K43 join: a second folder with the same
/// ids) showed as a second entry of the same series. Now the extra episodes join only when the library merges series; otherwise they stay hidden sources.
/// </summary>
public sealed class TvSeriesMergeTests
{
    private const string Plain = TvDuplicateFolderTests.Plain;

    private static int ActiveEpisodes(SyncRig rig, string seriesKey) =>
        rig.Entries.Count(e => e.Kind == EntryKind.Episode && e.State == EntryState.Active && e.ParentKey == seriesKey);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALibraryWithoutSeriesMerging_ShowsOneEntryPerSeries_AndOneWithItMergesAsBefore(bool merges)
    {
        var (rig, folder) = await TvDuplicateFolderTests.SharedLibraryRigAsync();
        await using var _ = rig;
        rig.Tv.SeriesGrouping = path => LibraryFolders.SamePath(path, folder) ? merges : null;
        TvDuplicateFolderTests.AddSeason2(rig); // season 1 overlaps Friend A's, season 2 Friend A does not have

        await rig.SyncAsync();

        var series = Assert.Single(rig.Entries, static e => e.Kind == EntryKind.Series);
        Assert.Equal("barleyseries0001", series.DuplicateOf);
        if (merges)
        {
            Assert.Equal(EntryState.Active, series.State);
            Assert.Equal(2, TvDuplicateFolderTests.SeriesFolders(folder).Count); // merged by the library into one entry
            Assert.Equal(3, ActiveEpisodes(rig, series.StubKey));
        }
        else
        {
            Assert.Equal(EntryState.DuplicatePeer, series.State);
            Assert.Null(series.FolderRel);
            Assert.Equal([Plain], TvDuplicateFolderTests.SeriesFolders(folder)); // never a second entry of the same series
            Assert.Equal(6, rig.Entries.Count(e => e.Kind == EntryKind.Episode && e.State == EntryState.DuplicatePeer));
        }
    }

    [Fact]
    public async Task TurningSeriesMergingOff_RemovesTheJoinedCopy()
    {
        var (rig, folder) = await TvDuplicateFolderTests.SharedLibraryRigAsync();
        await using var _ = rig;
        var merges = true;
        rig.Tv.SeriesGrouping = _ => merges;
        TvDuplicateFolderTests.AddSeason2(rig);
        await rig.SyncAsync();
        Assert.Equal(2, TvDuplicateFolderTests.SeriesFolders(folder).Count);

        merges = false; // an admin's own library that does not merge series (what 2.9.3 wrote into)
        rig.Time.Advance(System.TimeSpan.FromHours(6));
        await rig.SyncAsync();

        Assert.Equal([Plain], TvDuplicateFolderTests.SeriesFolders(folder));
        Assert.DoesNotContain(rig.Entries, static e => e.State == EntryState.Active);
    }
}
