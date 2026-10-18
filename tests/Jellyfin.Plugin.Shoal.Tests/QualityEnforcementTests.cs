using System;
using System.Collections.Generic;
using System.Linq;
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
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K51, the pure parts: the cap of a stored limit, the profile rule, the held query, the bitrate candidate and the answer check in the rebuild.
/// </summary>
public sealed class QualityCapTests
{
    private static readonly Guid PeerItem = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PeerSource = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TheCap_OfAStoredLimit_IsTheClassBoxAndTheMbps_AndOffWithoutMbpsIsNoCap()
    {
        Assert.Null(QualityCap.From(null));
        Assert.Null(QualityCap.From(new QualityLimit()));
        Assert.Equal(new QualityCap(1920, 1080, 0), QualityCap.From(new QualityLimit { MaxClass = QualityClass.Hd1080 }));
        Assert.Equal(new QualityCap(854, 480, 3_000_000), QualityCap.From(new QualityLimit { MaxClass = QualityClass.Sd480, MaxMbps = 3 }));
        Assert.Equal(new QualityCap(0, 0, 1_000_000_000), QualityCap.From(new QualityLimit { MaxMbps = 5000 })); // clamped even if the store held more
        Assert.Equal(new QualityCap(0, 0, 1_000_000), QualityCap.From(new QualityLimit { MaxMbps = -4 }));
    }

    [Fact]
    public void ASource_FitsOnlyWhenWidthAndHeightAndBitrateAreInside_AndUnknownNeverFits()
    {
        var hd = new QualityCap(1920, 1080, 0);
        Assert.True(hd.Allows(1920, 1080, 0));
        Assert.True(hd.Allows(1920, 800, 50_000_000)); // a scope film
        Assert.False(hd.Allows(3840, 1600, 8_000_000));
        Assert.False(hd.Allows(1920, 1200, 8_000_000)); // height over the box
        Assert.False(hd.Allows(0, 0, 8_000_000));

        var mbps = new QualityCap(0, 0, 10_000_000);
        Assert.True(mbps.Allows(3840, 2160, 10_000_000));
        Assert.False(mbps.Allows(1280, 720, 10_000_001));
        Assert.False(mbps.Allows(1280, 720, 0));
    }

    [Fact]
    public void TheProfileRule_IsAVideoCodecProfileForEveryCodec_WithRequiredWidthAndHeight()
    {
        var rule = new QualityCap(1280, 720, 0).ProfileRule()!;

        Assert.Equal(CodecType.Video, rule.Type);
        Assert.Equal(string.Empty, rule.Codec);
        Assert.Collection(
            rule.Conditions,
            c => Assert.Equal((ProfileConditionType.LessThanEqual, ProfileConditionValue.Width, "1280", true), (c.Condition, c.Property, c.Value, c.IsRequired)),
            c => Assert.Equal((ProfileConditionType.LessThanEqual, ProfileConditionValue.Height, "720", true), (c.Condition, c.Property, c.Value, c.IsRequired)));
        Assert.Null(new QualityCap(0, 0, 5_000_000).ProfileRule());
    }

    [Fact]
    public void TheForwardedProfile_CarriesTheRule_AfterTheClientsCappedEntries()
    {
        var client = new DeviceProfile { CodecProfiles = [.. Enumerable.Range(0, 80).Select(static _ => new CodecProfile { Type = CodecType.Video, Codec = "h264", Conditions = [], ApplyConditions = [] })] };

        var rebuilt = DeviceProfileRebuilder.Rebuild(client, 20_000_000, true, new QualityCap(1920, 1080, 20_000_000));

        Assert.Equal(65, rebuilt.CodecProfiles.Length);
        Assert.Equal(string.Empty, rebuilt.CodecProfiles[^1].Codec);
        Assert.Equal("1920", rebuilt.CodecProfiles[^1].Conditions[0].Value);
        Assert.Equal(64, DeviceProfileRebuilder.Rebuild(client, null, true).CodecProfiles.Length); // no limit: unchanged
    }

    [Fact]
    public void TheHeldQuery_ClampsOrAddsMaxWidthMaxHeightAndVideoBitrate_DropsAFixedSize_AndKeepsTheRest()
    {
        var cap = new QualityCap(1920, 1080, 20_000_000);
        IReadOnlyList<KeyValuePair<string, string>> query =
        [
            new("DeviceId", "abc"), new("Width", "3840"), new("height", "2160"), new("maxwidth", "3840"), new("VideoBitrate", "80000000"), new("VideoBitrate", "1"), new("AudioCodec", "aac"),
        ];

        var held = cap.Hold(query).ToList();

        KeyValuePair<string, string>[] expected = [new("DeviceId", "abc"), new("maxwidth", "1920"), new("VideoBitrate", "20000000"), new("AudioCodec", "aac"), new("MaxHeight", "1080")];
        Assert.Equal(expected, held);
        Assert.Equal(held, cap.Hold(held)); // stable
        Assert.Contains(new KeyValuePair<string, string>("MaxWidth", "1280"), new QualityCap(1280, 720, 0).Hold([new("MaxWidth", "abc")]));
        Assert.Contains(new KeyValuePair<string, string>("VideoBitrate", "4000000"), new QualityCap(0, 0, 8_000_000).Hold([new("VideoBitrate", "4000000")]));
        Assert.DoesNotContain(new QualityCap(0, 0, 8_000_000).Hold([new("Width", "3840")]), static p => p.Key == "MaxWidth"); // no box: no size keys
    }

    [Fact]
    public void W11L1_ACapIsStricter_WhenItsBoxOrBitrateIsSmaller_OrItLimitsWhatWasNotLimited()
    {
        var hd = new QualityCap(1920, 1080, 0);
        Assert.True(hd.IsStricterThan(null));
        Assert.True(hd.IsStricterThan(new QualityCap(3840, 2160, 0)));
        Assert.True(hd.IsStricterThan(new QualityCap(0, 0, 20_000_000))); // a box where there was only a bitrate
        Assert.False(hd.IsStricterThan(hd));
        Assert.False(hd.IsStricterThan(new QualityCap(1280, 720, 0)));
        Assert.True(new QualityCap(0, 0, 5_000_000).IsStricterThan(new QualityCap(1920, 1080, 8_000_000)));
        Assert.False(new QualityCap(3840, 2160, 0).IsStricterThan(hd));
    }

    [Fact]
    public void TheLimitsBitrate_IsOneMoreCandidate_ForLocalAndRemoteCallers()
    {
        var user = new CallerUser(Guid.NewGuid(), false, false, true, true, true, false, null);
        var local = (Caller)Activator.CreateInstance(typeof(Caller), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, [user, "tok", "dev", true], null)!;

        Assert.Equal(8_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(120_000_000, local, new BitrateLimits(0, null), 8_000_000));
        Assert.Equal(5_000_000, PlaybackInfoBuilder.ComputeMaxStreamingBitrate(null, local, new BitrateLimits(0, 5_000_000), 8_000_000));
        Assert.Null(PlaybackInfoBuilder.ComputeMaxStreamingBitrate(null, local, new BitrateLimits(0, null), 0));
    }

    [Fact]
    public void TheRebuild_RemovesDirectPlayOfAnOverLimitSource_AndKeepsTheHeldConversion()
    {
        var answer = Answer(3840, 2160, directPlay: true, transcodingUrl: $"/videos/{PeerItem:N}/master.m3u8?MaxWidth=3840&VideoBitrate=80000000&AudioCodec=aac");

        var built = PlaybackInfoBuilder.Rebuild(answer, Context(new QualityCap(1920, 1080, 0)), out var failure)!;

        Assert.Equal(BuildFailure.None, failure);
        Assert.Equal(PlayMethod.Transcode, built.Grant.Method);
        Assert.False(built.Response.MediaSources[0].SupportsDirectPlay);
        Assert.True(built.Grant.HeldByLimit);
        Assert.Equal((3840, 2160), (built.Grant.SourceWidth, built.Grant.SourceHeight));
        Assert.Contains(new KeyValuePair<string, string>("MaxWidth", "1920"), built.Grant.PeerMasterQuery);
        Assert.Contains(new KeyValuePair<string, string>("MaxHeight", "1080"), built.Grant.PeerMasterQuery);
        Assert.Equal(new QualityCap(1920, 1080, 0), built.Grant.Cap);
    }

    [Fact]
    public void TheRebuild_RefusesOverLimit_WhenTheFriendOffersNoConversion_ButNotAFittingSource()
    {
        Assert.Null(PlaybackInfoBuilder.Rebuild(Answer(3840, 2160, directPlay: true, transcodingUrl: null), Context(new QualityCap(1920, 1080, 0)), out var failure));
        Assert.Equal(BuildFailure.OverLimit, failure);

        var fits = PlaybackInfoBuilder.Rebuild(Answer(1920, 1080, directPlay: true, transcodingUrl: null), Context(new QualityCap(1920, 1080, 0)), out failure)!;
        Assert.Equal(PlayMethod.DirectPlay, fits.Grant.Method);
        Assert.False(fits.Grant.HeldByLimit);

        var noLimit = PlaybackInfoBuilder.Rebuild(Answer(3840, 2160, directPlay: true, transcodingUrl: null), Context(null), out failure)!;
        Assert.Equal(PlayMethod.DirectPlay, noLimit.Grant.Method);

        Assert.Null(PlaybackInfoBuilder.Rebuild(Answer(1920, 1080, directPlay: false, transcodingUrl: null), Context(new QualityCap(1920, 1080, 0)), out failure));
        Assert.Equal(BuildFailure.NoStream, failure); // not the limit's fault
    }

    [Fact]
    public void TheRebuild_HoldsTheBitrateToo_SoAFastFileWithinTheBoxIsConverted()
    {
        var built = PlaybackInfoBuilder.Rebuild(Answer(1920, 1080, directPlay: true, transcodingUrl: $"/videos/{PeerItem:N}/master.m3u8?VideoBitrate=40000000", bitrate: 40_000_000), Context(new QualityCap(0, 0, 10_000_000)), out _)!;

        Assert.Equal(PlayMethod.Transcode, built.Grant.Method);
        Assert.Contains(new KeyValuePair<string, string>("VideoBitrate", "10000000"), built.Grant.PeerMasterQuery);
    }

    private static RebuildContext Context(QualityCap? cap) =>
        new("grant-1", "0123456789abcdef0123456789abcdef", Guid.NewGuid(), "dev", "tok", Guid.NewGuid(), "peer", PeerItem, PeerSource, new CatalogEntry { StubKey = "k3m9q2x7v1tz8c4n", Title = "Film" }, DateTimeOffset.UnixEpoch, Cap: cap);

    private static PlaybackInfoResponse Answer(int width, int height, bool directPlay, string? transcodingUrl, int bitrate = 8_000_000) => new()
    {
        MediaSources =
        [
            new MediaSourceInfo
            {
                Id = PeerSource.ToString("N"),
                SupportsDirectPlay = directPlay,
                SupportsTranscoding = transcodingUrl is not null,
                TranscodingUrl = transcodingUrl,
                Bitrate = bitrate,
                MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = width, Height = height }],
            },
        ],
    };
}

/// <summary>
/// PLAN v2.9 K51 on the real routes: the body the friend gets, the refusal, the held master and variant queries (a client's MaxHeight is ignored), and the
/// static route that refuses an over-limit file with 403.
/// </summary>
public sealed class QualityPlaybackTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string HexToken = "0123456789abcdef0123456789abcdef";
    private const string PeerPlaySession = "peer-play-session-0123456789";

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public QualityPlaybackTests(PlaybackFixture fixture) => _fixture = fixture;

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
        P.Notice.QualityRefusals.Clear();
        P.Devices.Profiles.Clear();
        P.Limits = new BitrateLimits(0, null);
        P.ReadAhead.Enabled = false;
        H.Peer.PublicInfo = new Reply(0);
        H.Peer.StreamFileSize = 1_000_000;
        H.Peer.StreamContentType = "video/x-matroska";
        H.Peer.StreamOverride = null;
        H.Peer.HlsPlaylistReply = HlsProxyTests.DefaultPlaylists;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheFriend_IsAskedWithTheBoxRuleAndTheLimitsBitrate_FromTheStateNotTheRequest()
    {
        await LimitAsync(QualityClass.Hd1080, 20);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(1920, 1080, direct: true, transcode: false));

        using var response = await PostAsync(ProfileBody, "?MaxStreamingBitrate=120000000&MaxHeight=2160&MaxWidth=3840");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (JsonObject)JsonNode.Parse(H.Peer.Requests.Last(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal)).Body)!;
        Assert.Equal(20_000_000, (int)body["MaxStreamingBitrate"]!);
        var rule = body["DeviceProfile"]!["CodecProfiles"]!.AsArray().Last()!;
        Assert.Equal("Video", (string?)rule["Type"]);
        Assert.Equal("1920", (string?)rule["Conditions"]![0]!["Value"]);
        Assert.Equal("1080", (string?)rule["Conditions"]![1]!["Value"]);
        Assert.True((bool)rule["Conditions"]![1]!["IsRequired"]!);
        Assert.DoesNotContain("2160", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A1080pTitle_UnderA1080pLimit_StillPlaysDirect_AndStreams()
    {
        await LimitAsync(QualityClass.Hd1080, null);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(1920, 1080, direct: true, transcode: true));

        using var response = await PostAsync(ProfileBody);

        var source = (await ReadAsync(response))["MediaSources"]![0]!;
        Assert.True((bool)source["SupportsDirectPlay"]!);
        var grant = Grant();
        Assert.Equal(PlayMethod.DirectPlay, grant.Method);
        using var stream = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?MediaSourceId={Item}&Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));
        Assert.Equal(HttpStatusCode.PartialContent, stream.StatusCode);
    }

    [Fact]
    public async Task A4kTitle_DirectPlayIsRemoved_AndTheConversionIsHeldTo1080p()
    {
        await LimitAsync(QualityClass.Hd1080, null);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(3840, 2160, direct: true, transcode: true));

        using var response = await PostAsync(ProfileBody);

        var source = (await ReadAsync(response))["MediaSources"]![0]!;
        Assert.False((bool)source["SupportsDirectPlay"]!);
        Assert.NotNull((string?)source["TranscodingUrl"]);
        var grant = Grant();
        Assert.Equal(PlayMethod.Transcode, grant.Method);
        Assert.True(grant.HeldByLimit);
        Assert.Contains(new KeyValuePair<string, string>("MaxHeight", "1080"), grant.PeerMasterQuery);
        Assert.DoesNotContain(grant.PeerMasterQuery, static p => p.Key == "Width");
    }

    [Fact]
    public async Task A4kTitle_WithNoConversionLeft_IsRefused_AndTheUserIsTold()
    {
        await LimitAsync(QualityClass.Hd1080, null);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(3840, 2160, direct: true, transcode: false));

        using var response = await PostAsync(ProfileBody);

        Assert.Equal("NoCompatibleStream", (string?)(await ReadAsync(response))["ErrorCode"]);
        Assert.Equal([(MatrixWorld.Admin, H.World.PeerKey)], P.Notice.QualityRefusals);
        Assert.Empty(P.Grants.ForUser(MatrixWorld.Admin));
    }

    [Fact]
    public async Task TheHlsProxy_IgnoresATamperedMaxHeight_AndHoldsTheFriendsVariantQueryToo()
    {
        await LimitAsync(QualityClass.Hd1080, 20);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(3840, 2160, direct: false, transcode: true));
        H.Peer.HlsPlaylistReply = (path, query) => path.EndsWith("/master.m3u8", StringComparison.Ordinal)
            ? new Reply(200, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1372000,AVERAGE-BANDWIDTH=1372000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1280x544,FRAME-RATE=24\nmain.m3u8?DeviceId=shoal-a1b2c3d4e5f6-4b0c1d2e&MediaSourceId=" + PeerSourceN + "&PlaySessionId=" + PeerPlaySession + "&VideoCodec=h264&MaxWidth=3840&MaxHeight=2160&VideoBitrate=90000000\n", "application/x-mpegURL")
            : HlsProxyTests.DefaultPlaylists(path, query);

        using var info = await PostAsync(ProfileBody);
        var grant = Grant();
        using var master = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/master.m3u8?MaxHeight=2160&MaxWidth=3840&VideoBitrate=200000000&MaxStreamingBitrate=200000000");
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        using var main = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/main.m3u8?MaxHeight=2160");
        Assert.Equal(HttpStatusCode.OK, main.StatusCode);

        var masterQuery = Pairs(H.Peer.Requests.Last(static r => r.Path.EndsWith("/master.m3u8", StringComparison.Ordinal)).Query);
        Assert.Equal("1080", masterQuery["MaxHeight"]);
        Assert.Equal("1920", masterQuery["MaxWidth"]);
        Assert.Equal("20000000", masterQuery["VideoBitrate"]);
        Assert.DoesNotContain("MaxStreamingBitrate", masterQuery.Keys);
        var mainQuery = Pairs(H.Peer.Requests.Last(static r => r.Path.EndsWith("/main.m3u8", StringComparison.Ordinal)).Query);
        Assert.Equal("1080", mainQuery["MaxHeight"]);
        Assert.Equal("1920", mainQuery["MaxWidth"]);
        Assert.Equal("20000000", mainQuery["VideoBitrate"]);
    }

    [Fact]
    public async Task W11L1_AStricterLimitSetWhileAConversionRuns_StopsItAtTheNextFullReCheck_ALooserOneDoesNot()
    {
        await LimitAsync(QualityClass.Uhd2160, null);
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(3840, 2160, direct: false, transcode: true));
        using (var info = await PostAsync(ProfileBody))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }

        var grant = Grant();
        await LimitAsync(QualityClass.Off, null); // looser: keeps playing
        P.Time.Advance(TimeSpan.FromMinutes(6));
        using (var master = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/master.m3u8"))
        {
            Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        }

        await LimitAsync(QualityClass.Hd1080, null); // stricter: stops
        P.Time.Advance(TimeSpan.FromMinutes(6));
        using var again = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Shoal/Hls/{grant.GrantId}/master.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Null(P.Grants.Get(grant.GrantId));
    }

    [Fact]
    public async Task TheStaticRoute_RefusesAFileOverTheLimit_With403_AndNeverAsksTheFriend()
    {
        H.Peer.PlaybackInfoReply = (_, _) => Reply.Json(Answer(3840, 2160, direct: true, transcode: false));
        using var info = await PostAsync(ProfileBody); // no limit yet: a direct play grant for a 4K file
        Assert.Equal(PlayMethod.DirectPlay, Grant().Method);
        await LimitAsync(QualityClass.Hd1080, null); // the admin lowers the limit while it plays
        H.Peer.Requests.Clear();

        using var stream = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?MediaSourceId={Item}&Static=true&MaxHeight=4000", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));

        Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        Assert.DoesNotContain(H.Peer.Requests, static r => r.Path.EndsWith("/stream", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStaticRoute_WithNoGrant_AndASizeTheCatalogDoesNotKnow_IsRefusedUnderALimit()
    {
        await LimitAsync(QualityClass.Uhd2160, null);

        using var stream = await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Get, $"/Videos/{Item}/stream?MediaSourceId={Item}&Static=true", r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-9"));

        Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
    }

    // --- helpers

    private const string ProfileBody = """
        {
          "DeviceProfile": {
            "DirectPlayProfiles": [ { "Container": "mkv,mp4", "VideoCodec": "h264,hevc", "AudioCodec": "aac,eac3", "Type": "Video" } ],
            "TranscodingProfiles": [ { "Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls", "Context": "Streaming" } ],
            "CodecProfiles": [ { "Type": "Video", "Codec": "h264", "Conditions": [ { "Condition": "LessThanEqual", "Property": "VideoLevel", "Value": "52", "IsRequired": false } ] } ]
          }
        }
        """;

    private static Dictionary<string, string> Pairs(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(static p => p.Split('=', 2)).ToDictionary(static p => p[0], static p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty), StringComparer.Ordinal);

    private static object Answer(int width, int height, bool direct, bool transcode)
    {
        var source = new Dictionary<string, object?>
        {
            ["Id"] = PeerSourceN,
            ["Protocol"] = "File",
            ["Container"] = "mkv",
            ["Size"] = 1_000_000L,
            ["RunTimeTicks"] = 72_000_000_000L,
            ["Bitrate"] = 8_000_000,
            ["SupportsDirectPlay"] = direct,
            ["SupportsDirectStream"] = direct,
            ["SupportsTranscoding"] = transcode,
            ["DefaultAudioStreamIndex"] = 1,
            ["MediaStreams"] = new object[]
            {
                new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "hevc", ["Width"] = width, ["Height"] = height },
                new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "eac3", ["Language"] = "eng", ["Channels"] = 6, ["IsDefault"] = true },
            },
        };
        if (transcode)
        {
            source["TranscodingUrl"] = $"/videos/{PeerItemN}/master.m3u8?DeviceId=abc&MediaSourceId={PeerSourceN}&VideoCodec=h264&AudioCodec=aac&Width=3840&MaxWidth={width}&VideoBitrate=90000000";
            source["TranscodingSubProtocol"] = "hls";
            source["TranscodingContainer"] = "ts";
        }

        return new Dictionary<string, object?> { ["MediaSources"] = new[] { source }, ["PlaySessionId"] = PeerPlaySession };
    }

    private Task LimitAsync(QualityClass cls, int? mbps) =>
        H.State.UpdateAsync(s => s.Home.Peers[0].QualityLimit = new QualityLimit { MaxClass = cls, MaxMbps = mbps });

    private Grant Grant() => P.Grants.ForUser(MatrixWorld.Admin).Where(static g => g.LocalItemId == MatrixWorld.Item).OrderBy(static g => g.CreatedUtc).Last();

    private string Sign(Guid user)
    {
        var principal = MatrixWorld.Principal(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(CallerContext.TokenClaim)!);
        identity.AddClaim(new Claim(CallerContext.TokenClaim, HexToken));
        return H.World.Sign("q-" + user.ToString("N"), principal);
    }

    private async Task<HttpResponseMessage> PostAsync(string body, string query = "") =>
        await H.SendAsync(Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo{query}", r => r.Content = new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response) => (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
