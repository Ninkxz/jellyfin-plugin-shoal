using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>Pairing flow v2, backend (docs/ui-spec-pairing-v2.md section 11, items 1 and 5).</summary>
public sealed class PairingV2Tests
{
    [Fact]
    public async Task Item1_AnAddressIsChecked_WithoutAnInvite_AndTheInviteComesAtStart_WithAReprobe()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (share, invite) = await side.InviteAsync();

        var check = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        Assert.True(check.Ok);
        Assert.Equal(peer.ServerId[..8], check.ServerIdPrefix);
        var reprobe = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });
        Assert.True(reprobe.Ok);
        Assert.NotEqual(check.ProbeHandle, reprobe.ProbeHandle);

        var started = await rig.Service.BeginAsync(PairingRig.Admin, reprobe.ProbeHandle!, "Hops");

        Assert.Equal(PairingMethod.Invite, started.Method);
        await rig.UntilAsync(s => s.Step == PairingStep.AwaitingApproval && s.SafetyCode is not null, InviteHomeTests.Deadline);
        Assert.NotNull(share);
    }

    [Fact]
    public async Task Item1_TheProbeWithoutAnInvite_CannotStartAnInvitePairing()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = InviteHomeTests.NewRig();
        var check = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });

        var ex = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, check.ProbeHandle!, "Hops"));

        Assert.Equal(PairingConflict.MethodUnavailable, ex.Conflict);
    }

    [Fact]
    public async Task Item5_ANetworkFailureBeforeThePeerSawTheRequest_DoesNotBurnTheInvite()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (share, invite) = await side.InviteAsync();
        var real = peer.PairReply!;
        var down = true;
        var reached = 0;
        peer.PairReply = body =>
        {
            if (down)
            {
                return new Reply(503, "{}"); // the request never got to the invite book
            }

            reached++;
            return real(body);
        };

        var probe = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });
        await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops");
        var failed = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);
        Assert.Equal(PairingErrorKind.Network, failed.Error);

        down = false;
        var again = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });
        await rig.Service.BeginAsync(PairingRig.Admin, again.ProbeHandle!, "Hops");

        var waiting = await rig.UntilAsync(s => s.Step == PairingStep.AwaitingApproval && s.SafetyCode is not null, InviteHomeTests.Deadline);
        Assert.NotNull(waiting.SafetyCode);
        Assert.True(reached > 0);
    }
}
