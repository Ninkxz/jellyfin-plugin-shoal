using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// One matrix harness with the real filter, shared by the tests of a class (they run one after the other).
/// </summary>
public sealed class FilterFixture : IAsyncLifetime
{
    internal MatrixHarness H { get; private set; } = null!;

    public async Task InitializeAsync() => H = await MatrixHarness.StartAsync(withFilter: true);

    public async Task DisposeAsync() => await H.DisposeAsync();
}

/// <summary>
/// K13 acceptance (DESIGN 6.1, 8.7, 8.8): the interception table and the filter's mechanics, the T-SEC-15 matrix against the real routes of core
/// (static GET and HEAD, the core subtitle routes, PlaybackInfo) with recording handlers, T-CLI-1, T-CLI-5, T-CLI-6, R16, T-CLI-14, and the
/// p99 cost on requests that are not for a federated item.
/// </summary>
public sealed class InterceptFilterTests : IClassFixture<FilterFixture>, IAsyncLifetime
{
    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string ItemB = MatrixWorld.ItemB.ToString("N");
    private static readonly string Local = MatrixWorld.LocalItem.ToString("N");

    private readonly FilterFixture _fixture;

    public InterceptFilterTests(FilterFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, (HttpMethod Method, string Path)> Routes { get; } = new()
    {
        ["static-get"] = (HttpMethod.Get, $"/Videos/{Item}/stream?Static=true&MediaSourceId={Item}"),
        ["static-head"] = (HttpMethod.Head, $"/Videos/{Item}/stream?Static=true&MediaSourceId={Item}"),
        ["static-container"] = (HttpMethod.Get, $"/Videos/{Item}/stream.mkv?static=true&mediaSourceId={Item}"),
        ["subtitle"] = (HttpMethod.Get, $"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt"),
        ["subtitle-ticks"] = (HttpMethod.Get, $"/Videos/{Item}/{Item}/Subtitles/2/0/Stream.srt"),
        ["playbackinfo-post"] = (HttpMethod.Post, $"/Items/{Item}/PlaybackInfo"),
        ["playbackinfo-get"] = (HttpMethod.Get, $"/Items/{Item}/PlaybackInfo"),
    };

    private static readonly string[] RowsThatAreCallerContextOnly = ["R16", "R17"];

    public static TheoryData<string, string> RowsOnRoutes()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in Routes.Keys)
        {
            foreach (var row in CallerContextTests.Rows.Where(static r => !RowsThatAreCallerContextOnly.Contains(r.Id)))
            {
                // R4: for an [Authorize] route core's own policy handler throws before Shoal runs (core maps that to 401 or 404 itself).
                if (route.StartsWith("playbackinfo", StringComparison.Ordinal) && row.Id == "R4")
                {
                    continue;
                }

                data.Add(route, row.Id);
            }
        }

        return data;
    }

    private string Principal(Guid user, params Claim[] extra) => H.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user, extra));

    private string ApiKey() => Principal(MatrixWorld.Admin, new Claim(CallerContext.IsApiKeyClaim, "True"));

    private async Task<HttpResponseMessage> Send(string? principal, HttpMethod method, string path, Action<HttpRequestMessage>? configure = null)
    {
        await H.Filter!.Index.RebuildAsync();
        return await H.SendAsync(principal, method, path, configure);
    }

    // --- the table

    [Theory]
    [InlineData("Items/{itemId}/PlaybackInfo", RouteKind.PlaybackInfo, "itemId", false)]
    [InlineData("Items/{itemId:guid}/PlaybackInfo", RouteKind.PlaybackInfo, "itemId", false)]
    [InlineData("items/{ITEMID}/playbackinfo", RouteKind.PlaybackInfo, "ITEMID", false)]
    [InlineData("/Videos/{itemId}/stream", RouteKind.StaticStream, "itemId", true)]
    [InlineData("Videos/{itemId}/stream.{container}", RouteKind.StaticStream, "itemId", true)]
    [InlineData("Videos/{itemId}/stream.{container?}", RouteKind.StaticStream, "itemId", true)]
    [InlineData("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}", RouteKind.Subtitle, "routeItemId", true)]
    [InlineData("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks}/Stream.{routeFormat}", RouteKind.Subtitle, "routeItemId", true)]
    [InlineData("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks?}/Stream.{routeFormat}", RouteKind.Subtitle, "routeItemId", true)]
    [InlineData("Items/{itemId}/Download", RouteKind.Forbidden, "itemId", true)]
    [InlineData("Items/{itemId}/File", RouteKind.Forbidden, "itemId", true)]
    [InlineData("LiveStreams/Open", RouteKind.LiveStreamsOpen, "itemId", false)]
    [InlineData("Videos/{itemId}/master.m3u8", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/main.m3u8", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/live.m3u8", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/subtitles.m3u8", RouteKind.CoreHls, "itemId", true)]
    [InlineData("Videos/{itemId}/Trickplay/{width}/tiles.m3u8", RouteKind.TrickplayPlaylist, "itemId", true)]
    [InlineData("Videos/{itemId}/Trickplay/{width}/{index}.jpg", RouteKind.Trickplay, "itemId", true)]
    [InlineData("Videos/{videoId}/{mediaSourceId}/Attachments/{index}", RouteKind.Attachment, "videoId", true)]
    [InlineData("Audio/{itemId}/stream", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/stream.{container}", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/universal", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/master.m3u8", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/main.m3u8", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/hls/{segmentId}/stream.mp3", RouteKind.Audio, "itemId", true)]
    [InlineData("Audio/{itemId}/hls/{segmentId}/stream.aac", RouteKind.Audio, "itemId", true)]
    [InlineData("Videos/ActiveEncodings", RouteKind.ActiveEncodings, null, false)]
    public void EveryRowOfDesign61_IsInTheTable(string template, RouteKind kind, string? idName, bool readsStub)
    {
        var route = InterceptTable.Find(template);

        Assert.NotNull(route);
        Assert.Equal(kind, route.Kind);
        Assert.Equal(idName, route.IdName);
        Assert.Equal(readsStub, route.ReadsStub);
    }

    [Theory]
    [InlineData("Items/{itemId}/Images/{imageType}")]
    [InlineData("Items/{itemId}")]
    [InlineData("Videos/{itemId}/AdditionalParts")]
    [InlineData("Users/{userId}/Items/{itemId}")]
    [InlineData("MediaSegments/{itemId}")]
    [InlineData("Videos/{itemId}")]
    [InlineData("Audio/{itemId}/Lyrics")]
    [InlineData("System/Info")]
    [InlineData("")]
    [InlineData(null)]
    public void ARouteThatIsNotInTheTable_IsLeftAlone(string? template)
    {
        Assert.Null(InterceptTable.Find(template));
    }

    // --- T-SEC-15 against the real routes

    [Theory]
    [MemberData(nameof(RowsOnRoutes))]
    public async Task T_SEC_15_EveryRow_HoldsOnTheRealRoutes(string route, string rowId)
    {
        var row = CallerContextTests.Rows.Single(r => r.Id == rowId);
        var (method, path) = Routes[route];
        var isPlaybackInfo = route.StartsWith("playbackinfo", StringComparison.Ordinal);
        if (isPlaybackInfo && rowId == "R12")
        {
            // The PlaybackInfo column answers 403 with the NotAllowed code in a small JSON body.
            await H.ResetAsync();
            var key = await row.Arrange(H);
            await H.Filter!.Index.RebuildAsync();
            var before = H.Peer.Requests.Count;
            using var response = await H.SendAsync(key, method, path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("{\"ErrorCode\":\"NotAllowed\"}", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, H.Peer.Requests.Count);
            return;
        }

        Assert.Null(await H.CheckRouteAsync(row, method, path));
    }

    [Fact]
    public async Task AFederatedRequestWithoutCredentials_Is401_AndCountedOnlyAsNoCredentials()
    {
        var counted = H.Filter!.Counters.Get(InterceptCounters.NoCredentials);
        var other = H.Filter.Counters.Snapshot().Where(static p => p.Key != InterceptCounters.NoCredentials).Sum(static p => p.Value);

        foreach (var route in new[] { "static-get", "subtitle" }) // the anonymous core routes: the authorized ones are refused by core before Shoal runs
        {
            var (method, path) = Routes[route];
            using var response = await Send(null, method, path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(counted + 2, H.Filter.Counters.Get(InterceptCounters.NoCredentials));
        Assert.Equal(other, H.Filter.Counters.Snapshot().Where(static p => p.Key != InterceptCounters.NoCredentials).Sum(static p => p.Value));
    }

    [Fact]
    public async Task ThePositiveControl_ReachesTheHandler_ExactlyOnce_AndNeverCore()
    {
        var calls = H.Filter!.Handlers.Calls.Count;
        var key = Principal(MatrixWorld.Admin);

        using var response = await Send(key, HttpMethod.Get, Routes["static-get"].Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(calls + 1, H.Filter.Handlers.Calls.Count);
        Assert.Equal("Videos/{itemId}/stream", H.Filter.Handlers.Calls.Last());
        Assert.Contains("\"handled\":\"Videos/{itemId}/stream\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // --- items that are not federated

    [Theory]
    [InlineData("GET", "/Videos/{0}/stream?Static=true")]
    [InlineData("HEAD", "/Videos/{0}/stream?Static=true")]
    [InlineData("GET", "/Videos/{0}/stream.mkv?Static=true")]
    [InlineData("GET", "/Videos/{0}/{0}/Subtitles/2/Stream.vtt")]
    [InlineData("GET", "/Videos/{0}/{0}/Subtitles/2/0/Stream.srt")]
    [InlineData("GET", "/Videos/{0}/master.m3u8")]
    [InlineData("GET", "/Videos/{0}/hls1/main/0.mp4")]
    [InlineData("GET", "/Audio/{0}/stream?Static=true")]
    [InlineData("GET", "/Items/{0}/Download")]
    [InlineData("GET", "/Items/{0}/File")]
    [InlineData("GET", "/Items/{0}/PlaybackInfo")]
    [InlineData("POST", "/Items/{0}/PlaybackInfo")]
    [InlineData("POST", "/LiveStreams/Open?itemId={0}")]
    [InlineData("GET", "/Items/{0}/Images/Primary")]
    public async Task ALocalItem_PlaysExactlyAsBefore_CoreAnswersEveryRoute(string method, string template)
    {
        var key = Principal(MatrixWorld.Admin);
        var before = H.Peer.Requests.Count;

        using var response = await Send(key, new HttpMethod(method), string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Local));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(FakeCoreController.Header), "core's action must run for a local item");
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task ARouteOutsideTheTable_IsLeftToCore_EvenForAFederatedItem()
    {
        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Items/{Item}/Images/Primary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(FakeCoreController.Header));
    }

    [Fact]
    public async Task AnUnparseableItemId_IsLeftToCore_WhichAnswersItself()
    {
        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, "/Videos/not-a-guid/stream?Static=true");

        Assert.True(response.Headers.Contains(FakeCoreController.Header));
    }

    // --- T-CLI-1: credential-less direct play

    [Theory]
    [InlineData("/Videos/{0}/stream?container=mkv&static=true&mediaSourceId={0}&tag=abc&playSessionId=ps1&deviceId=tv-1")] // Android TV
    [InlineData("/Videos/{0}/stream.mkv?Static=true&mediaSourceId={0}&Tag=abc")] // Swiftfin
    [InlineData("/Videos/{0}/stream?Static=true&MediaSourceId={0}&mediaSourceId={0}")] // Findroid
    [InlineData("/Videos/{0}/{0}/Subtitles/2/Stream.vtt")]
    [InlineData("/Videos/{0}/{0}/Subtitles/2/0/Stream.srt")]
    public async Task T_CLI_1_CredentialLessRequests_Are401_WithNoPeerRequest(string template)
    {
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Item);
        var before = H.Peer.Requests.Count;

        using var response = await Send(null, HttpMethod.Get, path, r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(before, H.Peer.Requests.Count);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    // --- T-CLI-5, T-CLI-6

    [Theory]
    [InlineData("/Videos/{0}/stream?Static=true&Container=mkv&PlaySessionId=p&AudioStreamIndex=1&MediaSourceId={0}&UserId=u")] // Roku
    [InlineData("/Videos/{0}/stream.mkv?Static=true&mediaSourceId={0}&deviceId=tv-1&ApiKey=tok&Tag=etag")] // Smart-TV
    [InlineData("/Videos/{0}/stream.mov,mp4,m4a,3gp,3g2,mj2?Static=true&MediaSourceId={0}")]
    [InlineData("/Videos/{0}/stream?STATIC=TRUE&MediaSourceId={0}")]
    [InlineData("/Videos/{0}/stream?static=True")]
    public async Task T_CLI_5_TheStaticShapes_ReachTheHandler(string template)
    {
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Item);
        var calls = H.Filter!.Handlers.Calls.Count;

        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(calls + 1, H.Filter.Handlers.Calls.Count);
    }

    [Fact]
    public async Task T_CLI_5_AnEmptyContainer_MatchesNoRoute_SoRoutingAnswers404()
    {
        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream.?Static=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("anonymous")]
    [InlineData("apikey")]
    public async Task T_CLI_6_NonStaticStreams_AndTheAudioRoutes_Are404_ForEveryCaller_AndCounted(string caller)
    {
        var paths = new[]
        {
            $"/Videos/{Item}/stream?Static=false&MediaSourceId={Item}",
            $"/Videos/{Item}/stream",
            $"/Audio/{Item}/main.m3u8?AudioCodec=copy&SegmentContainer=mp4",
            $"/Audio/{Item}/universal",
            $"/Audio/{Item}/stream?Static=true",
            $"/Audio/{Item}/stream.mp3",
        };
        var nonStatic = H.Filter!.Counters.Get(InterceptCounters.NonStaticStream);
        var audio = H.Filter.Counters.Get(InterceptCounters.AudioRoute);
        var before = H.Peer.Requests.Count;

        foreach (var path in paths)
        {
            var key = caller switch { "user" => Principal(MatrixWorld.Admin), "apikey" => ApiKey(), _ => null };
            using var response = await Send(key, HttpMethod.Get, path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header), path);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(nonStatic + 2, H.Filter.Counters.Get(InterceptCounters.NonStaticStream)); // the two /Videos stream requests without Static=true
        Assert.Equal(audio + 4, H.Filter.Counters.Get(InterceptCounters.AudioRoute));
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task TheTrickplayPlaylist_Answers404_ForEveryCaller_AndIsCountedOnItsOwn()
    {
        var path = $"/Videos/{Item}/Trickplay/320/tiles.m3u8";
        var counted = H.Filter!.Counters.Get(InterceptCounters.TrickplayPlaylist);
        var core = H.Filter.Counters.Get(InterceptCounters.CoreHls);

        foreach (var key in new string?[] { null, Principal(MatrixWorld.Admin), ApiKey() })
        {
            using var response = await Send(key, HttpMethod.Get, path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header));
        }

        Assert.Equal(counted + 3, H.Filter.Counters.Get(InterceptCounters.TrickplayPlaylist));
        Assert.Equal(core, H.Filter.Counters.Get(InterceptCounters.CoreHls));
    }

    [Theory]
    [InlineData("/Videos/{0}/master.m3u8?DeviceId=d&MediaSourceId={0}")]
    [InlineData("/Videos/{0}/main.m3u8")]
    [InlineData("/Videos/{0}/live.m3u8")]
    [InlineData("/Videos/{0}/hls1/main/0.mp4")]
    [InlineData("/Videos/{0}/hls/main/0.ts")]
    [InlineData("/Videos/{0}/{0}/Subtitles/1/subtitles.m3u8")]
    [InlineData("/Videos/{0}/{0}/Attachments/1")]
    [InlineData("/Videos/{0}/Trickplay/320/0.jpg")]
    public async Task TheCoreHlsAttachmentAndTrickplayRoutes_Answer404_ForEveryCaller(string template)
    {
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Item);
        var counted = H.Filter!.Counters.Get(InterceptCounters.CoreHls);

        foreach (var key in new string?[] { null, Principal(MatrixWorld.Admin), ApiKey() })
        {
            using var response = await Send(key, HttpMethod.Get, path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header));
        }

        Assert.Equal(counted + 3, H.Filter.Counters.Get(InterceptCounters.CoreHls));
    }

    [Theory]
    [InlineData("Download")]
    [InlineData("File")]
    public async Task T_CLI_6_DownloadAndFile_AreCoresOwn401ForAnonymous_AndShoals403ForEverySignedInCaller(string name)
    {
        var path = $"/Items/{Item}/{name}";
        var before = H.Peer.Requests.Count;

        using (var anonymous = await Send(null, HttpMethod.Get, path))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); // core's [Authorize] runs first
            Assert.False(anonymous.Headers.Contains(FakeCoreController.Header));
        }

        foreach (var key in new[] { Principal(MatrixWorld.Admin), Principal(MatrixWorld.Other), ApiKey() })
        {
            using var response = await Send(key, HttpMethod.Get, path);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header));
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Theory]
    [InlineData("/LiveStreams/Open?itemId={F}", true)]
    [InlineData("/LiveStreams/Open?itemId={L}&itemId={F}", true)]
    [InlineData("/LiveStreams/Open?itemId={F}&itemId={L}", true)]
    [InlineData("/LiveStreams/Open?ITEMID={F}", true)]
    [InlineData("/LiveStreams/Open?itemId=%20{F}%20", true)]
    [InlineData("/LiveStreams/Open?itemId={FP}", true)]
    [InlineData("/LiveStreams/Open?itemId={FB}", true)]
    [InlineData("/LiveStreams/Open?itemId={FU}", true)]
    [InlineData("/LiveStreams/Open?itemId={L}", false)]
    [InlineData("/LiveStreams/Open?itemId=garbage", false)]
    [InlineData("/LiveStreams/Open", false)]
    public async Task LiveStreamsOpen_IsA400_WhenAnyItemIdValueIsFederated(string template, bool federated)
    {
        var f = MatrixWorld.Item;
        var path = template
            .Replace("{FP}", "(" + f.ToString("D") + ")", StringComparison.Ordinal)
            .Replace("{FB}", "{" + f.ToString("D") + "}", StringComparison.Ordinal)
            .Replace("{FU}", f.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("{F}", Item, StringComparison.Ordinal)
            .Replace("{L}", Local, StringComparison.Ordinal);

        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Post, path);

        if (federated)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header));
        }
        else
        {
            Assert.True(response.Headers.Contains(FakeCoreController.Header));
        }
    }

    // --- R16: the id that is authorized is the id that is served

    [Theory]
    [InlineData("{B}")]
    [InlineData("{BU}")]
    [InlineData("{BD}")]
    [InlineData("{BB}")]
    [InlineData("{BP}")]
    [InlineData("%20{B}%20")]
    [InlineData("{B}&itemId={B}")]
    [InlineData("{A}&itemId={B}")]
    [InlineData("{B}&itemId={A}")]
    [InlineData("{L}&itemId={B}")]
    [InlineData("garbage")]
    public async Task R16_AFederatedRouteIdWithADifferentItemId_Is404_BeforeAnythingElse(string queryValue)
    {
        var b = MatrixWorld.ItemB;
        var value = queryValue
            .Replace("{BU}", b.ToString("N").ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("{BD}", b.ToString("D"), StringComparison.Ordinal)
            .Replace("{BB}", "{" + b.ToString("D") + "}", StringComparison.Ordinal)
            .Replace("{BP}", "(" + b.ToString("D") + ")", StringComparison.Ordinal)
            .Replace("{B}", ItemB, StringComparison.Ordinal)
            .Replace("{A}", Item, StringComparison.Ordinal)
            .Replace("{L}", Local, StringComparison.Ordinal);
        var other = Principal(MatrixWorld.Other); // admitted to library 2 (item B) only
        var before = H.Peer.Requests.Count;
        var calls = H.Filter!.Handlers.Calls.Count;

        foreach (var suffix in new[] { $"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt?itemId={value}", $"/Videos/{Item}/{Item}/Subtitles/2/0/Stream.srt?itemId={value}&format=srt" })
        {
            using var response = await Send(other, HttpMethod.Get, suffix);

            if (queryValue == "{A}&itemId={A}")
            {
                continue;
            }

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(FakeCoreController.Header));
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(before, H.Peer.Requests.Count);
        Assert.Equal(calls, H.Filter.Handlers.Calls.Count);
    }

    [Theory]
    [InlineData("{L}&itemId={B}")]
    [InlineData("{B}&itemId={L}")]
    [InlineData("{L}&itemId=garbage&itemId={B}")]
    public async Task R16_ALocalRouteIdWithAFederatedItemId_Is404_AndCoreNeverResolvesTheStub(string queryValue)
    {
        var value = queryValue.Replace("{B}", ItemB, StringComparison.Ordinal).Replace("{L}", Local, StringComparison.Ordinal);
        var before = H.Peer.Requests.Count;

        using var response = await Send(Principal(MatrixWorld.Other), HttpMethod.Get, $"/Videos/{Local}/{Local}/Subtitles/2/Stream.vtt?itemId={value}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(before, H.Peer.Requests.Count);
    }

    [Fact]
    public async Task ARouteIdThatEqualsTheItemId_IsServedForTheRouteId()
    {
        var calls = H.Filter!.Handlers.Calls.Count;

        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt?itemId={Item.ToUpperInvariant()}&format=vtt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(calls + 1, H.Filter.Handlers.Calls.Count);
    }

    [Fact]
    public async Task ALocalSubtitleRoute_WithALocalItemId_PassesToCore()
    {
        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Local}/{Local}/Subtitles/2/Stream.vtt?itemId={Local}");

        Assert.True(response.Headers.Contains(FakeCoreController.Header));
    }

    [Fact]
    public async Task Warden07_L1_ALocalStaticStream_NamingAFederatedMediaSource_Is404_BeforeCoreAndBeforeAnyHandler()
    {
        var calls = H.Filter!.Handlers.Calls.Count;

        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Local}/stream?Static=true&mediaSourceId={Item}");
        using var upper = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Local}/stream.mkv?static=true&MediaSourceId={Item.ToUpperInvariant()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, upper.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.False(upper.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(calls, H.Filter.Handlers.Calls.Count);
    }

    [Fact]
    public async Task Warden07_L1_ALocalStaticStream_WithALocalMediaSource_StillGoesToCore()
    {
        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Local}/stream?Static=true&mediaSourceId={Local}");

        Assert.True(response.Headers.Contains(FakeCoreController.Header));
    }

    [Fact]
    public async Task Warden07_L1_ALocalSubtitleRoute_NamingAFederatedMediaSource_Is404()
    {
        var calls = H.Filter!.Handlers.Calls.Count;

        using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Local}/{Item}/Subtitles/2/Stream.vtt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(calls, H.Filter.Handlers.Calls.Count);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public async Task EveryGuidFormTheBinderAccepts_IsRecognizedOnTheRealRoutes(string format)
    {
        var id = MatrixWorld.Item.ToString(format);
        var routes = new[]
        {
            $"/Videos/{Uri.EscapeDataString(id)}/stream?Static=false",
            $"/Audio/{Uri.EscapeDataString(id)}/universal",
            $"/Items/{Uri.EscapeDataString(id)}/Download",
            $"/Videos/{Uri.EscapeDataString(id.ToUpperInvariant())}/master.m3u8",
        };

        foreach (var path in routes)
        {
            using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, path);

            Assert.False(response.Headers.Contains(FakeCoreController.Header), path);
        }
    }

    // --- failures, logging, limits

    [Fact]
    public async Task AHandlerThatThrows_Is500_WithNoBody_AndTheTypeOnlyInTheLog()
    {
        H.Filter!.Handlers.Throw = new InvalidOperationException("secret peer host hops.tail1234.ts.net");
        try
        {
            using var response = await Send(Principal(MatrixWorld.Admin), HttpMethod.Get, Routes["static-get"].Path);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Contains("InvalidOperationException", H.Filter.Log.All, StringComparison.Ordinal);
            Assert.DoesNotContain("hops.tail1234", H.Filter.Log.All, StringComparison.Ordinal);
        }
        finally
        {
            H.Filter.Handlers.Throw = null;
        }
    }

    [Fact]
    public async Task T_CLI_14_NoShoalLogLine_ContainsATokenAPathOrAQuery()
    {
        const string Token = "SUPERSECRETTOKEN0123456789";
        var key = Principal(MatrixWorld.Admin);
        var paths = new[]
        {
            $"/Videos/{Item}/stream?Static=true&ApiKey={Token}&MediaSourceId={Item}",
            $"/Videos/{Item}/stream.mkv?static=true&api_key={Token}",
            $"/Videos/{Item}/stream?Static=false&ApiKey={Token}",
            $"/Videos/{Item}/{Item}/Subtitles/2/Stream.vtt?ApiKey={Token}&itemId={ItemB}",
            $"/Items/{Item}/Download?ApiKey={Token}",
            $"/Audio/{Item}/stream?ApiKey={Token}",
            $"/Videos/{Item}/master.m3u8?ApiKey={Token}",
            $"/LiveStreams/Open?itemId={Item}&ApiKey={Token}",
            $"/Items/{Item}/PlaybackInfo?ApiKey={Token}&UserId={MatrixWorld.Admin}",
        };

        foreach (var path in paths)
        {
            using var response = await Send(key, HttpMethod.Get, path, r => r.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\", DeviceId=\"d\""));
        }

        var log = H.Filter!.Log.All;
        Assert.DoesNotContain(Token, log, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", log.Replace("(?)", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("/Videos/", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Item, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFederatedRequestWithAnOversizedQuery_Is414_ButALocalOneIsCoresBusiness()
    {
        await H.Filter!.Index.RebuildAsync();
        var filter = new ShoalInterceptFilter(
            H.Filter.Index,
            null!,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Counters,
            NullLogger<ShoalInterceptFilter>.Instance);
        var long8k = "?Static=true&x=" + new string('a', 9000);
        var federated = ContextFor("Videos/{itemId}/stream", MatrixWorld.Item);
        federated.HttpContext.Request.QueryString = new QueryString(long8k);
        var local = ContextFor("Videos/{itemId}/stream", MatrixWorld.LocalItem);
        local.HttpContext.Request.QueryString = new QueryString(long8k);
        var nextCalls = 0;
        Task<ResourceExecutedContext> Next()
        {
            nextCalls++;
            return Task.FromResult<ResourceExecutedContext>(null!);
        }

        await filter.OnResourceExecutionAsync(federated, Next);
        await filter.OnResourceExecutionAsync(local, Next);

        Assert.Equal(414, Assert.IsType<Jellyfin.Plugin.Shoal.Http.ShoalResult>(federated.Result).Status);
        Assert.Equal(1, H.Filter.Counters.Get(InterceptCounters.LongQuery));
        Assert.Null(local.Result);
        Assert.Equal(1, nextCalls);
        Assert.False(Jellyfin.Plugin.Shoal.Http.ShoalRequest.QueryTooLong(new DefaultHttpContext().Request));
    }


    [Fact]
    public async Task AStubScannedAfterTheLastSnapshot_IsStillInterceptedOnTheStubReadingRoutes()
    {
        await H.ResetAsync();
        var late = Guid.Parse("d0000000-0000-0000-0000-000000000001");
        await H.Filter!.Index.RebuildAsync(); // the snapshot does not know the item
        H.World.Federated[late] = new FederatedItem(H.World.PeerKey, H.World.MappingKey);
        foreach (var user in new[] { MatrixWorld.Admin })
        {
            H.World.Visible[(user, late)] = true;
        }

        var key = Principal(MatrixWorld.Admin);
        using var stream = await H.SendAsync(key, HttpMethod.Get, $"/Videos/{late:N}/stream?Static=true");
        using var download = await H.SendAsync(key, HttpMethod.Get, $"/Items/{late:N}/Download");
        using var subtitle = await H.SendAsync(key, HttpMethod.Get, $"/Videos/{late:N}/{late:N}/Subtitles/2/Stream.vtt");
        using var audio = await H.SendAsync(key, HttpMethod.Get, $"/Audio/{late:N}/stream?Static=true");

        Assert.False(stream.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
        Assert.False(subtitle.Headers.Contains(FakeCoreController.Header));
        Assert.Equal(HttpStatusCode.NotFound, audio.StatusCode);

        // The other routes use the index only (no I/O): a miss falls through to core until the next snapshot.
        H.World.Federated.TryRemove(late, out _);
    }

    [Fact]
    public async Task ActiveEncodings_IsAnsweredOnlyForAGrantTheCallerOwns()
    {
        var key = Principal(MatrixWorld.Admin);
        H.Filter!.Handlers.OwnedPlaySession = "ps-owned";
        try
        {
            using var owned = await Send(key, HttpMethod.Delete, "/Videos/ActiveEncodings?deviceId=d&playSessionId=ps-owned");
            using var other = await Send(key, HttpMethod.Delete, "/Videos/ActiveEncodings?deviceId=d&playSessionId=ps-other");

            Assert.Equal(HttpStatusCode.NoContent, owned.StatusCode);
            Assert.False(owned.Headers.Contains(FakeCoreController.Header));
            Assert.True(other.Headers.Contains(FakeCoreController.Header)); // no grant of that id: core handles it
        }
        finally
        {
            H.Filter.Handlers.OwnedPlaySession = null;
        }
    }

    [Fact]
    public async Task ThePeerIsNeverAskedForARefusedRequest_AcrossEveryRoute()
    {
        var before = H.Peer.Requests.Count;
        var paths = new[]
        {
            Routes["static-get"].Path,
            Routes["subtitle"].Path,
            $"/Items/{Item}/Download",
            $"/Audio/{Item}/universal",
            $"/Videos/{Item}/master.m3u8",
        };

        foreach (var path in paths)
        {
            foreach (var key in new string?[] { null, Principal(MatrixWorld.Other), Principal(MatrixWorld.ShareUser), ApiKey() })
            {
                using var response = await Send(key, HttpMethod.Get, path);

                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        Assert.Equal(before, H.Peer.Requests.Count);
    }

    // --- cost

    [Fact]
    public async Task OnRequestsThatAreNotForAFederatedItem_TheFilterCostsWellUnderATenthOfAMillisecond_AtP99()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
        var filter = new ShoalInterceptFilter(
            H.Filter.Index,
            null!,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Handlers,
            H.Filter.Counters,
            NullLogger<ShoalInterceptFilter>.Instance);
        var context = ContextFor("Videos/{itemId}/stream", Guid.Parse(Local));
        Task<ResourceExecutedContext> Next() => Task.FromResult<ResourceExecutedContext>(null!);

        for (var i = 0; i < 5000; i++)
        {
            await filter.OnResourceExecutionAsync(context, Next);
        }

        var batches = new List<double>();
        for (var batch = 0; batch < 200; batch++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 500; i++)
            {
                await filter.OnResourceExecutionAsync(context, Next);
            }

            batches.Add(watch.Elapsed.TotalMilliseconds / 500);
        }

        batches.Sort();
        var p99 = batches[(int)(batches.Count * 0.99)];
        Assert.True(p99 < 0.1, $"p99 of the per-call cost was {p99:F5} ms");
        Assert.Null(context.Result);
    }

    private static ResourceExecutingContext ContextFor(string template, Guid itemId)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.RouteValues["itemId"] = itemId.ToString("N");
        http.Request.QueryString = new QueryString("?Static=true");
        var descriptor = new ActionDescriptor { AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = template } };
        var action = new ActionContext(http, new RouteData(), descriptor);
        return new ResourceExecutingContext(action, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }
}
