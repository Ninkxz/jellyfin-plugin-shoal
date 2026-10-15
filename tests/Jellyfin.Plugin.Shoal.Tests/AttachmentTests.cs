using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K31 (DESIGN v2.6.6): T-ATT-1 (the rebuild table), T-ATT-2 (the route matrix), T-ATT-3 (hostile bodies), T-ATT-4 (one Peer call), T-ATT-5 (the breaker),
/// T-ATT-7 (no token in the logs), and the served type coming from the magic.
/// </summary>
public sealed class AttachmentTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private const string Token = "tok-attachment-secret-1";

    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public AttachmentTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    private FakeAttachments Fake => FakeAttachments.For(H.Peer);

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
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
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.AttachmentsEnabled = true;
        Fake.Reset();
        SetPeerAttachments(Font(1, "ttf", "A.ttf"), Font(2, "otf", "B.otf"));
        H.Peer.Requests.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- fixtures

    private static MediaAttachment Att(int index, string? mime, string? name) => new() { Index = index, MimeType = mime, FileName = name, Codec = "x", DeliveryUrl = "http://evil.example/steal" };

    private static Dictionary<string, object?> Font(int index, string ext, string name) => new()
    {
        ["Index"] = index,
        ["Codec"] = ext,
        ["FileName"] = name,
        ["MimeType"] = "application/x-truetype-font",
        ["DeliveryUrl"] = $"/Videos/{PeerItemN}/{PeerSourceN}/Attachments/{index}?ApiKey=peer-secret",
    };

    private void SetPeerAttachments(params Dictionary<string, object?>[] attachments) =>
        H.Peer.PlaybackInfoReply = (_, _) =>
        {
            var source = new Dictionary<string, object?>
            {
                ["Id"] = PeerSourceN,
                ["Protocol"] = "File",
                ["Container"] = "mkv",
                ["Size"] = 4_000_000_000L,
                ["Name"] = "The Matrix",
                ["RunTimeTicks"] = 72_000_000_000L,
                ["SupportsTranscoding"] = true,
                ["SupportsDirectPlay"] = true,
                ["Bitrate"] = 8_000_000,
                ["MediaStreams"] = new object[] { new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080 } },
                ["MediaAttachments"] = attachments,
            };
            return Reply.Json(new Dictionary<string, object?> { ["MediaSources"] = new[] { source }, ["PlaySessionId"] = "peer-play-session-0123456789" });
        };

    private string Sign(Guid user, params System.Security.Claims.Claim[] extra) => H.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user, extra));

    private async Task<JsonArray> OpenGrantAsync(string? principal = null)
    {
        using var response = await H.SendAsync(principal ?? Sign(MatrixWorld.Admin), HttpMethod.Post, $"/Items/{Item}/PlaybackInfo?ApiKey={Token}", r =>
            r.Content = new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["MediaSources"]![0]!["MediaAttachments"]!.AsArray();
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? principal = null) =>
        H.SendAsync(principal ?? Sign(MatrixWorld.Admin), HttpMethod.Get, path);

    private static string FontPath(int index, string? item = null, string? source = null, string query = "") => $"/Videos/{item ?? Item}/{source ?? Item}/Attachments/{index}{query}";

    private int FontRequests() => H.Peer.Requests.Count(static r => r.Path.Contains("/Attachments/", StringComparison.Ordinal));

    // --- T-ATT-1: the rebuild table

    private static AttachmentRebuild Rebuild(Func<bool>? enabled, params MediaAttachment[] offered) =>
        new AttachmentRebuilder(enabled ?? (() => true)).Rebuild(new MediaSourceInfo { MediaAttachments = offered }, MatrixWorld.Item, MatrixWorld.Item, Token);

    [Fact]
    public void T_ATT_1_OnlyFonts_AreKept_AndACoverIsDropped()
    {
        var result = Rebuild(null, Att(0, "image/jpeg", "cover.jpg"), Att(1, "font/ttf", "A.ttf"), Att(2, "application/octet-stream", "B.otf"), Att(3, "image/png", "c.png"), Att(4, "application/x-font-ttf", null), Att(5, "text/plain", "readme.txt"), Att(6, "font/woff2", "w.woff2"), Att(7, null, "x.ttc"));

        Assert.Equal([1, 2, 4, 6, 7], result.KeptIndexes);
        Assert.Equal(["font/ttf", "font/otf", "font/ttf", "font/woff2", "font/collection"], result.Attachments.Select(static a => a.MimeType));
    }

    [Fact]
    public void T_ATT_1_FortyFonts_AreCappedAtThirtyTwo_AndAnIndexOutsideTheRangeIsDropped()
    {
        var offered = Enumerable.Range(0, 40).Select(i => Att(i, "font/ttf", $"f{i}.ttf")).Concat([Att(128, "font/ttf", "x.ttf"), Att(-1, "font/ttf", "y.ttf"), Att(5, "font/ttf", "dup.ttf")]).ToArray();

        var result = Rebuild(null, offered);

        Assert.Equal(32, result.Attachments.Count);
        Assert.Equal(Enumerable.Range(0, 32), result.KeptIndexes);
        Assert.Equal("f5.ttf", result.Attachments[5].FileName);
    }

    [Theory]
    [InlineData("A.ttf", "A.ttf")]
    [InlineData("My Font (Bold)-1.otf", "My Font (Bold)-1.otf")]
    [InlineData("/etc/passwd.ttf", "passwd.ttf")]
    [InlineData("..\\..\\win\\evil.ttf", "evil.ttf")]
    [InlineData("<script>alert(1)</script>.ttf", "font3")]
    [InlineData("a\u0000b.ttf", "font3")]
    [InlineData("Ünïcode.ttf", "font3")]
    [InlineData("", "font3")]
    public void T_ATT_1_AHostileFileName_BecomesTheNeutralName(string name, string expected)
    {
        var result = Rebuild(null, Att(3, "font/ttf", name));

        Assert.Equal(expected, Assert.Single(result.Attachments).FileName);
    }

    [Fact]
    public void T_ATT_1_TheLongestAllowedNameIsKept_AndOneMoreIsNot()
    {
        var ok = new string('a', 60) + ".ttf";
        var tooLong = new string('a', 61) + ".ttf";

        Assert.Equal(ok, Assert.Single(Rebuild(null, Att(1, "font/ttf", ok)).Attachments).FileName);
        Assert.Equal("font1", Assert.Single(Rebuild(null, Att(1, "font/ttf", tooLong)).Attachments).FileName);
    }

    [Fact]
    public void T_ATT_1_TheDeliveryUrlGoesBackToHome_NeverToThePeer_AndTheTypeIsHomes()
    {
        var item = Att(2, "application/octet-stream", "x.ttf");
        item.DeliveryUrl = "https://evil.example/Videos/x?ApiKey=peer-secret";
        item.Codec = "ttf";

        var rebuilt = Assert.Single(Rebuild(null, item).Attachments);

        Assert.Equal($"/Videos/{Item}/{Item}/Attachments/2?ApiKey={Token}", rebuilt.DeliveryUrl);
        Assert.Equal("font/ttf", rebuilt.MimeType);
        Assert.Null(rebuilt.Codec);
        Assert.DoesNotContain("evil", rebuilt.DeliveryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void T_ATT_1_WithTheSwitchOff_NothingIsListed()
    {
        var result = Rebuild(() => false, Att(1, "font/ttf", "A.ttf"));

        Assert.Empty(result.Attachments);
        Assert.Empty(result.KeptIndexes);
    }

    [Fact]
    public async Task T_ATT_1_ThePlaybackInfoAnswer_ListsTheFonts_AndNothingOfThePeer()
    {
        SetPeerAttachments(Font(1, "ttf", "A.ttf"), new Dictionary<string, object?> { ["Index"] = 2, ["Codec"] = "mjpeg", ["FileName"] = "cover.jpg", ["MimeType"] = "image/jpeg" });

        var attachments = await OpenGrantAsync();

        var only = Assert.Single(attachments);
        Assert.Equal(1, (int)only!["Index"]!);
        Assert.Equal($"/Videos/{Item}/{Item}/Attachments/1?ApiKey=tok{MatrixWorld.Admin:N}", (string?)only["DeliveryUrl"]);
        Assert.DoesNotContain(PeerItemN, only.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("peer-secret", only.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_ATT_1_WithTheSwitchOff_PlaybackInfoListsNone_AndTheRouteIs404()
    {
        P.AttachmentsEnabled = false;
        Assert.Empty(await OpenGrantAsync());
        H.Peer.Requests.Clear();

        using var response = await GetAsync(FontPath(1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    // --- T-ATT-2: the route matrix

    [Fact]
    public async Task T_ATT_2_AListedFont_IsServed_WithHomesTypeAndHeaders_FromTheMagic()
    {
        await OpenGrantAsync();
        Fake.Reply = _ => TileReply.Font("OTTO", 3000, "text/html");

        using var response = await GetAsync(FontPath(1, query: $"?ApiKey={Token}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("font/otf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(3000, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
        Assert.Equal("default-src 'none'; sandbox", string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        var request = Assert.Single(H.Peer.Requests, static r => r.Path.Contains("/Attachments/", StringComparison.Ordinal));
        Assert.Equal($"/Videos/{PeerItemN}/{PeerSourceN}/Attachments/1", request.Path);
        Assert.DoesNotContain(Token, request.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\0\u0001\0\0", "font/ttf")]
    [InlineData("true", "font/ttf")]
    [InlineData("OTTO", "font/otf")]
    [InlineData("ttcf", "font/collection")]
    [InlineData("wOFF", "font/woff")]
    [InlineData("wOF2", "font/woff2")]
    public async Task T_ATT_2_TheServedType_FollowsTheMagic_NotThePeer(string magic, string expected)
    {
        await OpenGrantAsync();
        Fake.Reply = _ => TileReply.Font(magic, 500, "application/octet-stream");

        using var response = await GetAsync(FontPath(1));

        Assert.Equal(expected, response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task T_ATT_2_NoCredentials_Is401_AndAnApiKey_Is403_WithNoPeerCall()
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();
        var apiKey = H.World.Sign("apikey", MatrixWorld.Principal(MatrixWorld.Admin, new System.Security.Claims.Claim(CallerContext.IsApiKeyClaim, "True")));

        using var none = await H.SendAsync(null, HttpMethod.Get, FontPath(1));
        using var key = await GetAsync(FontPath(1), apiKey);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    [Theory]
    [InlineData(0)] // not listed (the peer offers index 1 and 2 only)
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(128)]
    [InlineData(99999999)]
    public async Task T_ATT_2_AnIndexTheGrantNeverListed_Is404_WithNoPeerCall(int index)
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();

        using var response = await GetAsync(FontPath(index));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1e0")]
    [InlineData("+1")]
    public async Task T_ATT_2_ANonNumberIndex_Is404(string index)
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();

        using var response = await GetAsync($"/Videos/{Item}/{Item}/Attachments/{index}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    [Fact]
    public async Task T_ATT_2_AForeignSet_AnotherUsersGrant_AndNoGrant_AreAll404_WithNoPeerCall()
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();

        using var foreign = await GetAsync(FontPath(1, source: MatrixWorld.ItemB.ToString("N")));
        using var garbage = await GetAsync(FontPath(1, source: "not-a-guid"));
        using var otherUser = await GetAsync(FontPath(1), Sign(MatrixWorld.Selected)); // allowed to see the item, but has no grant of its own
        using var otherItem = await GetAsync(FontPath(1, item: MatrixWorld.ItemB.ToString("N"), source: MatrixWorld.ItemB.ToString("N")));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherUser.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherItem.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    [Fact]
    public async Task T_ATT_2_AUserWithoutAccessToTheItem_NeverReachesThePeer()
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();

        using var response = await GetAsync(FontPath(1), Sign(MatrixWorld.Other));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, FontRequests());
    }

    // --- Warden 10 L1: the grant of an alternate version

    private sealed class FixedVersion : IVersionResolver
    {
        private readonly ResolvedVersion _version;

        public FixedVersion(ResolvedVersion version) => _version = version;

        public ResolvedVersion? Resolve(Guid routeItemId, string? requestedMediaSourceId) => _version;
    }

    private Grant AlternateGrant(Guid routeItem, Guid versionItem)
    {
        var grant = new Grant
        {
            GrantId = "alt-" + Guid.NewGuid().ToString("N"),
            LocalUserId = MatrixWorld.Admin,
            LocalItemId = routeItem,
            VersionItemId = versionItem,
            LocalMediaSourceId = versionItem,
            SetId = "setsetsetsetsetx",
            CallerDeviceId = "d",
            LocalPlaySessionId = new string('a', 32),
            PeerKey = H.World.PeerKey,
            PeerItemId = PlaybackParts.PeerItem,
            PeerMediaSourceId = PlaybackParts.PeerSource,
            AttachmentIndexes = [1],
            CreatedUtc = P.Time.GetUtcNow(),
            LastAccessUtc = P.Time.GetUtcNow(),
            LastAuthorizedUtc = P.Time.GetUtcNow(),
        };
        Assert.Contains(P.Grants.TryAdd(grant), new[] { GrantOutcome.Created, GrantOutcome.Replaced });
        return grant;
    }

    [Fact]
    public async Task Warden10_L1_FontsAreServed_ForAnAlternateVersionOfATitle()
    {
        var versionItem = MatrixWorld.ItemB;
        P.VersionOverride = new FixedVersion(new ResolvedVersion(versionItem, "stubstubstubstub", H.World.PeerKey, PlaybackParts.PeerItem, PlaybackParts.PeerSource, "setsetsetsetsetx"));
        try
        {
            AlternateGrant(MatrixWorld.Item, versionItem);

            using var response = await GetAsync(FontPath(1, source: versionItem.ToString("N")));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, FontRequests());
        }
        finally
        {
            P.VersionOverride = null;
        }
    }

    [Fact]
    public async Task Warden10_L1_AGrantOfAnotherVersion_OrAnotherTitle_DoesNotServe()
    {
        var wanted = MatrixWorld.ItemB;
        P.VersionOverride = new FixedVersion(new ResolvedVersion(wanted, "stubstubstubstub", H.World.PeerKey, PlaybackParts.PeerItem, PlaybackParts.PeerSource, "setsetsetsetsetx"));
        try
        {
            AlternateGrant(MatrixWorld.Item, MatrixWorld.ItemC); // the same title, a different version
            AlternateGrant(MatrixWorld.ItemB, wanted); // the right version, but of a different route item

            using var response = await GetAsync(FontPath(1, source: wanted.ToString("N")));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, FontRequests());
        }
        finally
        {
            P.VersionOverride = null;
        }
    }

    // --- T-ATT-3: hostile bodies

    public static TheoryData<string> HostileNames => new() { "html", "svg", "huge", "badMagic", "short", "empty", "status500", "redirectish" };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task T_ATT_3_AHostileAnswer_IsRefused_With_NoBodyOfThePeers(string name)
    {
        await OpenGrantAsync();
        Fake.Reply = _ => name switch
        {
            "html" => new TileReply(200, "font/ttf", "<html><script>alert(1)</script></html>"u8.ToArray()),
            "svg" => new TileReply(200, "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray()),
            "huge" => TileReply.Font("\0\u0001\0\0", 17 * 1024 * 1024),
            "badMagic" => TileReply.Font("BADD", 1000),
            "short" => new TileReply(200, "font/ttf", [0, 1]),
            "empty" => new TileReply(200, "font/ttf", []),
            "status500" => new TileReply(500, "font/ttf", [0, 1, 0, 0]),
            _ => new TileReply(302, "text/html", []),
        };

        using var response = await GetAsync(FontPath(1));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task T_ATT_3_APeer404_Is404()
    {
        await OpenGrantAsync();
        Fake.Reply = _ => new TileReply(404, null, []);

        using var response = await GetAsync(FontPath(1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task T_ATT_3_AGrant_ServesAtMost128Megabytes_OfFonts()
    {
        await OpenGrantAsync();
        Fake.Reply = _ => TileReply.Font("OTTO", 15 * 1024 * 1024);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 9; i++)
        {
            using var response = await GetAsync(FontPath(1));
            statuses.Add(response.StatusCode);
            await response.Content.ReadAsByteArrayAsync();
        }

        Assert.Equal(8, statuses.Count(static s => s == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.NotFound, statuses[^1]);
    }

    [Fact]
    public void T_ATT_3_TheGrantBudget_IsExact()
    {
        var grant = new Grant
        {
            GrantId = "g",
            LocalUserId = MatrixWorld.Admin,
            LocalItemId = MatrixWorld.Item,
            CallerDeviceId = "d",
            LocalPlaySessionId = new string('a', 32),
            PeerKey = "p",
            PeerItemId = PlaybackParts.PeerItem,
            PeerMediaSourceId = PlaybackParts.PeerSource,
        };

        Assert.True(grant.TryChargeAttachmentBytes(Grant.MaxAttachmentBytes));
        Assert.False(grant.TryChargeAttachmentBytes(1));
        Assert.Equal(0, FontRequests());
    }

    // --- T-ATT-4: one lane, one call

    [Fact]
    public async Task T_ATT_4_TwentyParallelRequestsForOneFont_MakeOnePeerCall()
    {
        await OpenGrantAsync();
        H.Peer.Requests.Clear();
        Fake.DelayMs = 400;

        var statuses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await GetAsync(FontPath(1));
            return response.StatusCode;
        }));

        Assert.All(statuses, static s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, FontRequests());
    }

    [Fact]
    public async Task T_ATT_4_ManyDifferentFonts_OfOneGrant_AreAllServed_ThroughOneLane()
    {
        SetPeerAttachments(Enumerable.Range(1, 10).Select(i => Font(i, "ttf", $"F{i}.ttf")).ToArray());
        await OpenGrantAsync();
        H.Peer.Requests.Clear();
        Fake.DelayMs = 60;

        var statuses = await Task.WhenAll(Enumerable.Range(1, 10).Select(async i =>
        {
            using var response = await GetAsync(FontPath(i));
            return response.StatusCode;
        }));

        Assert.All(statuses, static s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(10, FontRequests());
        Assert.Equal(1, Fake.MaxActive); // the Peer's extractor runs once at a time per source
    }

    // --- T-ATT-5: slow or bad extraction does not touch the breaker

    [Fact]
    public async Task T_ATT_5_ManyBadAnswers_DoNotOpenTheCircuit()
    {
        await OpenGrantAsync();
        Fake.Reply = _ => TileReply.Font("BADD", 100);

        for (var i = 0; i < 12; i++)
        {
            using var bad = await GetAsync(FontPath(1));
            Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        }

        Fake.Reply = _ => TileReply.Font("OTTO", 100);
        using var good = await GetAsync(FontPath(1));

        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(PeerStatus.Online, P.Registry.StatusOf(H.World.PeerKey));
    }

    // --- T-ATT-7: no token in the logs

    [Fact]
    public async Task T_ATT_7_NoApiKeyValue_IsInTheLogs()
    {
        await OpenGrantAsync();
        Fake.Reply = _ => TileReply.Font("BADD", 100);
        using var bad = await GetAsync(FontPath(1, query: $"?ApiKey={Token}"));
        Fake.Reply = _ => TileReply.Font("OTTO", 100);
        using var good = await GetAsync(FontPath(1, query: $"?ApiKey={Token}"));

        Assert.DoesNotContain(Token, H.Filter!.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeJellyfinPeer.DefaultToken, H.Filter.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain($"tok{MatrixWorld.Admin:N}", H.Filter.Log.All, StringComparison.Ordinal);
    }
}
