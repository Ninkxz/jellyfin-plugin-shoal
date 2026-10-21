using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.2 K1 for TV: a TV mapping follows its own peer library only, never a namesake.
/// </summary>
public sealed class TvSyncEngineTests
{
    [Fact]
    public async Task Mapping_SameNameOtherLibrary_HoldsTitles()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result);
        var episodes = rig.Entries.Count(static e => e.Kind == EntryKind.Episode);
        var strms = Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count();
        Assert.Equal(3, episodes);

        var other = Guid.Parse("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3");
        var picked = rig.Peer.Tv.LibraryId;
        rig.Peer.Tv.LibraryId = other; // the picked library left the share; another one with the same name and type is listed
        rig.Time.Advance(TimeSpan.FromHours(1));
        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.LibraryMissing, report.Alerts);
        Assert.Equal(episodes, rig.Entries.Count(static e => e.Kind == EntryKind.Episode)); // held, never deleted
        Assert.Equal(strms, Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count());
        Assert.DoesNotContain(rig.Peer.Requests, r => r.Contains(other.ToString("N"), StringComparison.OrdinalIgnoreCase)); // never listed
        Assert.Equal(picked.ToString("N"), rig.Catalog().Libraries.Single(l => l.MappingKey == SyncRig.TvMappingKey).PeerLibraryId);
    }

    [Fact]
    public async Task Mapping_OutOfScope_HoldsTitles()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(series: 1, seasonsPerSeries: 1, episodesPerSeason: 3);
        await rig.AddTvMappingAsync();
        Assert.Equal(SyncResultKind.Ok, (await rig.SyncAsync()).Result);
        var episodes = rig.Entries.Count(static e => e.Kind == EntryKind.Episode);
        var strms = Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count();

        rig.Peer.EnabledFolders = [rig.Peer.LibraryId.ToString("N")]; // 2.9.2 K2: the TV library is still a view, but the share no longer includes it
        rig.Time.Advance(TimeSpan.FromHours(1));
        var report = await rig.SyncAsync();

        Assert.Contains(SyncAlert.LibraryMissing, report.Alerts);
        Assert.Equal(episodes, rig.Entries.Count(static e => e.Kind == EntryKind.Episode)); // held, never deleted
        Assert.Equal(strms, Directory.EnumerateFiles(rig.TvFolder, "*.strm", SearchOption.AllDirectories).Count());
    }
}
