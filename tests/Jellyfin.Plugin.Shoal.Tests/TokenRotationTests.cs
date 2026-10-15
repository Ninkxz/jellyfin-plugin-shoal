using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K26 (DESIGN v2.6.1): Home's side of token rotation against a real HTTP fake peer. T-ROT-1 (Home part), T-ROT-5, T-ROT-6, T-ROT-7, and the scan for tokens
/// in the admin answers (T-SEC-5).
/// </summary>
public sealed class TokenRotationTests
{
    private const string DeviceId = "shoal-a1b2c3d4e5f6-4b0c1d2e";
    private const string OldToken = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";

    // --- T-ROT-1 (Home part)

    [Fact]
    public async Task T_ROT_1_AHappyRotation_UsesTheNewTokenAtOnce_StoresItAndReportsWhen()
    {
        await using var rig = await RotationRig.CreateAsync();
        var newToken = rig.Behavior.Token;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.None, result.Error);
        Assert.NotNull(result.RotatedUtc);
        Assert.Equal(newToken, rig.Secrets!.Stored?.Token); // on disk
        await rig.WaitForStoredAsync();
        Assert.Equal(newToken, rig.Endpoints.Find(rig.PeerKey)!.Token); // and active in memory
        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime, rig.State.Current.Home.Peers[0].LastRotatedUtc);
        Assert.Equal(PeerStatus.Online, rig.Registry.StatusOf(rig.PeerKey));
        Assert.False(rig.Registry.IsRotating(rig.PeerKey));
        Assert.Null(rig.Health.GetRotationError(rig.PeerKey));
        // The checks of 4.2 step 4 ran with the new token: Users/Me and System/Info carried it, the old token did not.
        var checks = rig.Peer.Requests.Where(static r => r.Path is "/Users/Me" or "/System/Info").ToList();
        Assert.Equal(2, checks.Count);
        Assert.All(checks, r => Assert.Contains(newToken, r.Headers["Authorization"], StringComparison.Ordinal));
        Assert.Single(rig.Peer.Requests, static r => r.Path == "/Shoal/Share/Rotate");
        var call = rig.Peer.Requests.Single(static r => r.Path == "/Shoal/Share/Rotate");
        Assert.Contains(OldToken, call.Headers["Authorization"], StringComparison.Ordinal); // the old token asked for the new one
        Assert.Equal("{\"v\":1}", System.Text.Encoding.UTF8.GetString(call.Body));
        Assert.DoesNotContain(newToken, rig.Log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OldToken, rig.Log.All, StringComparison.OrdinalIgnoreCase);
    }

    // --- T-ROT-5: refusals

    [Fact]
    public async Task T_ROT_5_AQuickConnectPairing_CannotRotate()
    {
        await using var rig = await RotationRig.CreateAsync(PairingMethod.Qc);

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.NotSupported, result.Error);
        Assert.Equal(0, rig.Behavior.Calls);
    }

    [Fact]
    public async Task T_ROT_5_AServerThatIsNotOnline_OrHasALapsedTrustWindow_OrAnOpenCircuit_IsRefusedWithoutACall()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Registry.SetStatus(rig.PeerKey, PeerStatus.Offline);
        Assert.Equal(RotateError.NotOnline, (await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default)).Error);

        rig.Registry.SetStatus(rig.PeerKey, PeerStatus.Online);
        rig.Registry.RecordPinVerified(rig.PeerKey);
        rig.Registry.RecordAuthVerified(rig.PeerKey);
        rig.Time.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(RotateError.NotOnline, (await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default)).Error);

        rig.GoOnline();
        var breaker = rig.Client.BreakerOf(rig.Endpoints.Find(rig.PeerKey)!);
        for (var i = 0; i < CircuitBreaker.OpenAfterFailures; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(RotateError.NotOnline, (await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default)).Error);
        Assert.Equal(0, rig.Behavior.Calls);
    }

    [Fact]
    public async Task T_ROT_5_AStreamThatIsRunning_RefusesTheRotation()
    {
        await using var rig = await RotationRig.CreateAsync();
        using var lease = rig.Slots.TryOpenStatic(Guid.NewGuid(), Guid.NewGuid(), rig.PeerKey, 4, out _);
        Assert.NotNull(lease);

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.StreamsActive, result.Error);
        Assert.Equal(0, rig.Behavior.Calls);
        Assert.False(rig.Registry.IsRotating(rig.PeerKey)); // a refusal leaves no mark
        Assert.Equal(OldToken, rig.Endpoints.Find(rig.PeerKey)!.Token);
    }

    [Fact]
    public async Task T_ROT_5_APairingInProgress_OrAnotherRotation_IsBusy()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Gate.Live = true;
        Assert.Equal(RotateError.Busy, (await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default)).Error);
        rig.Gate.Live = false;

        rig.Behavior.Hold = new TaskCompletionSource();
        var running = rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);
        await rig.Behavior.Entered;

        Assert.Equal(RotateError.Busy, (await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default)).Error);

        rig.Behavior.Hold.SetResult();
        Assert.Equal(RotateError.None, (await running).Error);
    }

    [Theory]
    [InlineData("{\"v\":1,\"token\":\"abc\",\"deviceId\":\"" + DeviceId + "\"}", "short token")]
    [InlineData("{\"v\":1,\"token\":\"9F2C4D6E8A0B1C3D5E7F9A0B2C4D6E8F\",\"deviceId\":\"" + DeviceId + "\"}", "upper case token")]
    [InlineData("{\"v\":1,\"token\":\"00112233445566778899aabbccddeeff\",\"deviceId\":\"shoal-ffffffffffff-ffffffff\"}", "another DeviceId")]
    [InlineData("{\"v\":2,\"token\":\"00112233445566778899aabbccddeeff\",\"deviceId\":\"" + DeviceId + "\"}", "another version")]
    [InlineData("{\"v\":1,\"deviceId\":\"" + DeviceId + "\"}", "no token")]
    [InlineData("[]", "not an object")]
    public async Task T_ROT_5_AnAnswerHomeDoesNotAccept_IsABadResponse_AndTheServerMustBePairedAgain(string body, string what)
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Behavior.Body = body;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.True(result.Error == RotateError.BadResponse, what);
        Assert.Equal(PeerStatus.AuthFailed, rig.Registry.StatusOf(rig.PeerKey));
        Assert.Equal("badResponse", rig.Health.GetRotationError(rig.PeerKey)?.Kind);
        Assert.Equal(0, rig.Secrets!.Puts); // nothing odd was stored
    }

    [Fact]
    public async Task T_ROT_5_ANewTokenThatBelongsToAnAdministrator_IsRefused_AndSignedOut()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Peer.Administrator = true;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.BadResponse, result.Error);
        Assert.Contains(rig.Peer.Requests, static r => r.Path == "/Sessions/Logout");
        Assert.Equal(0, rig.Secrets!.Puts);
        Assert.Equal(OldToken, rig.Endpoints.Find(rig.PeerKey)!.Token); // the in-memory token was dropped again
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(429)]
    public async Task T_ROT_5_APeerThatRefuses_IsRejected_AndNothingChanges(int status)
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Behavior.Status = status;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.Rejected, result.Error);
        Assert.Equal(PeerStatus.Online, rig.Registry.StatusOf(rig.PeerKey));
        Assert.Equal(OldToken, rig.Endpoints.Find(rig.PeerKey)!.Token);
        Assert.Equal("rejected", rig.Health.GetRotationError(rig.PeerKey)?.Kind);
        Assert.Equal(0, rig.Secrets!.Puts);
    }

    [Fact]
    public async Task T_ROT_5_APeerThatCouldNotMintANewToken_LeavesHomeWithADeadToken_AuthFailed()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Behavior.Status = 500;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.LostResponse, result.Error);
        Assert.Equal(PeerStatus.AuthFailed, rig.Registry.StatusOf(rig.PeerKey));
        Assert.Equal("lostResponse", rig.Health.GetRotationError(rig.PeerKey)?.Kind);
    }

    [Fact]
    public async Task T_ROT_5_ALostResponse_EndsAsAuthFailed_WhenTheOldTokenIsDead_AndAsNotOnline_WhenItIsStillAlive()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Behavior.AbortConnection = true;
        rig.Behavior.OnReceived = () => rig.Peer.MeReply = () => new Reply(401); // the Peer rotated, the answer never arrives

        var lost = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.LostResponse, lost.Error);
        Assert.Equal(PeerStatus.AuthFailed, rig.Registry.StatusOf(rig.PeerKey));
        Assert.Equal("lostResponse", rig.Health.GetRotationError(rig.PeerKey)?.Kind);

        await using var alive = await RotationRig.CreateAsync();
        alive.Behavior.AbortConnection = true; // the connection broke before the Peer did anything: the old token still works

        var unchanged = await alive.Rotation.RotateAsync(alive.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.NotOnline, unchanged.Error);
        Assert.Equal(PeerStatus.Online, alive.Registry.StatusOf(alive.PeerKey));
        Assert.Null(alive.Health.GetRotationError(alive.PeerKey));
    }

    // --- T-ROT-6: the rotating window

    [Fact]
    public async Task T_ROT_6_WhileARotationRuns_TokenCallsWaitLikeAnOpenCircuit_AndA401DoesNotSetAuthFailed()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Behavior.Hold = new TaskCompletionSource();
        var running = rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);
        await rig.Behavior.Entered;
        var endpoint = rig.Endpoints.Find(rig.PeerKey)!;

        var play = await Assert.ThrowsAsync<PeerCallException>(() => rig.Client.SendAsync(endpoint, PeerRequest.Post("/Items/x/PlaybackInfo", [123]), PeerCallProfile.PlaybackInfo, default));
        var sync = await Assert.ThrowsAsync<PeerCallException>(() => rig.Client.SendAsync(endpoint, PeerRequest.Get("/Items"), PeerCallProfile.Items, default));

        Assert.Equal(PeerFailureKind.CircuitOpen, play.Kind); // the callers already answer an open circuit without an offline notice
        Assert.Equal(PeerFailureKind.CircuitOpen, sync.Kind);
        Assert.True(rig.Registry.IsRotating(rig.PeerKey));
        var before = rig.Peer.Requests.Count(static r => r.Path == "/Users/Me");

        // The health tick would read the old token: it skips.
        var monitor = new HealthMonitor(rig.State, rig.Endpoints, rig.Client, new PeerTrust(rig.Client, rig.Registry, rig.Time), rig.Registry, new NoAdmins(), rig.Time, NullLogger<HealthMonitor>.Instance);
        Assert.Equal(PeerStatus.Online, await monitor.CheckAsync(rig.PeerKey, default));
        Assert.Equal(before, rig.Peer.Requests.Count(static r => r.Path == "/Users/Me"));

        // A 401 seen in the window is not a verdict.
        rig.Peer.MeReply = () => new Reply(401);
        Assert.Equal(AuthOutcome.Unavailable, await new PeerTrust(rig.Client, rig.Registry, rig.Time).VerifyAuthAsync(endpoint, default));
        Assert.Equal(PeerStatus.Online, rig.Registry.StatusOf(rig.PeerKey));
        rig.Peer.MeReply = null;

        rig.Behavior.Hold.SetResult();
        Assert.Equal(RotateError.None, (await running).Error);
        Assert.False(rig.Registry.IsRotating(rig.PeerKey));
    }

    [Fact]
    public async Task T_ROT_6_TheRotatingMark_EndsByItselfAfterFifteenSeconds()
    {
        await using var rig = await RotationRig.CreateAsync();
        Assert.Equal(RotationStart.Started, rig.Registry.BeginRotation(rig.PeerKey, () => true));
        Assert.True(rig.Registry.IsRotating(rig.PeerKey));
        Assert.Equal(RotationStart.AlreadyRotating, rig.Registry.BeginRotation(rig.PeerKey, () => true));

        rig.Time.Advance(TimeSpan.FromSeconds(16));

        Assert.False(rig.Registry.IsRotating(rig.PeerKey));
        Assert.Equal(RotationStart.Started, rig.Registry.BeginRotation(rig.PeerKey, () => true));
    }

    // --- T-ROT-7: persistence

    [Fact]
    public async Task T_ROT_7_TheNewTokenIsOnDisk_WithANewNonce_AndSurvivesARestartOfHome()
    {
        using var home = new PairingRig(time: new ManualTimeProvider());
        await home.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.Home.Peers[0].Pairing!.Method = PairingMethod.Invite;
        });
        await home.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(OldToken, DeviceId, new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc), PairingMethod.Invite));
        var before = File.ReadAllBytes(home.Paths.SecretsFile);
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var rig = await RotationRig.AroundAsync(home, peer, home.Secrets);
        var newToken = rig.Behavior.Token;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);
        await rig.WaitForStoredAsync();

        Assert.Equal(RotateError.None, result.Error);
        var after = File.ReadAllBytes(home.Paths.SecretsFile);
        Assert.NotEqual(before, after);
        Assert.Equal(newToken, home.Secrets.TryGet(StateSamples.PeerKeyA)?.Token);
        using var restarted = new SecretStore(home.Paths, home.State, home.Log, name => name == SecretStore.KeyVariable ? home.Key : null, null, null, null);
        Assert.Equal(newToken, restarted.TryGet(StateSamples.PeerKeyA)?.Token);
        Assert.Equal(DeviceId, restarted.TryGet(StateSamples.PeerKeyA)?.DeviceId);
        Assert.DoesNotContain(newToken, System.Text.Encoding.ASCII.GetString(after), StringComparison.Ordinal); // sealed, not text
    }

    [Fact]
    public async Task T_ROT_7_AFailingWrite_IsRetriedThreeTimes_WhileTheNewTokenKeepsWorking_ThenRaisesStoreFailed()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Secrets!.FailPuts = 10;
        var newToken = rig.Behavior.Token;

        var result = await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);

        Assert.Equal(RotateError.None, result.Error);
        Assert.Equal(newToken, rig.Endpoints.Find(rig.PeerKey)!.Token); // active in memory although the file has the old one
        await rig.WaitUntilAsync(() => rig.Health.GetRotationError(rig.PeerKey)?.Kind == "storeFailed");
        Assert.Equal(4, rig.Secrets!.PutAttempts);
        Assert.Equal(newToken, rig.Endpoints.Find(rig.PeerKey)!.Token); // still
    }

    [Fact]
    public async Task T_ROT_7_AWriteThatSucceedsOnTheSecondTry_ClearsEverything()
    {
        await using var rig = await RotationRig.CreateAsync();
        rig.Secrets!.FailPuts = 1;

        await rig.Rotation.RotateAsync(rig.PeerKey, RotationTrigger.Manual, default);
        await rig.WaitForStoredAsync();

        Assert.Equal(rig.Behavior.Token, rig.Secrets!.Stored?.Token);
        Assert.Null(rig.Health.GetRotationError(rig.PeerKey));
    }

    // --- the admin route

    [Fact]
    public async Task TheAdminRoute_AnswersTheDocumentedWords_OnePerTenMinutes_AndNeverAToken()
    {
        await using var rig = await RotationRig.CreateAsync();
        var controller = new AdminRotateController(rig.State, rig.Rotation) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var ok = await controller.Rotate(rig.PeerKey, default);
        var json = AdminTestSupport.Body(ok);
        Assert.True(json.TryGetProperty("rotatedUtc", out _));
        Assert.DoesNotContain(rig.Behavior.Token, AdminTestSupport.Text(ok), StringComparison.Ordinal);
        Assert.DoesNotContain(OldToken, AdminTestSupport.Text(ok), StringComparison.Ordinal);

        var again = AdminTestSupport.Render(await controller.Rotate(rig.PeerKey, default));
        Assert.Equal(429, again.Status);
        Assert.True(int.Parse(again.Headers["Retry-After"].ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("notFound", AdminTestSupport.Body(await controller.Rotate("0000000000000000", default), 404).GetProperty("error").GetString());

        var info = AdminTestSupport.Body(controller.GetRotation(rig.PeerKey));
        Assert.True(info.GetProperty("supported").GetBoolean());
        Assert.Equal(0, info.GetProperty("days").GetInt32());
        Assert.DoesNotContain(rig.Behavior.Token, info.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"days":30}""", 200)]
    [InlineData("""{"days":0}""", 200)]
    [InlineData("""{"days":45}""", 400)]
    [InlineData("""{"days":-1}""", 400)]
    [InlineData("""{}""", 400)]
    [InlineData("not json", 400)]
    public async Task TheScheduleAccepts_0_30_60_Or90_Only(string body, int status)
    {
        await using var rig = await RotationRig.CreateAsync();
        var controller = new AdminRotateController(rig.State, rig.Rotation) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));

        var rendered = AdminTestSupport.Render(await controller.SetRotation(rig.PeerKey, default));

        Assert.Equal(status, rendered.Status);
        if (status == 200)
        {
            Assert.Equal(rig.State.Current.Home.Peers[0].RotationDays, rendered.Json.GetProperty("days").GetInt32());
        }
    }

    [Fact]
    public async Task ASchedule_CannotBeSetOnAQuickConnectPairing()
    {
        await using var rig = await RotationRig.CreateAsync(PairingMethod.Qc);
        var controller = new AdminRotateController(rig.State, rig.Rotation) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{"days":30}"""));

        Assert.Equal(409, AdminTestSupport.Render(await controller.SetRotation(rig.PeerKey, default)).Status);
        Assert.False(AdminTestSupport.Body(controller.GetRotation(rig.PeerKey)).GetProperty("supported").GetBoolean());
    }

    // --- the schedule

    [Fact]
    public async Task TheScheduler_RotatesADuePeer_LeavesAnOffOnePeerAlone_AndReportsOverdueAfterADayOfBlocking()
    {
        await using var rig = await RotationRig.CreateAsync();
        var scheduler = new RotationScheduler(rig.State, rig.Rotation, NullLogger<RotationScheduler>.Instance);
        await scheduler.RunOnceAsync(default);
        Assert.Equal(0, rig.Behavior.Calls); // the default is off

        await rig.State.UpdateAsync(s => s.Home.Peers[0].RotationDays = 30);
        rig.Time.Advance(TimeSpan.FromDays(10));
        await scheduler.RunOnceAsync(default);
        Assert.Equal(0, rig.Behavior.Calls); // not due yet

        rig.Time.Advance(TimeSpan.FromDays(25));
        rig.GoOnline();
        using var lease = rig.Slots.TryOpenStatic(Guid.NewGuid(), Guid.NewGuid(), rig.PeerKey, 4, out _);
        await scheduler.RunOnceAsync(default);
        Assert.Equal(0, rig.Behavior.Calls); // due, but a stream runs
        Assert.False(rig.Rotation.Describe(rig.State.Current.Home.Peers[0]).Overdue);

        rig.Time.Advance(TimeSpan.FromHours(25));
        rig.GoOnline();
        await scheduler.RunOnceAsync(default);
        Assert.True(rig.Rotation.Describe(rig.State.Current.Home.Peers[0]).Overdue);

        lease!.Dispose();
        rig.Time.Advance(StreamSlots.Grace + TimeSpan.FromSeconds(1)); // a closed stream keeps its slot for the grace period
        rig.GoOnline();
        await scheduler.RunOnceAsync(default);
        Assert.Equal(1, rig.Behavior.Calls);
        var after = rig.Rotation.Describe(rig.State.Current.Home.Peers[0]);
        Assert.False(after.Overdue);
        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime.AddDays(30), after.NextDueUtc);
    }

    private sealed class SwitchGate : IPairingGate
    {
        public bool Live { get; set; }

        public bool IsLive(string peerKey) => Live;
    }

    private sealed class NoAdmins : IAdminNotifier
    {
        public Task NotifyAdminsAsync(string header, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRotateSecrets : ISecretStore
    {
        private readonly string _peerKey;

        public FakeRotateSecrets(string peerKey, PeerCredential initial)
        {
            _peerKey = peerKey;
            Stored = initial;
        }

        public SecretStoreStatus Status => SecretStoreStatus.Ready;

        public SecretStoreInfo Info => new() { Status = Status, KeySource = KeySource.KeyFile };

        public IReadOnlyCollection<string>? PeerKeys => [_peerKey];

        public PeerCredential? Stored { get; private set; }

        public int FailPuts { get; set; }

        public int PutAttempts { get; private set; }

        public int Puts { get; private set; }

        public PeerCredential? TryGet(string peerKey) => peerKey == _peerKey ? Stored : null;

        public Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default)
        {
            PutAttempts++;
            if (FailPuts > 0)
            {
                FailPuts--;
                throw new IOException("disk full");
            }

            Stored = credential;
            Puts++;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Points the endpoint of the sample peer at the fake peer and keeps a token override like the real directory does.</summary>
    private sealed class OverlayEndpoints : IActiveTokenSource
    {
        private readonly ISecretStore _secrets;
        private readonly string _peerKey;
        private readonly FakeJellyfinPeer _peer;
        private string? _active;

        public OverlayEndpoints(string peerKey, FakeJellyfinPeer peer, ISecretStore secrets)
        {
            _peerKey = peerKey;
            _peer = peer;
            _secrets = secrets;
        }

        public PeerEndpoint? Find(string peerKey) =>
            peerKey == _peerKey && _secrets.TryGet(peerKey) is { } credential
                ? new PeerEndpoint(peerKey, "Hops", new Uri(_peer.BaseUrl), credential.DeviceId, _active ?? credential.Token).Configured(PeerNetwork.Tailnet, null, false, _peer.ServerId)
                : null;

        public void SetActiveToken(string peerKey, string token) => _active = token;

        public void ClearActiveToken(string peerKey) => _active = null;
    }

    private sealed class RotationRig : IAsyncDisposable
    {
        private readonly FakeJellyfinPeer _peer;
        private readonly PairingRig _home;
        private readonly bool _ownsPeer;

        private RotationRig(PairingRig home, FakeJellyfinPeer peer, ISecretStore secrets, bool ownsPeer)
        {
            _home = home;
            _peer = peer;
            _ownsPeer = ownsPeer;
            PeerKey = StateSamples.PeerKeyA;
            Secrets = secrets as FakeRotateSecrets;
            Endpoints = new OverlayEndpoints(PeerKey, peer, secrets);
            Slots = new StreamSlots(home.Time, () => 4);
            Behavior = FakePeerRotate.For(peer);
            Behavior.DeviceId = DeviceId;
            Rotation = new TokenRotation(home.State, secrets, Endpoints, home.Client, home.Registry, Slots, Gate, home.Health, home.Time, home.Log) { StoreRetryDelay = TimeSpan.FromMilliseconds(5) };
            GoOnline();
        }

        public string PeerKey { get; }

        public FakeRotateSecrets? Secrets { get; }

        public OverlayEndpoints Endpoints { get; }

        public StreamSlots Slots { get; }

        public SwitchGate Gate { get; } = new();

        public RotateBehavior Behavior { get; }

        public TokenRotation Rotation { get; }

        public FakeJellyfinPeer Peer => _peer;

        public StateStore State => _home.State;

        public PeerRegistry Registry => _home.Registry;

        public PeerClient Client => _home.Client;

        public PeerHealthStore Health => _home.Health;

        public ManualTimeProvider Time => (ManualTimeProvider)_home.Time;

        public CapturingLogger Log => _home.Log;

        public static async Task<RotationRig> CreateAsync(PairingMethod method = PairingMethod.Invite)
        {
            var home = new PairingRig(time: new ManualTimeProvider());
            await home.State.UpdateAsync(s =>
            {
                StateSamples.Fill(s);
                s.Home.Peers[0].Pairing!.Method = method;
            });
            var peer = await FakeJellyfinPeer.StartAsync();
            var secrets = new FakeRotateSecrets(StateSamples.PeerKeyA, new PeerCredential(OldToken, DeviceId, new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc), method));
            return new RotationRig(home, peer, secrets, ownsPeer: true);
        }

        public static async Task<RotationRig> AroundAsync(PairingRig home, FakeJellyfinPeer peer, ISecretStore secrets)
        {
            await Task.CompletedTask;
            return new RotationRig(home, peer, secrets, ownsPeer: false);
        }

        public void GoOnline()
        {
            Registry.SetStatus(PeerKey, PeerStatus.Online);
            Registry.RecordPinVerified(PeerKey);
            Registry.RecordAuthVerified(PeerKey);
        }

        public async Task WaitForStoredAsync()
        {
            await WaitUntilAsync(() => Secrets is null || Secrets.Stored?.Token == Behavior.Token);
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 400 && !condition(); i++)
            {
                await Task.Delay(10);
            }

            Assert.True(condition());
        }

        public async ValueTask DisposeAsync()
        {
            if (_ownsPeer)
            {
                await _peer.DisposeAsync();
            }

            _home.Dispose();
        }
    }
}
