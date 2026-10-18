using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K42 through the real filter and handlers: PlaybackInfo of a grouped title asks the server the pick names, the grant holds that choice, and the
/// static stream that follows stays on it even when the pick would now be another server (a running stream never switches).
/// </summary>
public sealed class GroupPlayTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly Guid MemberItem = Guid.Parse("d0000000000000000000000000000001");
    private static readonly Guid MemberSource = Guid.Parse("d0000000000000000000000000000002");
    private static readonly Guid OtherItem = Guid.Parse("e0000000000000000000000000000001");

    private readonly PlaybackFixture _fixture;
    private readonly FakeGroups _groups = new();

    public GroupPlayTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.State.UpdateAsync(static s => s.Home.Peers[0].MaxConcurrentStreams = 4);
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
        foreach (var grant in P.Grants.ForUser(MatrixWorld.Admin))
        {
            P.Grants.Remove(grant.GrantId);
        }

        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.MaxStreams = 2;
        H.Peer.StreamFileSize = 1_000_000;
        H.Peer.StreamOverride = null;
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = MemberSource.ToString("N"), ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        });
        _groups.Choice = Choice(MemberItem, MemberSource);
        P.GroupOverride = _groups;
    }

    public Task DisposeAsync()
    {
        P.GroupOverride = null;
        return Task.CompletedTask;
    }

    private MemberChoice Choice(Guid item, Guid source) => new(
        H.World.PeerKey,
        new CatalogEntry { StubKey = "memberstub000001", PeerItemId = item.ToString("N"), PeerMediaSourceId = source.ToString("N"), MappingKey = "movies-1", State = EntryState.DuplicatePeer, Container = "mkv", SizeBytes = 4_000_000_000, Bitrate = 8_000_000, RuntimeTicks = 72_000_000_000 },
        item,
        source,
        false);

    private string Admin() => H.World.Sign("p-" + MatrixWorld.Admin.ToString("N"), MatrixWorld.Principal(MatrixWorld.Admin));

    [Fact]
    public async Task PlaybackInfo_AsksThePickedServer_AndTheGrantHoldsTheChoice()
    {
        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await info.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.DoesNotContain("ErrorCode", body, StringComparison.Ordinal);
        Assert.Contains(H.Peer.Requests, static r => r.Path == $"/Items/{MemberItem:N}/PlaybackInfo");
        Assert.DoesNotContain(MemberItem.ToString("N"), body, StringComparison.Ordinal); // the member's ids never reach the client
        var grant = Assert.Single(P.Grants.ForUser(MatrixWorld.Admin));
        Assert.Equal(MemberItem, grant.PeerItemId);
        Assert.Equal(MemberSource, grant.PeerMediaSourceId);
        Assert.Equal(string.Empty, grant.SetId); // the versions are hidden: one source per play
    }

    [Fact]
    public async Task TheStaticStream_StaysOnTheGrantsServer_WhenThePickHasMovedOn()
    {
        using (var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }

        _groups.Choice = Choice(OtherItem, OtherItem); // the order changed after the play started

        using var stream = await H.SendAsync(Admin(), HttpMethod.Get, $"/Videos/{Item}/stream?static=true", static r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-1023"));
        await stream.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, stream.StatusCode);
        var call = H.Peer.Requests.Last(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));
        Assert.Equal($"/Videos/{MemberItem:N}/stream", call.Path);
    }

    [Fact]
    public async Task K46_WhenThePickedServerFails_TheNextCandidateIsTriedInTheSameCall()
    {
        _groups.Next.Add(Choice(OtherItem, OtherItem));
        H.Peer.PlaybackInfoReply = (id, _) => id == MemberItem.ToString("N")
            ? new Reply(500)
            : Reply.Json(new Dictionary<string, object?>
            {
                ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = OtherItem.ToString("N"), ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
                ["PlaySessionId"] = "peer-play-session-0123456789",
            });

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.DoesNotContain("ErrorCode", await info.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(H.Peer.Requests, static r => r.Path == $"/Items/{MemberItem:N}/PlaybackInfo");
        Assert.Contains(H.Peer.Requests, static r => r.Path == $"/Items/{OtherItem:N}/PlaybackInfo");
        Assert.Equal(OtherItem, Assert.Single(P.Grants.ForUser(MatrixWorld.Admin)).PeerItemId);
    }

    [Fact]
    public async Task W11L7_APlaybackInfoOutOfTime_TriesNoFurtherServer()
    {
        _groups.Next.Add(Choice(OtherItem, OtherItem));
        H.Peer.Requests.Clear();
        P.Handler.AttemptBudget = TimeSpan.Zero;
        try
        {
            using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Contains("NotAllowed", await info.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(H.Peer.Requests, static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));
        }
        finally
        {
            P.Handler.AttemptBudget = TimeSpan.FromSeconds(20);
        }
    }

    [Fact]
    public async Task K46_WhenEveryCandidateFails_TheLastFailureStands_AndNoGrantIsMade()
    {
        H.Peer.Requests.Clear();
        _groups.Next.Add(Choice(OtherItem, OtherItem));
        H.Peer.PlaybackInfoReply = static (_, _) => new Reply(404);

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal(2, H.Peer.Requests.Count(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal) && (r.Path.Contains(MemberItem.ToString("N"), StringComparison.Ordinal) || r.Path.Contains(OtherItem.ToString("N"), StringComparison.Ordinal))));
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
    }

    [Fact]
    public async Task W10L2_AGrantsMember_IsCheckedAgainOnTheStaticRoute_AndALostLibraryStopsTheStream()
    {
        using (var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }

        _groups.Admit = false; // the admin took the member's library away from this user
        var before = H.Peer.Requests.Count(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));

        using var stream = await H.SendAsync(Admin(), HttpMethod.Get, $"/Videos/{Item}/stream?static=true", static r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-1023"));

        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal)));
        _groups.Admit = true;
    }

    [Fact]
    public async Task W10L2_TheHlsReCheck_CoversTheMembersLibrary_AndDropsTheGrant()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = MemberSource.ToString("N"), ["Container"] = "mkv", ["SupportsDirectPlay"] = false, ["SupportsTranscoding"] = true, ["TranscodingSubProtocol"] = "hls", ["TranscodingContainer"] = "ts",
                    ["TranscodingUrl"] = $"/videos/{MemberItem:N}/master.m3u8?MediaSourceId={MemberSource:N}&VideoCodec=h264", ["MediaStreams"] = Array.Empty<object>(),
                },
            },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        });
        using (var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }

        var grant = Assert.Single(P.Grants.ForUser(MatrixWorld.Admin));
        Assert.NotNull(grant.MemberAccess);
        _groups.Admit = false;
        P.Time.Advance(TimeSpan.FromMinutes(6)); // the next request runs the full re-check
        var before = H.Peer.Requests.Count;

        using var master = await H.SendAsync(Admin(), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/master.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, master.StatusCode);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
        Assert.Equal(before, H.Peer.Requests.Count);
        _groups.Admit = true;
    }

    [Fact]
    public async Task WithoutAGrant_TheStaticStreamGoesToTheBestServerNow()
    {
        _groups.Choice = Choice(OtherItem, OtherItem);

        using var stream = await H.SendAsync(Admin(), HttpMethod.Get, $"/Videos/{Item}/stream?static=true", static r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-1023"));
        await stream.Content.ReadAsByteArrayAsync();

        var call = H.Peer.Requests.Last(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));
        Assert.Equal($"/Videos/{OtherItem:N}/stream", call.Path);
    }

    [Fact]
    public async Task NoCandidate_PlaysFromTheStubsServer_AsBefore()
    {
        _groups.Choice = null;

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Contains(H.Peer.Requests, static r => r.Path == $"/Items/{PlaybackParts.PeerItem:N}/PlaybackInfo");
    }

    [Fact]
    public async Task AStoppedStubServer_StillRefuses_WhenNoOtherServerCanPlay()
    {
        _groups.Choice = null;
        _groups.Ids = [MemberItem];
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Disabled);
        var before = H.Peer.Requests.Count(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Contains("NotAllowed", await info.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, H.Peer.Requests.Count(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task K45_AnExtraCopy_PlaysFromTheMemberThePickAllows_AndAsksForThePickWithItsOwnLibrary()
    {
        await CopyAsync();
        _groups.Choice = null;
        _groups.CopyChoice = Choice(MemberItem, MemberSource);

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.DoesNotContain("ErrorCode", await info.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(CopyLib, _groups.CopyLibKey);
        Assert.Contains(H.Peer.Requests, static r => r.Path == $"/Items/{MemberItem:N}/PlaybackInfo");
    }

    [Fact]
    public async Task K45_AnExtraCopy_WithNoMemberTheCallerMayOpen_IsRefused_AndNeverFallsBackToTheOwner()
    {
        await CopyAsync();
        _groups.Choice = Choice(OtherItem, OtherItem); // what a plain stub would pick
        _groups.CopyChoice = null;
        var before = H.Peer.Requests.Count;

        using var info = await H.SendAsync(Admin(), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", static r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        using var stream = await H.SendAsync(Admin(), HttpMethod.Get, $"/Videos/{Item}/stream?static=true", static r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-1023"));

        Assert.Contains("NotAllowed", await info.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count); // neither the owner nor anyone else was asked
    }

    private const string CopyLib = "c0c0c0c0c0c0c0c0";

    // The matrix item becomes an extra copy in a shared library that admits the caller (K44/K45).
    private async Task CopyAsync()
    {
        await H.State.UpdateAsync(s => s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary
        {
            LibKey = CopyLib,
            Name = "Copies",
            Type = "movies",
            PathRel = Jellyfin.Plugin.Shoal.State.ShoalLibrary.LibsFolder + "/" + CopyLib,
            Feeds = [H.World.PeerKey],
            Access = new Jellyfin.Plugin.Shoal.State.LibraryAccess { Mode = Jellyfin.Plugin.Shoal.State.AccessMode.Selected, SelectedUserIds = [MatrixWorld.Admin] },
        }));
        H.World.Federated[MatrixWorld.Item] = new FederatedItem(H.World.PeerKey, H.World.MappingKey, CopyLib, IsCopy: true);
        await H.Filter!.Index.RebuildAsync();
        Assert.True(H.Filter.Index.Get(MatrixWorld.Item)!.IsCopy);
    }

    private sealed class FakeGroups : IGroupPlayback
    {
        public MemberChoice? Choice { get; set; }

        public IReadOnlyList<Guid> Ids { get; set; } = [];

        public List<MemberChoice> Next { get; } = [];

        public IReadOnlyList<MemberChoice>? Candidates(string stubPeerKey, CatalogEntry stubEntry, CallerUser user) => Choice is null ? [] : [Choice, .. Next];

        public MemberChoice? CopyChoice { get; set; }

        public string? CopyLibKey { get; private set; }

        public IReadOnlyList<MemberChoice> CandidatesForCopy(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string copyLibKey)
        {
            CopyLibKey = copyLibKey;
            return CopyChoice is null ? [] : [CopyChoice];
        }

        public MemberChoice? Member(string stubPeerKey, CatalogEntry stubEntry, string grantPeerKey, Guid grantPeerItemId) =>
            grantPeerItemId == MemberItem ? new MemberChoice(grantPeerKey, new CatalogEntry { StubKey = "memberstub000001", PeerItemId = MemberItem.ToString("N"), PeerMediaSourceId = MemberSource.ToString("N"), MappingKey = "movies-1", State = EntryState.DuplicatePeer, Container = "mkv", SizeBytes = 4_000_000_000 }, MemberItem, MemberSource, false) : null;

        public IReadOnlyList<Guid> PeerIds(CatalogEntry stubEntry) => Choice is null ? Ids : [Choice.PeerItemId, Choice.PeerMediaSourceId, .. Ids];

        public bool Admit { get; set; } = true;

        public bool Admits(string stubPeerKey, CatalogEntry stubEntry, MemberChoice member, CallerUser user, string? copyLibKey) => Admit;
    }
}
