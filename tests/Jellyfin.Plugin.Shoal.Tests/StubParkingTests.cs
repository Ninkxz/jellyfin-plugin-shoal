using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K47: parking by stub. A parked server's own stubs carry its park tag and are hidden through BlockedTags (also in a shared library, which is never
/// parked as a whole); a title group's copy carries the group's park tag and hides only when every server of the group is parked.
/// </summary>
public sealed class StubParkingTests
{
    [Fact]
    public void TheTags_AreShoalTags_AndDistinguishAServerFromAGroup()
    {
        var peer = LibraryFolders.ParkPeerTag("4b0c1d2e3f405162738495a6b7c8d9e0");
        var group = LibraryFolders.ParkGroupTag("grp0000000000001");

        Assert.Equal("shoal-park-p-4b0c1d2e", peer);
        Assert.Equal("shoal-park-g-grp00000", group);
        Assert.True(LibraryFolders.IsShoalTag(peer));
        Assert.True(LibraryFolders.IsShoalTag(group));
    }

    [Fact]
    public void AnItem_KeepsExactlyOneParkTag()
    {
        var tags = StubEnricher.WantedTags(["Shoal", "Shoal: Hops", "shoal-park-p-aaaaaaaa", "Kids"], "Hops", null, null, "shoal-park-g-grp00000");

        Assert.Contains("shoal-park-g-grp00000", tags);
        Assert.DoesNotContain("shoal-park-p-aaaaaaaa", tags);
        Assert.Contains("Kids", tags);
        var same = StubEnricher.WantedTags(tags, "Hops", null, null, "shoal-park-g-grp00000");
        Assert.Same(tags, same); // no change, no save
    }

    [Fact]
    public void AGroupHides_OnlyWhenEveryServerIsParked()
    {
        var book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        book.Update(d =>
        {
            d.Groups.Add(new TitleGroup { GroupKey = "grp0000000000001", MetaOwner = "a", Members = [new GroupMember { PeerKey = "a" }, new GroupMember { PeerKey = "b" }] });
            return true;
        });

        Assert.Empty(book.ParkedGroupTags(new HashSet<string> { "a" })); // the owner is parked, the member still plays it
        Assert.Equal(["shoal-park-g-grp00000"], book.ParkedGroupTags(new HashSet<string> { "a", "b" }));
    }

    [Fact]
    public void TheTagService_BlocksAParkedServersTag_AndTheParkedGroups_ForEveryAccountButSharingOnes()
    {
        var state = new ShoalState();
        state.Home.Visibility.Parked["4b0c1d2e3f405162738495a6b7c8d9e0"] = new ParkedPeer();
        var accounts = new FakeAccounts();
        var alice = accounts.Add("alice");
        var share = accounts.Add("shoal-bob");

        var desired = TagService.Desired(state, alice, new HashSet<Guid> { share.Id }, new HashSet<string>(), new Dictionary<string, Guid>(), ["shoal-park-g-grp00000"]);

        Assert.Contains("shoal-park-p-4b0c1d2e", desired);
        Assert.Contains("shoal-park-g-grp00000", desired);
        Assert.Empty(TagService.Desired(state, share, new HashSet<Guid> { share.Id }, new HashSet<string>(), new Dictionary<string, Guid>(), ["shoal-park-g-grp00000"]));
    }

    [Fact]
    public async Task AServerThatFeedsOnlyASharedLibrary_IsParkedByItsStubs_AndComesBack()
    {
        using var rig = new LayoutRig(LayoutPreset.AllMixed);
        Assert.True((await rig.Libs.CreateAsync(LayoutRig.PeerA, "movies-1", rig.Admin.Id)).Ok); // joins "Friends' movies"
        var tag = LibraryFolders.ParkPeerTag(LayoutRig.PeerA);

        await rig.Visibility.ParkPeerAsync(LayoutRig.PeerA);

        Assert.True(rig.State.Current.Home.Visibility.Parked.ContainsKey(LayoutRig.PeerA));
        Assert.Contains(tag, rig.TagsOf(rig.Alice));
        Assert.Contains(tag, rig.TagsOf(rig.Admin));

        await rig.Visibility.UnparkPeerAsync(LayoutRig.PeerA);

        Assert.False(rig.State.Current.Home.Visibility.Parked.ContainsKey(LayoutRig.PeerA));
        Assert.DoesNotContain(tag, rig.TagsOf(rig.Alice));
    }
}
