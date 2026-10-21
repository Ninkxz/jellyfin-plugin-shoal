using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K19 acceptance (DESIGN 6.8, T-PLAY-7, T-PLAY-8, T-SEC-20, T-CONC-2, T-CLI-7): the exact session bodies, the 20 s ping and 30 s keepalive, the stop
/// sequence (stopped report, scoped delete, slots released, grant dropped), grant ownership and the empty-PlaySessionId rule.
/// </summary>
public sealed class PeerSessionReporterTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string HexToken = "0123456789abcdef0123456789abcdef";
    private const string PeerPlaySession = "peer-play-session-0123456789";
    private const string PeerDevice = "shoal-a1b2c3d4e5f6-4b0c1d2e";

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string ItemB = MatrixWorld.ItemB.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;
    private PeerSessionReporter _reporter = null!;

    public PeerSessionReporterTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        H.Peer.Requests.Clear();
        await H.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].MaxConcurrentStreams = 4;
            s.Home.Peers[0].MaxConcurrentTranscodes = 4;
            s.Home.Peers[0].Libraries[1].Access.SelectedUserIds = [MatrixWorld.Other, MatrixWorld.Admin];
        });
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            foreach (var grant in P.Grants.ForUser(user))
            {
                P.StreamSlots.ReleaseHls(grant.GrantId);
                P.StreamSlots.ReleaseStatic(grant.LocalUserId, grant.LocalItemId);
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.MaxStreams = 2;
        H.Peer.PublicInfo = new Reply(0);
        H.Peer.KillStatus = 204;
        H.Peer.HlsPlaylistReply = HlsProxyTests.DefaultPlaylists;
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id, transcode: true));
        var endpoints = new FakeEndpoints(P.Endpoint);
        var jobs = new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance);
        _reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, jobs, P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        _reporter.Start();
    }

    public async Task DisposeAsync() => await _reporter.DisposeAsync();

    private static object Answer(string peerItem, bool transcode) => new Dictionary<string, object?>
    {
        ["MediaSources"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["Id"] = PeerSourceN,
                ["Protocol"] = "File",
                ["Container"] = "mkv",
                ["RunTimeTicks"] = 72_000_000_000L,
                ["SupportsDirectPlay"] = !transcode,
                ["SupportsDirectStream"] = !transcode,
                ["SupportsTranscoding"] = true,
                ["TranscodingUrl"] = transcode ? $"/videos/{peerItem}/master.m3u8?DeviceId=abc&MediaSourceId={PeerSourceN}&VideoCodec=h264&ApiKey={FakeJellyfinPeer.DefaultToken}" : null,
                ["TranscodingSubProtocol"] = transcode ? "hls" : null,
                ["TranscodingContainer"] = transcode ? "ts" : null,
                ["MediaStreams"] = Array.Empty<object>(),
            },
        },
        ["PlaySessionId"] = PeerPlaySession,
    };

    private string Sign(Guid user)
    {
        var principal = MatrixWorld.Principal(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(CallerContext.TokenClaim)!);
        identity.AddClaim(new Claim(CallerContext.TokenClaim, HexToken));
        return H.World.Sign("rep-" + user.ToString("N"), principal);
    }

    private async Task<Grant> NewGrantAsync(Guid? user = null, string? item = null, bool transcode = true, bool peerSession = true)
    {
        var owner = user ?? MatrixWorld.Admin;
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(peerSession ? Answer(id, transcode) : WithoutPeerSession(Answer(id, transcode)));
        using var response = await H.SendAsync(Sign(owner), HttpMethod.Post, $"/Items/{item ?? Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return P.Grants.ForUser(owner).OrderBy(static g => g.CreatedUtc).Last();
    }

    private async Task StartJobAsync(Grant grant)
    {
        using var master = await H.SendAsync(Sign(grant.LocalUserId), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/master.m3u8");
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        using var main = await H.SendAsync(Sign(grant.LocalUserId), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/main.m3u8");
        Assert.Equal(HttpStatusCode.OK, main.StatusCode);
        using var segment = await H.SendAsync(Sign(grant.LocalUserId), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/hls1/main/0.ts?runtimeTicks=0&actualSegmentLengthTicks=30000000");
        await segment.Content.ReadAsByteArrayAsync();
        Assert.True(grant.PeerJobStarted);
    }

    private static PlaybackNote Note(Grant grant, long? position = 0, bool paused = false, Guid? user = null, string? session = null) =>
        new(user ?? grant.LocalUserId, session ?? grant.LocalPlaySessionId, position, paused);

    private async Task<List<RecordedRequest>> WaitForAsync(Func<RecordedRequest, bool> match, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var found = H.Peer.Requests.Where(match).ToList();
            if (found.Count >= count)
            {
                await Task.Delay(50);
                return [.. H.Peer.Requests.Where(match)];
            }

            await Task.Delay(10);
        }

        return [.. H.Peer.Requests.Where(match)];
    }

    private static bool Report(RecordedRequest r) => r.Path.StartsWith("/Sessions/Playing", StringComparison.Ordinal);

    [Fact]
    public async Task T_SEC_20_TheStartReport_HasExactlyTheListedFields_AndHomesPeerIds()
    {
        var grant = await NewGrantAsync(transcode: false);

        _reporter.OnStart(Note(grant, position: 123_000_000, paused: false));
        var calls = await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1);

        var call = Assert.Single(calls);
        Assert.Equal("POST", call.Method);
        using var json = JsonDocument.Parse(call.Body);
        var names = json.RootElement.EnumerateObject().Select(static p => p.Name).ToList();
        Assert.Equal(["ItemId", "MediaSourceId", "PlaySessionId", "PlayMethod", "CanSeek", "PositionTicks", "IsPaused"], names);
        Assert.Equal(PeerItemN, json.RootElement.GetProperty("ItemId").GetString());
        Assert.Equal(PeerSourceN, json.RootElement.GetProperty("MediaSourceId").GetString());
        Assert.Equal(PeerPlaySession, json.RootElement.GetProperty("PlaySessionId").GetString());
        Assert.Equal("DirectPlay", json.RootElement.GetProperty("PlayMethod").GetString());
        Assert.Equal(123_000_000, json.RootElement.GetProperty("PositionTicks").GetInt64());
        Assert.StartsWith("MediaBrowser ", call.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal("application/json", call.Headers["Content-Type"].Split(';')[0]);
        var text = Encoding.UTF8.GetString(call.Body);
        Assert.DoesNotContain(grant.LocalPlaySessionId, text, StringComparison.Ordinal); // Home's own ids never go to the Peer
        Assert.DoesNotContain(Item, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranscodeStart_ReportsTranscode()
    {
        var grant = await NewGrantAsync();

        _reporter.OnStart(Note(grant));
        var calls = await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1);

        using var json = JsonDocument.Parse(Assert.Single(calls).Body);
        Assert.Equal("Transcode", json.RootElement.GetProperty("PlayMethod").GetString());
    }

    [Fact]
    public async Task Progress_PingsAtMostOncePerTwentySeconds_WithThePeersPlaySessionId()
    {
        var grant = await NewGrantAsync();
        _reporter.OnStart(Note(grant));
        await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1);

        for (var i = 0; i < 5; i++)
        {
            _reporter.OnProgress(Note(grant, position: i * 10_000_000L));
        }

        await Task.Delay(150);
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path.EndsWith("/Ping", StringComparison.Ordinal));
        P.Time.Advance(TimeSpan.FromSeconds(21));
        for (var i = 0; i < 5; i++)
        {
            _reporter.OnProgress(Note(grant, position: 1_000_000_000L));
        }

        var pings = await WaitForAsync(static r => r.Path.EndsWith("/Ping", StringComparison.Ordinal), 1);
        await Task.Delay(100);
        pings = [.. H.Peer.Requests.Where(static r => r.Path.EndsWith("/Ping", StringComparison.Ordinal))];

        var ping = Assert.Single(pings);
        Assert.Equal("POST", ping.Method);
        Assert.Equal("/Sessions/Playing/Ping", ping.Path);
        Assert.Equal($"?playSessionId={PeerPlaySession}", ping.Query);
    }

    [Fact]
    public async Task TheKeepAlive_PingsEveryPlayingGrant_SoAPausedTranscodeSurvives()
    {
        var grant = await NewGrantAsync();
        _reporter.OnStart(Note(grant));
        await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1);
        P.Time.Advance(TimeSpan.FromSeconds(30));

        _reporter.KeepAlive();
        _reporter.KeepAlive(); // a second tick right behind the first adds nothing

        var pings = await WaitForAsync(static r => r.Path.EndsWith("/Ping", StringComparison.Ordinal), 1);
        await Task.Delay(100);
        Assert.Single(H.Peer.Requests, static r => r.Path.EndsWith("/Ping", StringComparison.Ordinal));
        Assert.Contains($"playSessionId={PeerPlaySession}", pings[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_PLAY_7_Stop_ForADirectPlay_ReportsStopped_ReleasesTheSlot_AndDropsTheGrant()
    {
        var grant = await NewGrantAsync(transcode: false);
        using var play = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        _reporter.OnStart(Note(grant));

        _reporter.OnStop(Note(grant, position: 500_000_000));
        var stopped = await WaitForAsync(static r => r.Path == "/Sessions/Playing/Stopped", 1);
        await WaitUntilAsync(() => P.Grants.Get(grant.GrantId) is null);

        using var json = JsonDocument.Parse(Assert.Single(stopped).Body);
        Assert.Equal(500_000_000, json.RootElement.GetProperty("PositionTicks").GetInt64());
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE"); // direct play has no Peer job
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task T_PLAY_8_Stop_ForAStartedTranscode_ReportsStopped_ThenSendsTheScopedDelete_ThenReleases()
    {
        var grant = await NewGrantAsync();
        await StartJobAsync(grant);
        _reporter.OnStart(Note(grant));
        await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1); // the start report must have reached the Peer before the log is cleared
        Assert.Equal(1, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));
        H.Peer.Requests.Clear();

        _reporter.OnStop(Note(grant, position: 1));
        var calls = await WaitForAsync(static r => r.Path == "/Videos/ActiveEncodings", 1);
        await WaitUntilAsync(() => P.Grants.Get(grant.GrantId) is null);

        var order = H.Peer.Requests.Where(static r => Report(r) || r.Path == "/Videos/ActiveEncodings").Select(static r => r.Method + " " + r.Path).ToList();
        Assert.Equal(["POST /Sessions/Playing/Stopped", "DELETE /Videos/ActiveEncodings"], order);
        Assert.Equal($"?deviceId={PeerDevice}&playSessionId={PeerPlaySession}", Assert.Single(calls).Query);
        Assert.False(grant.PeerJobStarted);
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        Assert.False(P.StreamSlots.ConversionsFull(H.World.PeerKey, 1));
    }

    [Fact]
    public async Task AStopForATranscodeThatNeverStartedAJob_SendsNoDelete()
    {
        var grant = await NewGrantAsync();
        _reporter.OnStart(Note(grant));

        _reporter.OnStop(Note(grant));
        await WaitForAsync(static r => r.Path == "/Sessions/Playing/Stopped", 1);
        await WaitUntilAsync(() => P.Grants.Get(grant.GrantId) is null);

        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE");
    }

    [Fact]
    public async Task T_CONC_2_StoppingOneStream_EndsOnlyItsJob_AndTheOtherKeepsItsSlotAndJob()
    {
        var a = await NewGrantAsync(MatrixWorld.Admin);
        var b = await NewGrantAsync(MatrixWorld.Selected);
        await StartJobAsync(a);
        await StartJobAsync(b);
        H.Peer.Requests.Clear();

        _reporter.OnStop(Note(a));
        await WaitForAsync(static r => r.Path == "/Videos/ActiveEncodings", 1);
        await WaitUntilAsync(() => P.Grants.Get(a.GrantId) is null);

        Assert.Single(H.Peer.Requests, static r => r.Method == "DELETE");
        Assert.NotNull(P.Grants.Get(b.GrantId));
        Assert.True(b.PeerJobStarted);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Selected));
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task N19_AnotherUsersEvent_OrAnUnknownOrEmptyPlaySession_IsNeverForwarded()
    {
        var grant = await NewGrantAsync(MatrixWorld.Admin);
        await StartJobAsync(grant);
        H.Peer.Requests.Clear();

        _reporter.OnStart(Note(grant, user: MatrixWorld.Selected));
        _reporter.OnProgress(Note(grant, user: MatrixWorld.Selected));
        _reporter.OnStop(Note(grant, user: MatrixWorld.Selected));
        _reporter.OnStop(Note(grant, session: string.Empty));
        _reporter.OnStop(new PlaybackNote(grant.LocalUserId, null, 0, false));
        _reporter.OnStop(Note(grant, session: PeerPlaySession)); // the Peer's id is not Home's
        _reporter.OnStop(Note(grant, session: "no-such-session"));
        await Task.Delay(300);

        Assert.Empty(H.Peer.Requests);
        Assert.NotNull(P.Grants.Get(grant.GrantId));
        Assert.True(grant.PeerJobStarted);
    }

    // 2.9.2 K9: a direct play the Peer answered without a PlaySessionId (allowed, Grants.cs) still gives its slot and grant back on stop.
    private async Task<Grant> DirectPlayWithoutPeerSessionAsync(Guid user)
    {
        var grant = await NewGrantAsync(user, transcode: false, peerSession: false);
        Assert.True(string.IsNullOrEmpty(grant.PeerPlaySessionId));
        using var play = await H.SendAsync(Sign(user), HttpMethod.Get, $"/Videos/{Item}/stream?Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(HttpStatusCode.PartialContent, play.StatusCode);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(user));
        return grant;
    }

    private static object WithoutPeerSession(object answer)
    {
        ((Dictionary<string, object?>)answer).Remove("PlaySessionId");
        return answer;
    }

    [Fact]
    public async Task Stop_DirectPlayWithoutPeerSession_ReleasesSlotAndGrant()
    {
        var grant = await DirectPlayWithoutPeerSessionAsync(MatrixWorld.Admin);
        H.Peer.Requests.Clear();

        _reporter.OnStop(Note(grant, position: 500_000_000));

        Assert.Null(P.Grants.Get(grant.GrantId)); // settled synchronously
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        await Task.Delay(200);
        Assert.DoesNotContain(H.Peer.Requests, Report); // nothing the Peer could match, so nothing is sent
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Stop_OtherUsersSession_ReleasesNothing()
    {
        var grant = await DirectPlayWithoutPeerSessionAsync(MatrixWorld.Admin);
        H.Peer.Requests.Clear();

        _reporter.OnStop(Note(grant, user: MatrixWorld.Selected)); // another user names the admin's play session
        _reporter.OnStop(Note(grant, session: "no-such-session"));
        _reporter.OnStop(Note(grant, session: string.Empty));

        Assert.NotNull(P.Grants.Get(grant.GrantId));
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        await Task.Delay(200);
        Assert.Empty(H.Peer.Requests);
    }

    // 2.9.2 K9 (Pilot 22 FAIL): core's PlaybackStopEventArgs as SessionManager.OnPlaybackStopped (v12.1) builds it from jellyfin-web's real
    // PlaybackStopInfo for a direct play. jellyfin-web (playbackmanager.js createStreamInfo) builds the stream URL itself without a PlaySessionId, so
    // PlayState.PlaySessionId = getParam('playSessionId', url) = "". The body it posts: ItemId, MediaSourceId, PositionTicks, PlayMethod "DirectPlay",
    // PlaySessionId "", PlaylistItemId, NowPlayingQueue, VolumeLevel, IsMuted, IsPaused... Core keeps ItemId as the event's Item and the session's DeviceId.
    private static PlaybackNote WebStop(Grant grant, Guid? user = null, Guid? item = null, string? device = null)
    {
        var stop = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = item ?? grant.LocalItemId },
            Users = [new Jellyfin.Database.Implementations.Entities.User("friend", "auth", "reset") { Id = user ?? grant.LocalUserId }],
            PlaybackPositionTicks = 500_000_000,
            MediaSourceId = grant.LocalItemId.ToString("N"),
            DeviceId = device ?? grant.CallerDeviceId,
            ClientName = "Jellyfin Web",
            PlaySessionId = string.Empty,
        };
        return Assert.IsType<PlaybackNote>(SessionReporterHost.Note(stop));
    }

    [Fact]
    public async Task Stop_JellyfinWebDirectPlay_EmptyPlaySessionId_ReleasesSlotAndGrant_AndReportsToThePeer()
    {
        var grant = await NewGrantAsync(transcode: false);
        using var play = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(HttpStatusCode.PartialContent, play.StatusCode);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        H.Peer.Requests.Clear();

        _reporter.OnStop(WebStop(grant));

        Assert.Null(P.Grants.Get(grant.GrantId)); // settled at once, not after the 2 min Grace
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        var stopped = await WaitForAsync(static r => r.Path == "/Sessions/Playing/Stopped", 1);
        using var json = JsonDocument.Parse(Assert.Single(stopped).Body);
        Assert.Equal(PeerPlaySession, json.RootElement.GetProperty("PlaySessionId").GetString()); // the Peer's own id, never the empty one
    }

    [Fact]
    public async Task Stop_JellyfinWebEmptyPlaySessionId_OtherUserDeviceOrItem_ReleasesNothing()
    {
        var grant = await NewGrantAsync(transcode: false);
        using var play = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        H.Peer.Requests.Clear();

        _reporter.OnStop(WebStop(grant, user: MatrixWorld.Selected)); // another user naming the admin's item on the admin's device
        _reporter.OnStop(WebStop(grant, device: "another-device"));
        _reporter.OnStop(WebStop(grant, item: MatrixWorld.ItemB));
        _reporter.OnStop(WebStop(grant, item: Guid.Empty));

        Assert.NotNull(P.Grants.Get(grant.GrantId));
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        await Task.Delay(200);
        Assert.DoesNotContain(H.Peer.Requests, Report);
    }

    [Fact]
    public async Task APeerThatIsDown_NeverBreaksTheStopSequence()
    {
        var grant = await NewGrantAsync();
        await StartJobAsync(grant);
        H.Peer.KillStatus = 500;

        _reporter.OnStop(Note(grant));
        await WaitUntilAsync(() => P.Grants.Get(grant.GrantId) is null);

        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // Home gives the slots back on stop whatever the Peer said
    }


    [Fact]
    public async Task Warden07_L3_AStop_ReleasesTheSlotsAndDropsTheGrantSynchronously_EvenWithABlockedQueue()
    {
        await _reporter.DisposeAsync();
        var endpoints = new FakeEndpoints(P.Endpoint);
        var idle = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        var grant = await NewGrantAsync();
        await StartJobAsync(grant);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        idle.OnStop(Note(grant)); // the consumer was never started: nothing reaches the Peer

        Assert.Null(P.Grants.Get(grant.GrantId));
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        await idle.DisposeAsync();
        _reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
    }

    [Fact]
    public async Task Warden07_L3_RepeatedPlaybackStarts_ForOneGrant_AreSentOncePerTwentySeconds()
    {
        var grant = await NewGrantAsync(transcode: false);

        for (var i = 0; i < 20; i++)
        {
            _reporter.OnStart(Note(grant));
        }

        await WaitForAsync(static r => r.Path == "/Sessions/Playing", 1);
        await Task.Delay(150);
        Assert.Single(H.Peer.Requests, static r => r.Path == "/Sessions/Playing");
        P.Time.Advance(TimeSpan.FromSeconds(21));
        _reporter.OnStart(Note(grant));
        await WaitForAsync(static r => r.Path == "/Sessions/Playing", 2);
        Assert.Equal(2, H.Peer.Requests.Count(static r => r.Path == "/Sessions/Playing"));
    }

    [Fact]
    public async Task Shutdown_DisposingTheReporterTwice_IsHarmless()
    {
        // lab-server logged a FATAL ObjectDisposedException at shutdown: the host and the container both dispose the reporter.
        var endpoints = new FakeEndpoints(P.Endpoint);
        var reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        reporter.Start();

        await reporter.DisposeAsync();
        await reporter.DisposeAsync();
        await Task.WhenAll(reporter.DisposeAsync().AsTask(), reporter.DisposeAsync().AsTask());
    }

    public class QuietSessions : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }

    [Fact]
    public async Task Shutdown_TheHostStopsAndDisposesTwice_WithoutAnException()
    {
        var endpoints = new FakeEndpoints(P.Endpoint);
        var reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        var host = new SessionReporterHost(System.Reflection.DispatchProxy.Create<MediaBrowser.Controller.Session.ISessionManager, QuietSessions>(), reporter);

        await host.StartAsync(default);
        await host.StopAsync(default);
        await host.DisposeAsync();
        await host.DisposeAsync();
        await reporter.DisposeAsync(); // the container disposes the singleton as well
    }
    [Fact]
    public async Task TheBuilders_ThrowOnAnEmptyPeerPlaySessionId()
    {
        var grant = await NewGrantAsync();
        var empty = new Grant
        {
            GrantId = grant.GrantId,
            LocalUserId = grant.LocalUserId,
            LocalItemId = grant.LocalItemId,
            CallerDeviceId = "d",
            LocalPlaySessionId = grant.LocalPlaySessionId,
            PeerKey = grant.PeerKey,
            PeerItemId = grant.PeerItemId,
            PeerMediaSourceId = grant.PeerMediaSourceId,
            PeerPlaySessionId = string.Empty,
        };

        Assert.ThrowsAny<ArgumentException>(() => PeerSessionRequests.Report(false, empty, 0, false));
        Assert.ThrowsAny<ArgumentException>(() => PeerSessionRequests.Report(true, empty, 0, false));
        Assert.ThrowsAny<ArgumentException>(() => PeerSessionRequests.Ping(empty));
        Assert.ThrowsAny<ArgumentException>(() => PeerJobRequests.ScopedKill(PeerDevice, string.Empty));
    }

    [Fact]
    public async Task TheQueue_IsBounded_AndNeverBlocksTheCaller()
    {
        await _reporter.DisposeAsync();
        var endpoints = new FakeEndpoints(P.Endpoint);
        var idle = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
        var grant = await NewGrantAsync();

        for (var i = 0; i < 1000; i++)
        {
            P.Time.Advance(TimeSpan.FromSeconds(25));
            grant.LastAccessUtc = P.Time.GetUtcNow();
            idle.OnProgress(Note(grant)); // the consumer was never started
        }

        Assert.Equal(PeerSessionReporter.QueueSize, idle.Pending);
        await idle.DisposeAsync();
        _reporter = new PeerSessionReporter(P.Grants, endpoints, H.Client, new HlsJobControl(endpoints, H.Client, NullLogger<HlsJobControl>.Instance), P.StreamSlots, P.Time, NullLogger<PeerSessionReporter>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
