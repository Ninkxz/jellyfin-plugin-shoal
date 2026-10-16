using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// D17: nothing pairs by Quick Connect any more and nothing keeps running that was paired that way. An old state still loads: such a server is paused (its
/// libraries, stubs and catalog stay) until it is paired again by invite, and what a Quick Connect pairing left behind is cleaned up silently.
/// </summary>
public sealed class LegacyQuickConnectTests
{
    private const string OldToken = "aaaabbbbccccddddeeeeffff00001111";
    private const string OldDevice = "shoal-a1b2c3d4e5f6-0000aaaa";

    private static LibraryMapping Mapping() => new() { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = "Hops Movies", Subfolder = "movies" };

    private static async Task SeedQuickConnectPeerAsync(PairingRig rig, string peerKey, bool withCredential = true)
    {
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState
        {
            PeerKey = peerKey,
            DisplayName = "Old " + peerKey[..4],
            BaseUrl = "http://old.tail1234.ts.net:8096",
            Enabled = true,
            Pairing = new PairingInfo { Method = PairingMethod.Qc, CheckString = "K7QM", PairedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), PairedByUserId = PairingRig.Admin },
            Libraries = { Mapping() },
        }));
        if (withCredential)
        {
            await rig.Secrets.PutAsync(peerKey, new PeerCredential(OldToken, OldDevice, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), PairingMethod.Qc), CancellationToken.None);
        }
    }

    [Fact]
    public async Task T_D17_2_AnOldStateLoads_AServerPairedTheOldWayIsPaused_AndItsLibrariesStay()
    {
        using var rig = new PairingRig();
        var paired = "11111111111111111111111111111111";
        await SeedQuickConnectPeerAsync(rig, paired);

        await rig.Service.StartAsync(CancellationToken.None);

        var peer = rig.Peer(paired);
        Assert.False(peer.Enabled);
        Assert.Single(peer.Libraries);
        Assert.NotNull(rig.Secrets.TryGet(paired));
        Assert.Equal(PairingMethod.Qc, peer.Pairing!.Method); // kept so the card can say it must be paired again
        var card = AdminStatusBuilder.Server(peer, new PeerLiveInfo());
        Assert.Equal("Disabled", card.Status);
        Assert.Equal("qc", card.PairingMethod);
        Assert.True(card.NeedsRepair);
        Assert.Empty(rig.Notifier.Messages);
    }

    [Fact]
    public async Task T_D17_2_APairingInFlightTheOldWay_AndALegacyStop_AreCleanedUpSilently()
    {
        using var rig = new PairingRig();
        var neverPaired = "22222222222222222222222222222222";
        var stoppedNeverPaired = "33333333333333333333333333333333";
        var stoppedPaired = "44444444444444444444444444444444";
        await rig.State.UpdateAsync(s =>
        {
            s.Home.Peers.Add(new PeerState { PeerKey = neverPaired, DisplayName = "Half", BaseUrl = "http://half.tail1234.ts.net:8096", Enabled = false, PendingPairing = new PairingProgress { Phase = PairingPhase.AwaitingConfirm, Method = PairingMethod.Qc, StartedUtc = DateTime.UtcNow } });
            s.Home.Peers.Add(new PeerState { PeerKey = stoppedNeverPaired, DisplayName = "Stopped", BaseUrl = "http://stopped.tail1234.ts.net:8096", Enabled = false });
        });
        await SeedQuickConnectPeerAsync(rig, stoppedPaired);
        rig.Health.SetAbort(stoppedNeverPaired, new PairingAbortRecord { Kind = PairingErrorKind.QuickConnectOff, AtUtc = DateTime.UtcNow });
        rig.Health.SetAbort(stoppedPaired, new PairingAbortRecord { Kind = PairingErrorKind.RequestReplaced, AtUtc = DateTime.UtcNow });

        // Old health files with the legacy values load without a throw.
        var reloaded = new PeerHealthStore(rig.Paths, NullLogger<PeerHealthStore>.Instance);
        Assert.Equal(PairingErrorKind.QuickConnectOff, reloaded.GetAbort(stoppedNeverPaired)!.Kind);

        await rig.Service.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(rig.State.Current.Home.Peers, p => p.PeerKey == neverPaired); // never finished, no key: gone as Dismiss would remove it
        Assert.DoesNotContain(rig.State.Current.Home.Peers, p => p.PeerKey == stoppedNeverPaired);
        Assert.Contains(rig.State.Current.Home.Peers, p => p.PeerKey == stoppedPaired); // a paired server stays (paused)
        Assert.Null(rig.Health.GetAbort(stoppedPaired)); // the legacy stop is cleared without a pop-up
        Assert.Null(rig.Health.GetAbort(stoppedNeverPaired));
        Assert.Empty(rig.Notifier.Messages);
    }

    [Fact]
    public void APausedServerOnPlainHttpOutsideATailnet_SaysItsRePairIsBlocked_ButATailnetOrHttpsOneDoesNot()
    {
        PeerState Qc(string url, PeerNetwork network) => new() { PeerKey = "11111111111111111111111111111111", DisplayName = "Old", BaseUrl = url, Network = network, Enabled = false, Pairing = new PairingInfo { Method = PairingMethod.Qc } };

        var lan = AdminStatusBuilder.Server(Qc("http://192.168.1.20:8096", PeerNetwork.Lan), new PeerLiveInfo());
        var https = AdminStatusBuilder.Server(Qc("https://hops.example", PeerNetwork.Public), new PeerLiveInfo());
        var tailnet = AdminStatusBuilder.Server(Qc("http://hops.tail1234.ts.net:8096", PeerNetwork.Tailnet), new PeerLiveInfo());

        Assert.True(lan.NeedsRepair);
        Assert.True(lan.RepairBlockedByHttp);
        Assert.True(https.NeedsRepair);
        Assert.False(https.RepairBlockedByHttp);
        Assert.False(tailnet.RepairBlockedByHttp);
    }

    [Fact]
    public async Task L1_WithUnreadableKeys_NothingIsRemovedOrPaused()
    {
        using var rig = new PairingRig();
        var half = "22222222222222222222222222222222";
        var paired = "11111111111111111111111111111111";
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = half, DisplayName = "Half", BaseUrl = "http://half.tail1234.ts.net:8096", Enabled = false, PendingPairing = new PairingProgress { Phase = PairingPhase.BeforeApproval, Method = PairingMethod.Qc, StartedUtc = DateTime.UtcNow } }));
        await SeedQuickConnectPeerAsync(rig, paired, withCredential: false);
        System.IO.File.WriteAllText(rig.Paths.SecretsFile, "{ not a secrets file");
        using var broken = new SecretStore(rig.Paths, rig.State, rig.Log, name => name == SecretStore.KeyVariable ? rig.Key : null, null, null, null);
        using var service = new PairingService(rig.State, broken, rig.Client, rig.Registry, rig.Health, rig.Home, rig.Notifier, rig.Paths, rig.Log, rig.Time, rig.Options);

        await service.StartAsync(CancellationToken.None);

        Assert.Contains(rig.State.Current.Home.Peers, p => p.PeerKey == half); // a key that cannot be read does not make a server "never paired"
        Assert.True(rig.Peer(paired).Enabled);
        Assert.Single(rig.Peer(paired).Libraries);
    }

    [Fact]
    public async Task L2_AServerPausedByTheMigration_HasItsOldSessionSignedOut()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var key = "11111111111111111111111111111111";
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = key, DisplayName = "Old", BaseUrl = peer.BaseUrl, Enabled = true, Network = PeerNetwork.Tailnet, Pairing = new PairingInfo { Method = PairingMethod.Qc, CheckString = "K7QM", PairedUtc = DateTime.UtcNow }, Libraries = { Mapping() } }));
        await rig.Secrets.PutAsync(key, new PeerCredential(OldToken, OldDevice, DateTime.UtcNow, PairingMethod.Qc), CancellationToken.None);

        await rig.Service.StartAsync(CancellationToken.None);

        Assert.False(rig.Peer(key).Enabled);
        Assert.Contains(peer.To("/Sessions/Logout"), r => r.Headers["Authorization"].Contains("Token=\"" + OldToken + "\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_D17_3_APairedTheOldWayServer_IsPairedAgainByInvite_KeepsItsLibraries_AndTheOldTokenIsSignedOut()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ServerId = "fedcba9876543210fedcba9876543210";
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var key = "55555555555555555555555555555555";
        await rig.State.UpdateAsync(s => s.Home.Peers.Add(new PeerState
        {
            PeerKey = key,
            DisplayName = "Hops",
            BaseUrl = peer.BaseUrl,
            Enabled = false,
            PinnedServerId = peer.ServerId,
            Network = PeerNetwork.Tailnet,
            Pairing = new PairingInfo { Method = PairingMethod.Qc, CheckString = "K7QM", PairedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), PairedByUserId = PairingRig.Admin },
            Libraries = { Mapping() },
        }));
        await rig.Secrets.PutAsync(key, new PeerCredential(OldToken, OldDevice, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), PairingMethod.Qc), CancellationToken.None);
        var (share, invite) = await side.InviteAsync();

        await InviteHomeTests.StartAsync(rig, peer, invite, "Hops", key);
        var waiting = await rig.UntilAsync(s => s.Step == PairingStep.AwaitingApproval && s.SafetyCode is not null, InviteHomeTests.Deadline);
        await InviteHomeTests.AwaitPendingAsync(side, share.ShareId);
        var (outcome, _) = await side.Service.DecideAsync(share.ShareId, new ApprovalRequest { Approve = true, Sas = waiting.SafetyCode }, default);
        Assert.Equal(ShareOutcome.Ok, outcome);
        var done = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);

        Assert.Equal("confirmed", done.State);
        var after = rig.Peer(key);
        Assert.Equal(PairingMethod.Invite, after.Pairing!.Method);
        Assert.Null(after.Pairing.CheckString);
        Assert.True(after.Enabled);
        Assert.Single(after.Libraries);
        var credential = rig.Secrets.TryGet(key)!;
        Assert.Equal(PairingMethod.Invite, credential.Method);
        Assert.NotEqual(OldToken, credential.Token);
        Assert.Contains(peer.To("/Sessions/Logout"), r => r.Headers["Authorization"].Contains("Token=\"" + OldToken + "\"", StringComparison.Ordinal));
        Assert.False(AdminStatusBuilder.Server(after, new PeerLiveInfo()).NeedsRepair);
    }
}
