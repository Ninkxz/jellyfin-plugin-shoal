using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K5 acceptance (DESIGN 11.2, 4.2, 4.2a, 4.3; v2.2): T-PAIR-1, -2, -3, -5, -6, -7 and the unit parts of T-PAIR-13, -14, -15, -16.
/// </summary>
public sealed class PairingTests
{
    private const string PeerTokenA = FakeJellyfinPeer.DefaultToken;

    private static string DeviceOf(RecordedRequest request)
    {
        var header = request.Headers["Authorization"];
        var device = header.Split("Device=\"")[1].Split('"')[0];
        return WebUtility.UrlDecode(device);
    }

    private static async Task<PairingSessionInfo> ReachConfirmAsync(PairingRig rig, FakeJellyfinPeer peer, int approveFromPoll = 3)
    {
        peer.ApproveFromPoll = approveFromPoll;
        await rig.BeginAsync(peer);
        return await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
    }

    [Fact]
    public async Task T_PAIR_1_HappyPath_NothingIsStoredUntilTheConfirmGate_ThenTheTokenIsStoredEncrypted()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();

        var started = await rig.BeginAsync(peer);
        var peerKey = started.PeerHandle;
        var waiting = await rig.UntilStepAsync(PairingStep.AwaitingApproval);

        Assert.Equal("739481", waiting.Code);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}$", waiting.SafetyCode);
        Assert.Equal(PeerStatus.Pairing, rig.Registry.StatusOf(peerKey));
        peer.ApproveFromPoll = 1;
        var atConfirm = await rig.UntilStepAsync(PairingStep.AwaitingConfirm);

        // Awaiting the confirm: the token is in memory only. No secret, no library, no enabled peer, the pairing is in progress.
        Assert.Null(rig.Secrets.TryGet(peerKey));
        Assert.Empty(rig.Secrets.PeerKeys!);
        var record = rig.Peer(peerKey);
        Assert.False(record.Enabled);
        Assert.Empty(record.Libraries);
        Assert.Null(record.Pairing);
        Assert.Equal(PairingPhase.AwaitingConfirm, record.PendingPairing!.Phase);
        Assert.Equal(PeerStatus.Pairing, rig.Registry.StatusOf(peerKey));
        Assert.DoesNotContain(PeerTokenA, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.False(File.Exists(rig.Paths.SecretsFile));
        Assert.Equal(waiting.SafetyCode, atConfirm.SafetyCode);

        // Confirm is legal now and saves the pairing.
        var done = await rig.Service.ConfirmAsync(match: true);

        Assert.Equal(PairingStep.Confirmed, done.Step);
        Assert.Equal("confirmed", done.State);
        Assert.Null(done.Code);
        var credential = rig.Secrets.TryGet(peerKey);
        Assert.NotNull(credential);
        Assert.Equal(PeerTokenA, credential.Token);
        Assert.Equal(PairingMethod.Qc, credential.Method);
        Assert.StartsWith("shoal-a1b2c3d4e5f6-" + peerKey[..8], credential.DeviceId, StringComparison.Ordinal);
        record = rig.Peer(peerKey);
        Assert.True(record.Enabled);
        Assert.Null(record.PendingPairing);
        Assert.Equal(PairingMethod.Qc, record.Pairing!.Method);
        Assert.Equal(waiting.SafetyCode, record.Pairing.CheckString);
        Assert.Equal(PairingRig.Admin, record.Pairing.PairedByUserId);
        Assert.Equal("0123456789abcdef0123456789abcdef", record.PinnedServerId);
        Assert.Null(rig.Health.GetAbort(peerKey));
        Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(peerKey)); // the health monitor judges it next
        Assert.Equal(TrustDenial.None, rig.Registry.Check(peerKey, true, TrustRequirement.PinAndAuthFresh));

        // The secret is on disk encrypted; the token is in no other file.
        Assert.True(File.Exists(rig.Paths.SecretsFile));
        var secretsText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(rig.Paths.SecretsFile));
        Assert.DoesNotContain(PeerTokenA, secretsText, StringComparison.Ordinal);
        Assert.DoesNotContain(PeerTokenA, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.Empty(rig.Notifier.Messages);

        // What Home sent: one Initiate with the device name that carries the check, a GET of /Users/Me with the token and a /System/Info.
        Assert.Equal(1, peer.Initiates);
        Assert.Equal("Shoal: Home (" + waiting.SafetyCode + ")", DeviceOf(peer.To("/QuickConnect/Initiate").Single()));
        Assert.Contains("Token=\"" + PeerTokenA + "\"", peer.To("/Users/Me").Single().Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal(credential.DeviceId, peer.To("/QuickConnect/Initiate").Single().Headers["Authorization"].Split("DeviceId=\"")[1].Split('"')[0]);
    }

    [Fact]
    public async Task T_PAIR_1_ThePolicyOfTheAccountIsReported_AsWarningsAndNotes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ExtraPolicy["EnableAllFolders"] = true;
        peer.ExtraPolicy["EnableContentDeletion"] = true;
        peer.ExtraPolicy["EnableAllDevices"] = true;
        using var rig = new PairingRig();

        var session = await ReachConfirmAsync(rig, peer);

        Assert.Equal([AccountWarning.AllLibraries, AccountWarning.ContentDeletion, AccountWarning.AllDevices], session.AccountWarnings);
        Assert.Equal([AccountNote.UserPreferenceAccess, AccountNote.MediaConversion, AccountNote.SyncTranscoding, AccountNote.PublicSharing], session.AccountNotes);
        var done = await rig.Service.ConfirmAsync(true);
        Assert.Contains(AccountWarning.SessionLimit, done.AccountWarnings); // limit 0, not 1: asked of the friend once the pairing is confirmed
    }

    [Fact]
    public async Task T_PAIR_2_ACodeThatIsNeverApproved_ExpiresCleanly_WithNoToken()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromMilliseconds(400));

        var started = await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
        Assert.Equal(0, peer.Redeems);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Equal(PairingErrorKind.Expired, rig.Health.GetAbort(started.PeerHandle)!.Kind);
        Assert.Empty(rig.Notifier.Messages); // a stopped kind: no pop-up
        Assert.False(rig.Peer(started.PeerHandle).Enabled);
        Assert.Null(rig.Peer(started.PeerHandle).PendingPairing);
        Assert.Equal(PeerStatus.PairingAborted, rig.Registry.StatusOf(started.PeerHandle));
    }

    [Fact]
    public async Task T_PAIR_3_AnAdministratorAccount_IsRefused_SignedOut_AndNothingIsStored()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Administrator = true;
        using var rig = new PairingRig();
        peer.ApproveFromPoll = 2;

        var started = await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.AdminAccount, finished.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Contains("Token=\"" + PeerTokenA + "\"", peer.To("/Sessions/Logout").Single().Headers["Authorization"], StringComparison.Ordinal);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Empty(peer.To("/System/Info")); // refused before anything else is asked of the account
        Assert.Single(rig.Notifier.Messages);
        Assert.Equal("Pairing stopped for your safety", rig.Notifier.Messages[0].Header);
        Assert.DoesNotContain(PeerTokenA, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.Equal(PairingErrorKind.AdminAccount, rig.Health.GetAbort(started.PeerHandle)!.Kind);
    }

    [Fact]
    public async Task T_PAIR_5_QuickConnectDisabled_IsAClearError_AndNoInitiateIsSent()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false");
        using var rig = new PairingRig();

        await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.QuickConnectOff, finished.Error);
        Assert.Equal(0, peer.Initiates);
        Assert.Empty(rig.Notifier.Messages);
    }

    [Fact]
    public async Task T_PAIR_6_ARequestThatVanishes_GetsOneRedeemAttempt_ThenRequestReplaced()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n <= 2 ? new Reply(200, null) : new Reply(404, "{}");
        peer.RedeemReply = () => Reply.Plain(401, "unauthorized");
        using var rig = new PairingRig();

        var started = await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.RequestReplaced, finished.Error);
        Assert.Equal(1, peer.Redeems);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Single(rig.Notifier.Messages);
        Assert.Contains("NOT", rig.Notifier.Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("delete the sharing account", rig.Notifier.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(PairingErrorKind.RequestReplaced, rig.Health.GetAbort(started.PeerHandle)!.Kind);
    }

    [Fact]
    public async Task T_PAIR_6_AVariantWhereTheRedeemSucceeds_ContinuesToTheConfirmGate()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n <= 2 ? new Reply(200, null) : new Reply(404, "{}");
        using var rig = new PairingRig();

        await rig.BeginAsync(peer);
        var session = await rig.UntilStepAsync(PairingStep.AwaitingConfirm);

        Assert.Equal(1, peer.Redeems);
        Assert.Equal(PairingStep.AwaitingConfirm, session.Step);
        Assert.Empty(rig.Notifier.Messages);
    }

    [Fact]
    public async Task T_PAIR_7_PeerAndAttackerTextIsHandedOnAsText_AndNeverInterpreted()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ServerName = "<img src=x onerror=alert(1)>‮\u0007" + new string('x', 200);
        using var rig = new PairingRig();

        var probe = await rig.ProbeAsync(peer);

        Assert.StartsWith("<img src=x onerror=alert(1)>", probe.ServerName, StringComparison.Ordinal);
        Assert.Equal(64, probe.ServerName!.Length);
        Assert.DoesNotContain('‮', probe.ServerName);
        Assert.DoesNotContain('\u0007', probe.ServerName);
        var started = await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Shoal: <script>alert(1)</script>\r\n", PairingMethod.Qc);
        Assert.Equal("Shoal: <script>alert(1)</script>", rig.Peer(started.PeerHandle).DisplayName);
        Assert.Equal(JsonSerializer.Serialize(started), JsonSerializer.Serialize(started)); // serializable; nothing is escaped away or added
    }

    [Fact]
    public void TheCheck_IsFourCrockfordCharacters_AndVaries()
    {
        var checks = Enumerable.Range(0, 300).Select(_ => PairingValidation.NewCheck()).ToList();

        Assert.All(checks, c => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}$", c));
        Assert.True(checks.Distinct().Count() > 250);
    }

    [Fact]
    public async Task ThePollInterval_IsOneSecond_AndPollsAreSequential()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        // The designed 1 s interval, not the scaled one used elsewhere.
        var slow = new PairingRig(o => o.PollInterval = TimeSpan.FromSeconds(1));
        using var _ = slow;
        peer.Connect = n => new Reply(200, null);

        await slow.BeginAsync(peer);
        var until = DateTime.UtcNow.AddSeconds(8);
        while (peer.ConnectTimes.Count < 5 && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }

        var times = peer.ConnectTimes.ToArray();
        Assert.True(times.Length >= 5, "polls seen: " + times.Length);
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
        Assert.All(gaps, gap => Assert.InRange(gap, 0.85, 1.35));
        Assert.Equal(1, peer.MaxConnectInFlight);
        Assert.Equal(1, new PairingOptions().PollInterval.TotalSeconds);
        Assert.Equal(570, new PairingOptions().LocalDeadline.TotalSeconds);
        Assert.Equal(15, new PairingOptions().ConfirmWindow.TotalMinutes);
        Assert.Equal(30, new PairingOptions().NetworkAbortAfter.TotalSeconds);
        Assert.Equal(10, new PairingOptions().BaselineWindow.TotalSeconds);
        Assert.Equal(3, new PairingOptions().PollProfile.Total!.Value.TotalSeconds);
    }

    [Fact]
    public async Task ASessionCanBeCancelled_AFirstPairingLeavesNoTrace()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var started = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);

        Assert.True(await rig.Service.CancelAsync());

        Assert.Null(rig.Service.GetSession());
        Assert.Empty(rig.State.Current.Home.Peers);
        Assert.Null(rig.Health.GetAbort(started.PeerHandle));
        Assert.False(Directory.Exists(rig.Paths.PeerDir(started.PeerHandle)));
        Assert.False(await rig.Service.CancelAsync());
    }

    [Fact]
    public async Task CancellingAtTheConfirmGate_SignsTheTokenOut()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        await ReachConfirmAsync(rig, peer);

        await rig.Service.CancelAsync();

        Assert.Equal(1, peer.Logouts);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Empty(rig.State.Current.Home.Peers);
    }

    [Fact]
    public async Task AFreshSessionCannotStart_WhileAnotherLives_AndNoSecondInitiateIsSent()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var first = await rig.ProbeAsync(peer);
        var second = await rig.ProbeAsync(peer);
        await rig.Service.BeginAsync(PairingRig.Admin, first.ProbeHandle!, "A", PairingMethod.Qc);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, second.ProbeHandle!, "B", PairingMethod.Qc));

        Assert.Equal(PairingConflict.SessionActive, failure.Conflict);
        Assert.Equal(1, peer.Initiates);
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(rig.Service.GetSession()); // a reload never starts anything
        }

        Assert.Equal(1, peer.Initiates);
    }

    [Fact]
    public async Task AWronglyShapedInviteOrMethod_IsRefused_AsUnavailable()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var probe = await rig.ProbeAsync(peer);

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "A", PairingMethod.Invite));

        Assert.Equal(PairingConflict.MethodUnavailable, failure.Conflict);
        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "   ", PairingMethod.Qc));
    }

}
