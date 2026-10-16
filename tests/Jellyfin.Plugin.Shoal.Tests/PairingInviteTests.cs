using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The pairing state machine over the invite path (the only one left, D17): what is stored when, which stops are safety stops, how a session
/// ends, how a restart or a cancel leaves things, the probe and the one-session-one-click rules. The Quick Connect cases that stood here are retired.
/// </summary>
public sealed class PairingInviteTests
{
    private const string Token = "0123456789abcdef0123456789abcdef"; // what the fake peer's invite book hands out

    private static async Task ApproveAsync(InviteHomeTests.PeerSide side, ShareState share, string? sas, bool approve = true)
    {
        var (outcome, _) = await side.Service.DecideAsync(share.ShareId, new ApprovalRequest { Approve = approve, Sas = sas }, default);
        Assert.Equal(ShareOutcome.Ok, outcome);
    }

    private static async Task<PairingSessionInfo> FailQuicklyAsync(PairingRig rig, FakeJellyfinPeer peer)
    {
        await rig.BeginAsync(peer);
        return await rig.UntilFinishedAsync();
    }

    // ---------------------------------------------------------------- T-PAIR-1, 3, 7

    [Fact]
    public async Task T_PAIR_1_HappyPath_NothingIsStoredBeforeTheApproval_ThenTheTokenIsStoredEncrypted()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ServerId = "fedcba9876543210fedcba9876543210"; // not the token's digits, so "the token is nowhere" means something
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();

        var (waiting, share) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);
        var peerKey = waiting.PeerHandle;

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", waiting.SafetyCode);
        Assert.Equal(PeerStatus.Pairing, rig.Registry.StatusOf(peerKey));
        Assert.Null(rig.Secrets.TryGet(peerKey));
        Assert.Empty(rig.Secrets.PeerKeys!);
        var record = rig.Peer(peerKey);
        Assert.False(record.Enabled);
        Assert.Empty(record.Libraries);
        Assert.Null(record.Pairing);
        Assert.Equal(PairingPhase.BeforeApproval, record.PendingPairing!.Phase);
        Assert.DoesNotContain(Token, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.False(File.Exists(rig.Paths.SecretsFile));

        await ApproveAsync(side, share, waiting.SafetyCode);
        var done = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);

        Assert.Equal(PairingStep.Confirmed, done.Step);
        Assert.Equal("confirmed", done.State);
        var credential = rig.Secrets.TryGet(peerKey);
        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.Equal(PairingMethod.Invite, credential.Method);
        Assert.StartsWith("shoal-a1b2c3d4e5f6-" + peerKey[..8], credential.DeviceId, StringComparison.Ordinal);
        record = rig.Peer(peerKey);
        Assert.True(record.Enabled);
        Assert.Null(record.PendingPairing);
        Assert.Equal(PairingMethod.Invite, record.Pairing!.Method);
        Assert.Null(record.Pairing.CheckString); // no device-name check any more
        Assert.Equal(PairingRig.Admin, record.Pairing.PairedByUserId);
        Assert.Equal(peer.ServerId, record.PinnedServerId);
        Assert.Null(rig.Health.GetAbort(peerKey));
        Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(peerKey)); // the health monitor judges it next
        Assert.Equal(TrustDenial.None, rig.Registry.Check(peerKey, true, TrustRequirement.PinAndAuthFresh));

        Assert.True(File.Exists(rig.Paths.SecretsFile));
        Assert.DoesNotContain(Token, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(rig.Paths.SecretsFile)), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.Empty(rig.Notifier.Messages);
        Assert.Contains("Token=\"" + Token + "\"", peer.To("/Users/Me").Single().Headers["Authorization"], StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Requests, static r => r.Path.StartsWith("/QuickConnect", StringComparison.Ordinal) || r.Path.Contains("AuthenticateWithQuickConnect", StringComparison.Ordinal)); // T-D17-1
    }

    [Fact]
    public async Task T_PAIR_1_ThePolicyOfTheAccountIsReported_AsWarningsAndNotes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ExtraPolicy["EnableAllFolders"] = true;
        peer.ExtraPolicy["EnableContentDeletion"] = true;
        peer.ExtraPolicy["EnableAllDevices"] = true;
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (waiting, share) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);

        await ApproveAsync(side, share, waiting.SafetyCode);
        var done = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);

        Assert.Contains(AccountWarning.AllLibraries, done.AccountWarnings);
        Assert.Contains(AccountWarning.ContentDeletion, done.AccountWarnings);
        Assert.Contains(AccountWarning.AllDevices, done.AccountWarnings);
        Assert.Contains(AccountNote.UserPreferenceAccess, done.AccountNotes);
    }

    [Fact]
    public async Task T_PAIR_3_AnAdministratorAccount_IsRefused_SignedOut_AndNothingIsStored()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ServerId = "fedcba9876543210fedcba9876543210"; // not the token's digits, so "the token is nowhere" means something
        peer.Administrator = true;
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (waiting, share) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);

        await ApproveAsync(side, share, waiting.SafetyCode);
        var finished = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);

        Assert.Equal(PairingErrorKind.AdminAccount, finished.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Contains("Token=\"" + Token + "\"", peer.To("/Sessions/Logout").Single().Headers["Authorization"], StringComparison.Ordinal);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Empty(peer.To("/System/Info")); // refused before anything else is asked of the account
        Assert.Single(rig.Notifier.Messages);
        Assert.Equal("Pairing stopped for your safety", rig.Notifier.Messages[0].Header);
        Assert.DoesNotContain(Token, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.Equal(PairingErrorKind.AdminAccount, rig.Health.GetAbort(waiting.PeerHandle)!.Kind);
    }

    [Fact]
    public async Task T_PAIR_7_PeerAndAttackerTextIsHandedOnAsText_AndNeverInterpreted()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ServerName = "<img src=x onerror=alert(1)>‮\u0007" + new string('x', 200);
        using var rig = new PairingRig();

        var probe = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));

        Assert.StartsWith("<img src=x onerror=alert(1)>", probe.ServerName, StringComparison.Ordinal);
        Assert.Equal(64, probe.ServerName!.Length);
        Assert.DoesNotContain('‮', probe.ServerName);
        Assert.DoesNotContain('\u0007', probe.ServerName);
        var started = await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Shoal: <script>alert(1)</script>\r\n");
        Assert.Equal("Shoal: <script>alert(1)</script>", rig.Peer(started.PeerHandle).DisplayName);
        Assert.Equal(JsonSerializer.Serialize(started), JsonSerializer.Serialize(started));
    }

    [Fact]
    public async Task T_D17_1_NoPairingFlowEverSendsAQuickConnectRequest()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        await rig.ProbeAsync(peer);
        await FailQuicklyAsync(rig, peer);

        Assert.DoesNotContain(peer.Requests, static r => r.Path.StartsWith("/QuickConnect", StringComparison.Ordinal) || r.Path.Contains("AuthenticateWithQuickConnect", StringComparison.Ordinal));
        Assert.Equal(0, peer.Initiates);
    }

    // ---------------------------------------------------------------- one session, one click, cancel

    [Fact]
    public async Task ASessionCanBeCancelled_AFirstPairingLeavesNoTrace()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (waiting, _) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);

        Assert.True(await rig.Service.CancelAsync());

        Assert.Null(rig.Service.GetSession());
        Assert.Empty(rig.State.Current.Home.Peers);
        Assert.Null(rig.Health.GetAbort(waiting.PeerHandle));
        Assert.False(Directory.Exists(rig.Paths.PeerDir(waiting.PeerHandle)));
        Assert.False(await rig.Service.CancelAsync());
    }

    [Fact]
    public async Task AFreshSessionCannotStart_WhileAnotherLives()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        await InviteHomeTests.BeginPendingAsync(rig, peer, side);
        var second = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, second.ProbeHandle!, "B"));

        Assert.Equal(PairingConflict.SessionActive, failure.Conflict);
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(rig.Service.GetSession()); // a reload never starts anything
        }
    }

    [Fact]
    public async Task ABlankName_IsRefused()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var probe = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));

        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "   "));
    }

    // ---------------------------------------------------------------- T-PAIR-15

    [Fact]
    public async Task T_PAIR_15_AProbeHandle_IsSingleUse_AndExpiresAfterTenMinutes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var clock = new ManualTimeProvider();
        using var rig = new PairingRig(time: clock);
        var used = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
        var old = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
        await rig.Service.BeginAsync(PairingRig.Admin, used.ProbeHandle!, "A");
        await rig.UntilFinishedAsync();

        var reuse = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, used.ProbeHandle!, "A"));
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var expired = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, old.ProbeHandle!, "A"));
        var unknown = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, "nonsense", "A"));

        Assert.Equal(PairingConflict.ProbeExpired, reuse.Conflict);
        Assert.Equal(PairingConflict.ProbeExpired, expired.Conflict);
        Assert.Equal(PairingConflict.ProbeExpired, unknown.Conflict);
    }

    [Fact]
    public async Task T_PAIR_15_TheSixthStartForOneServer_IsRateLimited_OnTheServer()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var clock = new ManualTimeProvider();
        using var rig = new PairingRig(time: clock);

        for (var i = 0; i < 5; i++)
        {
            await rig.BeginAsync(peer, "A" + i); // each ends at once: the Peer does not know the invite
            await rig.UntilFinishedAsync();
        }

        var probe = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
        var limited = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "A6"));

        Assert.Equal(PairingConflict.RateLimited, limited.Conflict);
        Assert.InRange(limited.RetryAfterSeconds, 1, 600);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var again = await rig.Service.BeginAsync(PairingRig.Admin, (await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer))).ProbeHandle!, "A7");
        Assert.NotEmpty(again.PeerHandle);
    }

    [Fact]
    public async Task T_PAIR_15_TheAddressOfThePeerCannotChangeWhileASessionLives()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (started, _) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);
        var addresses = new PeerAddressService(rig.State, rig.Secrets, rig.Registry, rig.Service);

        var failure = await Assert.ThrowsAsync<PeerAddressChangeException>(() => addresses.ChangeBaseUrlAsync(started.PeerHandle, "http://100.64.0.9:8096"));

        Assert.Equal(PeerAddressChangeProblem.PairingActive, failure.Problem);
        Assert.True(rig.Service.IsLive(started.PeerHandle));
        Assert.False(rig.Service.IsLive("00000000000000000000000000000000"));
    }

    [Fact]
    public async Task T_PAIR_15_APairingThatCannotBeSaved_IsRefusedAtTheStart_WhenTheSecretStoreIsUnreadable()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        File.WriteAllText(rig.Paths.SecretsFile, "{ not a secrets file");
        using var broken = new SecretStore(rig.Paths, rig.State, rig.Log, name => name == SecretStore.KeyVariable ? rig.Key : null, null, null, null);
        var service = new PairingService(rig.State, broken, rig.Client, rig.Registry, rig.Health, rig.Home, rig.Notifier, rig.Paths, rig.Log, rig.Time, rig.Options);
        var probeResult = await service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Invite = PairingRig.UnknownInvite(peer) });

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => service.BeginAsync(PairingRig.Admin, probeResult.ProbeHandle!, "A"));

        Assert.Equal(PairingConflict.SecretsUnreadable, failure.Conflict);
        Assert.DoesNotContain(peer.Requests, static r => r.Path == "/Shoal/Pair");
    }

    // ---------------------------------------------------------------- T-PAIR-16

    [Fact]
    public async Task T_PAIR_16_e_ARestart_TurnsAPairingInProgressIntoInterruptedWithItsPhase()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (started, _) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);
        Assert.Equal(PairingPhase.BeforeApproval, rig.Peer(started.PeerHandle).PendingPairing!.Phase);

        await rig.Service.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        var restarted = rig.Restart();
        await restarted.StartAsync(CancellationToken.None);

        var abort = rig.Health.GetAbort(started.PeerHandle)!;
        Assert.Equal(PairingErrorKind.Interrupted, abort.Kind);
        Assert.Equal(PairingPhase.BeforeApproval, abort.Phase);
        Assert.Null(rig.Peer(started.PeerHandle).PendingPairing);
        Assert.False(rig.Peer(started.PeerHandle).Enabled);
        Assert.Equal(PeerStatus.PairingAborted, rig.Registry.StatusOf(started.PeerHandle));
        Assert.Empty(rig.Notifier.Messages);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Null(restarted.GetSession());
        var again = rig.Restart();
        await again.StartAsync(CancellationToken.None);
        Assert.Equal(PairingErrorKind.Interrupted, rig.Health.GetAbort(started.PeerHandle)!.Kind);
    }

    [Fact]
    public async Task T_PAIR_16_f_AnAbort_SurvivesAReloadAndARestart()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var started = await rig.BeginAsync(peer);
        await rig.UntilFinishedAsync();

        var restarted = rig.Restart();
        await restarted.StartAsync(CancellationToken.None);

        Assert.Equal(PairingErrorKind.InviteInvalid, rig.Health.GetAbort(started.PeerHandle)!.Kind);
        Assert.Equal(PeerStatus.PairingAborted, rig.Registry.StatusOf(started.PeerHandle));
        Assert.Contains("inviteInvalid", File.ReadAllText(Path.Combine(rig.Paths.PeerDir(started.PeerHandle), "health.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_PAIR_16_g_OnlyTheThreeSafetyKinds_SendAPopUp()
    {
        // ServerChanged: the server that answers is not the one that was probed.
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            using var rig = new PairingRig();
            var probe = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
            peer.ServerId = "ffffffffffffffffffffffffffffff00";
            await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "A");
            var finished = await rig.UntilFinishedAsync();
            Assert.Equal(PairingErrorKind.ServerChanged, finished.Error);
            Assert.Equal("Pairing stopped for your safety", Assert.Single(rig.Notifier.Messages).Header);
        }

        // BadPeerResponse: the first answer carries a nonce that is not one.
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            peer.PairReply = _ => Reply.Json(new { v = 1, state = "pending", peerServerId = peer.ServerId, peerNonce = "!!!" });
            using var rig = new PairingRig();
            var finished = await FailQuicklyAsync(rig, peer);
            Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
            Assert.Equal("Pairing stopped for your safety", Assert.Single(rig.Notifier.Messages).Header);
        }

        // Stopped kinds: the friend's server does not know the invite; the friend's admin says no.
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            using var rig = new PairingRig();
            var finished = await FailQuicklyAsync(rig, peer);
            Assert.Equal(PairingErrorKind.InviteInvalid, finished.Error);
            Assert.Empty(rig.Notifier.Messages);
        }

        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            using var side = new InviteHomeTests.PeerSide(peer);
            using var rig = InviteHomeTests.NewRig();
            var (waiting, share) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);
            await ApproveAsync(side, share, waiting.SafetyCode, approve: false);
            var finished = await rig.UntilFinishedAsync(InviteHomeTests.Deadline);
            Assert.Equal(PairingErrorKind.Denied, finished.Error);
            Assert.Empty(rig.Notifier.Messages);
        }

        Assert.Equal(3, Enum.GetValues<PairingErrorKind>().Count(PairingErrors.IsSafety));
    }

    [Fact]
    public async Task T_PAIR_16_h_Dismiss_RemovesAFirstPairing_AndRestoresARePairedPeer()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var first = await rig.BeginAsync(peer);
        await rig.UntilFinishedAsync();

        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync("00000000000000000000000000000000"));
        var removed = await rig.Service.DismissPairingAsync(first.PeerHandle);
        Assert.True(removed.Removed);
        Assert.Empty(rig.State.Current.Home.Peers);
        Assert.False(Directory.Exists(rig.Paths.PeerDir(first.PeerHandle)));
        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync(first.PeerHandle));

        // A paired peer, then a re-pair that is stopped: the old secret and state are untouched, and Dismiss brings it back.
        var paired = await InviteHomeTests.PairByInviteAsync(rig, peer, side);
        var oldToken = rig.Secrets.TryGet(paired.PeerHandle)!.Token;
        var again = await rig.BeginAsync(peer, "Hops", paired.PeerHandle); // an invite nobody issued
        Assert.True(again.Paired);
        var stopped = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.InviteInvalid, stopped.Error);
        Assert.Equal(oldToken, rig.Secrets.TryGet(paired.PeerHandle)!.Token);
        Assert.True(rig.Peer(paired.PeerHandle).Enabled);
        Assert.NotNull(rig.Peer(paired.PeerHandle).Pairing);
        var restored = await rig.Service.DismissPairingAsync(paired.PeerHandle);
        Assert.True(restored.Restored);
        Assert.Null(rig.Health.GetAbort(paired.PeerHandle));
        Assert.Equal(oldToken, rig.Secrets.TryGet(paired.PeerHandle)!.Token);
        Assert.Single(rig.State.Current.Home.Peers);
    }

    [Fact]
    public async Task T_PAIR_16_h_Dismiss_IsRefusedWhileASessionIsLive()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var (started, _) = await InviteHomeTests.BeginPendingAsync(rig, peer, side);

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync(started.PeerHandle));

        Assert.Equal(PairingConflict.NothingToDismiss, failure.Conflict);
    }

    [Fact]
    public async Task ARePair_KeepsTheOldSecretAndTheLibraries_UntilApproved_ThenReplacesTheCredential()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var first = await InviteHomeTests.PairByInviteAsync(rig, peer, side);
        var share = side.State.Current.PeerRole.Shares[0];
        var oldDevice = rig.Secrets.TryGet(first.PeerHandle)!.DeviceId;
        var oldPaired = rig.Peer(first.PeerHandle).Pairing!.PairedUtc;
        await rig.State.UpdateAsync(s => s.Home.Peers.Single(p => p.PeerKey == first.PeerHandle).Libraries.Add(new LibraryMapping { MappingKey = "movies-1", RemoteName = "Movies", LocalLibraryName = "Hops Movies", Subfolder = "movies" }));
        await Task.Delay(20);

        var invite = await side.ReinviteAsync(share.ShareId);
        var (second, _) = await InviteHomeTests.StartAsync(rig, peer, invite, "Hops", first.PeerHandle);
        var waiting = await rig.UntilAsync(s => s.Step == PairingStep.AwaitingApproval && s.SafetyCode is not null, InviteHomeTests.Deadline);
        await InviteHomeTests.AwaitPendingAsync(side, share.ShareId);

        Assert.True(second.Paired);
        Assert.Equal(first.PeerHandle, second.PeerHandle);
        Assert.NotNull(rig.Secrets.TryGet(first.PeerHandle));
        Assert.True(rig.Peer(first.PeerHandle).Enabled);
        Assert.Equal(PeerStatus.Pairing, rig.Registry.StatusOf(first.PeerHandle));
        Assert.Single(rig.State.Current.Home.Peers);

        await ApproveAsync(side, share, waiting.SafetyCode);
        await rig.UntilFinishedAsync(InviteHomeTests.Deadline);

        var after = rig.Peer(first.PeerHandle);
        Assert.Equal(oldDevice, rig.Secrets.TryGet(first.PeerHandle)!.DeviceId); // same DeviceId: the old token dies at the new authentication
        Assert.Equal(PairingMethod.Invite, after.Pairing!.Method);
        Assert.True(after.Pairing.PairedUtc > oldPaired);
        Assert.Single(after.Libraries); // the mappings stay
    }

    // ---------------------------------------------------------------- the probe

    [Fact]
    public async Task TheProbe_ReportsWhatItFound_AndRefusesWhatThePolicyRefuses()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();

        var ok = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl });

        Assert.True(ok.Ok);
        Assert.Equal("Hops", ok.ServerName);
        Assert.Equal("12.1.0", ok.Version);
        Assert.Equal("01234567", ok.ServerIdPrefix);
        Assert.NotNull(ok.ProbeHandle);
        Assert.InRange(ok.LatencyMs, 0, 5000);
        Assert.DoesNotContain(peer.Requests, static r => r.Path.StartsWith("/QuickConnect", StringComparison.Ordinal));

        using var strict = new PairingRig(o => o.RealPolicy = true);
        var refused = await strict.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        Assert.False(refused.Ok);
        Assert.Equal(AddressProblem.Blocked, refused.AddressProblem);
        Assert.Null(refused.ProbeHandle);
    }

    [Fact]
    public async Task TheProbe_SaysWhenTheServerIsNotJellyfinOrNotThere()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        peer.PublicInfo = Reply.Json(new { ServerName = "x", Id = "nope" });
        Assert.Equal("notJellyfin", (await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl })).Failure);
        peer.PublicInfo = new Reply(200, "<html></html>", "text/html");
        Assert.Equal("notJellyfin", (await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl })).Failure);
        peer.PublicInfo = Reply.Plain(500);
        Assert.Equal("unreachable", (await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl })).Failure);
        Assert.Equal("unreachable", (await rig.Service.ProbeAsync(new ProbeRequest { Url = "http://127.0.0.1:1" })).Failure);
        Assert.Equal("unknownPeer", (await rig.Service.ProbeAsync(new ProbeRequest { PeerHandle = "00000000000000000000000000000000" })).Failure);
        Assert.Equal(AddressProblem.NotAUrl, (await rig.Service.ProbeAsync(new ProbeRequest { Url = "nonsense" })).AddressProblem);
    }

    [Fact]
    public async Task W6_L3_AProbeIsRefused_WhenThePeersAddressChangedAfterIt()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);
        using var rig = InviteHomeTests.NewRig();
        var handle = (await InviteHomeTests.PairByInviteAsync(rig, peer, side)).PeerHandle;
        var probe = await rig.ProbeAsync(peer, handle, PairingRig.UnknownInvite(peer));

        await rig.State.UpdateAsync(s => s.Home.Peers.Single(p => p.PeerKey == handle).BaseUrl = "http://100.64.0.9:8096");
        var refused = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops"));

        Assert.Equal(PairingConflict.ProbeExpired, refused.Conflict);
    }

    [Fact]
    public void W6_L2_ARePairEndpoint_CarriesTheStoredCertificatePin()
    {
        var endpoint = PeerEndpoint.WithoutToken("k", "n", new Uri("https://hops.example/"), "d", null)
            .Configured(PeerNetwork.Public, null, false, "0123456789abcdef0123456789abcdef", "sha256/AAAA");

        Assert.Equal("sha256/AAAA", endpoint.TlsSpkiPin);
        Assert.Equal("sha256/AAAA", endpoint.Configured(PeerNetwork.Public, null, false).TlsSpkiPin); // kept when not given again
    }
}
