using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K18 acceptance (DESIGN 6.6, 6.6a, T-CLI-4, T-SEC-20): both Moonfin URL shapes and the Shoal DeliveryUrl, the mapped values, the exact Peer request,
/// the format and codec allowlists, the rebuilds, breaker exclusion and HLS subtitle segments.
/// </summary>
public sealed class SubtitleHandlerTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string HexToken = "0123456789abcdef0123456789abcdef";
    private const string PeerPlaySession = "peer-play-session-0123456789";

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public SubtitleHandlerTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        H.Peer.Requests.Clear();
        await H.State.UpdateAsync(s => s.Home.Peers[0].Libraries[1].Access.SelectedUserIds = [MatrixWorld.Other, MatrixWorld.Admin]);
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            foreach (var grant in P.Grants.ForUser(user))
            {
                P.StreamSlots.ReleaseHls(grant.GrantId);
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.Notice.Notices.Clear();
        H.Peer.PublicInfo = new Reply(0);
        H.Peer.SubtitleReply = null;
        H.Peer.SubtitleDelayMs = 0;
        H.Peer.HlsPlaylistReply = null;
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Answer(string peerItem) => new Dictionary<string, object?>
    {
        ["MediaSources"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["Id"] = PeerSourceN,
                ["Protocol"] = "File",
                ["Container"] = "mkv",
                ["Size"] = 4_000_000_000L,
                ["RunTimeTicks"] = 72_000_000_000L,
                ["SupportsDirectPlay"] = true,
                ["SupportsTranscoding"] = true,
                ["MediaStreams"] = new object[]
                {
                    new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264" },
                    new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "eac3", ["Language"] = "eng" },
                    new Dictionary<string, object?> { ["Index"] = 2, ["Type"] = "Subtitle", ["Codec"] = "subrip", ["Language"] = "eng", ["DeliveryMethod"] = "Embed", ["SupportsExternalStream"] = true },
                    new Dictionary<string, object?> { ["Index"] = 3, ["Type"] = "Subtitle", ["Codec"] = "ass", ["Language"] = "fre", ["IsExternal"] = true, ["DeliveryMethod"] = "External", ["DeliveryUrl"] = $"/Videos/{peerItem}/{PeerSourceN}/Subtitles/3/0/Stream.ass?ApiKey={FakeJellyfinPeer.DefaultToken}" },
                    new Dictionary<string, object?> { ["Index"] = 4, ["Type"] = "Subtitle", ["Codec"] = "pgssub", ["DeliveryMethod"] = "Embed" },
                    new Dictionary<string, object?> { ["Index"] = 5, ["Type"] = "Subtitle", ["Codec"] = "xsub", ["DeliveryMethod"] = "Embed" },
                },
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
        return H.World.Sign("sub-" + user.ToString("N"), principal);
    }

    private async Task<Grant> NewGrantAsync(Guid? user = null)
    {
        var owner = user ?? MatrixWorld.Admin;
        using var response = await H.SendAsync(Sign(owner), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return P.Grants.ForUser(owner).Last();
    }

    private Task<HttpResponseMessage> GetAsync(string path, Guid? user = null) => H.SendAsync(Sign(user ?? MatrixWorld.Admin), HttpMethod.Get, path);

    private IEnumerable<RecordedRequest> SubtitleCalls() => H.Peer.Requests.Where(static r => r.Path.Contains("/Subtitles/", StringComparison.Ordinal));

    [Fact]
    public async Task T_CLI_4_MoonfinCoreShape_WithTheTicksSegment_ServesSrt_WithHomesType()
    {
        await NewGrantAsync();

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/0/Stream.srt?ApiKey={HexToken}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-subrip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\nHello\n\n", text);
        var call = Assert.Single(SubtitleCalls());
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/2/0/Stream.srt", call.Path);
        Assert.Equal(string.Empty, call.Query);
        Assert.DoesNotContain(HexToken, call.Query + string.Concat(call.Headers.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_CLI_4_SmartTvShape_WithoutTicks_ServesVtt_AndJs()
    {
        await NewGrantAsync();

        using var vtt = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt?addVttTimeMap=true&copyTimestamps=true");
        using var js = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.js");
        using var json = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.JSON");

        Assert.Equal("text/vtt", vtt.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello", await vtt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("application/json", js.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"TrackEvents\"", await js.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        var calls = SubtitleCalls().ToList();
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/2/0/Stream.vtt", calls[0].Path);
        Assert.Equal("?copyTimestamps=true&addVttTimeMap=true", calls[0].Query);
        Assert.EndsWith("/Stream.js", calls[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/Stream.js", calls[2].Path, StringComparison.Ordinal); // json is sent to the Peer as js
    }

    [Fact]
    public async Task TheDeliveryUrlOfAnExternalTrack_IsShoalsOwnRoute_AndServesAss()
    {
        using var info = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await info.Content.ReadAsStringAsync();
        Assert.Contains($"/Shoal/Subtitles/{Item}/3/Stream.ass?ApiKey={HexToken}", body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeJellyfinPeer.DefaultToken, body, StringComparison.Ordinal);

        using var response = await GetAsync($"/Shoal/Subtitles/{Item}/3/Stream.ass?ApiKey={HexToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/x-ssa", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("[Script Info]", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sup", 2)]
    [InlineData("pgssub", 2)]
    [InlineData("srt", 4)] // a bitmap stream
    [InlineData("srt", 5)] // an unknown codec
    [InlineData("srt", 0)] // a video stream
    [InlineData("srt", 99)]
    [InlineData("exe", 2)]
    [InlineData("html", 2)]
    public async Task BitmapUnknownAndForeignStreams_Are404_WithZeroPeerRequests(string format, int index)
    {
        await NewGrantAsync();

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/{index}/Stream.{format}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Fact]
    public async Task NoGrant_AForeignMediaSourceId_AndAForeignItemQuery_Are404()
    {
        using var noGrant = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt");
        await NewGrantAsync();
        using var foreignSource = await GetAsync($"/Videos/{Item}/{MatrixWorld.ItemB:N}/Subtitles/2/Stream.srt");
        using var foreignItem = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt?itemId={MatrixWorld.ItemB:N}");

        Assert.Equal(HttpStatusCode.NotFound, noGrant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSource.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignItem.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Fact]
    public async Task AnotherUsersGrant_IsNeverUsed()
    {
        await NewGrantAsync(MatrixWorld.Admin);

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt", MatrixWorld.Selected);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Theory]
    [InlineData("/0/Stream.srt?endPositionTicks=abc")]
    [InlineData("/0/Stream.srt?endPositionTicks=1&endPositionTicks=2")]
    [InlineData("/0/Stream.srt?endPositionTicks=-5")]
    [InlineData("/0/Stream.srt?endPositionTicks=99999999999999999999")]
    [InlineData("/0/Stream.srt?endPositionTicks=72036000000001")]
    [InlineData("/5/Stream.srt?endPositionTicks=4")]
    [InlineData("/0/Stream.vtt?copyTimestamps=maybe")]
    [InlineData("/0/Stream.vtt?addVttTimeMap=true&addVttTimeMap=false")]
    [InlineData("/-1/Stream.srt")]
    [InlineData("/72000000000001/Stream.srt")]
    [InlineData("/abc/Stream.srt")]
    public async Task InvalidOrRepeatedMappedValues_Are400_WithZeroPeerRequests(string tail)
    {
        await NewGrantAsync();

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2{tail}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Fact]
    public async Task OnlyTheMappedValuesReachThePeer_EveryOtherKeyIsIgnored()
    {
        await NewGrantAsync();

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/30000000/Stream.vtt?endPositionTicks=90000000&copyTimestamps=false&addVttTimeMap=true&ApiKey={HexToken}&format=exe&index=9&mediaSourceId=evil&PlaySessionId=x&deviceId=y&X=1");
        await response.Content.ReadAsStringAsync();

        var call = Assert.Single(SubtitleCalls());
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/2/30000000/Stream.vtt", call.Path);
        Assert.Equal("?endPositionTicks=90000000&copyTimestamps=false&addVttTimeMap=true", call.Query);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/javascript")]
    public async Task F_RS_01_AScriptServedAsASubtitle_Is502(string contentType)
    {
        await NewGrantAsync();
        H.Peer.SubtitleReply = (_, _) => new Reply(200, "<script>alert(1)</script>", contentType);

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HostileCueText_NeverReachesTheClient_InAnyFormat()
    {
        await NewGrantAsync();
        const string Evil = "<script>alert(1)</script><img src=x onerror=alert(1)>";
        H.Peer.SubtitleReply = (path, _) => path.EndsWith(".vtt", StringComparison.Ordinal)
            ? new Reply(200, "WEBVTT\n\nSTYLE\n::cue { background-image: url(http://evil.example/p) }\n\n00:01.000 --> 00:02.000\n" + Evil + "ok\n", "text/vtt")
            : path.EndsWith(".srt", StringComparison.Ordinal)
                ? new Reply(200, "1\n00:00:01,000 --> 00:00:02,000\n" + Evil + "ok\n\n", "application/x-subrip")
                : new Reply(200, "{\"TrackEvents\":[{\"Id\":\"1\",\"Text\":\"" + Evil.Replace("\"", "\\\"", StringComparison.Ordinal) + "ok\",\"StartPositionTicks\":1,\"EndPositionTicks\":2}]}", "application/json");

        foreach (var format in new[] { "vtt", "srt", "js" })
        {
            using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.{format}");
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("evil.example", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<img", text, StringComparison.Ordinal);
            Assert.Contains("ok", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABrokenBody_Is502_ForJsonWithNoEvents_AndVttWithNoHeader()
    {
        await NewGrantAsync();
        H.Peer.SubtitleReply = (path, _) => path.EndsWith(".vtt", StringComparison.Ordinal) ? new Reply(200, "no header\n", "text/vtt") : new Reply(200, "{\"nope\":1}", "application/json");

        using var vtt = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt");
        using var js = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.js");

        Assert.Equal(HttpStatusCode.BadGateway, vtt.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, js.StatusCode);
    }

    [Fact]
    public async Task T_CLI_16_ASubtitleThatFailsOnThePeer_NeverOpensTheBreaker()
    {
        await NewGrantAsync();
        H.Peer.SubtitleReply = (_, _) => new Reply(500, "boom", "text/plain");
        var breaker = H.Client.BreakerOf(P.Endpoint);

        for (var i = 0; i < 6; i++)
        {
            P.Time.Advance(TimeSpan.FromSeconds(10)); // the 30 per minute rate
            using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        H.Peer.SubtitleReply = null;
        using var after = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task T_CLI_16_ConcurrentIdenticalRequests_MakeOnePeerCall()
    {
        await NewGrantAsync();
        H.Peer.SubtitleDelayMs = 400;

        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/0/Stream.srt");
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(results.Select(static r => r.Item2).Distinct());
        Assert.Single(SubtitleCalls());
    }

    [Fact]
    public async Task TheRateLimit_IsThirtyPerMinute_WithABurstOfFifteen()
    {
        await NewGrantAsync();
        var statuses = new List<int>();
        for (var i = 0; i < 17; i++)
        {
            using var response = await GetAsync($"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt");
            statuses.Add((int)response.StatusCode);
        }

        Assert.Equal(2, statuses.Count(static s => s == 429));
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_IsRefusedByCoreOrShoal_WithZeroPeerRequests()
    {
        await NewGrantAsync();

        using var response = await H.SendAsync(null, HttpMethod.Get, $"/Shoal/Subtitles/{Item}/2/Stream.srt");
        using var core = await H.SendAsync(null, HttpMethod.Get, $"/Videos/{Item}/{Item}/Subtitles/2/Stream.srt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, core.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    // --- HLS subtitle segments (6.4)

    [Fact]
    public async Task AnHlsSubtitleSegment_IsRebuiltAsVtt_FromValidatedTicksOnly()
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(TranscodeAnswer(id));
        var grant = await NewGrantAsync();

        using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/stream.vtt?StartPositionTicks=0&EndPositionTicks=300000000&ApiKey={HexToken}&CopyTimestamps=false&x=1");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vtt", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("WEBVTT\n", text, StringComparison.Ordinal);
        var call = Assert.Single(SubtitleCalls());
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/2/stream.vtt", call.Path);
        Assert.Equal("?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks=0&EndPositionTicks=300000000", call.Query);
    }

    [Theory]
    [InlineData("StartPositionTicks=5&EndPositionTicks=4")]
    [InlineData("StartPositionTicks=0")]
    [InlineData("EndPositionTicks=5")]
    [InlineData("StartPositionTicks=0&StartPositionTicks=1&EndPositionTicks=5")]
    [InlineData("StartPositionTicks=a&EndPositionTicks=5")]
    [InlineData("StartPositionTicks=0&EndPositionTicks=72036000000001")]
    [InlineData("")]
    public async Task AnHlsSubtitleSegment_WithBadTicks_Is400(string query)
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(TranscodeAnswer(id));
        var grant = await NewGrantAsync();

        using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/stream.vtt?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Fact]
    public async Task AnHlsSubtitleSegment_ForABitmapStream_Is404()
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(TranscodeAnswer(id));
        var grant = await NewGrantAsync();

        using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/4/stream.vtt?StartPositionTicks=0&EndPositionTicks=5");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(SubtitleCalls());
    }

    [Fact]
    public async Task HlsSubtitleSegments_AreNotLimitedToOneOpenLanePerUser()
    {
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(TranscodeAnswer(id));
        var grant = await NewGrantAsync();
        H.Peer.SubtitleDelayMs = 300;

        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/stream.vtt?StartPositionTicks={i}&EndPositionTicks={i + 10}");
            return response.StatusCode;
        });
        var statuses = await Task.WhenAll(tasks);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    private static object TranscodeAnswer(string peerItem)
    {
        var answer = (Dictionary<string, object?>)Answer(peerItem);
        var source = (Dictionary<string, object?>)((object[])answer["MediaSources"]!)[0]!;
        source["SupportsDirectPlay"] = false;
        source["SupportsDirectStream"] = false;
        source["TranscodingUrl"] = $"/videos/{peerItem}/master.m3u8?DeviceId=abc&MediaSourceId={PeerSourceN}&VideoCodec=h264&api_key={FakeJellyfinPeer.DefaultToken}&ApiKey={FakeJellyfinPeer.DefaultToken}";
        source["TranscodingSubProtocol"] = "hls";
        source["TranscodingContainer"] = "ts";
        return answer;
    }
}
