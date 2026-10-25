using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Progress;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class KeepMyPlaceRetentionTests
{
    internal sealed class Capture : IKeepMyPlaceRetention
    {
        public bool Allow { get; set; }

        public List<string> Paths { get; } = [];

        public Task<bool> RetainBeforeRemovalAsync(string absoluteStubPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(absoluteStubPath));
            Paths.Add(absoluteStubPath);
            return Task.FromResult(Allow);
        }
    }

    [Fact]
    public async Task LocalMovieDemotion_FailedCaptureKeepsPlayableStubAndRetries()
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("Film", 2000, tmdb: 1));
        await rig.SyncAsync();
        var entry = Assert.Single(rig.Entries);
        var path = Path.GetFullPath(Path.Combine(rig.LibraryFolder, entry.StubRelPath!));
        var capture = new Capture();
        rig.Sync.KeepMyPlace = capture;
        rig.Local.Movies.Add(new LocalMovie(new CanonicalIds(1, null, null), "Film", 2000, 10));
        var held = await rig.SyncAsync();
        Assert.Equal(0, held.Removed);
        Assert.Equal(EntryState.HeldRemoval, Assert.Single(rig.Entries).State);
        Assert.True(File.Exists(path));
        Assert.Equal(path, Assert.Single(capture.Paths));
        capture.Allow = true;
        var released = await rig.Sync.ReleaseAsync(SyncRig.PeerKey, ReleaseKind.Both);
        Assert.Equal(1, released.Removed);
        Assert.False(File.Exists(path));
        Assert.Equal(EntryState.DuplicateLocal, Assert.Single(rig.Entries).State);
    }

    [Fact]
    public async Task LocalSeriesDemotion_FailedEpisodeCaptureKeepsSeriesAndEpisode()
    {
        await using var rig = await SyncRig.CreateAsync();
        rig.Peer.Tv = FakeTvTree.Generate(1, 1, 1);
        await rig.AddTvMappingAsync();
        await rig.SyncAsync();
        var episode = rig.Entries.Single(e => e.Kind == EntryKind.Episode);
        var path = Path.GetFullPath(Path.Combine(rig.TvFolder, episode.StubRelPath!));
        var capture = new Capture();
        rig.Sync.KeepMyPlace = capture;
        rig.LocalSeries.Series.Add(new LocalSeries(new CanonicalIds(null, 100_000, null), "Show 0", 2010));
        await rig.SyncAsync();
        Assert.True(File.Exists(path));
        Assert.Equal(EntryState.HeldRemoval, rig.Entries.Single(e => e.Kind == EntryKind.Episode).State);
        Assert.Equal(EntryState.Active, rig.Entries.Single(e => e.Kind == EntryKind.Series).State);
        Assert.Contains(path, capture.Paths);
    }

    [Fact]
    public async Task LayoutCaptureFailure_HoldsBeforeTopologyChanges()
    {
        await using var rig = await JobRig.CreateAsync(p => p.Add("Film", 2000, tmdb: 1));
        var target = await rig.NewLibraryAsync("Target");
        var before = rig.State.Current.Home.Libraries.Single(l => l.LibKey == rig.LegacyKey).PathRel;
        var capture = new Capture();
        rig.Job.Retention = capture;
        var result = await rig.Job.StartAsync([new MoveRequest(SyncRig.PeerKey, "movies", target)], rig.Admin.Id);
        Assert.Equal(LayoutJobError.None, result);
        await rig.Job.Running;
        Assert.Equal(LayoutJobPhase.Failed, rig.Job.Status().Phase);
        Assert.Empty(rig.State.Current.Home.Libraries.Single(l => l.LibKey == target).Feeds);
        Assert.Equal(before, rig.State.Current.Home.Libraries.Single(l => l.LibKey == rig.LegacyKey).PathRel);
        Assert.NotEmpty(capture.Paths);
        Assert.True(Directory.Exists(rig.Sync.LibraryFolder));
    }
}
