using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K42: how one server's sync changes the global title groups (pure).
/// </summary>
public sealed class GroupPlannerTests
{
    private const string Owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Member = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Third = "cccccccccccccccccccccccccccccccc";
    private const string Lib = "0123456789abcdef";
    private const string OwnerStub = "ownerstub0000001";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { Owner, Member, Third, "dddddddddddddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };

    private static int _keys;

    private static string NewKey() => "group" + (++_keys).ToString("D11", System.Globalization.CultureInfo.InvariantCulture);

    private static CatalogEntry OwnerEntry() => new()
    {
        StubKey = OwnerStub,
        PeerItemId = new string('1', 32),
        PeerMediaSourceId = new string('1', 32),
        MappingKey = "movies-1",
        State = EntryState.Active,
        StubRelPath = "The Matrix (1999)/The Matrix (1999).strm",
        Sources = [new CatalogSource { StubKey = OwnerStub, PeerMediaSourceId = new string('1', 32) }],
    };

    private static CatalogEntry Duplicate(char id, string of = OwnerStub) => new()
    {
        StubKey = "dup" + new string(id, 13),
        PeerItemId = new string(id, 32),
        PeerMediaSourceId = new string(id, 32),
        MappingKey = "movies-1",
        State = EntryState.DuplicatePeer,
        DuplicateOf = of,
    };

    private static GroupPeerInput Input(string peer, IReadOnlyDictionary<string, IReadOnlyList<string>>? selected = null, params CatalogEntry[] movies) =>
        new(peer, movies, static _ => Lib, selected ?? new Dictionary<string, IReadOnlyList<string>>());

    private static OwnedCopy? OwnerOf(string stubKey) => stubKey == OwnerStub ? new OwnedCopy(Owner, OwnerEntry(), Lib) : null;

    [Fact]
    public void ADuplicateOfAnotherServersStub_StartsAGroup_WithTheStubAsItsCopy_AndTheOwnerFirst()
    {
        var doc = new GroupsDocument();

        Assert.True(GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey));

        var group = Assert.Single(doc.Groups);
        Assert.Equal(GroupKind.Movie, group.Kind);
        Assert.Equal(Owner, group.MetaOwner);
        Assert.Equal([Owner, Member], group.Members.Select(static m => m.PeerKey));
        Assert.Equal(new string('2', 32), group.Members[1].PeerItemId);
        var copy = Assert.Single(group.Copies);
        Assert.Equal(OwnerStub, copy.StubKey); // the sentinel names the group copy, which is the existing stub: nothing on disk changes
        Assert.Equal("The Matrix (1999)", copy.FolderRel);
        Assert.Equal(Lib, copy.LibKey);
        Assert.True(GroupStore.Validate(group));
        Assert.True(GroupPlanner.HidesExtras(group));
    }

    [Fact]
    public void ATitleOnOneServer_GetsNoGroup()
    {
        var doc = new GroupsDocument();

        Assert.False(GroupPlanner.ApplyPeer(doc, Input(Owner, null, OwnerEntry()), OwnerOf, Known, NewKey));
        Assert.Empty(doc.Groups);
        Assert.False(GroupPlanner.HidesExtras(null));
    }

    [Fact]
    public void TheSourcesSeenThisRun_AreTheMembersHiddenSources()
    {
        var doc = new GroupsDocument();
        var sources = new Dictionary<string, IReadOnlyList<string>> { [new string('2', 32)] = [new string('2', 32), new string('3', 32)] };

        GroupPlanner.ApplyPeer(doc, Input(Member, sources, Duplicate('2')), OwnerOf, Known, NewKey);

        Assert.Equal([new string('2', 32), new string('3', 32)], doc.Groups[0].Members[1].Sources);
    }

    [Fact]
    public void TheOwnersSync_KeepsItOwner_AndRefreshesItsMember()
    {
        var doc = new GroupsDocument();
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey);
        var owner = OwnerEntry();
        owner.PeerItemId = new string('9', 32); // the peer moved the file: same stub, new id

        Assert.True(GroupPlanner.ApplyPeer(doc, Input(Owner, null, owner), OwnerOf, Known, NewKey));

        Assert.Equal(Owner, doc.Groups[0].MetaOwner);
        Assert.Equal(new string('9', 32), doc.Groups[0].Members[0].PeerItemId);
        Assert.False(GroupPlanner.ApplyPeer(doc, Input(Owner, null, owner), OwnerOf, Known, NewKey)); // nothing new: no write
    }

    [Fact]
    public void AtMostFourServers_JoinAGroup()
    {
        var doc = new GroupsDocument();
        var peers = new[] { Member, Third, "dddddddddddddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };
        var n = '2';
        foreach (var peer in peers)
        {
            GroupPlanner.ApplyPeer(doc, Input(peer, null, Duplicate(n++)), OwnerOf, Known, NewKey);
        }

        var group = Assert.Single(doc.Groups);
        Assert.Equal(GroupsDocument.MaxMembers, group.Members.Count);
        Assert.DoesNotContain(group.Members, static m => m.PeerKey == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
    }

    [Fact]
    public void AMemberThatNoLongerHasTheTitle_Leaves_AndAGroupOfOneIsDropped()
    {
        var doc = new GroupsDocument();
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey);

        Assert.True(GroupPlanner.ApplyPeer(doc, Input(Member), OwnerOf, Known, NewKey));

        Assert.Empty(doc.Groups); // back to one server: the title behaves exactly as before
    }

    [Fact]
    public void WhenTheOwnersStubGoes_TheCopyGoes_AndTheGroupWithIt()
    {
        var doc = new GroupsDocument();
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey);
        GroupPlanner.ApplyPeer(doc, Input(Third, null, Duplicate('3')), OwnerOf, Known, NewKey);

        Assert.True(GroupPlanner.ApplyPeer(doc, Input(Owner), OwnerOf, Known, NewKey));

        Assert.Empty(doc.Groups);
    }

    [Fact]
    public void AServerThatIsNoLongerPaired_LeavesEveryGroup_AndAnOwnerThatIsGoneEndsIt()
    {
        var doc = new GroupsDocument();
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey);
        GroupPlanner.ApplyPeer(doc, Input(Third, null, Duplicate('3')), OwnerOf, Known, NewKey);
        var withoutThird = new HashSet<string>(Known, StringComparer.Ordinal);
        withoutThird.Remove(Third);

        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, withoutThird, NewKey);
        Assert.Equal([Owner, Member], doc.Groups[0].Members.Select(static m => m.PeerKey));

        var withoutOwner = new HashSet<string>(withoutThird, StringComparer.Ordinal);
        withoutOwner.Remove(Owner);
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, withoutOwner, NewKey);
        Assert.Empty(doc.Groups);
    }

    [Fact]
    public void ADuplicateThatPointsAtAnotherStub_MovesToThatStubsGroup()
    {
        var doc = new GroupsDocument();
        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2')), OwnerOf, Known, NewKey);
        GroupPlanner.ApplyPeer(doc, Input(Third, null, Duplicate('3')), OwnerOf, Known, NewKey);
        OwnedCopy? Other(string key) => key == "otherstub0000001" ? new OwnedCopy(Third, new CatalogEntry { StubKey = key, PeerItemId = new string('3', 32), PeerMediaSourceId = new string('3', 32), MappingKey = "movies-1", State = EntryState.Active, StubRelPath = "X (2000)/X (2000).strm" }, Lib) : OwnerOf(key);

        GroupPlanner.ApplyPeer(doc, Input(Member, null, Duplicate('2', "otherstub0000001")), Other, Known, NewKey);

        Assert.Equal(2, doc.Groups.Count);
        Assert.DoesNotContain(doc.Groups.Single(static g => g.MetaOwner == Owner).Members, static m => m.PeerKey == Member);
        Assert.Contains(doc.Groups.Single(static g => g.MetaOwner == Third).Members, static m => m.PeerKey == Member);
    }

    [Fact]
    public void ADuplicateOfHomesOwnCopy_StartsNoGroup()
    {
        var doc = new GroupsDocument();
        var local = Duplicate('2');
        local.State = EntryState.DuplicateLocal;

        Assert.False(GroupPlanner.ApplyPeer(doc, Input(Member, null, local), OwnerOf, Known, NewKey));
        Assert.Empty(doc.Groups);
    }
}
