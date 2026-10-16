using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// K15 acceptance (DESIGN 6.3, 6.9): Moonfin's direct-play shapes replayed against the real filter and the real handler (T-CLI-2, T-CLI-5, T-CLI-1),
/// byte-identical ranges, Home's own type and headers (F-RS-01, -02, -07, -08), peer errors (F-RS-06), the stream slots and limits (T-SEC-17,
/// T-SEC-25), the static rate limit and the lapsed trust window.
/// </summary>
public sealed class StaticStreamTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const long FileSize = 1_000_000;

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string ItemB = MatrixWorld.ItemB.ToString("N");
    private static readonly string ItemC = MatrixWorld.ItemC.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public StaticStreamTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        H.World.Federated[MatrixWorld.ItemC] = new FederatedItem(H.World.PeerKey, H.World.MappingKey);
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            H.World.Visible[(user, MatrixWorld.ItemC)] = true;
        }

        await H.State.UpdateAsync(s =>
        {
            s.Home.Peers[0].MaxConcurrentStreams = 4;
            s.Home.Peers[0].Libraries[1].Access.SelectedUserIds = [MatrixWorld.Other, MatrixWorld.Admin];
        });
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1)); // ages every slot and refills every bucket
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.Notice.Notices.Clear();
        P.MaxStreams = 2;
        H.Peer.StreamFileSize = FileSize;
        H.Peer.StreamContentType = "video/x-matroska";
        H.Peer.StreamHeaders.Clear();
        H.Peer.StreamOverride = null;
        H.Peer.StreamChunkDelayMs = 0;
        H.Peer.PublicInfo = new Reply(0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string Sign(Guid user, params System.Security.Claims.Claim[] extra) => H.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user, extra));

    private static byte[] Expected(long first, long count) => [.. Enumerable.Range(0, (int)count).Select(i => (byte)((first + i) % 251))];

    private Task<HttpResponseMessage> GetAsync(string path, string? principal = null, string? range = null, HttpMethod? method = null, Action<HttpRequestMessage>? configure = null) =>
        H.SendAsync(principal ?? Sign(MatrixWorld.Admin), method ?? HttpMethod.Get, path, r =>
        {
            if (range is not null)
            {
                r.Headers.TryAddWithoutValidation("Range", range);
            }

            configure?.Invoke(r);
        });

    private int StreamRequests() => H.Peer.Requests.Count(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));

    private RecordedRequest LastStream() => H.Peer.Requests.Last(static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));

    private async Task WaitForConnectionsAsync(Guid user, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (P.StreamSlots.OpenConnections(user) != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(expected, P.StreamSlots.OpenConnections(user));
    }

    // --- T-CLI-2: Moonfin's direct play, byte for byte

    [Theory]
    [InlineData("bytes=0-", 0L, 999_999L)]
    [InlineData("bytes=300000-", 300_000L, 999_999L)]
    [InlineData("bytes=-4096", 995_904L, 999_999L)]
    [InlineData("bytes=10-19", 10L, 19L)]
    [InlineData("bytes=999990-", 999_990L, 999_999L)]
    [InlineData("BYTES=5-6", 5L, 6L)]
    public async Task T_CLI_2_ARangeIsAnswered206_WithTheExactBytes_AndTheExactContentRange(string range, long first, long last)
    {
        var path = $"/Videos/{Item}/stream?MediaSourceId={Item}&Static=true&ApiKey=tok{MatrixWorld.Admin:N}";

        using var response = await GetAsync(path, range: range, configure: r =>
        {
            r.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"tokX\", DeviceId=\"d\"");
            r.Headers.TryAddWithoutValidation("X-Emby-Token", "tokX");
        });

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {first}-{last}/{FileSize}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(last - first + 1, response.Content.Headers.ContentLength);
        Assert.Equal(Expected(first, last - first + 1), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", string.Join(",", response.Headers.AcceptRanges));
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
        Assert.False(response.Headers.Contains("Content-Security-Policy")); // media: the base headers only
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task DirectPlay_IsReadAheadThroughABoundedBuffer_TheBytesArriveWhole_AndTheBudgetComesBack()
    {
        H.Peer.StreamChunkDelayMs = 2;

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true&MediaSourceId={Item}");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Expected(0, FileSize), bytes);
        Assert.Equal(0, P.StaticReadAhead.Used); // the few MB of the buffer went back when the stream ended
        Assert.Equal(4 * 1024 * 1024, StaticStreamResult.ReadAheadBytes);
    }

    [Fact]
    public async Task T_CLI_2_WithNoRange_TheWholeFileComesBack_As200()
    {
        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FileSize, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Equal(Expected(0, FileSize), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task T_CLI_2_AHead_IsAnswered200_WithTheLengthAndNoBody()
    {
        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true&MediaSourceId={Item}", method: HttpMethod.Head, range: "bytes=0-");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FileSize, response.Content.Headers.ContentLength);
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("HEAD", LastStream().Method);
        Assert.False(LastStream().Headers.ContainsKey("Range")); // no range goes with a HEAD
    }

    [Fact]
    public async Task T_SEC_20_ThePeerRequest_IsExactlyThatOf611_AndCarriesNothingOfTheClient()
    {
        using var response = await GetAsync(
            $"/Videos/{Item}/stream?Static=true&MediaSourceId={Item}&ApiKey=tokX&DeviceId=evil&Tag=x",
            range: "bytes=100-199",
            configure: r =>
            {
                r.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"tokX\", DeviceId=\"d\"");
                r.Headers.TryAddWithoutValidation("X-Emby-Token", "tokX");
                r.Headers.TryAddWithoutValidation("Cookie", "session=1");
                r.Headers.TryAddWithoutValidation("Referer", "http://home.example/");
                r.Headers.TryAddWithoutValidation("X-Forwarded-For", "9.9.9.9");
                r.Headers.TryAddWithoutValidation("Accept-Language", "de");
            });
        await response.Content.ReadAsByteArrayAsync();

        var call = LastStream();
        Assert.Equal("GET", call.Method);
        Assert.Equal($"/Videos/{PeerItemN}/stream", call.Path);
        Assert.Equal($"?static=true&mediaSourceId={PeerSourceN}", call.Query);
        Assert.Equal("bytes=100-199", call.Headers["Range"]);
        Assert.Equal("*/*", call.Headers["Accept"]);
        Assert.Equal("identity", call.Headers["Accept-Encoding"]);
        Assert.StartsWith("MediaBrowser ", call.Headers["Authorization"], StringComparison.Ordinal);
        Assert.DoesNotContain("tokX", call.Headers["Authorization"], StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Cookie", "Referer", "X-Forwarded-For", "X-Emby-Token", "Accept-Language", "Forwarded", "X-Real-IP" })
        {
            Assert.False(call.Headers.ContainsKey(forbidden), forbidden);
        }

        Assert.DoesNotContain("tokX", call.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("static=true")]
    [InlineData("Static=True")]
    [InlineData("STATIC=TRUE")]
    public async Task T_CLI_5_TheStaticFlag_IsCaseInsensitive(string flag)
    {
        using var response = await GetAsync($"/Videos/{Item}/stream?{flag}&MediaSourceId={Item}", range: "bytes=0-9");

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public async Task T_CLI_5_TheMediaSourceId_MayBeInNOrDForm_OrAbsent(string format)
    {
        using var named = await GetAsync($"/Videos/{Item}/stream?Static=true&mediaSourceId={MatrixWorld.Item.ToString(format)}", range: "bytes=0-9");
        using var absent = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-9");

        Assert.Equal(HttpStatusCode.PartialContent, named.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, absent.StatusCode);
    }

    [Fact]
    public async Task AForeignMediaSourceId_Is404_WithNoPeerRequest()
    {
        var before = StreamRequests();

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true&mediaSourceId={ItemB}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var unknown = await GetAsync($"/Videos/{Item}/stream?Static=true&mediaSourceId={Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode); // an id that is no federated item keeps the handler's 400
        Assert.Equal(before, StreamRequests());
    }

    [Fact]
    public async Task T_CLI_5_TheSmartTvShape_StreamDotContainer_ServesWithTheCatalogsType()
    {
        using var response = await GetAsync($"/Videos/{Item}/stream.mkv?Static=true&mediaSourceId={Item}&deviceId=tv-1&ApiKey=tokX&Tag=etag", range: "bytes=0-99");
        using var comma = await GetAsync($"/Videos/{ItemB}/stream.mov,mp4,m4a,3gp,3g2,mj2?Static=true&mediaSourceId={ItemB}", Sign(MatrixWorld.Other), "bytes=0-99");

        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.PartialContent, comma.StatusCode);
        Assert.Equal("video/quicktime", comma.Content.Headers.ContentType!.ToString()); // the first entry of the catalog's list, whatever the URL says
    }

    [Fact]
    public async Task TheUrlsContainer_NeverChoosesTheType()
    {
        using var response = await GetAsync($"/Videos/{Item}/stream.html?Static=true&Container=svg", range: "bytes=0-9");

        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task T_CLI_2_MoonfinCore_PlaybackInfoThenDirectPlay_EndToEnd()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = PeerSourceN, ["Protocol"] = "File", ["Path"] = "/media/x.mkv", ["Container"] = "mkv", ["Size"] = FileSize,
                    ["SupportsDirectPlay"] = true, ["SupportsDirectStream"] = true, ["SupportsTranscoding"] = true, ["RunTimeTicks"] = 72_000_000_000L,
                    ["MediaStreams"] = Array.Empty<object>(),
                },
            },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        });
        var key = Sign(MatrixWorld.Admin);

        using var info = await H.SendAsync(key, HttpMethod.Post, $"/Items/{Item}/PlaybackInfo?MaxStreamingBitrate=20000000&AudioStreamIndex=1", r =>
            r.Content = new StringContent("""{"MaxStreamingBitrate":20000000,"AudioStreamIndex":1,"DeviceProfile":{"Name":"Moonfin","DirectPlayProfiles":[{"Container":"mkv","Type":"Video"}]}}""", System.Text.Encoding.UTF8, "application/json"));
        var source = JsonNode.Parse(await info.Content.ReadAsStringAsync())!["MediaSources"]![0]!;
        var id = (string)source["Id"]!;
        var container = (string)source["Container"]!;
        Assert.Equal("File", (string?)source["Protocol"]);
        Assert.Null(source["Path"]);

        // the URL jellyfin-web and Moonfin build from that answer
        var playUrl = $"/Videos/{id}/stream.{container}?MediaSourceId={id}&Static=true&ApiKey=tok{MatrixWorld.Admin:N}";
        using var play = await GetAsync(playUrl, key, "bytes=0-");
        using var seek = await GetAsync(playUrl, key, "bytes=300000-300099");

        Assert.Equal(HttpStatusCode.PartialContent, play.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, seek.StatusCode);
        Assert.Equal(Expected(300_000, 100), await seek.Content.ReadAsByteArrayAsync());
        Assert.Single(P.Grants.ForUser(MatrixWorld.Admin));
        Assert.True(P.Grants.ForUser(MatrixWorld.Admin).Single().LocalItemId == MatrixWorld.Item);
    }

    // --- ranges Home refuses

    [Theory]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("bytes=abc")]
    [InlineData("bytes=5-1")]
    [InlineData("bytes=-")]
    [InlineData("bytes=--5")]
    [InlineData("bytes=1-2-3")]
    [InlineData("bytes=-0")]
    [InlineData("items=0-1")]
    [InlineData("0-1")]
    [InlineData("bytes=0-99999999999999999999")]
    [InlineData("bytes=0-1, 4-5")]
    public async Task AMultiRangeOrMalformedRange_Is416_WithNoPeerRequestAndNoSlot(string range)
    {
        var before = StreamRequests();

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: range);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(before, StreamRequests());
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Fact]
    public async Task ARangeBeyondTheEndOfTheFile_Is416_WithTheTotal()
    {
        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=5000000-");

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{FileSize}", response.Content.Headers.ContentRange!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    // --- T-CLI-1

    [Theory]
    [InlineData("/Videos/{0}/stream?container=mkv&static=true&mediaSourceId={0}&tag=abc&playSessionId=ps1&deviceId=tv-1")]
    [InlineData("/Videos/{0}/stream.mkv?Static=true&mediaSourceId={0}")]
    public async Task T_CLI_1_NoCredentials_Is401_WithNoPeerRequest(string template)
    {
        var before = StreamRequests();

        using var response = await H.SendAsync(null, HttpMethod.Get, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Item), r => r.Headers.Range = new RangeHeaderValue(0, 1023));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, StreamRequests());
    }

    // --- F-RS-01, -02, -07, -08

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/javascript")]
    public async Task F_RS_01_And_02_AScriptServedAsAVideo_Is502_AndNeverReachesTheClient(string contentType)
    {
        H.Peer.StreamContentType = contentType;

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-99");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task F_RS_01_AnAcceptedUpstreamType_IsReplacedByTheCatalogsType()
    {
        H.Peer.StreamContentType = "video/mp4";

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-99");

        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task F_RS_07_And_08_NothingHostileThePeerSends_InAHeader_ReachesTheClient()
    {
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["Set-Cookie"] = "session=stolen",
            ["Link"] = "<http://evil.example/>; rel=preload",
            ["Refresh"] = "0; url=http://evil.example/",
            ["Content-Disposition"] = "inline; filename=evil.html",
            ["Content-Security-Policy"] = "default-src *",
            ["Access-Control-Allow-Origin"] = "*",
            ["X-Peer-Secret"] = "nope",
            ["Server"] = "evil/1.0",
            ["Cache-Control"] = "public, max-age=31536000",
        })
        {
            H.Peer.StreamHeaders[name] = value;
        }

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-99");
        await response.Content.ReadAsByteArrayAsync();

        var all = response.Headers.Concat(response.Content.Headers).Select(static h => h.Key).ToList();
        foreach (var forbidden in new[] { "Set-Cookie", "Link", "Refresh", "Content-Disposition", "Content-Security-Policy", "Access-Control-Allow-Origin", "X-Peer-Secret" })
        {
            Assert.DoesNotContain(forbidden, all, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
        Assert.DoesNotContain("evil", string.Join(";", response.Headers.Select(static h => string.Join(",", h.Value))), StringComparison.OrdinalIgnoreCase);
    }

    // --- F-RS-06 and the other ways the Peer can misbehave

    [Theory]
    [InlineData(500, 502)]
    [InlineData(502, 502)]
    [InlineData(503, 502)]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 404)]
    [InlineData(302, 502)]
    [InlineData(307, 502)]
    public async Task F_RS_06_APeerErrorStatus_IsMapped_AndNeverFollowedOrRelayed(int peerStatus, int expected)
    {
        H.Peer.StreamOverride = async ctx =>
        {
            ctx.Response.StatusCode = peerStatus;
            ctx.Response.Headers.Location = "http://evil.example/";
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("secret peer detail");
            return true;
        };
        var before = StreamRequests();

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-99");

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains("Location"));
        Assert.Equal(before + 1, StreamRequests()); // no second request
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // a request that failed before any byte gives its slot back
    }

    [Fact]
    public async Task ABodyShorterThanDeclared_CutsTheConnection_NeverACleanEnd()
    {
        H.Peer.StreamOverride = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.ContentLength = 100_000;
            await ctx.Response.Body.WriteAsync(new byte[1000]);
            await ctx.Response.Body.FlushAsync();
            ctx.Abort();
            return true;
        };

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true");

        var result = await Record.ExceptionAsync(async () => Assert.Equal(100_000, (await response.Content.ReadAsByteArrayAsync()).Length));
        Assert.NotNull(result); // the client sees an error, not a 1,000 byte "file"
    }

    [Fact]
    public async Task ABodyWithoutADeclaredLength_Is502()
    {
        H.Peer.StreamOverride = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/x-matroska";
            await ctx.Response.WriteAsync("chunked body with no length");
            return true;
        };

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ALengthBeyondTheCatalogsSize_Is502()
    {
        H.Peer.StreamOverride = ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.ContentLength = 5_000_000_000; // the catalog says 4 GB
            return Task.FromResult(true);
        };

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", method: HttpMethod.Head);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("bytes 5-9/100", 5, "bytes=0-4")] // not the range that was asked for
    [InlineData("bytes 0-4/100", 10, "bytes=0-4")] // length and range disagree
    [InlineData("bytes 0-4/3", 5, "bytes=0-4")] // the range runs past the total
    [InlineData("bytes 0-4/100000000000", 5, "bytes=0-4")] // a total beyond the catalog's size
    [InlineData("items 0-4/100", 5, "bytes=0-4")]
    [InlineData("bytes a-b/c", 5, "bytes=0-4")]
    [InlineData("bytes 0-4/*", 5, "bytes=0-4")]
    [InlineData("", 5, "bytes=0-4")]
    public async Task AContentRangeThatIsNotWhatWasAsked_Is502(string contentRange, long length, string requested)
    {
        H.Peer.StreamOverride = async ctx =>
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.ContentLength = length;
            if (contentRange.Length > 0)
            {
                ctx.Response.Headers["Content-Range"] = contentRange;
            }

            await ctx.Response.Body.WriteAsync(new byte[Math.Min(length, 16)]);
            return true;
        };

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: requested);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task APeerThatIgnoresAnOffsetRange_AndSendsTheWholeFile_Is502()
    {
        H.Peer.StreamOverride = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/x-matroska";
            ctx.Response.ContentLength = 10;
            await ctx.Response.Body.WriteAsync(new byte[10]);
            return true;
        };

        using var offset = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=500-");
        using var suffix = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=-100");

        Assert.Equal(HttpStatusCode.BadGateway, offset.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, suffix.StatusCode);
    }

    // --- 503 when the Peer is not trusted

    [Fact]
    public async Task ALapsedTrustWindow_WhichCannotBeRenewed_Is503_WithTheOfflineNotice_AndNoStreamRequest()
    {
        P.Registry.Invalidate(H.World.PeerKey);
        H.Peer.PublicInfo = new Reply(500);
        var before = StreamRequests();

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-9");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Single(P.Notice.Notices);
        Assert.Equal(before, StreamRequests());
        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
    }

    [Theory]
    [InlineData(PeerStatus.AuthFailed)]
    [InlineData(PeerStatus.IdentityMismatch)]
    [InlineData(PeerStatus.TlsKeyChanged)]
    public async Task AStoppedPeer_Is503(PeerStatus status)
    {
        P.Registry.SetStatus(H.World.PeerKey, status);
        var before = StreamRequests();

        using var response = await GetAsync($"/Videos/{Item}/stream?Static=true");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, StreamRequests());
    }

    [Fact]
    public async Task AnOpenCircuit_Is503()
    {
        var breaker = H.Client.BreakerOf(P.Endpoint);
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }

        try
        {
            using var response = await GetAsync($"/Videos/{Item}/stream?Static=true");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            breaker.RecordSuccess();
        }
    }

    // --- limits (6.9)

    [Fact]
    public async Task TheStaticRequestRate_IsTwentyPerSecond_WithABurstOfOneHundred()
    {
        var statuses = new List<int>();
        for (var i = 0; i < 102; i++)
        {
            using var response = await GetAsync($"/Videos/{Item}/stream?Static=true", method: HttpMethod.Head);
            statuses.Add((int)response.StatusCode);
            if (i == 100)
            {
                Assert.Equal("1", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Assert.Equal(100, statuses.Count(static s => s == 200));
        Assert.Equal(2, statuses.Count(static s => s == 429));
        P.Time.Advance(TimeSpan.FromSeconds(1));
        using var after = await GetAsync($"/Videos/{Item}/stream?Static=true", method: HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode); // 20 per second refill
    }

    [Fact]
    public async Task TheSeventhOpenConnection_OfOneUser_Is429()
    {
        H.Peer.StreamFileSize = 200_000_000;
        H.Peer.StreamChunkDelayMs = 50;
        var held = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < StreamSlots.MaxConnectionsPerUser; i++)
            {
                var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: $"bytes={i * 1000}-");
                Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
                held.Add(response);
            }

            using var seventh = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-");

            Assert.Equal(HttpStatusCode.TooManyRequests, seventh.StatusCode);
            Assert.Single(P.Slots.Holding.Concat(Array.Empty<string>()).DefaultIfEmpty("")); // the harness ledger is untouched
            Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // six connections, one stream: the Range requests share a slot
        }
        finally
        {
            held.ForEach(static r => r.Dispose());
        }

        await WaitForConnectionsAsync(MatrixWorld.Admin, 0);
    }

    [Fact]
    public async Task TheSlotsOfAUser_AreCapped_RangeRequestsShareOne_AndTheyEndTwoMinutesAfterTheLastByte()
    {
        H.Peer.StreamFileSize = 200_000_000;
        H.Peer.StreamChunkDelayMs = 50;
        var admin = Sign(MatrixWorld.Admin);
        using var a1 = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=0-");
        using var a2 = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=5000000-"); // a seek on the same stream
        using var b = await GetAsync($"/Videos/{ItemB}/stream?Static=true", admin, "bytes=0-");
        Assert.Equal(2, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        using var third = await GetAsync($"/Videos/{ItemC}/stream?Static=true", admin, "bytes=0-");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);

        a1.Dispose();
        a2.Dispose();
        b.Dispose();
        await WaitForConnectionsAsync(MatrixWorld.Admin, 0);
        P.Time.Advance(TimeSpan.FromSeconds(100));
        using var stillHeld = await GetAsync($"/Videos/{ItemC}/stream?Static=true", admin, "bytes=0-9");
        Assert.Equal(HttpStatusCode.TooManyRequests, stillHeld.StatusCode); // the slots linger for the grace period

        P.Time.Advance(TimeSpan.FromSeconds(30));
        H.Peer.StreamChunkDelayMs = 0;
        using var released = await GetAsync($"/Videos/{ItemC}/stream?Static=true", admin, "bytes=0-9");
        Assert.Equal(HttpStatusCode.PartialContent, released.StatusCode);
    }

    [Fact]
    public async Task OneUser_MayHoldAtMostHalfOfAPeersStreams_AndTheOtherHalfIsLeftForOthers()
    {
        P.MaxStreams = 4;
        H.Peer.StreamFileSize = 200_000_000;
        H.Peer.StreamChunkDelayMs = 50;
        var admin = Sign(MatrixWorld.Admin);
        using var a = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=0-");
        using var b = await GetAsync($"/Videos/{ItemB}/stream?Static=true", admin, "bytes=0-");

        using var third = await GetAsync($"/Videos/{ItemC}/stream?Static=true", admin, "bytes=0-");
        using var other = await GetAsync($"/Videos/{Item}/stream?Static=true", Sign(MatrixWorld.Selected), "bytes=0-");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode); // half of 4, rounded up, is 2
        Assert.Equal(HttpStatusCode.PartialContent, other.StatusCode);
        Assert.Equal(3, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));
    }

    [Fact]
    public async Task Pilot03_AFifthStream_WhileFourPlay_Is429AtThePeerLimit_AndTheFourKeepPlaying()
    {
        P.MaxStreams = 2;
        H.Peer.StreamFileSize = 200_000_000;
        H.Peer.StreamChunkDelayMs = 10;
        var admin = Sign(MatrixWorld.Admin);
        var selected = Sign(MatrixWorld.Selected);
        var running = new List<(HttpResponseMessage Response, Stream Body)>();
        try
        {
            foreach (var (key, item) in new[] { (admin, Item), (admin, ItemB), (selected, Item), (selected, ItemC) })
            {
                var response = await GetAsync($"/Videos/{item}/stream?Static=true", key, "bytes=0-");
                Assert.True(response.StatusCode == HttpStatusCode.PartialContent, $"{item} {(key == admin ? "admin" : "sel")} {response.StatusCode}");
                running.Add((response, await response.Content.ReadAsStreamAsync()));
            }

            Assert.Equal(4, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));

            using var fifth = await GetAsync($"/Videos/{ItemB}/stream?Static=true", Sign(MatrixWorld.Other), "bytes=0-");

            Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
            Assert.NotNull(fifth.Headers.RetryAfter);
            foreach (var (_, body) in running)
            {
                var buffer = new byte[64_000];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await body.ReadAsync(buffer.AsMemory(read));
                    Assert.True(n > 0, "a running stream was disturbed by the refused fifth");
                    read += n;
                }

                Assert.Equal(Expected(0, 64_000), buffer);
            }

            Assert.Equal(4, P.StreamSlots.ActiveSlotsOnPeer(H.World.PeerKey));
        }
        finally
        {
            running.ForEach(static r => r.Response.Dispose());
        }
    }

    [Fact]
    public async Task Pilot03_MaxStreamsPerUser_RefusesTheNextItem_WithoutDisturbingTheRunningOne()
    {
        P.MaxStreams = 1;
        H.Peer.StreamFileSize = 200_000_000;
        H.Peer.StreamChunkDelayMs = 10;
        var admin = Sign(MatrixWorld.Admin);
        using var first = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=0-");
        var body = await first.Content.ReadAsStreamAsync();

        using var second = await GetAsync($"/Videos/{ItemB}/stream?Static=true", admin, "bytes=0-");
        using var seek = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=1000000-"); // the same stream, a seek: allowed

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, seek.StatusCode);
        var buffer = new byte[64_000];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read));
            Assert.True(n > 0);
            read += n;
        }

        Assert.Equal(Expected(0, 64_000), buffer);
    }


    [Fact]
    public async Task Pilot04_TwelveSwitches_DuringOneDirectPlay_AllGetAnAnswer_AndTheGrantIsReplacedEachTime()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = PeerSourceN, ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        });
        var admin = Sign(MatrixWorld.Admin);
        using var play = await GetAsync($"/Videos/{Item}/stream?Static=true", admin, "bytes=0-");
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));

        for (var i = 0; i < 12; i++)
        {
            using var info = await H.SendAsync(admin, HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            var body = await info.Content.ReadAsStringAsync();
            Assert.DoesNotContain("RateLimitExceeded", body, StringComparison.Ordinal);
        }

        Assert.Single(P.Grants.ForUser(MatrixWorld.Admin)); // the same (user, item, device): replaced, never added
    }
    [Fact]
    public async Task T_SEC_17_PlaybackInfoAlone_NeverTakesASlot_SoAnotherUserCanStillPlay()
    {
        P.MaxStreams = 1;
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = PeerSourceN, ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["MediaStreams"] = Array.Empty<object>() } },
            ["PlaySessionId"] = "peer-play-session-0123456789",
        });
        var admin = Sign(MatrixWorld.Admin);
        for (var i = 0; i < 20; i++)
        {
            using var info = await H.SendAsync(admin, HttpMethod.Post, $"/Items/{Item}/PlaybackInfo", r => r.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }

        Assert.Equal(0, P.StreamSlots.ActiveSlots(MatrixWorld.Admin));
        using var played = await GetAsync($"/Videos/{Item}/stream?Static=true", Sign(MatrixWorld.Selected), "bytes=0-9");
        Assert.Equal(HttpStatusCode.PartialContent, played.StatusCode);
    }

    [Fact]
    public async Task AClientThatLeavesMidStream_StopsThePeerTransfer_AndFreesTheConnection()
    {
        H.Peer.StreamFileSize = 3_000_000_000;
        H.Peer.StreamChunkDelayMs = 5;
        var before = H.Peer.StreamBytesSent;
        var response = await GetAsync($"/Videos/{Item}/stream?Static=true", range: "bytes=0-");
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[100_000];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            Assert.True(n > 0);
            read += n;
        }

        Assert.Equal(Expected(0, 100_000), buffer);
        response.Dispose();
        await WaitForConnectionsAsync(MatrixWorld.Admin, 0);

        await Task.Delay(300);
        var sent = H.Peer.StreamBytesSent - before;
        await Task.Delay(300);
        Assert.Equal(sent, H.Peer.StreamBytesSent - before); // the peer stopped: nothing is read ahead without a client
        Assert.True(sent < 50_000_000, "sent " + sent);
        Assert.Equal(1, P.StreamSlots.ActiveSlots(MatrixWorld.Admin)); // the slot lingers for the grace period
    }

    // --- slots (unit)

    [Fact]
    public void T_SEC_25_ReleasingAnHlsSlot_NeverReleasesAStaticSlot()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 2);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        using var lease = slots.TryOpenStatic(user, item, "p", 4, out _)!;
        var grant = new Grant
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = user,
            LocalItemId = item,
            CallerDeviceId = "d",
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
            CreatedUtc = time.GetUtcNow(),
            LastAccessUtc = time.GetUtcNow(),
        };

        Assert.True(slots.HoldsSlot(grant));
        Assert.False(slots.ReleaseHls(grant.GrantId)); // nothing HLS to release
        Assert.True(slots.HoldsSlot(grant));
        lease.Dispose(); // the body ended
        Assert.True(slots.ReleaseStatic(user, item)); // only PlaybackStopped releases it, and not while a connection streams (Warden 07 M-2)
        Assert.False(slots.HoldsSlot(grant));
    }

    [Fact]
    public void T_SEC_25_TwoDevicesOfOneUserOnOneItem_ShareTheStaticSlot_AndBothGrantsHoldIt()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 2);
        var store = new GrantStore(time, slots);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        Grant Make(string device) => new()
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = user,
            LocalItemId = item,
            CallerDeviceId = device,
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
            CreatedUtc = time.GetUtcNow(),
            LastAccessUtc = time.GetUtcNow(),
        };
        var tv = Make("tv");
        var phone = Make("phone");
        store.TryAdd(tv);
        store.TryAdd(phone);

        using var lease = slots.TryOpenStatic(user, item, "p", 4, out _)!;
        using var second = slots.TryOpenStatic(user, item, "p", 4, out _)!;

        Assert.Equal(1, slots.ActiveSlots(user)); // one slot for the pair
        Assert.True(slots.HoldsSlot(tv));
        Assert.True(slots.HoldsSlot(phone));
        Assert.False(slots.HoldsOwnSlot(tv)); // the static slot belongs to the stream, not to a grant
        var replacement = Make("tv");
        Assert.Equal(GrantOutcome.Replaced, store.TryAdd(replacement)); // a switch during direct play replaces the grant of the same device
        Assert.Null(store.Get(tv.GrantId));
        Assert.NotNull(store.Get(phone.GrantId));
        Assert.NotNull(store.Get(replacement.GrantId));
        Assert.Equal(2, store.ForUser(user).Count);
    }

    [Fact]
    public void Pilot04_ManyPlaybackInfoCallsDuringOneDirectPlay_NeverFillTheGrantCap()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 2);
        var store = new GrantStore(time, slots);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        Grant Make(string device) => MakeGrant(user, item, device, time.GetUtcNow());
        using var lease = slots.TryOpenStatic(user, item, "p", 4, out _)!;

        var outcomes = Enumerable.Range(0, 30).Select(_ => store.TryAdd(Make("tv"))).ToList();

        Assert.Equal(GrantOutcome.Created, outcomes[0]);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(GrantOutcome.Replaced, o));
        Assert.Single(store.ForUser(user));
        Assert.True(store.HasRoom(user, item, "tv"));
    }

    [Fact]
    public void Pilot04_AGrantWithAnHlsSlot_IsStillNeverReplacedOrEvicted()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 4);
        var store = new GrantStore(time, slots);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var playing = MakeGrant(user, item, "tv", time.GetUtcNow());
        store.TryAdd(playing);
        Assert.True(slots.TryTakeHls(playing, 8, 8, out _));

        Assert.Equal(GrantOutcome.Created, store.TryAdd(MakeGrant(user, item, "tv", time.GetUtcNow()))); // beside it, until the client's ActiveEncodings delete frees the slot
        for (var i = 0; i < 12; i++)
        {
            store.TryAdd(MakeGrant(user, Guid.NewGuid(), "d" + i, time.GetUtcNow()));
        }

        Assert.NotNull(store.Get(playing.GrantId));
        Assert.True(store.ForUser(user).Count <= GrantStore.MaxPerUser);
    }

    [Fact]
    public void AnHlsSlot_IsTakenOncePerGrant_CountsAgainstTheLimits_AndExpiresWithoutRequests()
    {
        var time = new ManualTimeProvider();
        var slots = new StreamSlots(time, () => 1);
        var user = Guid.NewGuid();
        Grant Make(Guid item) => new()
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = user,
            LocalItemId = item,
            CallerDeviceId = "d",
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
            CreatedUtc = time.GetUtcNow(),
            LastAccessUtc = time.GetUtcNow(),
        };
        var one = Make(Guid.NewGuid());
        var two = Make(Guid.NewGuid());

        Assert.True(slots.TryTakeHls(one, 4, 4, out _));
        Assert.True(slots.TryTakeHls(one, 4, 4, out _)); // the same grant again takes no second slot
        Assert.False(slots.TryTakeHls(two, 4, 4, out var denial));
        Assert.Equal(SlotDenial.User, denial);

        time.Advance(StreamSlots.Grace + TimeSpan.FromSeconds(1));
        Assert.True(slots.TryTakeHls(two, 4, 4, out _)); // the first expired: no request for 2 minutes
        Assert.False(slots.HoldsSlot(one));
    }

    private static Grant MakeGrant(Guid user, Guid item, string device, DateTimeOffset at = default) => new()
    {
        GrantId = GrantStore.NewGrantId(),
        LocalUserId = user,
        LocalItemId = item,
        CallerDeviceId = device,
        LocalPlaySessionId = GrantStore.NewPlaySessionId(),
        PeerKey = "p",
        PeerItemId = Guid.NewGuid(),
        PeerMediaSourceId = Guid.NewGuid(),
        CreatedUtc = at,
        LastAccessUtc = at,
    };


    [Fact]
    public void Warden07_L6_AFullIdleBucket_IsForgotten_ButABusyOneIsNot()
    {
        var time = new ManualTimeProvider();
        var limiter = new RateLimiter(time, 30, 15);
        var idle = Guid.NewGuid();
        var busy = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            limiter.TryTake(Guid.NewGuid());
        }

        for (var i = 0; i < 15; i++)
        {
            limiter.TryTake(busy);
        }

        limiter.TryTake(idle);
        Assert.Equal(102, limiter.Count);

        time.Advance(TimeSpan.FromMinutes(3)); // everything has refilled; the busy one is used again right now
        limiter.TryTake(busy);

        Assert.Equal(1, limiter.Count); // only the key just used is remembered
        Assert.True(limiter.TryTake(idle)); // a forgotten key starts with a full bucket, exactly as a remembered full one would
    }

    [Fact]
    public void Warden07_L6_PruningNeverGivesAnEmptyBucketBackItsTokens()
    {
        var time = new ManualTimeProvider();
        var limiter = new RateLimiter(time, 30, 15);
        var spent = Guid.NewGuid();
        for (var i = 0; i < 15; i++)
        {
            Assert.True(limiter.TryTake(spent));
        }

        time.Advance(TimeSpan.FromSeconds(30)); // 15 tokens refill in 30 s at 30 per minute: not full before that, and the limiter prunes only idle full buckets
        limiter.TryTake(Guid.NewGuid());

        Assert.True(limiter.Count >= 1);
        Assert.True(limiter.TryTake(spent));
    }
    [Fact]
    public void Warden07_M1_AStaticSlot_ExemptsOnlyTheFirstHlsSlotOfThePair()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 2);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        using var lease = slots.TryOpenStatic(user, item, "p", 8, out _)!;

        Assert.True(slots.TryTakeHls(MakeGrant(user, item, "tv"), 8, 8, out _)); // the same stream, played another way
        Assert.False(slots.TryTakeHls(MakeGrant(user, item, "phone"), 8, 8, out var denial)); // a stream of its own: the user limit applies
        Assert.Equal(SlotDenial.User, denial);
    }

    [Fact]
    public void Warden07_M1_FourDeviceIdsOnOneItem_CannotTakeFourConversionSlots()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 4);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        using var lease = slots.TryOpenStatic(user, item, "p", 8, out _)!;
        var taken = Enumerable.Range(0, 4).Count(i => slots.TryTakeHls(MakeGrant(user, item, "device-" + i), 8, 8, out _));

        Assert.True(taken <= 3, "taken " + taken);
        Assert.True(slots.ActiveSlotsOnPeer("p") <= 4);
    }

    [Fact]
    public void Warden07_M2_APlaybackStoppedCannotFreeASlotWhoseBodyIsStillStreaming()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 2);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var grant = MakeGrant(user, item, "d");
        var lease = slots.TryOpenStatic(user, item, "p", 4, out _)!;

        Assert.False(slots.ReleaseStatic(user, item)); // refused while a connection is open
        Assert.True(slots.HoldsSlot(grant));
        Assert.Equal(1, slots.ActiveSlots(user));

        lease.Dispose(); // the last connection ends: now the stop takes effect
        Assert.False(slots.HoldsSlot(grant));
        Assert.Equal(0, slots.ActiveSlots(user));
    }

    [Fact]
    public void Warden07_M2_AStopWithNoOpenConnection_ReleasesAtOnce()
    {
        var slots = new StreamSlots(new ManualTimeProvider(), () => 2);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var lease = slots.TryOpenStatic(user, item, "p", 4, out _)!;
        lease.Dispose(); // the body ended; the slot lingers for the grace period

        Assert.True(slots.ReleaseStatic(user, item));
        Assert.Equal(0, slots.ActiveSlots(user));
    }
}
