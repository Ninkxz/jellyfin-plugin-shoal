using System;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K22, Home side: Home redeems an invite on a real Peer-side invite book (through the fake peer), shows the safety code, and ends paired by invite.</summary>
public sealed class InviteHomeTests
{
    // Roomy call timeouts: the default 400 ms can be missed on a busy build machine, which has nothing to do with what is tested.
    private static PairingRig NewRig() => new(o =>
    {
        o.PollTotal = TimeSpan.FromSeconds(15);
        o.NetworkAbortAfter = TimeSpan.FromSeconds(30);
    });

    // These are deadlines, not delays: every wait returns the moment its condition is true. A loaded build machine can starve the thread pool for seconds.
    private const int Deadline = 60_000;

    // Home shows its safety code after the first call; the Peer's admin can only decide once the nonce reveal (the second call) has arrived.
    private static async Task AwaitPendingAsync(PeerSide side, string shareId)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (JsonSerializer.SerializeToElement(side.Service.Approval(shareId)).GetProperty("state").GetString() == "pending")
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("The Peer never saw the revealed nonce.");
    }

    private static async Task<(PairingSessionInfo Session, ProbeResult Probe)> StartAsync(PairingRig rig, FakeJellyfinPeer peer, string invite)
    {
        var probe = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });
        Assert.True(probe.Ok, probe.Failure);
        var session = await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops", PairingMethod.Invite);
        return (session, probe);
    }

    [Fact]
    public async Task AnInvite_PairsThroughTheTypedSafetyCode_AndNoCompareStep()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new PeerSide(peer);
        using var rig = NewRig();
        var (share, invite) = await side.InviteAsync();

        var (started, _) = await StartAsync(rig, peer, invite);
        Assert.Equal(PairingMethod.Invite, started.Method);
        var waiting = await rig.UntilAsync(s => s.Step == PairingStep.AwaitingApproval && s.SafetyCode is not null, Deadline);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", waiting.SafetyCode);
        Assert.Null(waiting.Code);
        await AwaitPendingAsync(side, share.ShareId);
        var approval = JsonSerializer.SerializeToElement(side.Service.Approval(share.ShareId));
        Assert.Equal("pending", approval.GetProperty("state").GetString());
        Assert.Equal("Home", approval.GetProperty("homeName").GetString());

        var (outcome, _) = await side.Service.DecideAsync(share.ShareId, new ApprovalRequest { Approve = true, Sas = waiting.SafetyCode }, default);
        Assert.Equal(ShareOutcome.Ok, outcome);

        var done = await rig.UntilFinishedAsync(Deadline);
        Assert.Equal("confirmed", done.State);
        var stored = rig.Peer(done.PeerHandle);
        Assert.True(stored.Enabled);
        Assert.Equal(PairingMethod.Invite, stored.Pairing!.Method);
        Assert.Equal(PairingMethod.Invite, rig.Secrets.TryGet(done.PeerHandle)!.Method);
        Assert.Equal("0123456789abcdef0123456789abcdef", rig.Secrets.TryGet(done.PeerHandle)!.Token);
    }

    [Fact]
    public async Task M2_AnInviteIsNeverRedeemedOverCleartextOutsideATailnet()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new PeerSide(peer);
        using var rig = NewRig();
        var (_, invite) = await side.InviteAsync();

        var lan = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Lan, LanAck = true, Invite = invite });

        Assert.False(lan.Ok);
        Assert.Equal("inviteLanHttp", lan.Failure);
        Assert.Empty(rig.State.Current.Home.Peers);
    }

    [Fact]
    public async Task AnInviteForAnotherServer_IsRefusedBeforeAnythingIsPaired()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new PeerSide(peer);
        using var rig = NewRig();
        var (_, invite) = await side.InviteAsync();
        var other = InviteCrypto.Format("00000000000000000000000000000000", InviteCrypto.NewSecret());

        var wrong = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = other });
        var malformed = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite[..20] });

        Assert.False(wrong.Ok);
        Assert.Equal("inviteWrongServer", wrong.Failure);
        Assert.Equal("inviteInvalid", malformed.Failure);
        Assert.Empty(rig.State.Current.Home.Peers);
    }

    [Fact]
    public async Task TheMethodMustMatchTheProbe()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new PeerSide(peer);
        using var rig = NewRig();
        var (_, invite) = await side.InviteAsync();
        var probe = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });

        var ex = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops", PairingMethod.Qc));

        Assert.Equal(PairingConflict.MethodUnavailable, ex.Conflict);
    }

    [Fact]
    public async Task ADeniedInvite_StopsAsDenied_AndThreeWrongCodesStopAsCodeMismatch_NothingIsStored()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new PeerSide(peer);
        using var rig = NewRig();
        var (share, invite) = await side.InviteAsync();
        await StartAsync(rig, peer, invite);
        await rig.UntilAsync(s => s.SafetyCode is not null, Deadline);
        await AwaitPendingAsync(side, share.ShareId);
        await side.Service.DecideAsync(share.ShareId, new ApprovalRequest { Approve = false }, default);
        var denied = await rig.UntilFinishedAsync(Deadline);
        Assert.Equal(PairingErrorKind.Denied, denied.Error);

        var second = JsonSerializer.SerializeToElement((await side.Service.InviteAsync(share.ShareId, true, default)).Body).GetProperty("invite").GetString()!;
        await StartAsync(rig, peer, second);
        await rig.UntilAsync(s => s.SafetyCode is not null, Deadline);
        await AwaitPendingAsync(side, share.ShareId);
        for (var i = 0; i < 3; i++)
        {
            await side.Service.DecideAsync(share.ShareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default);
        }

        var mismatch = await rig.UntilFinishedAsync(Deadline);
        Assert.Equal(PairingErrorKind.CodeMismatch, mismatch.Error);
        Assert.Empty(rig.Secrets.PeerKeys!);
    }

    [Fact]
    public async Task AnInviteThePeerDoesNotKnow_EndsAsInviteInvalid()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = NewRig();
        var invite = InviteCrypto.Format(peer.ServerId, InviteCrypto.NewSecret());

        await StartAsync(rig, peer, invite);
        var done = await rig.UntilFinishedAsync(Deadline);

        Assert.Equal(PairingErrorKind.InviteInvalid, done.Error);
        Assert.Empty(rig.Secrets.PeerKeys!);
    }

    private sealed class PeerSide : IDisposable
    {
        public PeerSide(FakeJellyfinPeer peer)
        {
            Dir = new TempDirectory();
            State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, TimeProvider.System, null);
            var users = new FakeShareUsers();
            var libraries = new FakeLibraries();
            Movies = libraries.AddLocal("Movies");
            Book = new InviteBook(State, users, new FakeHomeIdentity { ServerId = peer.ServerId }, TimeProvider.System, NullLogger.Instance);
            Service = new ShareService(State, users, libraries, Book, TimeProvider.System, NullLogger.Instance);
            peer.PairReply = Handle;
        }

        private Reply Handle(byte[] body)
        {
                var parsed = JsonSerializer.Deserialize<JsonElement>(body);
                var answer = Book.Redeem(new PairRequestData(
                    InviteCrypto.NormalizeCode(parsed.GetProperty("invite").GetString()!),
                    parsed.GetProperty("homeNonceCommit").GetString()!,
                    parsed.TryGetProperty("homeNonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    parsed.GetProperty("homeServerId").GetString()!,
                    parsed.GetProperty("homeName").GetString()!,
                    parsed.GetProperty("homeDeviceId").GetString()!,
                    "100.64.0.9"));
                return answer.Failed ? new Reply(404, "{}") : Reply.Json(new { v = 1, state = answer.State, peerServerId = answer.PeerServerId, peerNonce = answer.PeerNonce, token = answer.Token });
        }

        public TempDirectory Dir { get; }

        public StateStore State { get; }

        public Jellyfin.Plugin.Shoal.Visibility.LibraryInfo Movies { get; }

        public InviteBook Book { get; }

        public ShareService Service { get; }

        public async Task<(ShareState Share, string Invite)> InviteAsync()
        {
            await Service.CreateAsync(new ShareRequest { FriendName = "Rich", LibraryIds = [Movies.Id] }, default);
            var share = State.Current.PeerRole.Shares[0];
            var (_, body) = await Service.InviteAsync(share.ShareId, true, default);
            return (share, JsonSerializer.SerializeToElement(body).GetProperty("invite").GetString()!);
        }

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }
    }
}
