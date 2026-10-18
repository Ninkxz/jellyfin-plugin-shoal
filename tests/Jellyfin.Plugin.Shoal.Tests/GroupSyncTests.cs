using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K42 in the movie sync: groups form from <c>duplicate-peer</c> entries, the owner keeps its stub until it leaves (whatever the rank or status of the
/// others), and a group of two or more servers hides the owner's extra versions. Without a <see cref="GroupBook"/> the sync is v1 (the existing tests).
/// </summary>
public sealed class GroupSyncTests
{
    private const string Other = "ffffffffffffffffffffffffffffffff";
    private const string OtherStub = "bbbbbbbbbbbbbbbb";
    private const string OtherItem = "11111111111111111111111111111111";
    private static readonly Guid A = Guid.Parse("a0000000000000000000000000000001");
    private static readonly Guid B = Guid.Parse("b0000000000000000000000000000002");

    private static async Task<(SyncRig Rig, GroupBook Book)> RigAsync(Action<FakeLibraryPeer>? seed = null)
    {
        var rig = await SyncRig.CreateAsync(seed);
        Directory.CreateDirectory(rig.StubRoot);
        var book = new GroupBook(new GroupStore(() => rig.StubRoot, rig.Log), rig.Log);
        rig.Sync.Groups = book;
        return (rig, book);
    }

    private static async Task AddOtherWithTheMatrixAsync(SyncRig rig, int priority)
    {
        await rig.AddPeerAsync(Other, "Other", priority, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = new CatalogDocument { PeerKey = Other, CompletedOnce = true };
        catalog.Entries.Add(new CatalogEntry
        {
            StubKey = OtherStub,
            PeerItemId = OtherItem,
            PeerMediaSourceId = OtherItem,
            MappingKey = "movies-1",
            Title = "The Matrix",
            Year = 1999,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" },
            State = EntryState.Active,
            StubRelPath = "The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm",
        });
        rig.Catalogs.Save(catalog);
    }

    private static TitleGroup GroupOf(string owner, string ownerItem, string stubKey, string member, string memberItem) => new()
    {
        GroupKey = "grp0000000000001",
        Kind = GroupKind.Movie,
        MetaOwner = owner,
        Members =
        [
            new GroupMember { PeerKey = owner, PeerItemId = ownerItem, Sources = [ownerItem] },
            new GroupMember { PeerKey = member, PeerItemId = memberItem, Sources = [memberItem] },
        ],
        Copies = [new GroupCopy { StubKey = stubKey, FolderRel = "The Matrix (1999) [tmdbid-603]" }],
    };

    private static IEnumerable<string> Strms(SyncRig rig) => rig.AllStubFiles().Where(static f => f.EndsWith(".strm", StringComparison.Ordinal) && f.Contains(SyncRig.PeerKey, StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    [Fact]
    public async Task ADuplicateOfABetterServersStub_FormsAGroup_InGroupsJson()
    {
        var (rig, book) = await RigAsync(static p => p.Add("The Matrix", 1999, tmdb: 603));
        await using var _ = rig;
        await AddOtherWithTheMatrixAsync(rig, priority: 50);

        await rig.SyncAsync();

        var entry = Assert.Single(rig.Entries);
        Assert.Equal(EntryState.DuplicatePeer, entry.State);
        var group = Assert.Single(book.All());
        Assert.Equal(Other, group.MetaOwner);
        Assert.Equal(OtherStub, group.Copies.Single().StubKey);
        Assert.Equal([Other, SyncRig.PeerKey], group.Members.Select(static m => m.PeerKey));
        Assert.Equal(entry.PeerItemId, group.Members[1].PeerItemId);
        Assert.True(File.Exists(Path.Combine(rig.StubRoot, GroupStore.FileName)));
        Assert.Single(new GroupStore(() => rig.StubRoot, rig.Log).Load().Groups);
    }

    [Fact]
    public async Task AnOfflineOwner_KeepsItsStub_SoNothingIsPromotedAndNoItemIdChanges()
    {
        var (rig, book) = await RigAsync(static p => p.Add("The Matrix", 1999, tmdb: 603));
        await using var _ = rig;
        await AddOtherWithTheMatrixAsync(rig, priority: 50);
        await rig.SyncAsync();
        rig.DownPeers.Add(Other); // without a group the v1 rule promotes this server's copy (CatalogSyncTests)

        var report = await rig.SyncAsync();

        Assert.Equal(0, report.Added);
        Assert.Equal(EntryState.DuplicatePeer, rig.Entries.Single().State);
        Assert.Empty(Strms(rig));
        Assert.Single(book.All());
    }

    [Fact]
    public async Task AWorseRankedOwner_KeepsTheTitle_AndABetterServerJoinsAsAHiddenMember()
    {
        var (rig, book) = await RigAsync(static p => p.Add("The Matrix", 1999, tmdb: 603));
        await using var _ = rig;
        await AddOtherWithTheMatrixAsync(rig, priority: 200);
        book.Update(d =>
        {
            d.Groups.Add(GroupOf(Other, OtherItem, OtherStub, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", new string('2', 32)));
            return true;
        });

        await rig.SyncAsync();

        var entry = Assert.Single(rig.Entries);
        Assert.Equal(EntryState.DuplicatePeer, entry.State); // v1 would give this better-ranked server its own stub
        Assert.Equal(OtherStub, entry.DuplicateOf);
        Assert.Empty(Strms(rig));
        Assert.Contains(book.ByCopy(OtherStub)!.Members, static m => m.PeerKey == SyncRig.PeerKey);
    }

    [Fact]
    public async Task TheOwner_IsNotDemotedByABetterRankedCopy()
    {
        var (rig, book) = await RigAsync(static p => p.Add("The Matrix", 1999, tmdb: 603));
        await using var _ = rig;
        await rig.SyncAsync();
        var own = rig.Entries.Single();
        await AddOtherWithTheMatrixAsync(rig, priority: 50);
        book.Update(d =>
        {
            d.Groups.Add(GroupOf(SyncRig.PeerKey, own.PeerItemId, own.StubKey, Other, OtherItem));
            return true;
        });

        await rig.SyncAsync();

        var entry = rig.Entries.Single();
        Assert.Equal(EntryState.Active, entry.State);
        Assert.Equal(own.StubKey, entry.StubKey);
        Assert.Equal(own.StubRelPath, entry.StubRelPath);
        Assert.Single(Strms(rig));
        Assert.Equal(SyncRig.PeerKey, book.ByCopy(own.StubKey)!.MetaOwner);
    }

    [Fact]
    public async Task WithTwoServers_TheOwnersExtraVersionsAreHidden_AndComeBackWhenItIsOneServerAgain()
    {
        var (rig, book) = await RigAsync(p => p.Add("The Matrix", 1999, tmdb: 603, configure: m => m.Sources = [new PeerSource { Id = A, Height = 1080, Width = 1920 }, new PeerSource { Id = B, Height = 2160, Width = 3840, Size = 9_000_000 }]));
        await using var _ = rig;
        rig.Settings = new SyncSettings(25, 10, 500, 20, 2, Jellyfin.Plugin.Shoal.Configuration.ArtQuality.Standard) { AlternateVersions = true };
        await rig.SyncAsync();
        Assert.Equal(2, Strms(rig).Count());
        var own = rig.Entries.Single();
        await rig.AddPeerAsync(Other, "Other", 200, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        book.Update(d =>
        {
            var group = GroupOf(SyncRig.PeerKey, own.PeerItemId, own.StubKey, Other, OtherItem);
            group.Members[0].Sources = [.. own.Sources.Select(static s => s.PeerMediaSourceId)]; // what a member's sync records from the owner's entry
            d.Groups.Add(group);
            return true;
        });

        await rig.SyncAsync();

        Assert.Single(Strms(rig));
        var hidden = rig.Entries.Single();
        Assert.True(hidden.ExtrasHidden);
        Assert.Single(hidden.Sources);
        Assert.Equal(own.StubRelPath, hidden.StubRelPath); // the primary stub never moves
        var member = book.ByCopy(own.StubKey)!.Members.Single(static m => m.PeerKey == SyncRig.PeerKey);
        Assert.Equal(2, member.Sources.Count); // the extras are hidden sources of the member, not gone

        book.Update(static d =>
        {
            d.Groups.Clear();
            return true;
        });
        await rig.SyncAsync();

        Assert.Equal(2, Strms(rig).Count());
        Assert.False(rig.Entries.Single().ExtrasHidden);
    }

    [Fact]
    public async Task WhenTheOwnerLeaves_TheGroupEnds_AndTheMemberGetsTheTitleAtItsNextSync()
    {
        var (rig, book) = await RigAsync(static p => p.Add("The Matrix", 1999, tmdb: 603));
        await using var _ = rig;
        await AddOtherWithTheMatrixAsync(rig, priority: 50);
        await rig.SyncAsync();
        Assert.Single(book.All());

        await rig.State.UpdateAsync(static s => s.Home.Peers.RemoveAll(static p => p.PeerKey == Other)); // the owner was removed
        var report = await rig.SyncAsync();

        Assert.Equal(1, report.Added);
        Assert.Equal(EntryState.Active, rig.Entries.Single().State);
        Assert.Empty(book.All());
    }
}
