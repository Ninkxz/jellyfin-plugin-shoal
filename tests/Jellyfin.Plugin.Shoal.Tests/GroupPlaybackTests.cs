using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K42: the server of a grouped title is picked per play: by the connection order, skipping servers that are down, stopped or paused, and never
/// through a library the caller cannot open. A title on one server has no candidates (the old path).
/// </summary>
public sealed class GroupPlaybackTests : IAsyncLifetime
{
    private const string Owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string M1 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string M2 = "cccccccccccccccccccccccccccccccc";
    private const string OwnerStub = "ownerstub0000001";

    private static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Stranger = Guid.Parse("99999999-2222-3333-4444-555555555555");

    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeRanking _ranking = new();
    private StateStore _state = null!;
    private CatalogStore _catalogs = null!;
    private GroupBook _book = null!;
    private PeerRegistry _registry = null!;
    private GroupPlayback _playback = null!;
    private CatalogEntry _stub = null!;

    public async Task InitializeAsync()
    {
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, _time, null);
        Directory.CreateDirectory(_dir.Combine("stubs"));
        await _state.UpdateAsync(s =>
        {
            s.StubRoot = _dir.Combine("stubs");
            foreach (var (key, priority) in new[] { (Owner, 100), (M1, 110), (M2, 120) })
            {
                s.Home.Peers.Add(new PeerState
                {
                    PeerKey = key,
                    DisplayName = "P" + priority,
                    BaseUrl = "http://p" + priority + ".tail1234.ts.net:8096",
                    Priority = priority,
                    Libraries = { new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = "P" + priority, Subfolder = "movies", Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [User] } } },
                });
            }
        });
        _catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance);
        _stub = Entry(Owner, '1', EntryState.Active, OwnerStub);
        Save(Owner, _stub);
        Save(M1, Entry(M1, '2', EntryState.DuplicatePeer, "dupstub000000002"));
        Save(M2, Entry(M2, '3', EntryState.DuplicatePeer, "dupstub000000003"));
        _book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        _book.Update(d =>
        {
            d.Groups.Add(new TitleGroup
            {
                GroupKey = "grp0000000000001",
                MetaOwner = Owner,
                Members =
                [
                    new GroupMember { PeerKey = Owner, PeerItemId = new string('1', 32), Sources = [new string('1', 32)] },
                    new GroupMember { PeerKey = M1, PeerItemId = new string('2', 32), Sources = [new string('2', 32), new string('7', 32)] },
                    new GroupMember { PeerKey = M2, PeerItemId = new string('3', 32), Sources = [new string('3', 32)] },
                ],
                Copies = [new GroupCopy { StubKey = OwnerStub, FolderRel = "The Matrix (1999)" }],
            });
            return true;
        });
        _registry = new PeerRegistry(_time);
        foreach (var key in new[] { Owner, M1, M2 })
        {
            _registry.SetStatus(key, PeerStatus.Online);
        }

        _ranking.Keys = [Owner, M1, M2];
        _playback = new GroupPlayback(_book, new StubCatalog(_catalogs), _state, _registry, _ranking);
    }

    public Task DisposeAsync()
    {
        _state.Dispose();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    private static CallerUser Caller(Guid id) => new(id, false, false, true, true, true, false, null);

    private static CatalogEntry Entry(string peer, char id, EntryState state, string stubKey) => new()
    {
        StubKey = stubKey,
        PeerItemId = new string(id, 32),
        PeerMediaSourceId = new string(id, 32),
        MappingKey = "movies-1",
        Title = "The Matrix",
        Year = 1999,
        State = state,
        DuplicateOf = state == EntryState.DuplicatePeer ? OwnerStub : null,
        StubRelPath = state == EntryState.Active ? "The Matrix (1999)/The Matrix (1999).strm" : null,
        Width = 1920,
        Height = 1080,
        Bitrate = 8_000_000,
        RuntimeTicks = 72_000_000_000,
    };

    private void Save(string peer, CatalogEntry entry)
    {
        var doc = new CatalogDocument { PeerKey = peer, CompletedOnce = true };
        doc.Entries.Add(entry);
        _catalogs.Save(doc);
    }

    [Fact]
    public void TheCandidates_FollowTheConnectionOrder_OwnerIncluded()
    {
        _ranking.Keys = [M2, Owner, M1];

        var candidates = _playback.Candidates(Owner, _stub, Caller(User))!;

        Assert.Equal([M2, Owner, M1], candidates.Select(static c => c.PeerKey));
        Assert.True(candidates[1].IsOwner);
        Assert.Equal(Guid.ParseExact(new string('3', 32), "N"), candidates[0].PeerItemId);
        Assert.Equal(EntryState.DuplicatePeer, candidates[0].Entry.State);
    }

    [Fact]
    public void ATitleWithoutAGroup_HasNoCandidates()
    {
        var single = Entry(Owner, '5', EntryState.Active, "singlestub000001");

        Assert.Null(_playback.Candidates(Owner, single, Caller(User)));
        Assert.Empty(_playback.PeerIds(single));
    }

    [Fact]
    public async Task ServersThatAreDownStoppedOrPaused_AreSkippedAtOnce()
    {
        _registry.SetStatus(Owner, PeerStatus.Offline);
        _registry.SetStatus(M1, PeerStatus.AuthFailed);

        Assert.Equal([M2], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey));

        await _state.UpdateAsync(static s => s.Home.Peers.Single(static p => p.PeerKey == M2).Enabled = false);
        Assert.Empty(_playback.Candidates(Owner, _stub, Caller(User))!); // nothing up: the play goes to the stub's own server as before
    }

    [Fact]
    public async Task FailoverNeverCrossesALibraryTheCallerCannotOpen()
    {
        await _state.UpdateAsync(static s => s.Home.Peers.Single(static p => p.PeerKey == M1).Libraries[0].Access.SelectedUserIds = [Stranger]);

        Assert.Equal([Owner, M2], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey));
    }

    [Fact]
    public async Task AShareUser_NeverFallsOverToAnotherServer()
    {
        await _state.UpdateAsync(static s =>
        {
            foreach (var peer in s.Home.Peers)
            {
                peer.Libraries[0].Access.Mode = AccessMode.Everyone;
            }

            s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = Stranger });
        });

        Assert.Equal([Owner], _playback.Candidates(Owner, _stub, Caller(Stranger))!.Select(static c => c.PeerKey));
    }

    [Fact]
    public void AMemberWhoseCatalogNoLongerHoldsTheHiddenCopy_IsSkipped()
    {
        var promoted = Entry(M1, '2', EntryState.Active, "dupstub000000002");
        Save(M1, promoted);

        Assert.Equal([Owner, M2], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey));
    }

    [Fact]
    public async Task ACopyThatFitsTheQualityLimit_ComesFirstWithinItsTier_NeverAcrossTiers()
    {
        await _state.UpdateAsync(static s =>
        {
            s.Home.Peers.Single(static p => p.PeerKey == Owner).PriorityTier = PriorityTier.Prefer;
        });
        _ranking.Keys = [Owner, M1, M2];
        _ranking.T[M2] = 50; // M2 has T >= 1.5 x 8 Mbps; M1 and the owner were never measured

        Assert.Equal([Owner, M2, M1], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey));
    }

    [Fact]
    public async Task K52_ACopyInsideItsConnectionsLimit_GoesBeforeOneThatWouldBeConverted_EvenWithNoSpeedTest_ButNeverJumpsATier()
    {
        var uhd = Entry(M1, '2', EntryState.DuplicatePeer, "dupstub000000002");
        uhd.Width = 3840;
        uhd.Height = 2160;
        uhd.Bitrate = 40_000_000;
        Save(M1, uhd);
        await _state.UpdateAsync(static s =>
        {
            foreach (var peer in s.Home.Peers)
            {
                peer.QualityLimit = new QualityLimit { MaxClass = QualityClass.Hd1080 };
            }
        });
        _ranking.Keys = [M1, Owner, M2];

        Assert.Equal([Owner, M2, M1], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey));

        await _state.UpdateAsync(static s =>
        {
            s.Home.Peers.Single(static p => p.PeerKey == Owner).PriorityTier = PriorityTier.Backup;
            s.Home.Peers.Single(static p => p.PeerKey == M2).PriorityTier = PriorityTier.Backup;
        });

        Assert.Equal([M1, Owner, M2], _playback.Candidates(Owner, _stub, Caller(User))!.Select(static c => c.PeerKey)); // the 4K copy is converted, not skipped for a Backup
    }

    [Fact]
    public async Task W10L2_Admits_UsesTheSameLibraryRuleAsThePick_OwnerExempt_DownDoesNotMatter()
    {
        var member = _playback.Member(Owner, _stub, M1, Guid.ParseExact(new string('2', 32), "N"))!;
        var owner = _playback.Member(Owner, _stub, Owner, Guid.ParseExact(new string('1', 32), "N"))!;
        _registry.SetStatus(M1, PeerStatus.Offline);

        Assert.True(_playback.Admits(Owner, _stub, member, Caller(User), null));

        await _state.UpdateAsync(static s => s.Home.Peers.Single(static p => p.PeerKey == M1).Libraries[0].Access.SelectedUserIds = [Stranger]);

        Assert.False(_playback.Admits(Owner, _stub, member, Caller(User), null));
        Assert.True(_playback.Admits(Owner, _stub, owner, Caller(User), null)); // the owner's own stub was checked with the item itself
    }

    [Fact]
    public void TheGrantsServer_IsFoundAgain_ForTheByteRoutes()
    {
        var member = _playback.Member(Owner, _stub, M1, Guid.ParseExact(new string('2', 32), "N"));

        Assert.NotNull(member);
        Assert.Equal(M1, member!.PeerKey);
        Assert.Null(_playback.Member(Owner, _stub, M1, Guid.ParseExact(new string('9', 32), "N")));
        Assert.Null(_playback.Member(Owner, _stub, "dddddddddddddddddddddddddddddddd", Guid.ParseExact(new string('2', 32), "N")));
    }

    [Fact]
    public void ThePeerIds_CoverEveryMembersItemAndSources()
    {
        var ids = _playback.PeerIds(_stub);

        Assert.Contains(Guid.ParseExact(new string('7', 32), "N"), ids);
        Assert.Contains(Guid.ParseExact(new string('3', 32), "N"), ids);
        Assert.Equal(4, ids.Count);
    }

    private sealed class FakeRanking : IConnectionRanking
    {
        public IReadOnlyList<string> Keys { get; set; } = [];

        public Dictionary<string, double?> T { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Order() => Keys;

        public double? Throughput(string peerKey) => T.GetValueOrDefault(peerKey);
    }
}
