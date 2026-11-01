using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RestoredMovieRetirementTests
{
    [Fact]
    public async Task OfflineSingletonRetirementUsesRecordedStubAndLeavesPhysicalAndUnrelatedFiles()
    {
        await using var rig = await SyncRig.CreateAsync(p => { p.Add("Film", 2000, tmdb: 1); p.Add("Other", 2001, tmdb: 2); });
        await rig.SyncAsync();
        var entry = rig.Entries.Single(e => e.Title == "Film");
        var path = Path.GetFullPath(Path.Combine(rig.LibraryFolder, entry.StubRelPath!));
        var other = Path.Combine(rig.LibraryFolder, rig.Entries.Single(e => e.Title == "Other").StubRelPath!);
        var physical = rig.Dir.Combine("original.mkv");
        File.WriteAllBytes(physical, [1, 2, 3]);
        var unknown = Path.Combine(Path.GetDirectoryName(path)!, "notes.txt");
        File.WriteAllText(unknown, "keep");
        var groups = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        rig.Sync.Groups = groups;
        groups.Update(d => { d.Groups.Add(new TitleGroup { GroupKey = "aaaaaaaaaaaaaaaa", MetaOwner = SyncRig.PeerKey, Kind = GroupKind.Movie, Members = [new() { PeerKey = SyncRig.PeerKey, PeerItemId = entry.PeerItemId }], Copies = [new() { StubKey = entry.StubKey, FolderRel = entry.StubRelPath!.Split('/')[0] }] }); return true; });
        var capture = new KeepMyPlaceRetentionTests.Capture { Allow = true };
        rig.Sync.KeepMyPlace = capture;
        rig.Local.Movies.Add(new(new(1, null, null), "Film", 2000, 3));
        rig.HasCredentials = false;
        var requests = rig.Peer.Requests.Count;
        Assert.True(await rig.Sync.RetireRestoredMovieAsync(SyncRig.PeerKey, SyncRig.MappingKey, Guid.Parse(entry.PeerItemId), entry.ProviderIds, () => true, default));
        Assert.Equal(requests, rig.Peer.Requests.Count);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(other));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(physical));
        Assert.Equal("keep", File.ReadAllText(unknown));
        Assert.Equal(path, Assert.Single(capture.Paths));
        Assert.Equal(EntryState.DuplicateLocal, rig.Entries.Single(e => e.StubKey == entry.StubKey).State);
        Assert.Null(rig.Entries.Single(e => e.StubKey == entry.StubKey).StubRelPath);
        Assert.Empty(groups.All());
        Assert.Contains(rig.LibraryFolder, rig.Refresh.LibraryScans);
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("massGuard")]
    [InlineData("playback")]
    [InlineData("latePlayback")]
    [InlineData("canceled")]
    [InlineData("unknownStub")]
    [InlineData("changedSentinel")]
    [InlineData("noLocal")]
    [InlineData("newGroupMember")]
    public async Task GuardOrChangedAuthorityPreservesStubWithoutRemoteRequests(string condition)
    {
        await using var rig = await SyncRig.CreateAsync(p => p.Add("Film", 2000, tmdb: 1));
        await rig.SyncAsync();
        var entry = Assert.Single(rig.Entries);
        var path = Path.Combine(rig.LibraryFolder, entry.StubRelPath!);
        rig.Local.Movies.Add(new(new(1, null, null), "Film", 2000, 3));
        if (condition == "retention") rig.Sync.KeepMyPlace = new KeepMyPlaceRetentionTests.Capture();
        if (condition == "massGuard") rig.Settings = rig.Settings with { MassDeletionMinCount = 0, MassDeletionPercent = 0 };
        if (condition == "unknownStub") File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "unknown.strm"), "unrelated");
        if (condition == "changedSentinel") File.WriteAllText(path, "http://unrelated.invalid/movie");
        if (condition == "noLocal") rig.Local.Movies.Clear();
        if (condition == "newGroupMember")
        {
            var groups = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
            rig.Sync.Groups = groups;
            groups.Update(d => { d.Groups.Add(new TitleGroup { GroupKey = "aaaaaaaaaaaaaaaa", MetaOwner = SyncRig.PeerKey, Kind = GroupKind.Movie, Members = [new() { PeerKey = SyncRig.PeerKey, PeerItemId = entry.PeerItemId }, new() { PeerKey = "other", PeerItemId = Guid.NewGuid().ToString("N") }], Copies = [new() { StubKey = entry.StubKey }] }); return true; });
        }

        var requests = rig.Peer.Requests.Count;
        var checks = 0;
        Assert.False(await rig.Sync.RetireRestoredMovieAsync(SyncRig.PeerKey, SyncRig.MappingKey, Guid.Parse(entry.PeerItemId), entry.ProviderIds, () => condition != "playback" && (condition != "latePlayback" || ++checks == 1), new CancellationToken(condition == "canceled")));
        Assert.Equal(requests, rig.Peer.Requests.Count);
        Assert.True(File.Exists(path));
        Assert.NotNull(Assert.Single(rig.Entries).StubRelPath);
        if (condition is "retention" or "massGuard") Assert.Equal(EntryState.HeldRemoval, Assert.Single(rig.Entries).State);
    }
}
