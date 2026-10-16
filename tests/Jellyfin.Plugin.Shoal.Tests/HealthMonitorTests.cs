using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K20 acceptance (DESIGN 4.3, 4.4, 5.7): the 60 s check (identity without a token, then the token), the status transitions and alerts, the session
/// anchor for Quick Connect peers only, the foreign-device watch, fast failure with an open circuit, and the offline message limits.
/// T-PLAY-12, T-PLAY-16, T-NET-3 to T-NET-5, T-SEC-13.
/// </summary>
public sealed class HealthMonitorTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private static readonly string Item = MatrixWorld.Item.ToString("N");

    private readonly PlaybackFixture _fixture;
    private HealthMonitor _monitor = null!;

    public HealthMonitorTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        H.Peer.Requests.Clear();
        await H.State.UpdateAsync(s => s.Home.Peers[0].Pairing = new PairingInfo { Method = PairingMethod.Qc });
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(2));
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.Admins.Messages.Clear();
        P.Notice.Notices.Clear();
        H.Peer.PublicInfo = new Reply(0);
        H.Peer.MeReply = null;
        H.Peer.SessionsReply = null;
        H.Peer.PlaybackInfoReply = null;
        H.Client.BreakerOf(P.Endpoint).RecordSuccess();
        var trust = new PeerTrust(H.Client, P.Registry, P.Time);
        _monitor = new HealthMonitor(H.State, new FakeEndpoints(P.Endpoint), H.Client, trust, P.Registry, P.Admins, P.Time, NullLogger<HealthMonitor>.Instance);
    }

    public Task DisposeAsync()
    {
        H.Client.BreakerOf(P.Endpoint).RecordSuccess();
        return Task.CompletedTask;
    }

    // A client and a monitor on the test clock: the breaker's probe delays are then the test's to advance.
    private (PeerClient Client, HealthMonitor Monitor) ClockedMonitor()
    {
        var client = new PeerClient(() => "Home", () => "0.1.0.0", P.Time, P.Registry) { EnforceAddressPolicy = false };
        return (client, new HealthMonitor(H.State, new FakeEndpoints(P.Endpoint), client, new PeerTrust(client, P.Registry, P.Time), P.Registry, P.Admins, P.Time, NullLogger<HealthMonitor>.Instance));
    }

    private Task<PeerStatus> CheckAsync() => _monitor.CheckAsync(H.World.PeerKey, default);

    private static Reply Session(Guid user, string device, string name = "Phone") => Reply.Json(new[] { new Dictionary<string, object?> { ["UserId"] = user.ToString("N"), ["DeviceId"] = device, ["DeviceName"] = name } });

    [Fact]
    public async Task AHealthyPeer_IsOnline_AfterTheIdentityCheckThatCarriesNoToken_ThenTheTokenCheck()
    {
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Offline);

        var status = await CheckAsync();

        Assert.Equal(PeerStatus.Online, status);
        Assert.Equal(PeerStatus.Online, P.Registry.StatusOf(H.World.PeerKey));
        var order = H.Peer.Requests.Select(static r => r.Path).Where(static p => p is "/System/Info/Public" or "/Users/Me").ToList();
        Assert.Equal(["/System/Info/Public", "/Users/Me"], order.Take(2)); // the watch reads the share user id with a third call
        Assert.False(H.Peer.Requests.First(static r => r.Path == "/System/Info/Public").Headers.ContainsKey("Authorization") && H.Peer.Requests.First(static r => r.Path == "/System/Info/Public").Headers["Authorization"].Contains("Token=", StringComparison.Ordinal));
        Assert.NotNull(P.Registry.PinVerifiedUtc(H.World.PeerKey));
        Assert.NotNull(P.Registry.AuthVerifiedUtc(H.World.PeerKey));
    }

    [Fact]
    public async Task NoSessionAnchorIsSentToAnyPeer_AnymoreD17()
    {
        await CheckAsync();
        await H.State.UpdateAsync(s => s.Home.Peers[0].Pairing = new PairingInfo { Method = PairingMethod.Invite });
        await CheckAsync();

        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path == "/Sessions/Capabilities");
    }


    [Fact]
    public async Task T_NET_3_AfterThreeTimeoutsAgainstAPausedPeer_HomeIsOnlineAgainWithinOneHealthInterval_OnceThePeerIsBack()
    {
        var (client, monitor) = ClockedMonitor();
        // The peer is paused: PlaybackInfo and the ping hang. Three calls time out (a short profile stands in for the 10 s of the real one).
        H.Peer.PlaybackInfoReply = (_, _) => Reply.HangForever;
        var shortProfile = new PeerCallProfile("short", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300), 64 * 1024);
        for (var i = 0; i < CircuitBreaker.OpenAfterFailures; i++)
        {
            var timeout = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(P.Endpoint, PeerRequest.Post("/Items/x/PlaybackInfo", [123]), shortProfile, default));
            Assert.Equal(PeerFailureKind.Timeout, timeout.Kind);
        }

        Assert.True(client.BreakerOf(P.Endpoint).IsOpen);
        Assert.Equal(PeerStatus.Offline, await monitor.CheckAsync(H.World.PeerKey, default)); // the probe is not due yet: no call, no wait

        // The peer comes back. The next health check after the probe delay pings it and closes the breaker.
        H.Peer.PlaybackInfoReply = null;
        P.Time.Advance(HealthMonitor.Interval);
        var status = await monitor.CheckAsync(H.World.PeerKey, default);

        Assert.Equal(PeerStatus.Online, status);
        Assert.False(client.BreakerOf(P.Endpoint).IsOpen);
        Assert.Contains(H.Peer.Requests, static r => r.Path == "/System/Ping");
    }

    [Fact]
    public void TheProbeDelay_NeverGrowsPastAMinute_SoARestartedPeerIsSeenAgainQuickly()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(time);
        for (var i = 0; i < CircuitBreaker.OpenAfterFailures; i++)
        {
            breaker.RecordFailure();
        }

        for (var i = 0; i < 12; i++)
        {
            Assert.True((breaker.NextProbeUtc!.Value - time.GetUtcNow()).TotalSeconds <= 60);
            time.Advance(TimeSpan.FromSeconds(61));
            Assert.True(breaker.TryAcquire(isProbe: true));
            breaker.RecordFailure();
        }

        Assert.True((breaker.NextProbeUtc!.Value - time.GetUtcNow()).TotalSeconds <= 60);
    }

    [Fact]
    public async Task TheQuickRound_ChecksOnlyPeersThatAreOffline()
    {
        var (_, monitor) = ClockedMonitor();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        H.Peer.Requests.Clear();

        await monitor.CheckAllAsync(onlyOffline: true, default);

        Assert.Empty(H.Peer.Requests);
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Offline);

        await monitor.CheckAllAsync(onlyOffline: true, default);

        Assert.NotEmpty(H.Peer.Requests);
    }

    [Fact]
    public async Task T_NET_3_AProbeThatFails_KeepsThePeerOffline_AndSchedulesTheNextOneLater()
    {
        var (client, monitor) = ClockedMonitor();
        for (var i = 0; i < CircuitBreaker.OpenAfterFailures; i++)
        {
            client.BreakerOf(P.Endpoint).RecordFailure();
        }

        var first = client.BreakerOf(P.Endpoint).NextProbeUtc;
        FakePeerPing.For(H.Peer).Status = 503;
        P.Time.Advance(HealthMonitor.Interval);

        Assert.Equal(PeerStatus.Offline, await monitor.CheckAsync(H.World.PeerKey, default));

        Assert.True(client.BreakerOf(P.Endpoint).IsOpen);
        Assert.True(client.BreakerOf(P.Endpoint).NextProbeUtc > first);
        FakePeerPing.For(H.Peer).Status = 200;
        P.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PeerStatus.Online, await monitor.CheckAsync(H.World.PeerKey, default));
    }
    [Fact]
    public async Task T_NET_AnUnreachablePeer_IsOffline_AndComesBackOnTheNextGoodCheck()
    {
        H.Peer.PublicInfo = new Reply(500);

        var down = await CheckAsync();
        H.Peer.PublicInfo = new Reply(0);
        var up = await CheckAsync();

        Assert.Equal(PeerStatus.Offline, down);
        Assert.Equal(PeerStatus.Online, up);
    }

    [Fact]
    public async Task T_NET_AnIdentityMismatch_StopsEverything_AlertsOnce_AndNeverSendsTheToken()
    {
        H.Peer.PublicInfo = Reply.Json(new { ServerName = "Impostor", Version = "12.1.0", Id = "ffffffffffffffffffffffffffffff00" });

        var first = await CheckAsync();
        var second = await CheckAsync();

        Assert.Equal(PeerStatus.IdentityMismatch, first);
        Assert.Equal(PeerStatus.IdentityMismatch, second);
        Assert.Single(P.Admins.Messages);
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path == "/Users/Me" || r.Path == "/Sessions/Capabilities" || r.Path == "/Sessions");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task T_NET_ARefusedToken_IsAuthFailed_AndAlertsOnce(int status)
    {
        H.Peer.MeReply = () => new Reply(status);

        var first = await CheckAsync();
        var second = await CheckAsync();

        Assert.Equal(PeerStatus.AuthFailed, first);
        Assert.Equal(PeerStatus.AuthFailed, second);
        Assert.Single(P.Admins.Messages);
        Assert.Contains("Pair it again", P.Admins.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_NET_AnAlertCanComeAgain_AnHourLater()
    {
        H.Peer.MeReply = () => new Reply(401);
        await CheckAsync();
        P.Time.Advance(TimeSpan.FromMinutes(61));

        await CheckAsync();

        Assert.Equal(2, P.Admins.Messages.Count);
    }

    [Fact]
    public async Task T_SEC_13_AnotherDeviceOnTheShareAccount_Alerts_WithTheDeviceNameAsText()
    {
        H.Peer.SessionsReply = () => Session(H.Peer.UserId, "someone-elses-tablet", "Eve's <b>tablet</b>");

        await CheckAsync();

        var alert = Assert.Single(P.Admins.Messages);
        Assert.Contains("Another device is using your account", alert, StringComparison.Ordinal);
        Assert.Contains("Eve's", alert, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", alert, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_SEC_13_TwoSessionsOnHomesOwnDeviceId_AreStillAnAlert_Token_lessSessionsCanReuseIt()
    {
        H.Peer.SessionsReply = () => Reply.Json(new[]
        {
            new Dictionary<string, object?> { ["UserId"] = H.Peer.UserId.ToString("N"), ["DeviceId"] = P.Endpoint.DeviceId, ["DeviceName"] = "Shoal", ["Client"] = "Shoal" },
            new Dictionary<string, object?> { ["UserId"] = H.Peer.UserId.ToString("N"), ["DeviceId"] = P.Endpoint.DeviceId, ["DeviceName"] = "Shoal", ["Client"] = "Jellyfin Web" },
        });

        await CheckAsync();

        Assert.Single(P.Admins.Messages);
    }

    [Fact]
    public async Task T_SEC_13_OnlyHomesOwnSession_AndOtherAccountsSessions_NeverAlert()
    {
        H.Peer.SessionsReply = () => Reply.Json(new[]
        {
            new Dictionary<string, object?> { ["UserId"] = H.Peer.UserId.ToString("N"), ["DeviceId"] = P.Endpoint.DeviceId, ["DeviceName"] = "Shoal" },
            new Dictionary<string, object?> { ["UserId"] = Guid.NewGuid().ToString("N"), ["DeviceId"] = "other-account", ["DeviceName"] = "Friend's TV" },
            new Dictionary<string, object?> { ["UserId"] = Guid.Empty.ToString("N"), ["DeviceId"] = "anonymous", ["DeviceName"] = "Browser" },
            new Dictionary<string, object?> { ["DeviceId"] = "no-user" },
        });

        await CheckAsync();

        Assert.Empty(P.Admins.Messages);
        Assert.DoesNotContain("Friend", string.Concat(P.Admins.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWatch_RunsEveryTenMinutes_NotEveryMinute()
    {
        await CheckAsync();
        P.Time.Advance(TimeSpan.FromMinutes(1));
        await CheckAsync();
        P.Time.Advance(TimeSpan.FromMinutes(1));
        await CheckAsync();

        Assert.Single(H.Peer.Requests, static r => r.Path == "/Sessions");

        P.Time.Advance(TimeSpan.FromMinutes(10));
        await CheckAsync();

        Assert.Equal(2, H.Peer.Requests.Count(static r => r.Path == "/Sessions"));
    }

    [Fact]
    public async Task TheWatch_IsNotRun_ForAPeerWithoutPairingInfo()
    {
        await H.State.UpdateAsync(s => s.Home.Peers[0].Pairing = null);

        await CheckAsync();

        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path == "/Sessions");
    }

    [Fact]
    public async Task T_PLAY_12_WithTheCircuitOpen_PlayFailsInUnderASecond_WithNoPeerRequest()
    {
        H.Peer.PublicInfo = new Reply(500);
        for (var i = 0; i < 3; i++)
        {
            await CheckAsync();
        }

        Assert.True(H.Client.BreakerOf(P.Endpoint).IsOpen);
        H.Peer.Requests.Clear();
        var principal = H.World.Sign("hm-admin", MatrixWorld.Principal(MatrixWorld.Admin));
        var watch = Stopwatch.StartNew();

        using var response = await H.SendAsync(principal, HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();

        Assert.Contains("Server offline", body, StringComparison.Ordinal); // the offline card (the harness runs with it on, as the plugin does)
        Assert.DoesNotContain("ErrorCode\":\"", body, StringComparison.Ordinal);
        Assert.True(watch.ElapsedMilliseconds < 1000, "took " + watch.ElapsedMilliseconds + " ms");
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));
        Assert.Single(P.Notice.Notices);
    }

    // --- Pilot 04: the admin page shows what the monitor knows

    private sealed class NoGate : Jellyfin.Plugin.Shoal.Pairing.IPairingGate
    {
        public bool IsLive(string peerKey) => false;
    }

    [Fact]
    public async Task Pilot04_AHealthyPeer_IsShownOnline_WithRttAndNextCheck()
    {
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Offline);
        await CheckAsync();
        var dir = new TempDirectory();
        var live = new Jellyfin.Plugin.Shoal.Api.Admin.StateBackedLiveStatus(new PeerHealthStore(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(dir.Path), NullLogger<PeerHealthStore>.Instance), new NoGate(), registry: P.Registry, monitor: _monitor);

        var info = live.ForPeer(H.World.PeerKey);

        Assert.Equal("Online", info.Status);
        Assert.NotNull(info.RttMs);
        Assert.Equal(P.Time.GetUtcNow().UtcDateTime + HealthMonitor.Interval, info.NextCheckUtc);
        dir.Dispose();
    }


    [Fact]
    public void Pilot04_ActiveStreams_ReadsTheSlotLedger()
    {
        var dir = new TempDirectory();
        var live = new Jellyfin.Plugin.Shoal.Api.Admin.StateBackedLiveStatus(new PeerHealthStore(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(dir.Path), NullLogger<PeerHealthStore>.Instance), new NoGate(), registry: P.Registry, monitor: _monitor, slots: P.StreamSlots);
        Assert.Equal(0, live.ForPeer(H.World.PeerKey).ActiveStreams);
        var user = Guid.NewGuid();

        using var one = P.StreamSlots.TryOpenStatic(user, Guid.NewGuid(), H.World.PeerKey, 8, out _)!;
        using var two = P.StreamSlots.TryOpenStatic(user, Guid.NewGuid(), H.World.PeerKey, 8, out _)!;
        var info = live.ForPeer(H.World.PeerKey);
        var peer = H.State.Current.Home.Peers.Single();
        var card = Jellyfin.Plugin.Shoal.Api.Admin.AdminStatusBuilder.Server(peer, info);

        Assert.Equal(2, info.ActiveStreams);
        Assert.Equal(2, card.ActiveStreams.N);
        dir.Dispose();
    }
    [Fact]
    public void Pilot04_BeforeTheFirstCheck_NothingIsClaimed()
    {
        Assert.Null(_monitor.Snapshot("never-checked"));
    }

    // --- the offline message (5.7)

    public class SessionsProbe : DispatchProxy
    {
        public int SessionReads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Sessions")
            {
                SessionReads++;
                return Array.Empty<SessionInfo>();
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    [Fact]
    public async Task T_PLAY_16_TheOfflineMessage_IsSentOncePerUserAndPeerEveryHalfMinute_AndOnlyWhenEnabled()
    {
        var probe = DispatchProxy.Create<ISessionManager, SessionsProbe>();
        var enabled = true;
        var notice = new ClientOfflineNotice(probe, () => enabled, P.Time, NullLogger<ClientOfflineNotice>.Instance);
        var user = Guid.NewGuid();
        var counter = (SessionsProbe)(object)probe;

        notice.PeerOffline(user, "peer", "Hops");
        notice.PeerOffline(user, "peer", "Hops");
        notice.PeerOffline(Guid.NewGuid(), "peer", "Hops"); // another user has their own allowance
        notice.PeerOffline(user, "other-peer", "Elsewhere"); // and so does another peer
        await Task.Delay(200);
        Assert.Equal(3, counter.SessionReads);

        P.Time.Advance(TimeSpan.FromSeconds(31));
        notice.PeerOffline(user, "peer", "Hops");
        await Task.Delay(200);
        Assert.Equal(4, counter.SessionReads);

        enabled = false;
        P.Time.Advance(TimeSpan.FromMinutes(6));
        notice.PeerOffline(user, "peer", "Hops");
        await Task.Delay(200);
        Assert.Equal(4, counter.SessionReads);
    }
}
