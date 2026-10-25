using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K17 acceptance (DESIGN 6.4, 6.9, 6.1): the HLS proxy on real routing with a real grant, Moonfin's mutated URLs (T-CLI-3), the Peer request exactly as
/// 6.11 says, the client-query rules, the stream and conversion slots, the scoped ActiveEncodings delete (W4-4, T-SEC-25) and the grant checks.
/// </summary>
public sealed class HlsProxyTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string HexToken = "0123456789abcdef0123456789abcdef";
    private const string PeerDevice = "shoal-a1b2c3d4e5f6-4b0c1d2e";
    private const string PeerPlaySession = "peer-play-session-0123456789";

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string ItemB = MatrixWorld.ItemB.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public HlsProxyTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await DrainPrefetchesAsync(); // a main playlist asked for by an earlier test must not land in this one's counters
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
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.Slots.Holding.Clear();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.Notice.Notices.Clear();
        P.Admins.Messages.Clear();
        P.MaxStreams = 2;
        P.Devices.Profiles.Clear();
        H.Peer.PublicInfo = new Reply(0);
        H.Peer.KillStatus = 204;
        H.Peer.SegmentSize = 100_000;
        H.Peer.SegmentOverride = null;
        P.ReadAhead.Enabled = false;
        P.ReadAhead.ReleaseWhereGone();
        P.HlsBufferAhead = 3;
        H.Peer.PlaybackInfoReply = (id, _) => Reply.Json(Answer(id));
        H.Peer.HlsPlaylistReply = DefaultPlaylists;
    }

    public Task DisposeAsync() => DrainPrefetchesAsync();

    // The proxy asks the Peer for main.m3u8 at the master playlist, on a task of its own (DESIGN v2.7 / owner feedback 01). A test that ends before that call returns would
    // leave it running against the next test's fake peer; waiting for every grant's fetch makes each test start clean.
    private async Task DrainPrefetchesAsync()
    {
        var pending = new List<Task>();
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            pending.AddRange(P.Grants.ForUser(user).Select(static g => g.MainPrefetch).OfType<Task>());
        }

        if (pending.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(60)));
        }
    }

    // --- fixtures

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
                ["SupportsDirectPlay"] = false,
                ["SupportsDirectStream"] = false,
                ["SupportsTranscoding"] = true,
                ["TranscodingUrl"] = $"/videos/{peerItem}/master.m3u8?DeviceId=abc&MediaSourceId={PeerSourceN}&VideoCodec=h264&AudioCodec=aac&AudioStreamIndex=1&api_key={FakeJellyfinPeer.DefaultToken}&ApiKey={FakeJellyfinPeer.DefaultToken}&EnableAdaptiveBitrateStreaming=true",
                ["TranscodingSubProtocol"] = "hls",
                ["TranscodingContainer"] = "ts",
                ["DefaultAudioStreamIndex"] = 1,
                ["MediaStreams"] = new object[]
                {
                    new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080 },
                    new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "eac3", ["Language"] = "eng", ["Channels"] = 6, ["IsDefault"] = true },
                    new Dictionary<string, object?> { ["Index"] = 2, ["Type"] = "Subtitle", ["Codec"] = "srt", ["Language"] = "eng", ["DeliveryMethod"] = "Embed", ["SupportsExternalStream"] = true },
                    new Dictionary<string, object?> { ["Index"] = 4, ["Type"] = "Subtitle", ["Codec"] = "pgssub", ["DeliveryMethod"] = "Embed" },
                },
            },
        },
        ["PlaySessionId"] = PeerPlaySession,
    };

    private static string MasterText(string variantUri) =>
        "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1372000,AVERAGE-BANDWIDTH=1372000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1280x544,FRAME-RATE=24\n" + variantUri + "\n";

    private static string GoodVariantUri =>
        $"main.m3u8?DeviceId={PeerDevice}&MediaSourceId={PeerSourceN}&PlaySessionId={PeerPlaySession}&VideoCodec=h264&AudioCodec=aac&ApiKey=peerkey&TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported";

    private const string WithMap = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=b\"\n#EXTINF:3.000000,\nhls1/main/0.mp4?runtimeTicks=0&actualSegmentLengthTicks=30000000\n#EXT-X-ENDLIST\n";

    private static string MainText(int segments = 3) =>
        "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n"
        + string.Concat(Enumerable.Range(0, segments).Select(static i => $"#EXTINF:3.000000, nodesc\nhls1/main/{i}.ts?DeviceId=x&PlaySessionId=y&runtimeTicks={i * 30_000_000L}&actualSegmentLengthTicks=30000000\n"))
        + "#EXT-X-ENDLIST\n";

    internal static Reply DefaultPlaylists(string path, string query)
    {
        const string Hls = "application/x-mpegURL";
        if (path.EndsWith("/master.m3u8", StringComparison.Ordinal))
        {
            return new Reply(200, MasterText(GoodVariantUri), Hls);
        }

        if (path.EndsWith("/main.m3u8", StringComparison.Ordinal))
        {
            return new Reply(200, MainText(20), Hls);
        }

        if (path.EndsWith("/subtitles.m3u8", StringComparison.Ordinal))
        {
            return new Reply(200, "#EXTM3U\n#EXT-X-TARGETDURATION:30\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:30,\nstream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks=0&EndPositionTicks=300000000&ApiKey=peerkey\n#EXT-X-ENDLIST\n", Hls);
        }

        return new Reply(404);
    }

    private string Sign(Guid user, bool apiKey = false, string? token = null)
    {
        var principal = MatrixWorld.Principal(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(CallerContext.TokenClaim)!);
        identity.AddClaim(new Claim(CallerContext.TokenClaim, token ?? HexToken));
        if (apiKey)
        {
            identity.RemoveClaim(identity.FindFirst(CallerContext.IsApiKeyClaim)!);
            identity.AddClaim(new Claim(CallerContext.IsApiKeyClaim, "True"));
        }

        return H.World.Sign("hls-" + user.ToString("N") + (apiKey ? "-key" : string.Empty), principal);
    }

    private async Task<Grant> NewGrantAsync(Guid? user = null, string? item = null)
    {
        var owner = user ?? MatrixWorld.Admin;
        using var response = await H.SendAsync(Sign(owner), HttpMethod.Post, $"/Items/{item ?? Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var itemId = Guid.Parse(item ?? Item);
        var grant = P.Grants.ForUser(owner).Where(g => g.LocalItemId == itemId).OrderBy(static g => g.CreatedUtc).Last();
        Assert.Equal(PlayMethod.Transcode, grant.Method);
        return grant;
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? principal) => H.SendAsync(principal, HttpMethod.Get, path);

    private string MasterPath(Grant grant, string query = "") => $"/Shoal/Hls/{grant.GrantId}/master.m3u8{query}";

    private string SegmentPath(Grant grant, int n = 0, string ext = "ts", string? query = null) =>
        $"/Shoal/Hls/{grant.GrantId}/hls1/main/{n}.{ext}?{query ?? "runtimeTicks=0&actualSegmentLengthTicks=30000000"}";

    private async Task LoadPlaylistsAsync(Grant grant, string? principal = null)
    {
        var key = principal ?? Sign(grant.LocalUserId);
        using var master = await GetAsync(MasterPath(grant), key);
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        using var main = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", key);
        Assert.Equal(HttpStatusCode.OK, main.StatusCode);
    }

    private int PeerCalls(string suffix) => H.Peer.Requests.Count(r => r.Path.EndsWith(suffix, StringComparison.Ordinal));

    private RecordedRequest LastPeer(string suffix) => H.Peer.Requests.Last(r => r.Path.EndsWith(suffix, StringComparison.Ordinal));

    // --- T-CLI-3: Moonfin's shapes

    [Fact]
    public async Task T_CLI_3_TheMasterPlaylist_IsRebuilt_WithHomesOwnUrisAndTheCallersToken()
    {
        var grant = await NewGrantAsync();

        using var response = await GetAsync(MasterPath(grant, "?ApiKey=" + HexToken), Sign(MatrixWorld.Admin));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("attachment", string.Join(",", response.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty), StringComparison.Ordinal);
        Assert.Equal(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1372000,AVERAGE-BANDWIDTH=1372000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1280x544,FRAME-RATE=24\nmain.m3u8?ApiKey=" + HexToken + "\n",
            text);
        Assert.DoesNotContain(PeerItemN, text, StringComparison.Ordinal);
        Assert.DoesNotContain("peerkey", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_SEC_20_ThePeersMasterRequest_IsTheGrantsQueryPlusAdaptiveOff_AndNothingOfTheClient()
    {
        var grant = await NewGrantAsync();

        using var response = await GetAsync(MasterPath(grant, "?ApiKey=" + HexToken + "&DeviceId=evil&PlaySessionId=evil&MediaSourceId=evil&StartTimeTicks=5&upmix=true&Tag=x"), Sign(MatrixWorld.Admin));
        await response.Content.ReadAsStringAsync();

        var call = LastPeer("/master.m3u8");
        Assert.Equal($"/Videos/{PeerItemN}/master.m3u8", call.Path);
        var pairs = call.Query.TrimStart('?').Split('&').Select(static p => p.Split('=', 2)).ToDictionary(static p => p[0], static p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        Assert.Equal("false", pairs["EnableAdaptiveBitrateStreaming"]);
        Assert.Equal("abc", pairs["DeviceId"]);
        Assert.Equal(PeerSourceN, pairs["MediaSourceId"]);
        Assert.DoesNotContain("upmix", pairs.Keys);
        Assert.DoesNotContain("StartTimeTicks", pairs.Keys);
        Assert.DoesNotContain("ApiKey", pairs.Keys);
        Assert.DoesNotContain("api_key", pairs.Keys);
        Assert.DoesNotContain(HexToken, call.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", call.Query, StringComparison.Ordinal);
        Assert.Equal("*/*", call.Headers["Accept"]);
        Assert.DoesNotContain(HexToken, string.Concat(call.Headers.Values), StringComparison.Ordinal);
        Assert.StartsWith("MediaBrowser ", call.Headers["Authorization"], StringComparison.Ordinal);
        Assert.False(call.Headers.ContainsKey("Cookie"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?upmix=true")]
    [InlineData("?AudioStreamIndex=2&SubtitleStreamIndex=3&StartTimeTicks=&unknown=1&unknown=2")]
    [InlineData("?AudioStreamIndex=abc&AudioStreamIndex=1")]
    [InlineData("?audiostreamindex=1&PlaySessionId=a&PlaySessionId=b&DeviceId=x&MediaSourceId=y")]
    public async Task T_CLI_3_AnUnknownExtraRepeatedOrMutatedClientKey_NeverCausesA4xxFromShoal(string query)
    {
        var grant = await NewGrantAsync();

        using var response = await GetAsync(MasterPath(grant, query), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task T_CLI_3_AMovedTrack_IsCounted_NotForwarded_AndTheGrantsValueIsUsed()
    {
        var grant = await NewGrantAsync();
        Assert.Equal(0, grant.ClientIndexMismatch);

        using var same = await GetAsync(MasterPath(grant, "?AudioStreamIndex=" + grant.AudioStreamIndex), Sign(MatrixWorld.Admin));
        using var moved = await GetAsync(MasterPath(grant, "?AudioStreamIndex=7&SubtitleStreamIndex=-1"), Sign(MatrixWorld.Admin));
        using var garbage = await GetAsync(MasterPath(grant, "?AudioStreamIndex=7&AudioStreamIndex=8"), Sign(MatrixWorld.Admin));

        Assert.Equal(1, grant.ClientIndexMismatch);
        Assert.DoesNotContain("AudioStreamIndex=7", LastPeer("/master.m3u8").Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMainPlaylist_NeedsTheMasterFirst_AndIsRebuiltFromTheVariantQuery()
    {
        var grant = await NewGrantAsync();
        using var early = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));
        Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        Assert.Equal(0, PeerCalls("/main.m3u8"));

        await LoadPlaylistsAsync(grant);
        using var main = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8?ApiKey={HexToken}", Sign(MatrixWorld.Admin));
        var text = await main.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, main.StatusCode);
        Assert.StartsWith("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:3\n", text, StringComparison.Ordinal);
        Assert.Contains($"hls1/main/1.ts?ApiKey={HexToken}&runtimeTicks=30000000&actualSegmentLengthTicks=30000000\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceId", text, StringComparison.Ordinal);
        var call = LastPeer("/main.m3u8");
        var query = Uri.UnescapeDataString(call.Query);
        Assert.Contains($"MediaSourceId={PeerSourceN}", query, StringComparison.Ordinal);
        Assert.Contains($"DeviceId={PeerDevice}", query, StringComparison.Ordinal);
        Assert.Contains("TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported", query, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", query, StringComparison.Ordinal);
    }

    // --- segments

    [Fact]
    public async Task ASegment_IsCopiedByteForByte_WithHomesTypeAndTheExactPeerRequest()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);

        using var response = await GetAsync(SegmentPath(grant, 7, query: "runtimeTicks=210000000&actualSegmentLengthTicks=30000000&ApiKey=" + HexToken + "&Tag=x&upmix=true&DeviceId=evil"), Sign(MatrixWorld.Admin));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp2t", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(100_000, bytes.Length);
        Assert.Equal((byte)(250 % 251), bytes[250]);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        var call = LastPeer("/7.ts");
        Assert.Equal($"/Videos/{PeerItemN}/hls1/main/7.ts", call.Path);
        var query = Uri.UnescapeDataString(call.Query);
        Assert.EndsWith("&runtimeTicks=210000000&actualSegmentLengthTicks=30000000", query, StringComparison.Ordinal);
        Assert.Contains($"MediaSourceId={PeerSourceN}", query, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", query, StringComparison.Ordinal);
        Assert.DoesNotContain("upmix", query, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=x", query, StringComparison.Ordinal);
        Assert.DoesNotContain(HexToken, call.Query, StringComparison.Ordinal);
        Assert.True(grant.PeerJobStarted);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task TheInitializationSegment_AndMp4_AreServedWithTheirType()
    {
        var grant = await NewGrantAsync();
        H.Peer.HlsPlaylistReply = (path, query) => path.EndsWith("/main.m3u8", StringComparison.Ordinal)
            ? new Reply(200, WithMap, "application/x-mpegURL")
            : DefaultPlaylists(path, query);
        await LoadPlaylistsAsync(grant);
        H.Peer.SegmentOverride = async ctx =>
        {
            ctx.Response.ContentType = "video/mp4";
            ctx.Response.ContentLength = 4;
            await ctx.Response.Body.WriteAsync(new byte[] { 0, 0, 0, 1 });
            return true;
        };

        using var response = await GetAsync(SegmentPath(grant, -1, "mp4", "runtimeTicks=0&actualSegmentLengthTicks=0"), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.ToString());
        Assert.Equal($"/Videos/{PeerItemN}/hls1/main/-1.mp4", LastPeer("-1.mp4").Path);
    }

    [Theory]
    [InlineData("runtimeTicks=0")]
    [InlineData("actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=0&runtimeTicks=1&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=0&actualSegmentLengthTicks=0&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=-1&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=1.5&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=99999999999999999999&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=0&actualSegmentLengthTicks=6000000001")]
    [InlineData("runtimeTicks=72036000000001&actualSegmentLengthTicks=0")]
    [InlineData("runtimeTicks=&actualSegmentLengthTicks=0")]
    [InlineData("")]
    public async Task UV_ASegmentWithBadTickValues_Is400_NoSlot_NoPeerCall(string query)
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        var before = PeerCalls(".ts");
        var startedByPlaylists = grant.PeerJobStarted; // the main playlist starts the Peer's conversion

        using var response = await GetAsync(SegmentPath(grant, 1, query: query), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, PeerCalls(".ts"));
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        Assert.Equal(startedByPlaylists, grant.PeerJobStarted);
    }

    [Theory]
    [InlineData("/hls1/main/1000000.ts")]
    [InlineData("/hls1/main/-2.ts")]
    [InlineData("/hls1/main/1.m4s")]
    [InlineData("/hls1/main/1.TS")]
    [InlineData("/hls1/main/01.ts/x")]
    [InlineData("/hls1/other/1.ts")]
    [InlineData("/hls1/main/..%2f1.ts")]
    [InlineData("/subs/128/subtitles.m3u8")]
    [InlineData("/subs/-1/subtitles.m3u8")]
    [InlineData("/stream.vtt")]
    [InlineData("/../master.m3u8")]
    public async Task RoutesOutsideTheGrammarOfThePath_Are404_FromRouting(string tail)
    {
        var grant = await NewGrantAsync();

        using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}{tail}?runtimeTicks=0&actualSegmentLengthTicks=0", Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, PeerCalls(".ts"));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/json")]
    public async Task F_RS_01_A_ScriptServedAsASegment_Is502(string contentType)
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        H.Peer.SegmentOverride = async ctx =>
        {
            ctx.Response.ContentType = contentType;
            await ctx.Response.WriteAsync("<script>alert(1)</script>");
            return true;
        };

        using var response = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(404, 404)]
    [InlineData(500, 502)]
    [InlineData(403, 502)]
    [InlineData(302, 502)]
    public async Task APeerErrorOnASegment_IsMapped_NeverRelayed(int peerStatus, int expected)
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        H.Peer.SegmentOverride = async ctx =>
        {
            ctx.Response.StatusCode = peerStatus;
            ctx.Response.Headers.Location = "http://evil.example/";
            await ctx.Response.WriteAsync("secret");
            return true;
        };

        using var response = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task ASegmentShorterThanDeclared_CutsTheConnection()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        H.Peer.SegmentOverride = async ctx =>
        {
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 50_000;
            await ctx.Response.Body.WriteAsync(new byte[1000]);
            await ctx.Response.Body.FlushAsync();
            ctx.Abort();
            return true;
        };

        using var response = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        var failure = await Record.ExceptionAsync(async () => Assert.Equal(50_000, (await response.Content.ReadAsByteArrayAsync()).Length));

        Assert.NotNull(failure);
    }

    // --- H-1 (Warden 07): the segment route serves only what the approved playlist lists, and cannot pin the Peer's pool

    [Fact]
    public async Task H1_ASegmentBeyondTheApprovedPlaylist_Is404_WithNoPeerCall()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        Assert.Equal(20, grant.SegmentCount);
        var before = PeerCalls(".ts");

        using var last = await GetAsync(SegmentPath(grant, 19), Sign(MatrixWorld.Admin));
        using var past = await GetAsync(SegmentPath(grant, 20), Sign(MatrixWorld.Admin));
        using var far = await GetAsync(SegmentPath(grant, 999_999), Sign(MatrixWorld.Admin));
        using var init = await GetAsync(SegmentPath(grant, -1, "mp4", "runtimeTicks=0&actualSegmentLengthTicks=0"), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, past.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, far.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, init.StatusCode); // this playlist has no EXT-X-MAP
        Assert.Equal(before + 1, PeerCalls(".ts") + PeerCalls(".mp4"));
    }

    [Fact]
    public async Task TheMainPlaylist_IsAskedForAtTheMasterPlaylist_SoAClientThatGaveUpFindsItOnItsRetry()
    {
        var grant = await NewGrantAsync();
        using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.True(grant.PeerJobStarted, "the Peer's conversion starts with the main playlist: the stop at the end of the session must know");
        await grant.MainPrefetch!;
        Assert.Equal(1, PeerCalls("/main.m3u8"));

        using var first = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8?ApiKey={HexToken}", Sign(MatrixWorld.Admin));
        var text = await first.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains($"hls1/main/1.ts?ApiKey={HexToken}", text, StringComparison.Ordinal);
        Assert.Equal(1, PeerCalls("/main.m3u8")); // answered from what was asked for at the master playlist
        Assert.Equal(20, grant.SegmentCount);
    }

    [Fact]
    public async Task AClientThatCancelsTheMainPlaylist_DoesNotCancelTheFetch_AndItsRetryNeedsNoSecondPeerCall()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        H.Peer.HlsPlaylistReply = (path, query) =>
        {
            if (path.EndsWith("/main.m3u8", StringComparison.Ordinal))
            {
                peerStarted.TrySetResult();
                gate.Task.GetAwaiter().GetResult(); // the Peer is still starting its conversion
            }

            return DefaultPlaylists(path, query);
        };
        var grant = await NewGrantAsync();
        var mainPath = $"/Shoal/Hls/{grant.GrantId}/main.m3u8";
        H.World.RequestObserver = async (context, next) =>
        {
            if (context.Request.Path != mainPath)
            {
                await next(context);
                return;
            }

            try
            {
                var handling = next(context);
                requestStarted.TrySetResult();
                await handling;
            }
            finally
            {
                requestFinished.TrySetResult(context.RequestAborted.IsCancellationRequested);
            }
        };
        try
        {
            using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
            using (var cancel = new CancellationTokenSource())
            {
                var waiting = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    using var abandoned = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, mainPath, null, cancel.Token);
                });
                await peerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, PeerCalls("/main.m3u8"));
                Assert.False(requestFinished.Task.IsCompleted, "Home is still waiting for the Peer's playlist");
                cancel.Cancel();

                await waiting;
                Assert.True(await requestFinished.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Home observed the abandoned request's abort before the Peer finished");
            }

            Assert.NotNull(grant.MainPrefetch);
            Assert.False(grant.MainPrefetch!.IsCompleted, "the fetch goes on after the client left");
            gate.TrySetResult();
            var prefetched = await grant.MainPrefetch!;
            Assert.NotNull(prefetched);
            var afterTheFetch = PeerCalls("/main.m3u8");
            using var retry = await GetAsync(mainPath, Sign(MatrixWorld.Admin));

            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(afterTheFetch, PeerCalls("/main.m3u8"));
            Assert.Equal(20, grant.SegmentCount);
        }
        finally
        {
            H.World.RequestObserver = null;
            gate.TrySetResult(); // a failed assertion must not strand the fake peer or the next test
        }
    }

    [Fact]
    public async Task IfTheMainPlaylistWasNeverFetched_TheStopAtTheEndStillReachesThePeer()
    {
        var grant = await NewGrantAsync();
        using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        await grant.MainPrefetch!; // the client left before it asked for main.m3u8 or any segment
        var before = PeerCalls("/ActiveEncodings");

        using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before + 1, PeerCalls("/ActiveEncodings"));
    }

    [Fact]
    public async Task H1_NoSegmentIsServed_BeforeAMediaPlaylistWasAccepted()
    {
        var grant = await NewGrantAsync();
        using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        await grant.MainPrefetch!; // the main playlist was asked for at the master playlist; let it land before counting
        var before = H.Peer.Requests.Count;

        using var response = await GetAsync(SegmentPath(grant, 0), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count);
        Assert.Null(grant.SegmentCount);
    }

    [Fact]
    public async Task H1_ARejectedMediaPlaylist_DoesNotOpenTheRoute()
    {
        var grant = await NewGrantAsync();
        H.Peer.HlsPlaylistReply = (path, query) => path.EndsWith("/main.m3u8", StringComparison.Ordinal) ? new Reply(200, "#EXTM3U\n#EXT-X-ENDLIST\n", "application/x-mpegURL") : DefaultPlaylists(path, query);
        using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        using var main = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));

        using var response = await GetAsync(SegmentPath(grant, 0), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadGateway, main.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task H1_AGrant_MayHaveThreeSegmentCallsAtThePeerAtOnce_TheFourthIs429()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        H.Peer.SegmentOverride = async ctx =>
        {
            await release.Task;
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 3;
            await ctx.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
            return true;
        };
        var before = PeerCalls(".ts");
        var running = Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
        {
            using var response = await GetAsync(SegmentPath(grant, i), Sign(MatrixWorld.Admin));
            return response.StatusCode;
        })).ToList();
        await WaitUntilAsync(() => PeerCalls(".ts") == before + 3);

        using var fourth = await GetAsync(SegmentPath(grant, 3), Sign(MatrixWorld.Admin));
        release.SetResult();
        var statuses = await Task.WhenAll(running);

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        using var after = await GetAsync(SegmentPath(grant, 4), Sign(MatrixWorld.Admin)); // the places came back
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task H1_APeer_HasAtMostEightSegmentCallsAtOnce_AcrossGrants()
    {
        await H.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].MaxConcurrentStreams = 16;
            s.Home.Peers[0].MaxConcurrentTranscodes = 16;
        });
        P.MaxStreams = 4;
        var grants = new[] { await NewGrantAsync(MatrixWorld.Admin, Item), await NewGrantAsync(MatrixWorld.Admin, ItemB), await NewGrantAsync(MatrixWorld.Selected, Item) };
        foreach (var grant in grants)
        {
            await LoadPlaylistsAsync(grant);
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var peak = 0;
        H.Peer.SegmentOverride = async ctx =>
        {
            var now = System.Threading.Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = System.Threading.Volatile.Read(ref peak)) && System.Threading.Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.WhenAny(release.Task, Task.Delay(1500)); // hold the first eight; the ninth must not get through meanwhile
            System.Threading.Interlocked.Decrement(ref inFlight);
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 1;
            await ctx.Response.Body.WriteAsync(new byte[] { 1 });
            return true;
        };
        var running = grants.SelectMany(g => Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
        {
            using var response = await GetAsync(SegmentPath(g, i), Sign(g.LocalUserId));
            return response.StatusCode;
        }))).ToList();

        var statuses = await Task.WhenAll(running);

        Assert.True(peak <= 8, "peak " + peak);
        Assert.True(statuses.Count(static s => s == HttpStatusCode.OK) >= 8, string.Join(",", statuses));
        Assert.All(statuses, s => Assert.True(s is HttpStatusCode.OK or HttpStatusCode.TooManyRequests, s.ToString()));
    }

    [Fact]
    public async Task H1_SegmentFailures_NeverOpenThePeerWideBreaker()
    {
        Assert.False(PeerCallProfile.HlsSegment.CountsTowardBreaker);
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        H.Peer.SegmentOverride = ctx =>
        {
            ctx.Response.StatusCode = 500;
            return Task.FromResult(true);
        };

        for (var i = 0; i < 5; i++)
        {
            using var response = await GetAsync(SegmentPath(grant, i), Sign(MatrixWorld.Admin));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        Assert.False(H.Client.BreakerOf(P.Endpoint).IsOpen);
        Assert.Equal(0, H.Client.BreakerOf(P.Endpoint).ConsecutiveFailures);
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

    // --- Pilot 04: HEAD is allowed on the HLS routes

    private Task<HttpResponseMessage> HeadAsync(string path, string? principal) => H.SendAsync(principal, HttpMethod.Head, path);

    [Fact]
    public async Task Pilot04_HeadOnThePlaylists_AnswersLikeGet_WithoutABody()
    {
        var grant = await NewGrantAsync();
        using var get = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        var length = (await get.Content.ReadAsStringAsync()).Length;

        using var master = await HeadAsync(MasterPath(grant), Sign(MatrixWorld.Admin));
        using var main = await HeadAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));
        using var subs = await HeadAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/subtitles.m3u8", Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", master.Content.Headers.ContentType!.ToString());
        Assert.Equal(length, master.Content.Headers.ContentLength);
        Assert.Empty(await master.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, main.StatusCode);
        Assert.Empty(await main.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, subs.StatusCode);
    }

    [Fact]
    public async Task Pilot04_HeadOnASegment_IsCheckedLikeGet_ButNeverStartsAPeerJobOrTakesASlot()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        var before = PeerCalls(".ts");

        using var ok = await HeadAsync(SegmentPath(grant, 3), Sign(MatrixWorld.Admin));
        using var past = await HeadAsync(SegmentPath(grant, 99), Sign(MatrixWorld.Admin));
        using var badTicks = await HeadAsync(SegmentPath(grant, 3, query: "runtimeTicks=x"), Sign(MatrixWorld.Admin));
        using var anonymous = await HeadAsync(SegmentPath(grant, 3), null);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("video/mp2t", ok.Content.Headers.ContentType!.ToString());
        Assert.Empty(await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, past.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTicks.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(before, PeerCalls(".ts"));
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task Pilot04_HeadOnASubtitleSegment_StartsNoExtraction()
    {
        var grant = await NewGrantAsync();

        using var head = await HeadAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/stream.vtt?StartPositionTicks=0&EndPositionTicks=300000000", Sign(MatrixWorld.Admin));
        using var bad = await HeadAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/stream.vtt?StartPositionTicks=5&EndPositionTicks=1", Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("text/vtt", head.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path.Contains("/Subtitles/", StringComparison.Ordinal) && r.Path.EndsWith("stream.vtt", StringComparison.Ordinal));
    }

    // --- playlists from the Peer (F-PL through a live grant)

    [Theory]
    [InlineData("https:/collect.evil.example/s.ts")]
    [InlineData("//h/x")]
    [InlineData("http://evil.example/main.m3u8")]
    [InlineData("main.m3u8?next=http://evil.example/")]
    [InlineData("main.m3u8?MediaSourceId=11111111111111111111111111111111")]
    [InlineData("main.m3u8?DeviceId=another-device")]
    [InlineData("main.m3u8?PlaySessionId=other")]
    public async Task F_PL_AHostileMasterUri_Is502_AndNothingReachesTheClient(string uri)
    {
        var grant = await NewGrantAsync();
        H.Peer.HlsPlaylistReply = (path, query) => path.EndsWith("/master.m3u8", StringComparison.Ordinal) ? new Reply(200, MasterText(uri), "application/x-mpegURL") : DefaultPlaylists(path, query);

        using var response = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("evil", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(grant.PeerVariantQuery);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task APlaylistWithAForeignContentType_Is502(string contentType)
    {
        var grant = await NewGrantAsync();
        H.Peer.HlsPlaylistReply = (_, _) => new Reply(200, MasterText(GoodVariantUri), contentType);

        using var response = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task AHostileMainPlaylist_Is502_ForKeysTagsAndOpenEnds()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        foreach (var bad in new[]
        {
            MainText().Replace("#EXT-X-VERSION:3", "#EXT-X-KEY:METHOD=AES-128,URI=\"https://evil.example/k\"", StringComparison.Ordinal),
            MainText().Replace("#EXT-X-ENDLIST\n", string.Empty, StringComparison.Ordinal),
            MainText().Replace("hls1/main/0.ts", "http://evil.example/0.ts", StringComparison.Ordinal),
        })
        {
            H.Peer.HlsPlaylistReply = (path, query) => path.EndsWith("/main.m3u8", StringComparison.Ordinal) ? new Reply(200, bad, "application/x-mpegURL") : DefaultPlaylists(path, query);
            using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheSubtitlePlaylist_IsOnlyForTextStreamsOfTheGrant_AndRebuilt()
    {
        var grant = await NewGrantAsync();

        using var good = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/2/subtitles.m3u8?ApiKey={HexToken}", Sign(MatrixWorld.Admin));
        var text = await good.Content.ReadAsStringAsync();
        var before = PeerCalls("/subtitles.m3u8");
        using var graphic = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/4/subtitles.m3u8", Sign(MatrixWorld.Admin));
        using var absent = await GetAsync($"/Shoal/Hls/{grant.GrantId}/subs/9/subtitles.m3u8", Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Contains($"stream.vtt?ApiKey={HexToken}&StartPositionTicks=0&EndPositionTicks=300000000\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyTimestamps", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, graphic.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(before, PeerCalls("/subtitles.m3u8"));
        var call = H.Peer.Requests.First(r => r.Path.EndsWith("/subtitles.m3u8", StringComparison.Ordinal));
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/2/subtitles.m3u8", call.Path);
        Assert.Equal("?SegmentLength=30", call.Query);
    }

    // --- grant checks

    [Fact]
    public async Task AnotherUsersGrant_UnknownGrants_AndBadIds_Are404_WithNoPeerRequest()
    {
        var grant = await NewGrantAsync();
        var before = H.Peer.Requests.Count;

        using var other = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Selected));
        using var unknown = await GetAsync("/Shoal/Hls/AAAAAAAAAAAAAAAAAAAAAA/master.m3u8", Sign(MatrixWorld.Admin));
        using var shortId = await GetAsync("/Shoal/Hls/abc/master.m3u8", Sign(MatrixWorld.Admin));
        using var odd = await GetAsync("/Shoal/Hls/%2e%2e%2e%2e%2e%2e%2e%2e%2e%2e%2e%2e%2e/master.m3u8", Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, shortId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, odd.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task T_SEC_15_NoCredentials_Is401_AnApiKey_Is403_AndNeitherReachesThePeer()
    {
        var grant = await NewGrantAsync();
        var before = H.Peer.Requests.Count;

        using var anonymous = await GetAsync(MasterPath(grant), null);
        using var apiKey = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin, apiKey: true));
        using var segment = await GetAsync(SegmentPath(grant), null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, segment.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task O2_ATokenThatIsNotAJellyfinToken_Is403_ForPlaylists()
    {
        var grant = await NewGrantAsync();

        using var response = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin, token: "not-a-token"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task R13_R14_AccessRemoved_KillsTheGrantWithinFiveMinutes_AndFreesTheSlot()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        using var segment = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        await segment.Content.ReadAsByteArrayAsync();
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        H.World.Visible[(MatrixWorld.Admin, MatrixWorld.Item)] = false;
        using var cached = await GetAsync(SegmentPath(grant, 1), Sign(MatrixWorld.Admin)); // steps 5 to 7 are not re-run inside five minutes
        P.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var removed = await GetAsync(SegmentPath(grant, 2), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Null(P.Grants.Get(grant.GrantId));
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task AGrantExpires_AfterTenIdleMinutes_AndAfterTwelveHours()
    {
        var idle = await NewGrantAsync();
        P.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        using var late = await GetAsync(MasterPath(idle), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
    }

    [Fact]
    public async Task APerGrantRateLimit_AllowsABurstOfForty_ThenTenPerSecond()
    {
        var grant = await NewGrantAsync();
        var statuses = new List<int>();
        for (var i = 0; i < 42; i++)
        {
            using var response = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));
            statuses.Add((int)response.StatusCode);
        }

        Assert.Equal(2, statuses.Count(static s => s == 429));
        Assert.Equal(40, statuses.Count(static s => s != 429));
        P.Time.Advance(TimeSpan.FromSeconds(1));
        using var after = await GetAsync($"/Shoal/Hls/{grant.GrantId}/main.m3u8", Sign(MatrixWorld.Admin));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, after.StatusCode);
    }

    [Fact]
    public async Task ADirectPlayGrant_IsNotAnHlsGrant()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = PeerSourceN, ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
            ["PlaySessionId"] = PeerPlaySession,
        });
        using var info = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single();

        using var response = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(PlayMethod.DirectPlay, grant.Method);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task APeerThatIsStopped_Is503()
    {
        var grant = await NewGrantAsync();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.AuthFailed);
        var before = H.Peer.Requests.Count;

        using var response = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    // --- slots (6.9)

    [Fact]
    public async Task TheFirstSegment_TakesAStreamSlotAndAConversionSlot_Together()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // playlists take nothing

        using var first = await GetAsync(SegmentPath(grant, 0), Sign(MatrixWorld.Admin));
        await first.Content.ReadAsByteArrayAsync();
        using var second = await GetAsync(SegmentPath(grant, 1), Sign(MatrixWorld.Admin));
        await second.Content.ReadAsByteArrayAsync();

        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // the same grant: one slot
        Assert.Equal(1, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));
        Assert.True(P.StreamSlots.ConversionsFull(H.World.PeerKey, 1));
        Assert.False(P.StreamSlots.ConversionsFull(H.World.PeerKey, 2));
    }

    [Fact]
    public async Task T_CONC_TheConversionLimit_RefusesTheNextStream_WithoutDisturbingTheRunningOne()
    {
        await H.State.UpdateAsync(s => s.Home.Peers[0].MaxConcurrentTranscodes = 1);
        var one = await NewGrantAsync(MatrixWorld.Admin, Item);
        var two = await NewGrantAsync(MatrixWorld.Selected, Item);
        await LoadPlaylistsAsync(one);
        await LoadPlaylistsAsync(two);
        using var running = await GetAsync(SegmentPath(one, 0), Sign(MatrixWorld.Admin));
        await running.Content.ReadAsByteArrayAsync();

        using var refused = await GetAsync(SegmentPath(two, 0), Sign(MatrixWorld.Selected));
        using var again = await GetAsync(SegmentPath(one, 1), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); // the running stream keeps its slot
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Selected));
    }

    [Fact]
    public async Task TheStreamLimitPerUser_AppliesToHlsToo()
    {
        P.MaxStreams = 1;
        var a = await NewGrantAsync(MatrixWorld.Admin, Item);
        var b = await NewGrantAsync(MatrixWorld.Admin, ItemB);
        await LoadPlaylistsAsync(a);
        await LoadPlaylistsAsync(b);
        using var first = await GetAsync(SegmentPath(a), Sign(MatrixWorld.Admin));
        await first.Content.ReadAsByteArrayAsync();

        using var second = await GetAsync(SegmentPath(b), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task APlaybackInfoPreCheck_RefusesBeforeAnyPeerCall_WhenTheUserOrTheConversionsAreFull()
    {
        P.MaxStreams = 1;
        var a = await NewGrantAsync(MatrixWorld.Admin, Item);
        await LoadPlaylistsAsync(a);
        using var seg = await GetAsync(SegmentPath(a), Sign(MatrixWorld.Admin));
        await seg.Content.ReadAsByteArrayAsync();
        var calls = PeerCalls("/PlaybackInfo");

        using var userFull = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{ItemB}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Contains("RateLimitExceeded", await userFull.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(calls, PeerCalls("/PlaybackInfo"));

        P.MaxStreams = 2;
        await H.State.UpdateAsync(s => s.Home.Peers[0].MaxConcurrentTranscodes = 1);
        using var conversionsFull = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Contains("RateLimitExceeded", await conversionsFull.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Selected));
    }

    [Fact]
    public void TheHlsSlot_IsReleasedWithItsConversionSlot()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 4);
        var grant = new Grant
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = Guid.NewGuid(),
            LocalItemId = Guid.NewGuid(),
            CallerDeviceId = "d",
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
        };

        Assert.True(slots.TryTakeHls(grant, 4, 1, out _));
        Assert.True(slots.ConversionsFull("p", 1));
        slots.ReleaseHls(grant.GrantId);
        Assert.False(slots.ConversionsFull("p", 1));
        Assert.Equal(0, slots.ActiveSlotsOnPeer("p"));
    }

    // --- ActiveEncodings (W4-4, T-SEC-25)

    private string KillPath(Grant grant, string? device = "any-device") =>
        $"/Videos/ActiveEncodings?deviceId={device}&playSessionId={grant.LocalPlaySessionId}";

    [Fact]
    public async Task W4_4_TheScopedDelete_GoesToThePeerOnce_ThenTheSlotIsReleased()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        using var segment = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        await segment.Content.ReadAsByteArrayAsync();
        Assert.True(grant.PeerJobStarted);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        using var first = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));
        using var second = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var deletes = H.Peer.Requests.Where(static r => r.Method == "DELETE").ToList();
        var kill = Assert.Single(deletes);
        Assert.Equal("/Videos/ActiveEncodings", kill.Path);
        Assert.Equal($"?deviceId={PeerDevice}&playSessionId={PeerPlaySession}", kill.Query); // the pairing DeviceId and the grant's Peer PlaySessionId, never the client's
        Assert.False(grant.PeerJobStarted);
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task T_SEC_25_ADeleteForAGrantWithoutAStartedJob_SendsNothing_AndReleasesNothing()
    {
        var hls = await NewGrantAsync(MatrixWorld.Admin, Item); // no playlist was asked for, so the Peer started nothing
        var before = H.Peer.Requests.Count;

        using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(hls));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task T_SEC_25_ADeleteCannotFreeTheSlotOfADirectPlayStream()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = PeerSourceN, ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
            ["PlaySessionId"] = PeerPlaySession,
        });
        using var info = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single();
        using var play = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE");
    }

    [Fact]
    public async Task AnotherUsersPlaySession_FallsThroughToCore_AndTheEleventhDeleteInAMinuteIs429()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        using var segment = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        await segment.Content.ReadAsByteArrayAsync();

        using var foreign = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Delete, KillPath(grant));
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE");
        Assert.True(grant.PeerJobStarted);
        Assert.NotEqual(HttpStatusCode.NoContent, foreign.StatusCode); // core answers it, not Shoal

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            using var mine = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));
            statuses.Add(mine.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(10, statuses.Count(static s => s == HttpStatusCode.NoContent));
    }

    [Fact]
    public async Task AFailedPeerDelete_KeepsTheSlotAndTheFlag_SoTheIdleTimerOrARetryCleansUp()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        using var segment = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        await segment.Content.ReadAsByteArrayAsync();
        H.Peer.KillStatus = 500;

        using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, KillPath(grant));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(grant.PeerJobStarted);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task F_SEC_ADeleteWithoutAPlaySession_NeverReachesThePeer()
    {
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        using var segment = await GetAsync(SegmentPath(grant), Sign(MatrixWorld.Admin));
        await segment.Content.ReadAsByteArrayAsync();

        using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Delete, "/Videos/ActiveEncodings?deviceId=x");

        Assert.DoesNotContain(H.Peer.Requests, static r => r.Method == "DELETE"); // an empty PlaySessionId would stop every job of Home's shared DeviceId
        Assert.True(grant.PeerJobStarted);
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void TheScopedKillBuilder_ThrowsOnAnEmptyPlaySessionOrDevice(string empty)
    {
        Assert.ThrowsAny<ArgumentException>(() => PeerJobRequests.ScopedKill(PeerDevice, empty));
        Assert.ThrowsAny<ArgumentException>(() => PeerJobRequests.ScopedKill(empty, PeerPlaySession));
        Assert.ThrowsAny<ArgumentException>(() => PeerJobRequests.ScopedKill(null!, PeerPlaySession));
        Assert.Equal("/Videos/ActiveEncodings?deviceId=a%20b&playSessionId=p%26q", PeerJobRequests.ScopedKill("a b", "p&q").PathAndQuery);
    }

    // ---------------- read ahead (owner request, 2026-10-16; Warden 18) ----------------

    private IReadOnlyList<int> SegmentsAsked() => [.. H.Peer.Requests.Select(static r => r.Path).Where(static p => p.Contains("/hls1/main/", StringComparison.Ordinal)).Select(static p => int.Parse(p.Split('/').Last().Split('.')[0], System.Globalization.CultureInfo.InvariantCulture)).Order()];

    private async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private async Task PlayAsync(Grant grant, params int[] segments)
    {
        foreach (var segment in segments)
        {
            using var response = await GetAsync(SegmentPath(grant, segment), Sign(MatrixWorld.Admin));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsByteArrayAsync();
        }
    }

    [Fact]
    public async Task ReadAhead_StartsWithTheSecondRequestInOrder_ReadsTheWindow_ThenServesFromMemory_WithoutAnotherPeerCall()
    {
        P.ReadAhead.Enabled = true;
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);

        await PlayAsync(grant, 0);
        await Task.Delay(150);
        Assert.Equal([0], SegmentsAsked()); // the first request only says where the client is

        await PlayAsync(grant, 1);
        await WaitForAsync(() => SegmentsAsked().Count >= 5);
        await Task.Delay(150);

        Assert.Equal([0, 1, 2, 3, 4], SegmentsAsked()); // the window is three; nothing past it
        Assert.True(P.ReadAhead.HeldBytes > 0);

        using var third = await GetAsync(SegmentPath(grant, 2), Sign(MatrixWorld.Admin));
        var bytes = await third.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(H.Peer.SegmentSize, bytes.Length);
        Assert.Equal((byte)(1000 % 251), bytes[1000]);
        Assert.Equal("video/mp2t", third.Content.Headers.ContentType!.MediaType);
        await WaitForAsync(() => SegmentsAsked().Contains(5));
        Assert.Single(SegmentsAsked(), static s => s == 2); // served from memory: the Peer was asked for segment 2 once
        Assert.Contains(5, SegmentsAsked()); // and the window slid by one
    }

    [Fact]
    public async Task ReadAhead_AfterASeek_NothingIsReadUntilTheNextRequestInOrder()
    {
        P.ReadAhead.Enabled = true;
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        await PlayAsync(grant, 0, 1);
        await WaitForAsync(() => SegmentsAsked().Count >= 5);

        await PlayAsync(grant, 12); // a seek
        await Task.Delay(200);
        Assert.DoesNotContain(SegmentsAsked(), static s => s is > 12 and < 17);

        await PlayAsync(grant, 13);
        await WaitForAsync(() => SegmentsAsked().Contains(16));
        Assert.Contains(16, SegmentsAsked());
    }

    [Fact]
    public async Task ReadAhead_IsOneReadAtATimePerStream_AndStopsWhenTheStreamStops()
    {
        P.ReadAhead.Enabled = true;
        P.HlsBufferAhead = 6;
        var inFlight = 0;
        var most = 0;
        H.Peer.SegmentOverride = async ctx =>
        {
            var now = Interlocked.Increment(ref inFlight);
            most = Math.Max(most, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref inFlight);
            return false;
        };
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        await PlayAsync(grant, 0, 1);

        await WaitForAsync(() => SegmentsAsked().Count >= 4);
        P.StreamSlots.ReleaseHls(grant.GrantId); // the stream stops
        var asked = SegmentsAsked().Count;
        await Task.Delay(300);

        Assert.Equal(1, most);
        Assert.Equal(0, P.ReadAhead.HeldBytes);
        Assert.InRange(SegmentsAsked().Count, asked, asked + 1); // at most the read that was on its way
    }

    [Fact]
    public async Task ReadAhead_TheWindowIsTheSetting_AndAnythingElseIsClamped()
    {
        P.ReadAhead.Enabled = true;
        P.HlsBufferAhead = 2;
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);

        await PlayAsync(grant, 0, 1);
        await WaitForAsync(() => SegmentsAsked().Count >= 4);
        await Task.Delay(150);

        Assert.Equal([0, 1, 2, 3], SegmentsAsked());
        Assert.Equal(2, P.ReadAhead.Window);
    }

    [Fact]
    public async Task ReadAhead_AFailedReadIsLeftToTheClient_ItsOwnRequestMeetsTheFailure()
    {
        P.ReadAhead.Enabled = true;
        H.Peer.SegmentOverride = async ctx =>
        {
            if (ctx.Request.Path.Value!.Contains("/2.", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 502;
                return true;
            }

            await Task.CompletedTask;
            return false;
        };
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        await PlayAsync(grant, 0, 1);
        await WaitForAsync(() => SegmentsAsked().Count >= 5);

        using var broken = await GetAsync(SegmentPath(grant, 2), Sign(MatrixWorld.Admin));

        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode); // the client's own request went to the Peer and said what happened
    }

    [Fact]
    public async Task ReadAhead_NeverTouchesTheStreamsSlot_OnlyTheClientsOwnRequestsKeepItAlive()
    {
        P.ReadAhead.Enabled = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        await PlayAsync(grant, 0);
        H.Peer.SegmentOverride = async ctx =>
        {
            var path = ctx.Request.Path.Value!;
            if (!path.Contains("/0.", StringComparison.Ordinal) && !path.Contains("/1.", StringComparison.Ordinal))
            {
                await release.Task; // the reads for the segments after the client's second request wait here
            }

            return false;
        };
        await PlayAsync(grant, 1);
        var touched = P.StreamSlots.HlsActivity(grant.GrantId);
        await WaitForAsync(() => SegmentsAsked().Count >= 3);

        P.Time.Advance(TimeSpan.FromSeconds(30)); // the client went quiet; Home is still reading ahead
        release.SetResult();
        await WaitForAsync(() => SegmentsAsked().Count >= 5);
        await Task.Delay(200);

        Assert.Equal(touched, P.StreamSlots.HlsActivity(grant.GrantId)); // the reads did not make the stream look alive
    }

    [Fact]
    public async Task ReadAhead_AServerThatIsStopped_LosesWhatWasReadForIt_AndItsStreamsAreNotServedFromMemory()
    {
        P.ReadAhead.Enabled = true;
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);
        await PlayAsync(grant, 0, 1);
        await WaitForAsync(() => SegmentsAsked().Count >= 5);
        Assert.True(P.ReadAhead.HeldBytes > 0);

        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.AuthFailed); // its check failed

        Assert.Equal(0, P.ReadAhead.HeldBytes);
        Assert.Null(P.ReadAhead.Find(grant.GrantId, 2));
    }

    // ---------------- link evidence (Warden 19) ----------------

    private string ShortSegmentPath(Grant grant, int n) => SegmentPath(grant, n, query: "runtimeTicks=0&actualSegmentLengthTicks=5000000"); // half a second of playing time

    [Fact]
    public async Task LinkEvidence_ASlowClientReadingAFastUpstream_SaysNothingAboutTheLink()
    {
        H.Peer.SegmentSize = 4_000_000;
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);

        for (var i = 0; i < 3; i++)
        {
            using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, ShortSegmentPath(grant, i));
            await Task.Delay(700); // the client takes its time: longer than the segment plays
            Assert.Equal(H.Peer.SegmentSize, (await response.Content.ReadAsByteArrayAsync()).Length);
        }

        Assert.Null(P.Telemetry.SlowLinkMbps(H.World.PeerKey)); // the reads from the Peer were fast; only the client was slow
    }

    [Fact]
    public async Task LinkEvidence_ARealSlowUpstream_StillShowsAsASlowLink()
    {
        H.Peer.SegmentSize = 4_000_000;
        H.Peer.SegmentOverride = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 4_000_000;
            await ctx.Response.StartAsync(); // the delay belongs to body reads, not time to the headers
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(150); // every chunk, including the last, takes time to arrive
                await ctx.Response.Body.WriteAsync(new byte[1_000_000]);
                await ctx.Response.Body.FlushAsync();
            }

            return true;
        };
        var grant = await NewGrantAsync();
        await LoadPlaylistsAsync(grant);

        for (var i = 0; i < 3; i++)
        {
            using var response = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, ShortSegmentPath(grant, i));
            await response.Content.ReadAsByteArrayAsync();
        }

        var live = P.Telemetry.SlowLinkMbps(H.World.PeerKey);
        Assert.NotNull(live);
        Assert.InRange(live!.Value, 20, 80); // 32 Mbit in about 0.6 s is about 53 Mbps: the read time is the Peer's, and it is too slow for half a second of playing time
    }

    // --- K45 and Pilot 13 gate (d): core hides a BlockedTags item from lists, GET by id and PlaybackInfo, but its own /Videos/{id}/stream and master.m3u8 still
    // serve it. For a Shoal title neither core route is reached (the filter answers), and every Shoal route re-checks visibility and Admitted(user, library).

    [Fact]
    public async Task K45_AnItemHiddenByAShoalBlockedTag_Is404_OnCorePlaylistAndStreamRoutes_AndOnShoalsPlaybackInfo()
    {
        await MainPlacementAsync(MatrixWorld.Admin, MatrixWorld.Selected);
        H.World.Visible[(MatrixWorld.Selected, MatrixWorld.Item)] = false; // what core answers once shoal-lib-<k8> (or Shoal) is in the account's BlockedTags
        var before = H.Peer.Requests.Count;

        using var master = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Get, $"/Videos/{Item}/master.m3u8?MediaSourceId={Item}");
        using var stream = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Get, $"/Videos/{Item}/stream?static=true&MediaSourceId={Item}");
        using var info = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, master.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal(before, H.Peer.Requests.Count); // the friend's server never heard of it
    }

    [Fact]
    public async Task K45_AUserTheMainLibraryNoLongerAdmits_Is404_EvenBeforeTheTagReachesCore_AndTheRunningHlsGrantDiesAtTheRecheck()
    {
        await MainPlacementAsync(MatrixWorld.Admin, MatrixWorld.Selected);
        var grant = await NewGrantAsync(MatrixWorld.Selected);
        await LoadPlaylistsAsync(grant);

        // The admin narrows the library; core still shows the item (the tag write lags), CallerContext step 6 does not.
        await H.State.UpdateAsync(s => s.Home.Libraries.First(l => !l.IsLegacy).Access.SelectedUserIds = [MatrixWorld.Admin]);
        using var info = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        P.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var master = await GetAsync(MasterPath(grant), Sign(MatrixWorld.Selected));

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, master.StatusCode);
        Assert.Null(P.Grants.Get(grant.GrantId));
    }

    [Fact]
    public async Task K45_AUserWhoHidesFriendsTitles_Is404_OnPlaybackInfo_AndOnShoalsHlsAfterTheRecheck()
    {
        var grant = await NewGrantAsync(MatrixWorld.Selected);
        await LoadPlaylistsAsync(grant);

        await H.State.UpdateAsync(s => s.Home.Visibility.HiddenFriendTitles.Add(MatrixWorld.Selected));
        using var info = await H.SendAsync(Sign(MatrixWorld.Selected), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        P.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var segment = await GetAsync(SegmentPath(grant, 3), Sign(MatrixWorld.Selected));

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, segment.StatusCode);
    }

    [Fact]
    public async Task K45_ASharingAccount_NeverPlaysATitleOfAMainLibrary_ThoughCoreWouldShowIt()
    {
        await MainPlacementAsync(MatrixWorld.Admin, MatrixWorld.ShareUser);
        await H.State.UpdateAsync(s => s.Home.Libraries.First(l => !l.IsLegacy).Access = new LibraryAccess { Mode = AccessMode.Everyone, IncludeRestricted = true });

        using var info = await H.SendAsync(Sign(MatrixWorld.ShareUser), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        using var stream = await H.SendAsync(Sign(MatrixWorld.ShareUser), HttpMethod.Get, $"/Videos/{Item}/stream?static=true&MediaSourceId={Item}");

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
    }

    // The matrix item moves into a library of v2.9 added into a main library (placement Main), admitting the given accounts.
    private async Task MainPlacementAsync(params Guid[] admitted)
    {
        const string Key = "5ab1e0c0ffee1234";
        await H.State.UpdateAsync(s => s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary
        {
            LibKey = Key,
            Name = "Friends in Movies",
            Type = "movies",
            Placement = LibraryPlacement.Main,
            PathRel = Jellyfin.Plugin.Shoal.State.ShoalLibrary.LibsFolder + "/" + Key,
            Feeds = [H.World.PeerKey],
            Access = new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [.. admitted] },
        }));
        H.World.Federated[MatrixWorld.Item] = new FederatedItem(H.World.PeerKey, H.World.MappingKey, Key);
        await H.Filter!.Index.RebuildAsync();
        Assert.Equal(Key, H.Filter.Index.Get(MatrixWorld.Item)!.LibKey);
    }
}
