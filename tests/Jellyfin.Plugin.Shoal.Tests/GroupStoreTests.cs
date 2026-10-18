using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K42: <c>&lt;StubRoot&gt;/groups.json</c> (atomic, validated on read, sharded past the threshold) and the copy-on-write <see cref="GroupBook"/>.
/// </summary>
public sealed class GroupStoreTests : IDisposable
{
    private const string PeerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PeerB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PeerC = "cccccccccccccccccccccccccccccccc";
    private const string PeerD = "dddddddddddddddddddddddddddddddd";
    private const string PeerE = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private readonly TempDirectory _dir = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _dir.Dispose();

    internal static TitleGroup Group(string key, string stubKey, params string[] peers)
    {
        var group = new TitleGroup { GroupKey = key, Kind = GroupKind.Movie, MetaOwner = peers[0] };
        var n = 0;
        foreach (var peer in peers)
        {
            n++;
            group.Members.Add(new GroupMember { PeerKey = peer, PeerItemId = new string((char)('0' + n), 32), Sources = [new string((char)('0' + n), 32)], LibKey = "0123456789abcdef" });
        }

        group.Copies.Add(new GroupCopy { LibKey = "0123456789abcdef", StubKey = stubKey, FolderRel = "The Matrix (1999)" });
        return group;
    }

    [Fact]
    public void NoFile_ReadsAsEmpty_AndNoStubRoot_WritesNothing()
    {
        Directory.CreateDirectory(_dir.Combine("stubs"));
        Assert.Empty(new GroupStore(() => _dir.Combine("stubs"), _log).Load().Groups);

        var none = new GroupStore(() => null, _log);
        none.Save(new GroupsDocument { Groups = [Group("g000000000000001", "s000000000000001", PeerA, PeerB)] });
        Assert.Empty(none.Load().Groups);
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "groups*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARoundTrip_KeepsEveryField()
    {
        var root = _dir.Combine("stubs");
        Directory.CreateDirectory(root);
        var store = new GroupStore(() => root, _log);
        var group = Group("g000000000000001", "s000000000000001", PeerA, PeerB);
        group.Copies[0].AltTags = ["shoal-alt-0123abcd"];
        group.RankCache = new GroupRankCache { Order = [PeerB, PeerA], ComputedUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc) };
        store.Save(new GroupsDocument { Groups = [group] });

        Assert.True(File.Exists(Path.Combine(root, GroupStore.FileName)));
        var back = Assert.Single(store.Load().Groups);
        Assert.Equal("g000000000000001", back.GroupKey);
        Assert.Equal(PeerA, back.MetaOwner);
        Assert.Equal([PeerA, PeerB], back.Members.Select(static m => m.PeerKey));
        Assert.Equal("s000000000000001", back.Copies[0].StubKey);
        Assert.Equal("The Matrix (1999)", back.Copies[0].FolderRel);
        Assert.Equal(["shoal-alt-0123abcd"], back.Copies[0].AltTags);
        Assert.Equal([PeerB, PeerA], back.RankCache!.Order);
    }

    [Fact]
    public void ADamagedFile_ReadsAsEmpty_WithAWarning()
    {
        var root = _dir.Combine("stubs");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, GroupStore.FileName), "{ not json");

        Assert.Empty(new GroupStore(() => root, _log).Load().Groups);
        Assert.Contains(_log.Lines, static l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_DropsBadGroups_CapsMembersAtFour_AndRefusesAStubKeyInTwoGroups()
    {
        var root = _dir.Combine("stubs");
        Directory.CreateDirectory(root);
        var store = new GroupStore(() => root, _log);
        var five = Group("g000000000000001", "s000000000000001", PeerA, PeerB, PeerC, PeerD, PeerE);
        var badKey = Group("BAD", "s000000000000002", PeerA, PeerB);
        var noOwner = Group("g000000000000003", "s000000000000003", PeerA, PeerB);
        noOwner.MetaOwner = PeerC;
        var single = Group("g000000000000004", "s000000000000004", PeerA);
        var sameCopy = Group("g000000000000005", "s000000000000001", PeerC, PeerD);
        var badFolder = Group("g000000000000006", "s000000000000006", PeerA, PeerB);
        badFolder.Copies[0].FolderRel = "../escape";
        store.Save(new GroupsDocument { Groups = [five, badKey, noOwner, single, sameCopy, badFolder] });

        var loaded = store.Load().Groups;

        var kept = Assert.Single(loaded);
        Assert.Equal("g000000000000001", kept.GroupKey);
        Assert.Equal(GroupsDocument.MaxMembers, kept.Members.Count);
        Assert.Contains(_log.Lines, static l => l.Contains("dropped 5 groups", StringComparison.Ordinal));
    }

    [Fact]
    public void PastTheThreshold_TheFileIsSharded_ReadsBackWhole_AndShrinkingRemovesTheStaleShards()
    {
        var root = _dir.Combine("stubs");
        Directory.CreateDirectory(root);
        var store = new GroupStore(() => root, _log) { ShardThreshold = 2_000 };
        var groups = Enumerable.Range(0, 60).Select(static i => Group("g" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), "s" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), PeerA, PeerB)).ToList();
        store.Save(new GroupsDocument { Groups = groups });

        Assert.True(Directory.EnumerateFiles(root, "groups.*.json").Count() >= 2);
        Assert.Equal(groups.Select(static g => g.GroupKey).Order(), store.Load().Groups.Select(static g => g.GroupKey).Order());

        store.Save(new GroupsDocument { Groups = groups.Take(1).ToList() });

        Assert.Empty(Directory.EnumerateFiles(root, "groups.*.json"));
        Assert.Single(store.Load().Groups);
    }

    [Fact]
    public void AMissingShard_ReadsAsEmpty_NeverAsHalfTheGroups()
    {
        var root = _dir.Combine("stubs");
        Directory.CreateDirectory(root);
        var store = new GroupStore(() => root, _log) { ShardThreshold = 2_000 };
        store.Save(new GroupsDocument { Groups = Enumerable.Range(0, 60).Select(static i => Group("g" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), "s" + i.ToString("D15", System.Globalization.CultureInfo.InvariantCulture), PeerA, PeerB)).ToList() });
        File.Delete(Directory.EnumerateFiles(root, "groups.*.json").First());

        Assert.Empty(store.Load().Groups);
    }

    [Fact]
    public void TheBook_FindsAGroupByItsCopy_AndAChangeThatSaysNoWritesNothing()
    {
        var store = new MemoryGroupStore();
        var book = new GroupBook(store, _log);
        Assert.Null(book.ByCopy("s000000000000001"));

        Assert.True(book.Update(d =>
        {
            d.Groups.Add(Group("g000000000000001", "s000000000000001", PeerA, PeerB));
            return true;
        }));
        Assert.Equal(1, store.Saves);
        Assert.Equal("g000000000000001", book.ByCopy("s000000000000001")!.GroupKey);

        Assert.False(book.Update(static _ => false));
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void TheBook_ChangesACopy_SoAReaderNeverSeesAHalfChange()
    {
        var store = new MemoryGroupStore();
        var book = new GroupBook(store, _log);
        book.Update(d =>
        {
            d.Groups.Add(Group("g000000000000001", "s000000000000001", PeerA, PeerB));
            return true;
        });
        var before = book.ByCopy("s000000000000001")!;

        book.Update(d =>
        {
            d.Groups[0].Members.RemoveAt(1);
            return true;
        });

        Assert.Equal(2, before.Members.Count); // the old snapshot is untouched
        Assert.Single(book.ByCopy("s000000000000001")!.Members);
    }
}

/// <summary>
/// An <see cref="IGroupStore"/> in memory.
/// </summary>
internal sealed class MemoryGroupStore : IGroupStore
{
    private byte[] _saved = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new GroupsDocument());

    public int Saves { get; private set; }

    public GroupsDocument Load() => System.Text.Json.JsonSerializer.Deserialize<GroupsDocument>(_saved)!;

    public void Save(GroupsDocument document)
    {
        _saved = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document);
        Saves++;
    }
}
