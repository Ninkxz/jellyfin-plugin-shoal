using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using MediaBrowser.Model.Dlna;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// One matrix harness with the real filter and the real PlaybackInfo handler, shared by the tests of a class.
/// </summary>
public sealed class PlaybackFixture : IAsyncLifetime
{
    internal MatrixHarness H { get; private set; } = null!;

    public async Task InitializeAsync() => H = await MatrixHarness.StartAsync(withFilter: true, realPlayback: true);

    public async Task DisposeAsync() => await H.DisposeAsync();
}

/// <summary>
/// K14 acceptance (DESIGN 6.2, 3.5): T-SEC-20 (the body equals 6.2.1), the DeviceProfile rebuild, the query/body merge, the whitelisted answer with
/// sanitized streams, the canary, T-PLAY-13, the grant caps and the PlaybackInfo bucket, T-CLI-2, T-CLI-8, T-CLI-9, T-CLI-10 and T-CLI-15.
/// </summary>
public sealed class PlaybackInfoTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string PeerToken = FakeJellyfinPeer.DefaultToken;

    private static readonly string LocalItem = MatrixWorld.Item.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public PlaybackInfoTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1)); // refills every bucket and ages every old grant
        foreach (var user in new[] { MatrixWorld.Admin, MatrixWorld.Selected, MatrixWorld.Other })
        {
            foreach (var grant in P.Grants.ForUser(user))
            {
                P.Grants.Remove(grant.GrantId);
            }
        }

        P.Slots.Holding.Clear();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Devices.Profiles.Clear();
        P.Notice.Notices.Clear();
        P.Admins.Messages.Clear();
        P.HlsSubtitles = true;
        P.Limits = new BitrateLimits(0, null);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- fixtures

    private static object Stream(int index, string type, string codec, Action<Dictionary<string, object?>>? tweak = null)
    {
        var stream = new Dictionary<string, object?> { ["Index"] = index, ["Type"] = type, ["Codec"] = codec, ["IsDefault"] = index <= 1, ["IsExternal"] = false, ["SupportsExternalStream"] = type == "Subtitle" };
        tweak?.Invoke(stream);
        return stream;
    }

    private static object[] DefaultStreams() =>
    [
        Stream(0, "Video", "h264", s => { s["Width"] = 1920; s["Height"] = 1080; s["Profile"] = "High"; s["AspectRatio"] = "16:9"; }),
        Stream(1, "Audio", "eac3", s => { s["Language"] = "eng"; s["Channels"] = 6; s["ChannelLayout"] = "5.1"; s["Title"] = "English"; }),
        Stream(2, "Subtitle", "srt", s => { s["Language"] = "eng"; s["DeliveryMethod"] = "Embed"; }),
        Stream(3, "Subtitle", "srt", s => { s["Language"] = "fre"; s["IsExternal"] = true; s["DeliveryMethod"] = "External"; s["DeliveryUrl"] = $"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/3/0/Stream.srt?ApiKey={PeerToken}"; }),
        Stream(4, "Subtitle", "pgssub", s => { s["DeliveryMethod"] = "Embed"; }),
    ];

    private static object Answer(Action<Dictionary<string, object?>>? tweak = null, object[]? streams = null)
    {
        var source = new Dictionary<string, object?>
        {
            ["Id"] = PeerSourceN,
            ["Protocol"] = "File",
            ["Path"] = "/media/movies/The Matrix (1999)/The Matrix.mkv",
            ["Type"] = "Default",
            ["Container"] = "mkv",
            ["Size"] = 4_000_000_000L,
            ["Name"] = "The Matrix",
            ["IsRemote"] = false,
            ["ETag"] = "peer-etag-secret",
            ["RunTimeTicks"] = 72_000_000_000L,
            ["SupportsTranscoding"] = true,
            ["SupportsDirectStream"] = true,
            ["SupportsDirectPlay"] = true,
            ["Bitrate"] = 8_000_000,
            ["DefaultAudioStreamIndex"] = 1,
            ["DefaultSubtitleStreamIndex"] = -1,
            ["MediaStreams"] = streams ?? DefaultStreams(),
            ["MediaAttachments"] = new object[] { new Dictionary<string, object?> { ["Codec"] = "ttf", ["DeliveryUrl"] = $"/Videos/{PeerItemN}/{PeerSourceN}/Attachments/1?ApiKey={PeerToken}" } },
            ["RequiredHttpHeaders"] = new Dictionary<string, string> { ["X-Peer"] = "secret" },
            ["EncoderPath"] = "/usr/bin/ffmpeg",
            ["OpenToken"] = "peer-open-token",
            ["LiveStreamId"] = "peer-live",
        };
        tweak?.Invoke(source);
        return new Dictionary<string, object?> { ["MediaSources"] = new[] { source }, ["PlaySessionId"] = "peer-play-session-0123456789" };
    }

    private static object TranscodeAnswer(string url) => Answer(s =>
    {
        s["SupportsDirectPlay"] = false;
        s["SupportsDirectStream"] = false;
        s["TranscodingUrl"] = url;
        s["TranscodingSubProtocol"] = "hls";
        s["TranscodingContainer"] = "ts";
    });

    private string Sign(Guid user, params Claim[] extra) => H.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user, extra));

    private async Task<HttpResponseMessage> PostAsync(string? body, string query = "", string? principal = null, string? path = null)
    {
        var key = principal ?? Sign(MatrixWorld.Admin);
        return await H.SendAsync(key, HttpMethod.Post, path ?? $"/Items/{LocalItem}/PlaybackInfo{query}", r =>
        {
            if (body is not null)
            {
                r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        });
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response) => (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private RecordedRequest PeerCall() => H.Peer.Requests.Last(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));

    private int PeerPlaybackCalls() => H.Peer.Requests.Count(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal));

    private static readonly HashSet<string> AllowedBodyKeys = new(StringComparer.Ordinal)
    {
        "MediaSourceId", "AutoOpenLiveStream", "DeviceProfile", "MaxStreamingBitrate", "StartTimeTicks", "AudioStreamIndex", "SubtitleStreamIndex",
        "MaxAudioChannels", "EnableDirectPlay", "EnableDirectStream", "EnableTranscoding", "AllowVideoStreamCopy", "AllowAudioStreamCopy",
        "AlwaysBurnInSubtitleWhenTranscoding",
    };

    private const string RichBody = """
        {
          "UserId": "a0000000-0000-0000-0000-0000000000ff",
          "MaxStreamingBitrate": 20000000,
          "StartTimeTicks": 123,
          "AudioStreamIndex": 1,
          "SubtitleStreamIndex": 2,
          "MaxAudioChannels": 6,
          "MediaSourceId": "b0000000000000000000000000000001",
          "LiveStreamId": "leak-me",
          "AutoOpenLiveStream": true,
          "EnableDirectPlay": true,
          "EnableDirectStream": true,
          "EnableTranscoding": true,
          "AllowVideoStreamCopy": true,
          "AllowAudioStreamCopy": true,
          "AlwaysBurnInSubtitleWhenTranscoding": false,
          "SomethingNew": {"nested": [1,2,3]},
          "DeviceProfile": {
            "Name": "Moonfin on Pat's iPhone",
            "Id": "11111111-2222-3333-4444-555555555555",
            "FriendlyName": "Pat's phone", "Manufacturer": "Apple", "ModelName": "iPhone15", "SerialNumber": "SN123",
            "MaxStreamingBitrate": 120000000,
            "MaxStaticBitrate": 100000000,
            "DirectPlayProfiles": [ { "Container": "mp4,m4v", "VideoCodec": "h264,hevc", "AudioCodec": "aac,ac3", "Type": "Video" } ],
            "TranscodingProfiles": [ { "Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls", "Context": "Streaming", "MaxAudioChannels": "6", "MinSegments": 1, "SegmentLength": 6, "BreakOnNonKeyFrames": true } ],
            "ContainerProfiles": [],
            "CodecProfiles": [ { "Type": "Video", "Codec": "h264", "Conditions": [ { "Condition": "LessThanEqual", "Property": "VideoLevel", "Value": "52", "IsRequired": false } ] } ],
            "SubtitleProfiles": [ { "Format": "srt", "Method": "External" }, { "Format": "pgssub", "Method": "External" }, { "Format": "ass", "Method": "Embed" } ]
          }
        }
        """;

    // --- T-SEC-20: the body that goes to the Peer is exactly 6.2.1

    [Fact]
    public async Task U9_TheLinkBudget_CapsWhatThePeerIsAskedFor_SoItTranscodesDown()
    {
        P.Limits = new BitrateLimits(0, 5_000_000); // 7.5 Mbps measured, divided by the 1.5 headroom

        using var response = await PostAsync(RichBody, "?MaxStreamingBitrate=40000000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (JsonObject)JsonNode.Parse(PeerCall().Body)!;
        Assert.Equal(5_000_000, (int)body["MaxStreamingBitrate"]!);
    }

    [Fact]
    public async Task T_SEC_20_TheForwardedBody_HoldsExactlyTheFieldsOf621_WithTheValuesOf621()
    {
        using var response = await PostAsync(RichBody, "?MaxStreamingBitrate=15000000&userId=a0000000000000000000000000000099&ApiKey=client-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = PeerCall();
        Assert.Equal("POST", call.Method);
        Assert.Equal($"/Items/{PeerItemN}/PlaybackInfo", call.Path);
        Assert.Equal(string.Empty, call.Query); // no query string at all
        var body = (JsonObject)JsonNode.Parse(call.Body)!;
        Assert.All(body.Select(static p => p.Key), key => Assert.Contains(key, AllowedBodyKeys));
        Assert.Equal(PeerSourceN, (string?)body["MediaSourceId"]);
        Assert.False((bool)body["AutoOpenLiveStream"]!);
        Assert.Equal(15_000_000, (int)body["MaxStreamingBitrate"]!); // the query value won, and it is the smaller
        Assert.Equal(123, (long)body["StartTimeTicks"]!);
        Assert.Equal(1, (int)body["AudioStreamIndex"]!);
        Assert.Equal(2, (int)body["SubtitleStreamIndex"]!);
        Assert.Equal(6, (int)body["MaxAudioChannels"]!);
        Assert.True((bool)body["EnableDirectPlay"]!);
        Assert.True((bool)body["EnableDirectStream"]!);
        Assert.True((bool)body["EnableTranscoding"]!);
        Assert.True((bool)body["AllowVideoStreamCopy"]!);
        Assert.True((bool)body["AllowAudioStreamCopy"]!);
        Assert.False((bool)body["AlwaysBurnInSubtitleWhenTranscoding"]!);
        var text = Encoding.UTF8.GetString(call.Body);
        foreach (var leak in new[] { "UserId", "LiveStreamId", "leak-me", "Pat", "iPhone", "Apple", "SN123", "SomethingNew", "a0000000", "client-token" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task T_SEC_20_TheForwardedProfile_IsANewOne_NamedShoal()
    {
        using var response = await PostAsync(RichBody);

        var profile = (JsonObject)((JsonObject)JsonNode.Parse(PeerCall().Body)!)["DeviceProfile"]!;
        Assert.Equal("Shoal", (string?)profile["Name"]);
        Assert.Null(profile["Id"]);
        Assert.Equal(20_000_000, (int)profile["MaxStreamingBitrate"]!);
        Assert.Equal(20_000_000, (int)profile["MaxStaticBitrate"]!); // min(client 100M, 20M)
        Assert.Equal("mp4,m4v", (string?)profile["DirectPlayProfiles"]![0]!["Container"]);
        var subtitles = profile["SubtitleProfiles"]!.AsArray().Select(static n => ((string?)n!["Format"], (string?)n["Method"])).ToList();
        Assert.Equal([("srt", "External"), ("ass", "Embed")], subtitles); // the bitmap External entry is gone (T-CLI-15)
        var serialized = profile.ToJsonString();
        Assert.DoesNotContain("Pat", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Apple", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SN123", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithoutAnyProfile_ForwardsNone()
    {
        using var response = await PostAsync("{}");

        var body = (JsonObject)JsonNode.Parse(PeerCall().Body)!;
        Assert.False(body.ContainsKey("DeviceProfile"));
        Assert.Equal(PeerSourceN, (string?)body["MediaSourceId"]);
        Assert.False(body.ContainsKey("MaxStreamingBitrate"));
        Assert.False(body.ContainsKey("StartTimeTicks"));
    }

    // --- the merge (T-CLI-2)

    [Fact]
    public async Task T_CLI_2_ForEachField_TheQueryValueWins_ThenTheBody_ThenTheDefault()
    {
        using var response = await PostAsync(
            """{"AudioStreamIndex": 1, "SubtitleStreamIndex": 2, "MaxAudioChannels": 2, "EnableTranscoding": true, "EnableDirectPlay": true, "AllowAudioStreamCopy": true}""",
            "?audioStreamIndex=3&subtitleStreamIndex=-1&EnableTranscoding=false");

        var body = (JsonObject)JsonNode.Parse(PeerCall().Body)!;
        Assert.Equal(3, (int)body["AudioStreamIndex"]!); // query wins, key matched without regard to case
        Assert.Equal(-1, (int)body["SubtitleStreamIndex"]!);
        Assert.Equal(2, (int)body["MaxAudioChannels"]!); // body only
        Assert.False((bool)body["EnableTranscoding"]!);
        Assert.True((bool)body["EnableDirectPlay"]!);
        Assert.True((bool)body["EnableDirectStream"]!); // core's default
        Assert.True((bool)body["AllowVideoStreamCopy"]!);
    }

    [Fact]
    public async Task AUserIdInTheQuery_OrTheBody_IsIgnored_TheUserIsTheCaller()
    {
        using var response = await PostAsync("""{"UserId":"a0000000-0000-0000-0000-000000000003"}""", $"?UserId={MatrixWorld.Other:N}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MatrixWorld.Admin, P.Grants.ForUser(MatrixWorld.Admin).Single().LocalUserId);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Other));
        Assert.DoesNotContain("a0000000", Encoding.UTF8.GetString(PeerCall().Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?audioStreamIndex=abc")]
    [InlineData("?MaxStreamingBitrate=1e9")]
    [InlineData("?EnableDirectPlay=maybe")]
    [InlineData("?StartTimeTicks=9999999999999999999999")]
    [InlineData("?MediaSourceId=b0000000000000000000000000000099")]
    [InlineData("?MediaSourceId=not-a-guid")]
    public async Task AQueryValueThatDoesNotParse_OrAForeignMediaSource_Is400_WithNoPeerCall(string query)
    {
        var calls = PeerPlaybackCalls();

        using var response = await PostAsync("{}", query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(calls, PeerPlaybackCalls());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"DeviceProfile": {"DirectPlayProfiles": [{"Type": "NotAType"}]}}""")]
    [InlineData("""{"AudioStreamIndex": "one"}""")]
    [InlineData("""{"EnableDirectPlay": "yes"}""")]
    public async Task ABodyThatDoesNotParse_OrCarriesAnUnknownEnum_Is400(string body)
    {
        var calls = PeerPlaybackCalls();

        using var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(calls, PeerPlaybackCalls());
    }

    [Fact]
    public async Task ABodyOver256Kilobytes_Is413()
    {
        var body = "{\"x\":\"" + new string('a', 300 * 1024) + "\"}";

        using var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ADeeplyNestedBody_Is400()
    {
        var body = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + new string('}', 100);

        using var response = await PostAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public async Task T_CLI_10_TheMediaSourceId_MayBeTheItemIdInNOrDForm(string format)
    {
        using var response = await PostAsync("{}", "?MediaSourceId=" + MatrixWorld.Item.ToString(format));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = (await ReadAsync(response))["MediaSources"]![0]!;
        Assert.Equal(LocalItem, (string?)source["Id"]);
    }

    [Fact]
    public async Task T_CLI_2_WithNoProfile_TheDevicesOwnRegisteredProfileIsUsed_ButNeverAnotherUsersDevice()
    {
        var mine = new DeviceProfile { Name = "Registered", DirectPlayProfiles = [new DirectPlayProfile { Container = "mkv", Type = DlnaProfileType.Video }] };
        P.Devices.Profiles[(MatrixWorld.Admin, "device-" + MatrixWorld.Admin.ToString("N")[^4..])] = mine;

        using var own = await PostAsync("{}");
        var ownProfile = ((JsonObject)JsonNode.Parse(PeerCall().Body)!)["DeviceProfile"]!;
        Assert.Equal("Shoal", (string?)ownProfile["Name"]);
        Assert.Equal("mkv", (string?)ownProfile["DirectPlayProfiles"]![0]!["Container"]);

        // user B presents A's device id: the fake only knows (user, device) pairs, so B finds nothing
        using var other = await PostAsync("{}", principal: Sign(MatrixWorld.Selected));
        Assert.False(((JsonObject)JsonNode.Parse(PeerCall().Body)!).ContainsKey("DeviceProfile"));
    }

    // --- policy and bitrate

    [Fact]
    public async Task TheLocalPolicy_IsAndedIntoTheFlags()
    {
        H.World.Users[MatrixWorld.Admin] = MatrixWorld.MakeUser(MatrixWorld.Admin) with { CanTranscodeVideo = false, CanRemux = false, ForceRemoteSourceTranscoding = true };

        using var response = await PostAsync("{}");

        var body = (JsonObject)JsonNode.Parse(PeerCall().Body)!;
        Assert.False((bool)body["EnableTranscoding"]!);
        Assert.False((bool)body["EnableDirectPlay"]!);
        Assert.False((bool)body["EnableDirectStream"]!);
        Assert.False((bool)body["AllowVideoStreamCopy"]!);
        Assert.True((bool)body["AllowAudioStreamCopy"]!);
    }

    [Fact]
    public void TheStreamingLimit_IsTheSmallestOfTheClientsTheUsersAndTheServers_ForRemoteCallers()
    {
        var remote = Caller(isLocal: false, userLimit: 30_000_000);
        var local = Caller(isLocal: true, userLimit: 30_000_000);
        var limits = new BitrateLimits(25_000_000, 40_000_000);

        Assert.Equal(20_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(20_000_000, remote, limits));
        Assert.Equal(25_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(120_000_000, remote, limits));
        Assert.Equal(25_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(null, remote, limits));
        Assert.Equal(40_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(null, local, limits)); // a local caller is not held to the remote limits
        Assert.Equal(40_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(120_000_000, local, limits));
        Assert.Null(PlaybackInfoBuilder.ComputeMaxStreamingBitrate(null, local, new BitrateLimits(0, null)));
        Assert.Null(PlaybackInfoBuilder.ComputeMaxStreamingBitrate(0, Caller(isLocal: false, userLimit: null), new BitrateLimits(0, null)));
    }

    private static Caller Caller(bool isLocal, int? userLimit)
    {
        var user = new CallerUser(Guid.NewGuid(), false, false, true, true, true, false, userLimit);
        return (Caller)Activator.CreateInstance(typeof(Caller), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, [user, "tok", "dev", isLocal], null)!;
    }

    [Fact]
    public async Task TheStartPosition_IsClampedToTheRuntime()
    {
        using var response = await PostAsync("""{"StartTimeTicks": 99999999999999}""");

        Assert.Equal(72_000_000_000, (long)((JsonObject)JsonNode.Parse(PeerCall().Body)!)["StartTimeTicks"]!);
    }

    [Fact]
    public async Task OutOfRangeIndexes_AreLeftOut()
    {
        using var response = await PostAsync("""{"AudioStreamIndex": 500, "SubtitleStreamIndex": -5, "MaxAudioChannels": 99}""");

        var body = (JsonObject)JsonNode.Parse(PeerCall().Body)!;
        Assert.False(body.ContainsKey("AudioStreamIndex"));
        Assert.False(body.ContainsKey("SubtitleStreamIndex"));
        Assert.False(body.ContainsKey("MaxAudioChannels"));
    }

    // --- the answer (6.2.3)

    [Fact]
    public async Task T_PLAY_13_ThePeersOtherVersions_AreDropped_ExactlyOneSourceComesBack()
    {
        H.Peer.PlaybackInfoReply = (_, _) =>
        {
            var two = (Dictionary<string, object?>)Answer();
            var first = ((Dictionary<string, object?>[])two["MediaSources"]!)[0];
            var other = new Dictionary<string, object?>(first) { ["Id"] = "99999999999999999999999999999999", ["Name"] = "The Matrix 4K" };
            return Reply.Json(new Dictionary<string, object?> { ["MediaSources"] = new[] { other, first }, ["PlaySessionId"] = "peer-play-session-0123456789" });
        };

        using var response = await PostAsync("{}");

        var json = await response.Content.ReadAsStringAsync();
        var sources = JsonNode.Parse(json)!["MediaSources"]!.AsArray();
        Assert.Single(sources);
        Assert.DoesNotContain("99999999999999999999999999999999", json, StringComparison.Ordinal);
        Assert.Equal("The Matrix", (string?)sources[0]!["Name"]); // the catalog's source id picked the right one
    }

    [Fact]
    public async Task TheAnswer_IsRebuiltFromScratch_AndHoldsNothingOfThePeer()
    {
        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        var json = await response.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { PeerToken, PeerItemN, PeerSourceN, PlaybackParts.PeerItem.ToString("D"), PlaybackParts.PeerSource.ToString("D"), "peer-play-session", "peer-etag-secret", "/media/movies", "ffmpeg", "peer-open-token", "peer-live", "X-Peer", H.Peer.BaseUrl, new Uri(H.Peer.BaseUrl).Host + ":" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        var root = JsonNode.Parse(json)!;
        var source = root["MediaSources"]![0]!;
        Assert.Equal(LocalItem, (string?)source["Id"]);
        Assert.Equal("File", (string?)source["Protocol"]);
        Assert.False((bool)source["IsRemote"]!);
        Assert.Null(source["Path"]);
        Assert.False((bool)source["SupportsDirectStream"]!);
        Assert.True((bool)source["SupportsDirectPlay"]!);
        Assert.Equal("mkv", (string?)source["Container"]);
        Assert.Equal(4_000_000_000, (long)source["Size"]!);
        Assert.Empty(source["MediaAttachments"]!.AsArray());
        Assert.Null(source["EncoderPath"]);
        Assert.Null(source["OpenToken"]);
        Assert.Null(source["LiveStreamId"]);
        Assert.Null(source["TranscodingUrl"]);
        Assert.Empty(source["RequiredHttpHeaders"]!.AsObject());
        Assert.NotEqual("peer-etag-secret", (string?)source["ETag"]);
        Assert.Matches("^[0-9a-f]{32}$", (string?)source["ETag"]);
        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single();
        Assert.Equal(grant.LocalPlaySessionId, (string?)root["PlaySessionId"]);
        Assert.Matches("^[0-9a-f]{32}$", grant.LocalPlaySessionId);
        Assert.Equal(PlayMethod.DirectPlay, grant.Method);
    }

    [Fact]
    public async Task TheContainer_IsKeptWhenThePeerOrTheCatalogHasAValidOne()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(s => s["Container"] = "mov,mp4,m4a,3gp,3g2,mj2"));
        using var comma = await PostAsync("{}");
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", (string?)(await ReadAsync(comma))["MediaSources"]![0]!["Container"]);

        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(s => s["Container"] = "mkv\"><script>"));
        using var hostile = await PostAsync("{}");
        Assert.Equal("mkv", (string?)(await ReadAsync(hostile))["MediaSources"]![0]!["Container"]); // the catalog's, which passed 5.5 at sync

        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(s => s.Remove("Container")));
        using var none = await PostAsync("{}");
        Assert.Equal("mkv", (string?)(await ReadAsync(none))["MediaSources"]![0]!["Container"]);
    }

    [Fact]
    public async Task F_MD_01_to_03_TheStreams_AreSanitized_OrDropped_AndTheDisplayTitleIsRecomputed()
    {
        var hostile = new object[]
        {
            Stream(0, "Video", "h264", s => { s["Width"] = 99999; s["Height"] = 1080; s["Title"] = "<img src=x onerror=alert(1)>"; s["DisplayTitle"] = "<img src=x onerror=alert(1)>"; }),
            Stream(1, "Audio", "AC3\"><svg>", s => { s["Language"] = "<b>"; s["Title"] = "A\u202Eb\u0000c\"d'e&f`g"; s["Channels"] = 5000; s["ChannelLayout"] = "5.1<x>"; s["DisplayTitle"] = "<script>alert(1)</script>"; }),
            Stream(2, "Audio", "aac", s => { s["Title"] = new string('t', 100_000); }),
            Stream(2, "Audio", "dup-index", null),
            Stream(300, "Audio", "aac", null),
            Stream(5, "Data", "bin", null),
            Stream(6, "Subtitle", "srt", s => { s["IsExternalUrl"] = true; s["DeliveryUrl"] = "http://evil.example/sub.srt"; s["DeliveryMethod"] = "External"; }),
            Stream(7, "Subtitle", "srt", s => { s["DeliveryUrl"] = "//evil.example/sub.srt"; s["DeliveryMethod"] = "External"; }),
            Stream(8, "Subtitle", "srt", s => { s["Path"] = "https://evil.example/x.srt"; }),
            Stream(9, "Subtitle", "srt", s => { s["Title"] = "French"; s["Language"] = "fre"; }),
        };
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(null, hostile));

        using var response = await PostAsync("{}");

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<img", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", json.Replace("\\u003C", "<", StringComparison.Ordinal).Replace("\\u003c", "<", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('t', 80), json, StringComparison.Ordinal);
        var streams = JsonNode.Parse(json)!["MediaSources"]![0]!["MediaStreams"]!.AsArray();
        var indexes = streams.Select(static s => (int)s!["Index"]!).ToList();
        Assert.Equal([0, 1, 2, 9], indexes); // the duplicate, the out-of-range, the data stream and the three absolute-URL streams are gone
        Assert.Equal("img src=x onerror=alert(1)", (string?)streams[0]!["Title"]); // the brackets are gone, so it is text and cannot become an element
        Assert.Equal("Abcdefg", (string?)streams[1]!["Title"]);
        Assert.Null(streams[1]!["Language"]);
        Assert.Null(streams[1]!["ChannelLayout"]);
        Assert.Null(streams[1]!["Channels"]);
        Assert.Null(streams[0]!["Width"]);
        Assert.Equal(64, ((string?)streams[2]!["Title"])!.Length);
        Assert.Equal("French", (string?)streams[3]!["Title"]);
        Assert.Equal(4, P.Grants.ForUser(MatrixWorld.Admin).Single().Streams.Count);
    }

    [Fact]
    public async Task AnExternalSubtitle_GetsShoalsOwnDeliveryUrl_WithTheCallersTokenAndNothingOfThePeers()
    {
        using var response = await PostAsync("{}", principal: Sign(MatrixWorld.Admin));

        var json = await response.Content.ReadAsStringAsync();
        var external = JsonNode.Parse(json)!["MediaSources"]![0]!["MediaStreams"]!.AsArray().Single(static s => (int)s!["Index"]! == 3)!;
        Assert.Equal($"/Shoal/Subtitles/{LocalItem}/3/Stream.srt?ApiKey=tok{MatrixWorld.Admin:N}", (string?)external["DeliveryUrl"]);
        Assert.DoesNotContain(PeerToken, json, StringComparison.Ordinal);
        var embedded = JsonNode.Parse(json)!["MediaSources"]![0]!["MediaStreams"]!.AsArray().Single(static s => (int)s!["Index"]! == 2)!;
        Assert.Null(embedded["DeliveryUrl"]);
    }

    [Fact]
    public async Task AnExternalSubtitleInAFormatNobodyServes_LosesItsDeliveryUrl()
    {
        var streams = new object[]
        {
            Stream(0, "Video", "h264", null),
            Stream(3, "Subtitle", "ttml", s => { s["IsExternal"] = true; s["DeliveryMethod"] = "External"; s["DeliveryUrl"] = $"/Videos/{PeerItemN}/{PeerSourceN}/Subtitles/3/0/Stream.ttml?ApiKey={PeerToken}"; }),
        };
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(null, streams));

        using var response = await PostAsync("{}");

        var subtitle = (await ReadAsync(response))["MediaSources"]![0]!["MediaStreams"]![1]!;
        Assert.Null(subtitle["DeliveryUrl"]);
        Assert.Null(subtitle["DeliveryMethod"]);
        Assert.False((bool)subtitle["IsExternal"]!);
    }

    // --- transcoding

    [Fact]
    public async Task ATranscode_GetsShoalsMasterUrl_AndTheGrantKeepsThePeersQueryWithoutItsCredentials()
    {
        var url = $"/videos/{PeerItemN}/master.m3u8?DeviceId=abc&MediaSourceId={PeerSourceN}&VideoCodec=h264&AudioCodec=aac&api_key={PeerToken}&ApiKey={PeerToken}&TranscodeReasons=ContainerNotSupported%2CAudioCodecNotSupported&EnableAdaptiveBitrateStreaming=true";
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(TranscodeAnswer(url));

        using var response = await PostAsync("{}", principal: Sign(MatrixWorld.Admin));

        var json = await response.Content.ReadAsStringAsync();
        var grant = P.Grants.ForUser(MatrixWorld.Admin).Single();
        var source = JsonNode.Parse(json)!["MediaSources"]![0]!;
        Assert.Equal($"/Shoal/Hls/{grant.GrantId}/master.m3u8?ApiKey=tok{MatrixWorld.Admin:N}", (string?)source["TranscodingUrl"]);
        Assert.False((bool)source["SupportsDirectPlay"]!);
        Assert.Equal("ts", (string?)source["TranscodingContainer"]);
        Assert.Equal(PlayMethod.Transcode, grant.Method);
        var query = grant.PeerMasterQuery.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", query.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", query.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("false", query["EnableAdaptiveBitrateStreaming"]);
        Assert.Equal("ContainerNotSupported,AudioCodecNotSupported", query["TranscodeReasons"]);
        Assert.DoesNotContain(PeerToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PeerItemN, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://evil.example/videos/{P}/master.m3u8?x=1")]
    [InlineData("/videos/{O}/master.m3u8?x=1")]
    [InlineData("/videos/{P}/main.m3u8?x=1")]
    [InlineData("/videos/{P}/master.m3u8")]
    [InlineData("/videos/{P}/master.m3u8?next=http://evil.example/")]
    [InlineData("/videos/{P}/master.m3u8?x=a b")]
    [InlineData("/videos/{P}/master.m3u8?x=%ZZ")]
    [InlineData("//evil.example/videos/{P}/master.m3u8?x=1")]
    [InlineData("/Videos/{P}/../{O}/master.m3u8?x=1")]
    public async Task ATranscodingUrlThatIsNotThePeersOwnMasterPlaylist_Is502(string template)
    {
        var url = template.Replace("{P}", PeerItemN, StringComparison.Ordinal).Replace("{O}", PlaybackParts.PeerItemB.ToString("N"), StringComparison.Ordinal);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(TranscodeAnswer(url));

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APeerPathInUpperCaseOrDForm_IsStillItsOwnMasterPlaylist()
    {
        var url = $"/VIDEOS/{PlaybackParts.PeerItem.ToString("D").ToUpperInvariant()}/master.m3u8?DeviceId=x";
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(TranscodeAnswer(url));

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ASourceThatNeitherPlaysNorTranscodes_IsNoCompatibleStream()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(s => { s["SupportsDirectPlay"] = false; s["SupportsDirectStream"] = true; }));

        using var response = await PostAsync("{}");

        var root = await ReadAsync(response);
        Assert.Equal("NoCompatibleStream", (string?)root["ErrorCode"]);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
    }

    // --- the Peer misbehaving

    [Fact]
    public async Task APeerErrorCode_IsRelayedAsTheEnumOnly()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?> { ["MediaSources"] = Array.Empty<object>(), ["ErrorCode"] = "NoCompatibleStream", ["Leak"] = PeerToken });

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("NoCompatibleStream", (string?)JsonNode.Parse(json)!["ErrorCode"]);
        Assert.DoesNotContain(PeerToken, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, "stack trace with the host and the token")]
    [InlineData(502, "bad gateway")]
    [InlineData(429, "slow down")]
    public async Task APeerErrorStatus_IsA502_AndItsBodyIsNeverRelayed(int status, string peerBody)
    {
        H.Peer.PlaybackInfoReply = (_, _) => new Reply(status, peerBody, "text/plain");

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ThePeerRefusingTheToken_IsNotAllowed()
    {
        H.Peer.PlaybackInfoReply = (_, _) => new Reply(401, "no");

        using var response = await PostAsync("{}");

        Assert.Equal("NotAllowed", (string?)(await ReadAsync(response))["ErrorCode"]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"MediaSources": "nope"}""")]
    [InlineData("""{"MediaSources": []}""")]
    public async Task AnAnswerThatIsNotAPlaybackInfoResponse_Is502(string body)
    {
        H.Peer.PlaybackInfoReply = (_, _) => new Reply(200, body);

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task AnAnswerWithAnUnknownEnum_Is502_NotAnException()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(s => s["Protocol"] = "FutureProtocol"));

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData(PeerStatus.AuthFailed)]
    [InlineData(PeerStatus.IdentityMismatch)]
    [InlineData(PeerStatus.TlsKeyChanged)]
    [InlineData(PeerStatus.Disabled)]
    public async Task AStoppedPeer_IsNotAllowed_WithNoPeerCall(PeerStatus status)
    {
        P.Registry.SetStatus(H.World.PeerKey, status);
        var calls = PeerPlaybackCalls();

        using var response = await PostAsync("{}");

        Assert.Equal("NotAllowed", (string?)(await ReadAsync(response))["ErrorCode"]);
        Assert.Equal(calls, PeerPlaybackCalls());
    }

    [Fact]
    public async Task AnOpenCircuit_IsRateLimitExceeded_WithTheOfflineNotice_AndNoPeerCall()
    {
        var breaker = H.Client.BreakerOf(P.Endpoint);
        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }

        try
        {
            var calls = PeerPlaybackCalls();

            using var response = await PostAsync("{}");

            Assert.Equal("RateLimitExceeded", (string?)(await ReadAsync(response))["ErrorCode"]);
            Assert.Single(P.Notice.Notices);
            Assert.Equal(calls, PeerPlaybackCalls());
        }
        finally
        {
            breaker.RecordSuccess();
        }
    }

    // --- canary (T-SEC-21)

    [Fact]
    public async Task T_SEC_21_APeerIdInAStreamTitle_IsA502_AndAnAlert()
    {
        var streams = new object[] { Stream(0, "Video", "h264", null), Stream(1, "Audio", "aac", s => s["Title"] = "Dub " + PeerItemN[..16] + PeerItemN[16..]) };
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(null, streams));

        using var response = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Contains("Redacted canary hit in PlaybackInfo", H.Filter!.Log.All, StringComparison.Ordinal);
        await Task.Delay(100);
        Assert.Single(P.Admins.Messages);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
    }

    [Fact]
    public async Task K14_L1_TheCanaryAlert_IsThrottledToOnePerPeerPerFiveMinutes()
    {
        var streams = new object[] { Stream(0, "Video", "h264", null), Stream(1, "Audio", "aac", s => s["Title"] = "Dub " + PeerItemN) };
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(null, streams));

        for (var i = 0; i < 5; i++)
        {
            using var response = await PostAsync("{}");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); // every attempt is still refused
        }

        await Task.Delay(100);
        Assert.Single(P.Admins.Messages);
        P.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var later = await PostAsync("{}");
        await Task.Delay(100);
        Assert.Equal(2, P.Admins.Messages.Count);
    }


    [Theory]
    [InlineData("peer-play-session-0123456789", "peer-play-session-0123456789")]
    [InlineData("a1b2c3d4", "a1b2c3d4")]
    [InlineData("short", "")]
    [InlineData("        ", "")]
    [InlineData("has space in it 123", "")]
    [InlineData("quote\"inside-12345", "")]
    [InlineData("new\nline-123456", "")]
    [InlineData("unicode-é-123456", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Warden07_L4_APeerPlaySessionId_IsKeptOnlyInTheShapeJellyfinWrites(string? value, string expected) =>
        Assert.Equal(expected, PlaybackInfoBuilder.ValidPeerPlaySession(value));

    [Fact]
    public void Warden07_L4_ATooLongPeerPlaySessionId_IsDropped() =>
        Assert.Equal(string.Empty, PlaybackInfoBuilder.ValidPeerPlaySession(new string('a', 65)));
    // --- hostile nulls (K14-L2)

    [Fact]
    public async Task K14_L2_ANullMediaSourceInThePeersAnswer_IsNotA500()
    {
        var real = (Dictionary<string, object?>)((object[])((Dictionary<string, object?>)Answer())["MediaSources"]!)[0]!;
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?> { ["MediaSources"] = new object?[] { null, real }, ["PlaySessionId"] = "peer-play-session-0123456789" });
        using var mixed = await PostAsync("{}");
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(new Dictionary<string, object?> { ["MediaSources"] = new object?[] { null }, ["PlaySessionId"] = "peer-play-session-0123456789" });
        using var onlyNull = await PostAsync("{}");

        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, onlyNull.StatusCode); // nothing usable: a refused answer, not a crash
    }

    [Fact]
    public async Task K14_L2_NullElementsInTheClientsProfile_AreDropped_NotA500()
    {
        const string Body = """
            {"DeviceProfile":{"DirectPlayProfiles":[null,{"Container":"mkv","Type":"Video"}],"TranscodingProfiles":[null],"ContainerProfiles":[null],"SubtitleProfiles":[null],
            "CodecProfiles":[null,{"Type":"Video","Codec":"h264","Conditions":[null,{"Condition":"LessThanEqual","Property":"VideoLevel","Value":"51","IsRequired":false}],"ApplyConditions":[null]}]}}
            """;

        using var response = await PostAsync(Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void T_SEC_21_TheHostIsMatchedOnLabelBoundaries()
    {
        const string Token = "0123456789abcdef0123456789abcdef";
        var ids = new[] { Guid.Parse("11111111-1111-1111-1111-111111111111") };

        Assert.False(PlaybackInfoBuilder.CanaryHit("{\"Name\":\"TV Show\"}", Token, ids, "ps", "tv")); // "TV" is not the host as written
        Assert.False(PlaybackInfoBuilder.CanaryHit("{\"Name\":\"tvs and tvdb\"}", Token, ids, "ps", "tv"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"Name\":\"watch on tv now\"}", Token, ids, "ps", "tv"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"Url\":\"http://hops.tail1234.ts.net:8096/x\"}", Token, ids, "ps", "hops.tail1234.ts.net"));
        Assert.False(PlaybackInfoBuilder.CanaryHit("{\"Url\":\"http://xhops.tail1234.ts.net/x\"}", Token, ids, "ps", "hops.tail1234.ts.net"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"a\":\"" + Token.ToUpperInvariant() + "\"}", Token, ids, "ps", "tv"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"a\":\"11111111111111111111111111111111\"}", Token, ids, "ps", "tv"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"a\":\"11111111-1111-1111-1111-111111111111\"}", Token, ids, "ps", "tv"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"a\":\"peer-play-session-0123\"}", Token, ids, "peer-play-session-0123", "tv"));
        Assert.False(PlaybackInfoBuilder.CanaryHit("{\"a\":\"ps\"}", Token, ids, "ps", "tv")); // a short play session id would match everything: it is not checked
        Assert.False(PlaybackInfoBuilder.CanaryHit("{}", Token, ids, null, "10.0.0.5"));
        Assert.True(PlaybackInfoBuilder.CanaryHit("{\"a\":\"on 10.0.0.5 now\"}", Token, ids, null, "10.0.0.5"));
        Assert.False(PlaybackInfoBuilder.CanaryHit("{\"a\":\"210.0.0.55\"}", Token, ids, null, "10.0.0.5"));
    }

    // --- limits and grants (3.5, 6.9)

    [Fact]
    public async Task APlaybackInfoCall_TakesNoStreamSlot_AndOneGrantPerKey()
    {
        for (var i = 0; i < 5; i++)
        {
            using var response = await PostAsync("""{"AudioStreamIndex": 1}""");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Single(P.Grants.ForUser(MatrixWorld.Admin)); // the Smart-TV storm: five calls replace each other
        Assert.Empty(P.Slots.Holding);
    }

    [Fact]
    public async Task T_CLI_8_TheBucketHolds15Calls_ThenRefusesWithRateLimitExceeded_AndRefills()
    {
        var results = new List<string?>();
        for (var i = 0; i < 17; i++)
        {
            using var response = await PostAsync("{}");
            var root = await ReadAsync(response);
            results.Add((string?)root["ErrorCode"]);
        }

        Assert.Equal(15, results.Count(static r => r is null));
        Assert.Equal(["RateLimitExceeded", "RateLimitExceeded"], results.Skip(15));

        P.Time.Advance(TimeSpan.FromSeconds(10)); // 30 per minute: five more tokens
        using var again = await PostAsync("{}");
        Assert.Null((string?)(await ReadAsync(again))["ErrorCode"]);
    }

    [Fact]
    public async Task APlayingGrant_IsNeverReplacedOrEvicted_AndWithNoSlotFreeGrantTheCallIsRefused()
    {
        // eight grants for the admin: different devices of the same item cannot be made through the harness, so the store is driven directly
        var store = P.Grants;
        var grants = new List<Grant>();
        for (var i = 0; i < GrantStore.MaxPerUser; i++)
        {
            var grant = NewGrant(MatrixWorld.Admin, Guid.NewGuid(), "dev-" + i, P.Time.GetUtcNow().AddSeconds(i));
            Assert.Equal(i == 0 ? GrantOutcome.Created : GrantOutcome.Created, store.TryAdd(grant));
            grants.Add(grant);
            P.Slots.Holding.Add(grant.GrantId);
        }

        using var refused = await PostAsync("{}");

        Assert.Equal("RateLimitExceeded", (string?)(await ReadAsync(refused))["ErrorCode"]);
        Assert.Equal(GrantStore.MaxPerUser, store.ForUser(MatrixWorld.Admin).Count);
        Assert.All(grants, g => Assert.NotNull(store.Get(g.GrantId)));
    }

    private Grant NewGrant(Guid user, Guid item, string device, DateTimeOffset access) => new()
    {
        GrantId = GrantStore.NewGrantId(),
        LocalUserId = user,
        LocalItemId = item,
        CallerDeviceId = device,
        LocalPlaySessionId = GrantStore.NewPlaySessionId(),
        PeerKey = H.World.PeerKey,
        PeerItemId = PlaybackParts.PeerItem,
        PeerMediaSourceId = PlaybackParts.PeerSource,
        CreatedUtc = access,
        LastAccessUtc = access,
        LastAuthorizedUtc = access,
    };

    // --- grant store rules (unit)

    [Fact]
    public void TheGrantCaps_FollowTheOrderReplaceThenEvictOwnThenGlobal()
    {
        var time = new ManualTimeProvider();
        var slots = new FakeSlots();
        var store = new GrantStore(time, slots);
        var user = Guid.NewGuid();
        Grant Make(Guid u, Guid item, string device, int ageSeconds) => new()
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = u,
            LocalItemId = item,
            CallerDeviceId = device,
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
            CreatedUtc = time.GetUtcNow(),
            LastAccessUtc = time.GetUtcNow().AddSeconds(ageSeconds),
            LastAuthorizedUtc = time.GetUtcNow(),
        };

        // (1) the same key replaces a slot-free grant
        var item1 = Guid.NewGuid();
        var first = Make(user, item1, "d", 0);
        Assert.Equal(GrantOutcome.Created, store.TryAdd(first));
        var second = Make(user, item1, "d", 1);
        Assert.Equal(GrantOutcome.Replaced, store.TryAdd(second));
        Assert.Null(store.Get(first.GrantId));
        Assert.NotNull(store.Get(second.GrantId));

        // ... but never one that holds a slot: then the new grant is a second one for the pair
        slots.Holding.Add(second.GrantId);
        var third = Make(user, item1, "d", 2);
        Assert.Equal(GrantOutcome.Created, store.TryAdd(third));
        Assert.NotNull(store.Get(second.GrantId));
        slots.Holding.Clear();
        store.Remove(third.GrantId);

        // (2) at 8, the oldest slot-free grant of the user goes
        var all = new List<Grant> { second };
        for (var i = 1; i < GrantStore.MaxPerUser; i++)
        {
            var g = Make(user, Guid.NewGuid(), "d" + i, 10 + i);
            Assert.Equal(GrantOutcome.Created, store.TryAdd(g));
            all.Add(g);
        }

        slots.Holding.Add(second.GrantId); // the oldest is playing, so the next oldest goes
        var ninth = Make(user, Guid.NewGuid(), "dx", 50);
        Assert.Equal(GrantOutcome.EvictedOwn, store.TryAdd(ninth));
        Assert.NotNull(store.Get(second.GrantId));
        Assert.Null(store.Get(all[1].GrantId));
        Assert.Equal(GrantStore.MaxPerUser, store.ForUser(user).Count);

        // with no slot-free grant, refuse
        foreach (var g in store.ForUser(user))
        {
            slots.Holding.Add(g.GrantId);
        }

        Assert.Equal(GrantOutcome.RefusedUser, store.TryAdd(Make(user, Guid.NewGuid(), "dy", 60)));
        Assert.False(store.HasRoom(user, Guid.NewGuid(), "dz"));

        // (3) the global cap is checked last: others are refused, a replacement is not
        slots.Holding.Clear();
        var others = new List<Grant>();
        for (var i = 0; store.Count < GrantStore.MaxTotal; i++)
        {
            var g = Make(Guid.NewGuid(), Guid.NewGuid(), "x", 0);
            Assert.Equal(GrantOutcome.Created, store.TryAdd(g));
            others.Add(g);
        }

        Assert.Equal(GrantStore.MaxTotal, store.Count);
        var newcomer = Guid.NewGuid();
        Assert.Equal(GrantOutcome.RefusedGlobal, store.TryAdd(Make(newcomer, Guid.NewGuid(), "n", 0)));
        var replacing = Make(user, ninth.LocalItemId, ninth.CallerDeviceId, 70);
        Assert.Equal(GrantOutcome.Replaced, store.TryAdd(replacing));
        Assert.Equal(GrantStore.MaxTotal, store.Count);
        Assert.True(store.HasRoom(user, ninth.LocalItemId, ninth.CallerDeviceId));
        Assert.False(store.HasRoom(newcomer, Guid.NewGuid(), "n"));
    }

    [Fact]
    public void Grants_ExpireByIdleTime_AndByAbsoluteLifetime_ButNotWhileStreaming()
    {
        var time = new ManualTimeProvider();
        var slots = new FakeSlots();
        var store = new GrantStore(time, slots);
        Grant Make(PlayMethod method) => new()
        {
            GrantId = GrantStore.NewGrantId(),
            LocalUserId = Guid.NewGuid(),
            LocalItemId = Guid.NewGuid(),
            CallerDeviceId = "d",
            LocalPlaySessionId = GrantStore.NewPlaySessionId(),
            PeerKey = "p",
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid(),
            Method = method,
            CreatedUtc = time.GetUtcNow(),
            LastAccessUtc = time.GetUtcNow(),
            LastAuthorizedUtc = time.GetUtcNow(),
        };
        var hls = Make(PlayMethod.Transcode);
        var direct = Make(PlayMethod.DirectPlay);
        var playing = Make(PlayMethod.DirectPlay);
        store.TryAdd(hls);
        store.TryAdd(direct);
        store.TryAdd(playing);
        slots.Holding.Add(playing.GrantId);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(store.Get(hls.GrantId)); // HLS: 10 minutes idle
        Assert.NotNull(store.Get(direct.GrantId));

        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(store.Get(direct.GrantId)); // direct play: 2 hours idle
        Assert.NotNull(store.Get(playing.GrantId)); // a running stream keeps its grant

        time.Advance(TimeSpan.FromHours(10));
        Assert.Null(store.Get(playing.GrantId)); // but nothing lives past 12 hours
    }

    [Fact]
    public void TheGrantIds_AreRandomAndLongEnough()
    {
        var ids = Enumerable.Range(0, 1000).Select(static _ => GrantStore.NewGrantId()).ToHashSet();

        Assert.Equal(1000, ids.Count);
        Assert.All(ids, id => Assert.Matches("^[A-Za-z0-9_-]{22}$", id));
        Assert.All(Enumerable.Range(0, 50).Select(static _ => GrantStore.NewPlaySessionId()), id => Assert.Matches("^[0-9a-f]{32}$", id));
    }

    [Fact]
    public void TheRateLimiter_IsPerUser()
    {
        var time = new ManualTimeProvider();
        var limiter = new RateLimiter(time, 30, 15);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        for (var i = 0; i < 15; i++)
        {
            Assert.True(limiter.TryTake(a));
        }

        Assert.False(limiter.TryTake(a));
        Assert.True(limiter.TryTake(b));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryTake(a));
        Assert.False(limiter.TryTake(a));
    }

    // --- the profile rebuild (unit)

    [Fact]
    public void TheProfileRebuild_CapsTheLists_DropsOddStrings_AndKeepsTheRest()
    {
        var client = new DeviceProfile
        {
            Name = "Whatever",
            DirectPlayProfiles = [.. Enumerable.Range(0, 100).Select(i => new DirectPlayProfile { Container = "c" + i, Type = DlnaProfileType.Video })],
            TranscodingProfiles = [.. Enumerable.Range(0, 50).Select(i => new TranscodingProfile { Container = "ts", Type = DlnaProfileType.Video, VideoCodec = i == 3 ? "h264<script>" : "h264", AudioCodec = "aac" })],
            ContainerProfiles = [new ContainerProfile { Container = "mp4", Conditions = [.. Enumerable.Range(0, 40).Select(static _ => new ProfileCondition(ProfileConditionType.Equals, ProfileConditionValue.Width, "1920", false))] }],
            CodecProfiles = [new CodecProfile { Type = CodecType.Video, Codec = "h264", Conditions = [new ProfileCondition(ProfileConditionType.Equals, ProfileConditionValue.Width, "19\"20", false)] }],
            SubtitleProfiles =
            [
                new SubtitleProfile { Format = "vtt", Method = SubtitleDeliveryMethod.Hls },
                new SubtitleProfile { Format = "vtt", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "SRT", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "smi", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "sub", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "dvbsub", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "pgs", Method = SubtitleDeliveryMethod.Embed },
                new SubtitleProfile { Format = new string('x', 300), Method = SubtitleDeliveryMethod.Embed },
            ],
            MaxStaticBitrate = 5_000_000,
            MusicStreamingTranscodingBitrate = 2_000_000_000,
        };

        var rebuilt = DeviceProfileRebuilder.Rebuild(client, 10_000_000, hlsSubtitlesEnabled: false);

        Assert.Equal("Shoal", rebuilt.Name);
        Assert.Equal(64, rebuilt.DirectPlayProfiles.Length);
        Assert.Equal(32, rebuilt.TranscodingProfiles.Length);
        Assert.DoesNotContain(rebuilt.TranscodingProfiles, static p => p.VideoCodec.Contains('<', StringComparison.Ordinal));
        Assert.Equal(32, rebuilt.ContainerProfiles.Single().Conditions.Length);
        Assert.Empty(rebuilt.CodecProfiles); // the entry with a quote in a condition value is dropped whole
        Assert.Equal(["vtt", "SRT", "pgs"], rebuilt.SubtitleProfiles.Select(static p => p.Format).ToArray());
        Assert.Equal(5_000_000, rebuilt.MaxStaticBitrate);
        Assert.Equal(10_000_000, rebuilt.MaxStreamingBitrate);
        Assert.Equal(1_000_000_000, rebuilt.MusicStreamingTranscodingBitrate);

        var withHls = DeviceProfileRebuilder.Rebuild(client, null, hlsSubtitlesEnabled: true);
        Assert.Contains(withHls.SubtitleProfiles, static p => p.Method == SubtitleDeliveryMethod.Hls);
        Assert.Null(withHls.MaxStreamingBitrate);
    }

    [Theory]
    [InlineData("srt", true)]
    [InlineData("subrip", true)]
    [InlineData("ASS", true)]
    [InlineData("ssa", true)]
    [InlineData("vtt", true)]
    [InlineData("webvtt", true)]
    [InlineData("js", true)]
    [InlineData("json", true)]
    [InlineData("sub", false)]
    [InlineData("smi", false)]
    [InlineData("ttml", false)]
    [InlineData("xsub", false)]
    [InlineData("pgssub", false)]
    [InlineData("pgs", false)]
    [InlineData("dvdsub", false)]
    [InlineData("dvbsub", false)]
    [InlineData("hdmv_text_subtitle", false)]
    [InlineData("", false)]
    public void T_CLI_15_OnlyTheEightServedFormats_SurviveAsExternal(string format, bool kept)
    {
        var profile = new DeviceProfile { SubtitleProfiles = [new SubtitleProfile { Format = format, Method = SubtitleDeliveryMethod.External }] };

        var rebuilt = DeviceProfileRebuilder.Rebuild(profile, null, hlsSubtitlesEnabled: true);

        Assert.Equal(kept, rebuilt.SubtitleProfiles.Length == 1);
    }

    [Theory]
    [InlineData("mp4", true)]
    [InlineData("mp4,m4v", true)]
    [InlineData("a b|c:d+e/f-g_h.i,j", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("h264\"", false)]
    [InlineData("h264<", false)]
    [InlineData("a;b", false)]
    [InlineData("a\nb", false)]
    [InlineData("café", false)]
    public void TheProfileAlphabet_IsClosed(string? value, bool safe)
    {
        Assert.Equal(safe, DeviceProfileRebuilder.IsSafe(value));
        Assert.False(DeviceProfileRebuilder.IsSafe(new string('a', 257)));
        Assert.True(DeviceProfileRebuilder.IsSafe(new string('a', 256)));
    }

    [Fact]
    public void TheStreamTitleRule_RemovesQuotesAmpersandsAndBackticks_AndCuts()
    {
        Assert.Equal("ab cd", StreamSanitizer.Title("a\"b 'c&d`"));
        Assert.Null(StreamSanitizer.Title("\"'&`"));
        Assert.Null(StreamSanitizer.Title(null));
        Assert.Equal(64, StreamSanitizer.Title(new string('x', 500))!.Length);
    }

    [Fact]
    public void TheSettingsClamp_MaxStreamsPerUser_To1Through4()
    {
        var config = new Jellyfin.Plugin.Shoal.Configuration.PluginConfiguration { MaxStreamsPerUser = 10 };

        Jellyfin.Plugin.Shoal.Configuration.SettingsValidator.Apply(config);

        Assert.Equal(4, config.MaxStreamsPerUser);
        config.MaxStreamsPerUser = 0;
        Jellyfin.Plugin.Shoal.Configuration.SettingsValidator.Apply(config);
        Assert.Equal(1, config.MaxStreamsPerUser);
    }

}
