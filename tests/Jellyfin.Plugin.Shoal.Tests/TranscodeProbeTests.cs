using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>U9 (DESIGN 7.1): the deep test's transcode probe and the suggested "Conversions at once".</summary>
public sealed class TranscodeProbeTests
{
    private const string PlaySession = "probe-session-1";
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SourceId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static CatalogEntry Entry() => new() { PeerItemId = ItemId.ToString("N"), PeerMediaSourceId = SourceId.ToString("N"), SizeBytes = 1000, RuntimeTicks = 20 * TimeSpan.TicksPerMinute, State = EntryState.Active };

    private static string MainPlaylist(double seconds, int count) =>
        "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n"
        + string.Concat(Enumerable.Range(0, count).Select(i => $"#EXTINF:{seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}, nodesc\nhls1/main/{i}.ts?DeviceId=x&PlaySessionId={PlaySession}&ApiKey=peerkey&runtimeTicks={i}&actualSegmentLengthTicks=6000000\n"))
        + "#EXT-X-ENDLIST\n";

    private static void Script(FakeJellyfinPeer peer, double segmentSeconds, int segments, int startupMs, int steadyMs, bool transcodeUrl = true)
    {
        peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?>
        {
            ["MediaSources"] = new[] { new Dictionary<string, object?> { ["Id"] = SourceId.ToString("N"), ["SupportsDirectPlay"] = false, ["TranscodingUrl"] = transcodeUrl ? $"/videos/{ItemId:N}/master.m3u8?DeviceId=x&MediaSourceId={SourceId:N}&PlaySessionId={PlaySession}&ApiKey=peerkey&VideoCodec=h264" : null } },
            ["PlaySessionId"] = PlaySession
        });
        peer.HlsPlaylistReply = (path, _) => path.EndsWith("/master.m3u8", StringComparison.Ordinal)
            ? new Reply(200, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000\nmain.m3u8?DeviceId=x&PlaySessionId=" + PlaySession + "&ApiKey=peerkey&VideoCodec=h264\n", "application/x-mpegURL")
            : path.EndsWith("/main.m3u8", StringComparison.Ordinal) ? new Reply(200, MainPlaylist(segmentSeconds, segments), "application/x-mpegURL") : new Reply(404);
        peer.SegmentOverride = async ctx =>
        {
            var index = int.Parse(((string)ctx.Request.RouteValues["segment"]!).Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            await Task.Delay(index < 3 ? startupMs : steadyMs);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.ContentLength = 1000;
            await ctx.Response.Body.WriteAsync(new byte[1000]);
            return true;
        };
    }

    private static async Task<(TranscodeProbeResult Result, FakeJellyfinPeer Peer)> RunAsync(Action<FakeJellyfinPeer> script)
    {
        var peer = await FakeJellyfinPeer.StartAsync();
        script(peer);
        using var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
        var endpoint = new PeerEndpoint("4b0c1d2e3f405162738495a6b7c8d9e0", "Fern", new Uri(peer.BaseUrl), "shoal-test-device", "tok-123");
        var result = await new TranscodeProbe(client, NullLogger.Instance).RunAsync(endpoint, Entry(), default);
        return (result, peer);
    }

    [Fact]
    public async Task TheProbe_TimesSegments3To8_AndEndsWithTheScopedDelete()
    {
        var (result, peer) = await RunAsync(p => Script(p, segmentSeconds: 0.6, segments: 30, startupMs: 700, steadyMs: 200));
        await using (peer)
        {
            Assert.Null(result.Error);
            Assert.InRange(result.MeanTtfbMs!.Value, 190, 2000); // the slow start of segments 0 to 2 is not counted (the delay is a floor; a slow machine only adds)
            Assert.InRange(result.Speed!.Value, 0.3, 3.2); // 0.6 s of video per at least 0.2 s of waiting
            Assert.Equal(9, peer.To("/Videos/" + ItemId.ToString("N") + "/hls1/main/0.ts").Count() + Enumerable.Range(1, 8).Sum(i => peer.To($"/Videos/{ItemId:N}/hls1/main/{i}.ts").Count()));

            var kill = Assert.Single(peer.Requests, r => r.Method == "DELETE");
            Assert.Equal("/Videos/ActiveEncodings", kill.Path);
            Assert.Contains("deviceId=shoal-test-device", kill.Query, StringComparison.Ordinal);
            Assert.Contains("playSessionId=" + PlaySession, kill.Query, StringComparison.Ordinal);

            var info = peer.Requests.Single(r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));
            var body = (JsonObject)JsonNode.Parse(info.Body)!;
            Assert.Equal(8_000_000, (int)body["MaxStreamingBitrate"]!);
            Assert.False((bool)body["EnableDirectPlay"]!);
            Assert.True((bool)body["EnableTranscoding"]!);
            Assert.DoesNotContain(peer.Requests, r => r.Query.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)); // the peer's own key never goes into a URL
        }
    }

    [Fact]
    public async Task APeerThatAnswersWithoutATranscode_GivesNoSpeed_AndNothingToStop()
    {
        var (result, peer) = await RunAsync(p => Script(p, 0.6, 30, 10, 10, transcodeUrl: false));
        await using (peer)
        {
            Assert.Equal("noTranscode", result.Error);
            Assert.Null(result.Speed);
            Assert.DoesNotContain(peer.Requests, r => r.Method == "DELETE");
        }
    }

    [Fact]
    public async Task AHostilePlaylist_IsRefused_ButTheJobIsStillStopped()
    {
        var (result, peer) = await RunAsync(p =>
        {
            Script(p, 0.6, 30, 10, 10);
            p.HlsPlaylistReply = (path, _) => path.EndsWith("/master.m3u8", StringComparison.Ordinal)
                ? new Reply(200, "#EXTM3U\nhttp://evil.example/main.m3u8?x=1\n", "application/x-mpegURL")
                : new Reply(404);
        });
        await using (peer)
        {
            Assert.Equal("badPlaylist", result.Error);
            Assert.Single(peer.Requests, r => r.Method == "DELETE");
            Assert.DoesNotContain(peer.Requests, r => r.Path.Contains("evil", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.49, 1)]
    [InlineData(1.5, 1)]
    [InlineData(3.0, 2)]
    [InlineData(4.4, 2)]
    [InlineData(4.5, 3)]
    [InlineData(6.0, 4)]
    [InlineData(40.0, 4)]
    public void TheSuggestedConversions_AreFloorOfSpeedOverOnePointFive_BetweenOneAndFour(double speed, int expected) =>
        Assert.Equal(expected, TranscodeProbe.SuggestedConversions(speed));

    [Fact]
    public void ThePickedTitle_IsTheSmallestOneLongEnoughForNineSegments()
    {
        var small = new CatalogEntry { PeerItemId = ItemId.ToString("N"), SizeBytes = 100, RuntimeTicks = TimeSpan.TicksPerMinute, State = EntryState.Active }; // a trailer
        var good = new CatalogEntry { PeerItemId = Guid.NewGuid().ToString("N"), SizeBytes = 900, RuntimeTicks = 90 * TimeSpan.TicksPerMinute, State = EntryState.Active };
        var big = new CatalogEntry { PeerItemId = Guid.NewGuid().ToString("N"), SizeBytes = 5000, RuntimeTicks = 120 * TimeSpan.TicksPerMinute, State = EntryState.Active };
        var held = new CatalogEntry { PeerItemId = Guid.NewGuid().ToString("N"), SizeBytes = 10, RuntimeTicks = 90 * TimeSpan.TicksPerMinute, State = EntryState.HeldRemoval };

        Assert.Same(good, TranscodeProbe.Pick([big, small, good, held]));
        Assert.Same(small, TranscodeProbe.Pick([small]));
        Assert.Null(TranscodeProbe.Pick([held]));
        Assert.Null(TranscodeProbe.Pick([]));
    }

    [Fact]
    public void TheUriRules_RefuseWhatCouldLeaveThePeer()
    {
        Assert.True(TranscodeProbe.SafeUri("hls1/main/3.ts?DeviceId=x&runtimeTicks=1"));
        Assert.False(TranscodeProbe.SafeUri("http://evil/x"));
        Assert.False(TranscodeProbe.SafeUri("//evil/x"));
        Assert.False(TranscodeProbe.SafeUri("/abs"));
        Assert.False(TranscodeProbe.SafeUri("hls1/../../x"));
        Assert.False(TranscodeProbe.SafeUri("a b"));
        Assert.Equal("main.m3u8?a=b&c=d", TranscodeProbe.WithoutKeys("main.m3u8?a=b&ApiKey=secret&c=d&api_key=s2"));
        Assert.Equal("main.m3u8", TranscodeProbe.WithoutKeys("main.m3u8?ApiKey=secret"));
    }
}
