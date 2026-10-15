using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The Quick Connect state machine, case by case: T-PAIR-13 (poll classification), T-PAIR-14 (validation and secrecy), T-PAIR-15 (one session,
/// one click) and T-PAIR-16 (confirm gate and durable abort), plus the session-limit mapping Pilot found (T-SEC-22).
/// </summary>
public sealed class PairingProtocolTests
{
    private static async Task<PairingSessionInfo> RunToEndAsync(PairingRig rig, FakeJellyfinPeer peer)
    {
        await rig.BeginAsync(peer);
        return await rig.UntilFinishedAsync();
    }

    private static string Device(RecordedRequest request) => WebUtility.UrlDecode(request.Headers["Authorization"].Split("Device=\"")[1].Split('"')[0]);

    // ---------------------------------------------------------------- T-PAIR-13

    [Fact]
    public async Task T_PAIR_13_b_AWrongBasePath_IsUnreachable_NeverReplaced_AndTheCodeIsNeverShown()
    {
        var replies = new Func<int, Reply>[]
        {
            _ => new Reply(404, "{}"),
            _ => new Reply(302, null, Location: "/web/index.html"),
            _ => new Reply(200, "<html><body>proxy</body></html>", "text/html"),
            _ => new Reply(200, "not json at all"),
        };
        foreach (var script in replies)
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            peer.Connect = script;
            using var rig = new PairingRig();
            var seen = new List<PairingSessionInfo>();

            await rig.BeginAsync(peer);
            var finished = await rig.UntilAsync(s =>
            {
                seen.Add(s);
                return s.State != "running";
            });

            Assert.Equal(PairingErrorKind.QuickConnectUnreachable, finished.Error);
            Assert.All(seen, s => Assert.Null(s.Code));
            Assert.All(seen, s => Assert.Null(s.SafetyCode));
            Assert.Equal(0, peer.Redeems);
            Assert.Empty(rig.Notifier.Messages);
        }
    }

    [Fact]
    public async Task T_PAIR_13_c_QuickConnectSwitchedOffMidFlight_StopsWithoutARedeem()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n < 3 ? new Reply(200, null) : Reply.Plain(401, "off");
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.QuickConnectOff, finished.Error);
        Assert.Equal(0, peer.Redeems);
    }

    [Fact]
    public async Task T_PAIR_13_d_ThreeTimeoutsInARow_SetPeerSilent_AGoodPollClearsIt_NoAbort()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n is >= 3 and <= 5 ? Reply.HangForever : new Reply(200, null);
        peer.ApproveFromPoll = 9;
        using var rig = new PairingRig(o =>
        {
            o.PollTotal = TimeSpan.FromMilliseconds(120);
            o.PollInterval = TimeSpan.FromMilliseconds(300);
        });
        var silent = new List<bool>();

        await rig.BeginAsync(peer);
        var done = await rig.UntilAsync(s =>
        {
            if (s.Step == PairingStep.AwaitingApproval)
            {
                silent.Add(s.PeerSilent);
            }

            return s.Step == PairingStep.AwaitingConfirm;
        });

        Assert.Contains(true, silent);
        Assert.False(done.PeerSilent);
        Assert.Null(done.Error);
        Assert.True(silent.IndexOf(true) > 0, "not silent before the third miss");
    }

    [Fact]
    public async Task T_PAIR_13_e_ThirtySecondsWithoutAGoodPoll_IsANetworkError()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n <= 1 ? new Reply(200, null) : Reply.HangForever;
        using var rig = new PairingRig(o =>
        {
            o.PollTotal = TimeSpan.FromMilliseconds(100);
            o.NetworkAbortAfter = TimeSpan.FromMilliseconds(700);
        });

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.Network, finished.Error);
        Assert.Empty(rig.Notifier.Messages);
        Assert.Equal(0, peer.Redeems);
    }

    [Fact]
    public async Task T_PAIR_13_f_AnotherCodeInAPoll_IsABadPeerResponse_NoRedeem()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n < 3
            ? new Reply(200, null)
            : Reply.Json(new { Authenticated = true, Secret = FakeJellyfinPeer.DefaultSecret, Code = "000001" });
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Equal(0, peer.Redeems);
        Assert.Single(rig.Notifier.Messages);
    }

    [Fact]
    public async Task T_PAIR_13_f_AnotherSecretInAPoll_IsABadPeerResponse()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n < 3
            ? new Reply(200, null)
            : Reply.Json(new { Authenticated = true, Secret = new string('C', 64), Code = peer.Code });
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Equal(0, peer.Redeems);
    }

    [Fact]
    public async Task T_PAIR_13_h_TheLocalDeadline_IsANaturalExpiry()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromMilliseconds(300));

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
        Assert.Equal(0, peer.Redeems);
        Assert.Equal(1, peer.MaxConnectInFlight);
    }

    [Fact]
    public async Task T_PAIR_13_ANotFoundAfterTheDeadline_IsExpired_NotReplaced()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Connect = n => n < 3 ? new Reply(200, null) : new Reply(404, "{}");
        peer.RedeemReply = () => Reply.Plain(404);
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromMilliseconds(1));

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
        Assert.Equal(0, peer.Redeems);
    }

    [Fact]
    public async Task T_PAIR_13_AServerThatCannotBeReached_IsQuickConnectUnreachable()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var probe = await rig.ProbeAsync(peer);
        await peer.DisposeAsync();

        await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Gone", PairingMethod.Qc);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.QuickConnectUnreachable, finished.Error);
    }

    [Fact]
    public async Task T_PAIR_13_APeerThatAnswersAsAnotherServer_StopsBeforeInitiate()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var probe = await rig.ProbeAsync(peer);
        peer.ServerId = "ffffffffffffffffffffffffffffff00"; // a reset server behind the same address

        await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops", PairingMethod.Qc);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.ServerChanged, finished.Error);
        Assert.Equal(0, peer.Initiates);
        Assert.Single(rig.Notifier.Messages);
    }

    // ---------------------------------------------------------------- T-PAIR-14

    public static TheoryData<string> BadCodes => new() { "12345a", "1234567", "12345", string.Empty, "１２３４５６" };

    public static TheoryData<string> BadSecrets => new()
    {
        new string('A', 63),
        new string('A', 62) + "G",
        new string('A', 65),
        string.Empty
    };

    [Theory]
    [MemberData(nameof(BadCodes))]
    public async Task T_PAIR_14_AMalformedCodeFromInitiate_IsABadPeerResponse_AndIsNeverShown(string code)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.InitiateReply = () => Reply.Json(new { Authenticated = false, Secret = FakeJellyfinPeer.DefaultSecret, Code = code });
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Equal(0, peer.ConnectCalls);
        Assert.DoesNotContain("12345a", rig.Log.All, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadSecrets))]
    public async Task T_PAIR_14_AMalformedSecretFromInitiate_IsABadPeerResponse_AndNeverLogged(string secret)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.InitiateReply = () => Reply.Json(new { Authenticated = false, Secret = secret, Code = "739481" });
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Equal(0, peer.ConnectCalls);
        Assert.DoesNotContain(secret.Length > 5 ? secret : "no-such-text", rig.Log.All, StringComparison.Ordinal);
        Assert.Empty(peer.To("/QuickConnect/Connect")); // the malformed secret never goes into a URL
    }

    [Fact]
    public async Task T_PAIR_14_ARealUpperCaseSecret_IsAccepted()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); // upper-case, as core emits it
        Assert.Matches("^[0-9A-F]{64}$", peer.Secret);
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        await rig.BeginAsync(peer);
        var session = await rig.UntilStepAsync(PairingStep.AwaitingConfirm);

        Assert.Null(session.Error);
        Assert.Equal(peer.Secret, peer.To("/QuickConnect/Connect").First().Query.Split("secret=")[1]);
    }

    [Theory]
    [InlineData("9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f0")] // 33 hex
    [InlineData("9F2C4D6E8A0B1C3D5E7F9A0B2C4D6E8F")] // upper case
    [InlineData("9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e\"f")]
    [InlineData("9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e,f")]
    [InlineData("9f2c4d6e-8a0b-1c3d-5e7f-9a0b2c4d6e8f")]
    [InlineData("")]
    public async Task T_PAIR_14_AMalformedToken_IsABadPeerResponse_AndNeverAppearsAnywhere(string token)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Token = token;
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Empty(peer.To("/Users/Me")); // it never reached a header
        Assert.Equal(0, peer.Logouts);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.True(token.Length == 0 || !rig.Log.All.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task T_PAIR_14_AUserIdThatDiffersFromUsersMe_IsABadPeerResponse_AndTheTokenIsSignedOut()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.RedeemReply = () => Reply.Json(new { AccessToken = peer.Token, ServerId = peer.ServerId, User = new { Id = Guid.NewGuid().ToString("N") } });
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Empty(rig.Secrets.PeerKeys!);
    }

    [Fact]
    public async Task T_PAIR_14_AServerIdInTheAuthenticationResult_ThatIsNotThePin_IsABadPeerResponse()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.RedeemReply = () => Reply.Json(new { AccessToken = peer.Token, ServerId = "ffffffffffffffffffffffffffffff00", User = new { Id = peer.UserId.ToString("N") } });
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
        Assert.Empty(peer.To("/Users/Me"));
    }

    [Fact]
    public async Task T_PAIR_14_ASystemInfoWithAnotherId_IsServerChanged_AndTheTokenIsSignedOut()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.SystemInfo = Reply.Json(new { Id = "ffffffffffffffffffffffffffffff00", ServerName = "Imposter" });
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.ServerChanged, finished.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Single(rig.Notifier.Messages);
    }

    [Fact]
    public async Task T_PAIR_14_AnOddUsersMe_IsRefused()
    {
        foreach (var reply in new[] { Reply.Json(new { Id = Guid.NewGuid().ToString("N"), Policy = new { IsAdministrator = false } }), Reply.Json(new { Id = "x", Policy = new { } }), Reply.Plain(200, "<html>"), Reply.Plain(403) })
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            peer.MeReply = () => reply;
            peer.ApproveFromPoll = 2;
            using var rig = new PairingRig();

            var finished = await RunToEndAsync(rig, peer);

            Assert.Equal(PairingErrorKind.BadPeerResponse, finished.Error);
            Assert.Empty(rig.Secrets.PeerKeys!);
            Assert.Equal(1, peer.Logouts);
        }
    }

    [Fact]
    public async Task T_PAIR_14_TheServerNameWithMarkupAndSpecialCharacters_ReachesThePeerByteExact_AfterTheCoreDecode()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(homeName: "A+B 100% \"x\", y");

        var started = await rig.BeginAsync(peer);
        var waiting = await rig.UntilStepAsync(PairingStep.AwaitingApproval);

        Assert.Equal("Shoal: A+B 100% x y (" + waiting.SafetyCode + ")", Device(peer.To("/QuickConnect/Initiate").Single()));
        Assert.All(peer.Requests.Where(r => r.Path.StartsWith("/QuickConnect", StringComparison.Ordinal)), r => Assert.Equal(Device(r), "Shoal: A+B 100% x y (" + waiting.SafetyCode + ")"));
        Assert.NotEmpty(started.PeerHandle);
    }

    [Fact]
    public async Task T_PAIR_14_NoSecretCodeOrTokenIsInAnyLogLine_AnyFile_OrTheSessionJson()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Code = "428571";
        using var rig = new PairingRig();
        peer.ApproveFromPoll = 3;
        await rig.BeginAsync(peer);
        var session = await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        var json = JsonSerializer.Serialize(session);
        await rig.Service.ConfirmAsync(true);

        // The session JSON shows the code and the check (the admin reads them out), and nothing else secret.
        Assert.Contains("428571", json, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Token, json, StringComparison.Ordinal);
        foreach (var secret in new[] { peer.Secret, peer.Token, "428571" })
        {
            Assert.DoesNotContain(secret, rig.Log.All, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rig.AllFilesAsText(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("secret=", rig.Log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/QuickConnect/Connect", rig.Log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(peer.Secret, JsonSerializer.Serialize(rig.Service.GetSession()), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the session limit Pilot found (T-SEC-22)

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(401)]
    public async Task AnApprovedLookingFlowThatEndsInABare403Or404_IsLikelySessionLimit_WithoutLookingAtText(int status)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.RedeemReply = () => Reply.Plain(status, "Error processing request.");
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.LikelySessionLimit, finished.Error);
        Assert.Equal(1, peer.Redeems);
        Assert.Empty(rig.Notifier.Messages); // an ordinary outcome, not a safety kind
        Assert.False(PairingErrors.IsSafety(PairingErrorKind.LikelySessionLimit));
    }

    [Fact]
    public async Task ARedeemThatFailsWithAServerError_IsAnetworkError_NotASessionLimit()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.RedeemReply = () => Reply.Plain(503, "maximum number of sessions"); // the words mean nothing to Home
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.Network, finished.Error);
    }

    // ---------------------------------------------------------------- T-PAIR-15

    [Fact]
    public async Task T_PAIR_15_AProbeHandle_IsSingleUse_AndExpiresAfterTenMinutes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var clock = new ManualTimeProvider();
        using var rig = new PairingRig(time: clock);
        var used = await rig.ProbeAsync(peer);
        var old = await rig.ProbeAsync(peer);
        await rig.Service.BeginAsync(PairingRig.Admin, used.ProbeHandle!, "A", PairingMethod.Qc);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);
        await rig.Service.CancelAsync();

        var reuse = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, used.ProbeHandle!, "A", PairingMethod.Qc));
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var expired = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, old.ProbeHandle!, "A", PairingMethod.Qc));
        var unknown = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, "nonsense", "A", PairingMethod.Qc));

        Assert.Equal(PairingConflict.ProbeExpired, reuse.Conflict);
        Assert.Equal(PairingConflict.ProbeExpired, expired.Conflict);
        Assert.Equal(PairingConflict.ProbeExpired, unknown.Conflict);
        Assert.Equal(1, peer.Initiates);
    }

    [Fact]
    public async Task T_PAIR_15_TheSixthStartForOneServer_IsRateLimited_OnTheServer()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false"); // each session ends at once
        var clock = new ManualTimeProvider();
        using var rig = new PairingRig(time: clock);

        for (var i = 0; i < 5; i++)
        {
            await rig.BeginAsync(peer, "A" + i);
            await rig.UntilFinishedAsync();
        }

        var probe = await rig.ProbeAsync(peer);
        var limited = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "A6", PairingMethod.Qc));

        Assert.Equal(PairingConflict.RateLimited, limited.Conflict);
        Assert.InRange(limited.RetryAfterSeconds, 1, 600);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var again = await rig.Service.BeginAsync(PairingRig.Admin, (await rig.ProbeAsync(peer)).ProbeHandle!, "A7", PairingMethod.Qc);
        Assert.NotEmpty(again.PeerHandle);
    }

    [Fact]
    public async Task T_PAIR_15_TheAddressOfThePeerCannotChangeWhileASessionLives()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var started = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);
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
        var probe = await rig.ProbeAsync(peer);
        File.WriteAllText(rig.Paths.SecretsFile, "{ not a secrets file");
        using var broken = new SecretStore(rig.Paths, rig.State, rig.Log, name => name == SecretStore.KeyVariable ? rig.Key : null, null, null, null);
        var service = new PairingService(rig.State, broken, rig.Client, rig.Registry, rig.Health, rig.Home, rig.Notifier, rig.Paths, rig.Log, rig.Time, rig.Options);
        var probeResult = await service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl });

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => service.BeginAsync(PairingRig.Admin, probeResult.ProbeHandle!, "A", PairingMethod.Qc));

        Assert.Equal(PairingConflict.SecretsUnreadable, failure.Conflict);
        Assert.Equal(0, peer.Initiates);
        Assert.NotNull(probe.ProbeHandle);
    }

    // ---------------------------------------------------------------- T-PAIR-16

    [Fact]
    public async Task T_PAIR_16_a_ConfirmIsRefused_InEveryStateExceptAwaitingConfirm()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();

        // No session at all.
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.ConfirmAsync(true))).Conflict);

        // Waiting for approval.
        await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.ConfirmAsync(true))).Conflict);
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.ConfirmAsync(false))).Conflict);
        await rig.Service.CancelAsync();

        // Getting the code (the baseline never succeeds).
        await using var slow = await FakeJellyfinPeer.StartAsync();
        slow.Connect = _ => Reply.HangForever;
        using var slowRig = new PairingRig(o => o.PollTotal = TimeSpan.FromMilliseconds(150));
        await slowRig.BeginAsync(slow);
        await slowRig.UntilStepAsync(PairingStep.GettingCode);
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => slowRig.Service.ConfirmAsync(true))).Conflict);
        await slowRig.UntilFinishedAsync();

        // After an abort, and after a confirmed pairing.
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => slowRig.Service.ConfirmAsync(true))).Conflict);
        await using var good = await FakeJellyfinPeer.StartAsync();
        good.ApproveFromPoll = 2;
        using var goodRig = new PairingRig();
        await goodRig.BeginAsync(good);
        await goodRig.UntilStepAsync(PairingStep.AwaitingConfirm);
        await goodRig.Service.ConfirmAsync(true);
        Assert.Equal(PairingConflict.NotAwaitingConfirm, (await Assert.ThrowsAsync<PairingConflictException>(() => goodRig.Service.ConfirmAsync(true))).Conflict);
    }

    [Fact]
    public async Task T_PAIR_16_c_TheyDontMatch_SignsTheTokenOut_AndStoresNothing()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        peer.ApproveFromPoll = 2;
        await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);

        var result = await rig.Service.ConfirmAsync(match: false);

        Assert.Equal(PairingErrorKind.CodesDidNotMatch, result.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Contains("Token=\"" + peer.Token + "\"", peer.To("/Sessions/Logout").Single().Headers["Authorization"], StringComparison.Ordinal);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.False(File.Exists(rig.Paths.SecretsFile));
        Assert.DoesNotContain(peer.Token, rig.AllFilesAsText(), StringComparison.Ordinal);
        Assert.Single(rig.Notifier.Messages);
        Assert.Contains("delete the sharing account", rig.Notifier.Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_PAIR_16_d_TheConfirmWindow_EndsWithALogoutAndExpired()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(o => o.ConfirmWindow = TimeSpan.FromMilliseconds(300));
        peer.ApproveFromPoll = 2;

        var finished = await RunToEndAsync(rig, peer);

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
        Assert.Equal(1, peer.Logouts);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Empty(rig.Notifier.Messages);
    }

    [Theory]
    [InlineData(PairingPhase.BeforeApproval)]
    [InlineData(PairingPhase.AwaitingConfirm)]
    public async Task T_PAIR_16_e_ARestart_TurnsAPairingInProgressIntoInterruptedWithItsPhase(PairingPhase phase)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        if (phase == PairingPhase.AwaitingConfirm)
        {
            peer.ApproveFromPoll = 2;
        }

        var started = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(phase == PairingPhase.BeforeApproval ? PairingStep.AwaitingApproval : PairingStep.AwaitingConfirm);
        Assert.Equal(phase, rig.Peer(started.PeerHandle).PendingPairing!.Phase);

        // Home stops (the pending record stays on disk), starts again and looks.
        await rig.Service.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        var restarted = rig.Restart();
        await restarted.StartAsync(CancellationToken.None);

        var abort = rig.Health.GetAbort(started.PeerHandle)!;
        Assert.Equal(PairingErrorKind.Interrupted, abort.Kind);
        Assert.Equal(phase, abort.Phase);
        Assert.Null(rig.Peer(started.PeerHandle).PendingPairing);
        Assert.False(rig.Peer(started.PeerHandle).Enabled);
        Assert.Equal(PeerStatus.PairingAborted, rig.Registry.StatusOf(started.PeerHandle));
        Assert.Empty(rig.Notifier.Messages);
        Assert.Empty(rig.Secrets.PeerKeys!);
        Assert.Null(restarted.GetSession());

        // The abort survives a second restart, and nothing sends another Initiate.
        var again = rig.Restart();
        await again.StartAsync(CancellationToken.None);
        Assert.Equal(PairingErrorKind.Interrupted, rig.Health.GetAbort(started.PeerHandle)!.Kind);
        Assert.Equal(1, peer.Initiates);
    }

    [Fact]
    public async Task T_PAIR_16_f_AnAbort_SurvivesAReloadAndARestart()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false");
        using var rig = new PairingRig();
        var started = await rig.BeginAsync(peer);
        await rig.UntilFinishedAsync();

        var restarted = rig.Restart();
        await restarted.StartAsync(CancellationToken.None);

        Assert.Equal(PairingErrorKind.QuickConnectOff, rig.Health.GetAbort(started.PeerHandle)!.Kind);
        Assert.Equal(PeerStatus.PairingAborted, rig.Registry.StatusOf(started.PeerHandle));
        var file = File.ReadAllText(Path.Combine(rig.Paths.PeerDir(started.PeerHandle), "health.json"));
        Assert.Contains("quickConnectOff", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_PAIR_16_g_OnlyTheFiveSafetyKinds_SendAPopUp()
    {
        var safety = new Dictionary<PairingErrorKind, Func<FakeJellyfinPeer, Task>>
        {
            [PairingErrorKind.RequestReplaced] = p => { p.Connect = n => n < 3 ? new Reply(200, null) : new Reply(404, "{}"); p.RedeemReply = () => Reply.Plain(401); return Task.CompletedTask; },
            [PairingErrorKind.ServerChanged] = p => { p.SystemInfo = Reply.Json(new { Id = "ffffffffffffffffffffffffffffff00" }); p.ApproveFromPoll = 2; return Task.CompletedTask; },
            [PairingErrorKind.AdminAccount] = p => { p.Administrator = true; p.ApproveFromPoll = 2; return Task.CompletedTask; },
            [PairingErrorKind.BadPeerResponse] = p => { p.InitiateReply = () => Reply.Json(new { Secret = "x", Code = "1" }); return Task.CompletedTask; },
        };
        var stopped = new Dictionary<PairingErrorKind, Func<FakeJellyfinPeer, Task>>
        {
            [PairingErrorKind.QuickConnectOff] = p => { p.Enabled = new Reply(200, "false"); return Task.CompletedTask; },
            [PairingErrorKind.QuickConnectUnreachable] = p => { p.Connect = _ => new Reply(404, "{}"); return Task.CompletedTask; },
            [PairingErrorKind.LikelySessionLimit] = p => { p.RedeemReply = () => Reply.Plain(403); p.ApproveFromPoll = 2; return Task.CompletedTask; },
        };

        foreach (var (kind, arrange) in safety)
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            await arrange(peer);
            using var rig = new PairingRig();

            var finished = await RunToEndAsync(rig, peer);

            Assert.Equal(kind, finished.Error);
            var message = Assert.Single(rig.Notifier.Messages);
            Assert.Equal("Pairing stopped for your safety", message.Header);
            Assert.True(PairingErrors.IsSafety(kind));
            Assert.DoesNotContain(peer.Token, message.Text, StringComparison.Ordinal);
        }

        foreach (var (kind, arrange) in stopped)
        {
            await using var peer = await FakeJellyfinPeer.StartAsync();
            await arrange(peer);
            using var rig = new PairingRig();

            var finished = await RunToEndAsync(rig, peer);

            Assert.Equal(kind, finished.Error);
            Assert.Empty(rig.Notifier.Messages);
            Assert.False(PairingErrors.IsSafety(kind));
        }

        Assert.Equal(5, Enum.GetValues<PairingErrorKind>().Count(PairingErrors.IsSafety));
    }

    [Fact]
    public async Task T_PAIR_16_h_Dismiss_RemovesAFirstPairing_AndRestoresARePairedPeer()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.Enabled = new Reply(200, "false");
        using var rig = new PairingRig();
        var first = await rig.BeginAsync(peer);
        await rig.UntilFinishedAsync();

        // Nothing to dismiss for a peer without an abort; a first pairing's record is removed.
        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync("00000000000000000000000000000000"));
        var removed = await rig.Service.DismissPairingAsync(first.PeerHandle);
        Assert.True(removed.Removed);
        Assert.Empty(rig.State.Current.Home.Peers);
        Assert.False(Directory.Exists(rig.Paths.PeerDir(first.PeerHandle)));
        Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(first.PeerHandle));
        await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync(first.PeerHandle));

        // A paired peer, then a re-pair that is stopped: the old secret and state are untouched, and Dismiss brings it back.
        peer.Enabled = new Reply(200, "true");
        peer.ApproveFromPoll = 2;
        var paired = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        await rig.Service.ConfirmAsync(true);
        var oldToken = rig.Secrets.TryGet(paired.PeerHandle)!.Token;
        peer.Enabled = new Reply(200, "false");
        var again = await rig.BeginAsync(peer, "Hops", paired.PeerHandle);
        Assert.True(again.Paired);
        Assert.Equal(paired.PeerHandle, again.PeerHandle);
        var stopped = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.QuickConnectOff, stopped.Error);
        Assert.Equal(oldToken, rig.Secrets.TryGet(paired.PeerHandle)!.Token);
        Assert.True(rig.Peer(paired.PeerHandle).Enabled);
        Assert.NotNull(rig.Peer(paired.PeerHandle).Pairing);
        Assert.Equal(PeerStatus.Offline, rig.Registry.StatusOf(paired.PeerHandle));
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
        using var rig = new PairingRig();
        var started = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingApproval);

        var failure = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.DismissPairingAsync(started.PeerHandle));

        Assert.Equal(PairingConflict.NothingToDismiss, failure.Conflict);
    }

    [Fact]
    public async Task ARePair_KeepsTheOldSecret_UntilConfirmed_ThenReplacesIt()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        peer.ApproveFromPoll = 2;
        var first = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        await rig.Service.ConfirmAsync(true);
        var oldToken = peer.Token;
        var oldDevice = rig.Secrets.TryGet(first.PeerHandle)!.DeviceId;
        var newToken = "1a2b3c4d5e6f70819a0b1c2d3e4f5061";
        peer.Token = newToken;
        peer.Code = "112233";
        peer.ApproveFromPoll = 2;
        peer.ApproveFromPoll = peer.ConnectCalls + 2;
        var second = await rig.BeginAsync(peer, "Hops", first.PeerHandle);
        var waiting = await rig.UntilStepAsync(PairingStep.AwaitingConfirm);

        // While the new token waits for the confirm gate, the old one is still the stored one.
        Assert.True(second.Paired);
        Assert.Equal(first.PeerHandle, second.PeerHandle);
        Assert.Equal(oldToken, rig.Secrets.TryGet(first.PeerHandle)!.Token);
        Assert.True(rig.Peer(first.PeerHandle).Enabled);
        Assert.Equal(PeerStatus.Pairing, rig.Registry.StatusOf(first.PeerHandle));
        Assert.Single(rig.State.Current.Home.Peers);
        Assert.Equal(2, peer.Initiates);

        await rig.Service.ConfirmAsync(true);

        Assert.Equal(newToken, rig.Secrets.TryGet(first.PeerHandle)!.Token);
        Assert.Equal(oldDevice, rig.Secrets.TryGet(first.PeerHandle)!.DeviceId); // same DeviceId: the old token dies at the new authentication
        Assert.Equal(waiting.SafetyCode, rig.Peer(first.PeerHandle).Pairing!.CheckString);
        Assert.DoesNotContain(newToken, rig.AllFilesAsText(), StringComparison.Ordinal);
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
        Assert.Equal(0, peer.Initiates);
        Assert.Empty(peer.To("/QuickConnect/Enabled")); // a probe asks nothing about Quick Connect

        using var strict = new PairingRig(o => o.RealPolicy = true);
        var refused = await strict.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet });
        Assert.False(refused.Ok);
        Assert.Equal(AddressProblem.Blocked, refused.AddressProblem);
        Assert.Null(refused.ProbeHandle);
        Assert.Single(peer.To("/System/Info/Public"));
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

    // ---------------------------------------------------------------- W6 L-1, L-2, L-3

    private static async Task<string> PairOnceAsync(PairingRig rig, FakeJellyfinPeer peer)
    {
        peer.ApproveFromPoll = 2;
        var first = await rig.BeginAsync(peer);
        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        await rig.Service.ConfirmAsync(true);
        peer.ApproveFromPoll = int.MaxValue;
        return first.PeerHandle;
    }

    [Fact]
    public async Task T_PAIR_13_i1_ARePairThatRunsOutUnapproved_WhileTheAccountShowsALimit_IsLikelySessionLimit_BeforeApproval()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromSeconds(1));
        var handle = await PairOnceAsync(rig, peer);
        peer.ExtraPolicy["MaxActiveSessions"] = 1; // the friend's account now has a limit; the fake never approves the new code

        await rig.BeginAsync(peer, "Hops", handle);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.LikelySessionLimit, finished.Error);
        Assert.Equal(PairingPhase.BeforeApproval, rig.Health.GetAbort(handle)!.Phase);
        Assert.Empty(rig.Notifier.Messages); // a stopped kind, no pop-up
    }

    [Fact]
    public async Task T_PAIR_13_i2_AFirstPairingThatRunsOut_IsExpired_EvenWithALimit()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ExtraPolicy["MaxActiveSessions"] = 1;
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromMilliseconds(500));

        var started = await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
    }

    [Fact]
    public async Task T_PAIR_13_i1b_ARePairWithNoLimit_OrAnUnreadableAccount_IsExpired()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig(o => o.LocalDeadline = TimeSpan.FromSeconds(1));
        var handle = await PairOnceAsync(rig, peer);

        await rig.BeginAsync(peer, "Hops", handle); // MaxActiveSessions stays 0
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.Expired, finished.Error);
    }

    [Fact]
    public async Task T_PAIR_13_i3_ARedeemRefusedAfterAnApproval_IsLikelySessionLimit_AfterApproval()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.RedeemReply = () => Reply.Plain(403, "Error processing request.");
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();

        var started = await rig.BeginAsync(peer);
        var finished = await rig.UntilFinishedAsync();

        Assert.Equal(PairingErrorKind.LikelySessionLimit, finished.Error);
        Assert.Equal(PairingPhase.AfterApproval, rig.Health.GetAbort(started.PeerHandle)!.Phase);
    }

    [Fact]
    public async Task W6_L3_AProbeIsRefused_WhenThePeersAddressChangedAfterIt()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var rig = new PairingRig();
        var handle = await PairOnceAsync(rig, peer);
        var probe = await rig.ProbeAsync(peer, handle);

        await rig.State.UpdateAsync(s => s.Home.Peers.Single(p => p.PeerKey == handle).BaseUrl = "http://100.64.0.9:8096");
        var refused = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops", PairingMethod.Qc));

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
