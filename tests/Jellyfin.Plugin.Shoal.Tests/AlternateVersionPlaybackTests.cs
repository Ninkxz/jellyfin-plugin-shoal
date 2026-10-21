using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K29 (DESIGN v2.6.4): alternate versions on the playback side, against the real handlers and a real HTTP fake peer. T-ALT-6 (PlaybackInfo and static
/// per version, one slot for two versions), T-ALT-7 (a source of another title is refused before any Peer call) and the T-SEC-20 canary.
/// </summary>
public sealed class AlternateVersionPlaybackTests : IAsyncLifetime
{
    private static readonly Guid PeerItem = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PeerSourceA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid PeerSourceB = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111");
    private static readonly Guid PeerItemOther = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Primary = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Extra = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid Stranger = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private TempDirectory _dir = null!;
    private StateStore _state = null!;
    private FakeJellyfinPeer _peer = null!;
    private PeerClient _client = null!;
    private PeerRegistry _registry = null!;
    private ManualTimeProvider _time = null!;
    private StreamSlots _slots = null!;
    private GrantStore _grants = null!;
    private ShoalIndex _index = null!;
    private ShoalVersionResolver _resolver = null!;
    private PlaybackInfoHandler _info = null!;
    private StaticStreamHandler _static = null!;

    public async Task InitializeAsync()
    {
        _dir = new TempDirectory();
        _time = new ManualTimeProvider();
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, _time, null);
        await _state.UpdateAsync(StateSamples.Fill);
        var folder = "Title 0001";
        var otherFolder = "Title 0002";
        var catalogs = new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance);
        catalogs.Save(new CatalogDocument
        {
            PeerKey = StateSamples.PeerKeyA,
            CompletedOnce = true,
            Entries =
            [
                new CatalogEntry
                {
                    StubKey = "k3m9q2x7v1tz8c4n",
                    PeerItemId = PeerItem.ToString("N"),
                    PeerMediaSourceId = PeerSourceA.ToString("N"),
                    MappingKey = "movies-1",
                    Title = "The Matrix",
                    State = EntryState.Active,
                    SizeBytes = 1_000_000,
                    Container = "mkv",
                    RuntimeTicks = 72_000_000_000,
                    StubRelPath = $"{folder}/{folder}.strm",
                    Sources =
                    [
                        new CatalogSource { StubKey = "k3m9q2x7v1tz8c4n", PeerMediaSourceId = PeerSourceA.ToString("N"), StubRelPath = $"{folder}/{folder}.strm", SizeBytes = 1_000_000, Container = "mkv", RuntimeTicks = 72_000_000_000 },
                        new CatalogSource { StubKey = "x3m9q2x7v1tz8c4n", PeerMediaSourceId = PeerSourceB.ToString("N"), Label = "720p", StubRelPath = $"{folder}/{folder} - 720p.strm", SizeBytes = 400_000, Container = "mp4", RuntimeTicks = 72_000_000_000 },
                    ],
                },
                new CatalogEntry
                {
                    StubKey = "o3m9q2x7v1tz8c4n",
                    PeerItemId = PeerItemOther.ToString("N"),
                    PeerMediaSourceId = PeerItemOther.ToString("N"),
                    MappingKey = "movies-1",
                    Title = "Alien",
                    State = EntryState.Active,
                    SizeBytes = 1_000_000,
                    Container = "mkv",
                    RuntimeTicks = 72_000_000_000,
                    StubRelPath = $"{otherFolder}/{otherFolder}.strm",
                },
            ],
        });
        var root = Path.Combine(_state.Current.StubRoot, StateSamples.PeerKeyA, "movies");
        var paths = new Dictionary<Guid, string>
        {
            [Primary] = Path.Combine(root, folder, folder + ".strm"),
            [Extra] = Path.Combine(root, folder, folder + " - 720p.strm"),
            [Stranger] = Path.Combine(root, otherFolder, otherFolder + ".strm"),
        };
        _index = new ShoalIndex(new FixedPaths(paths), _state, NullLogger<ShoalIndex>.Instance);
        await _index.RebuildAsync();
        var catalog = new StubCatalog(catalogs);
        _resolver = new ShoalVersionResolver(_index, catalog);

        _peer = await FakeJellyfinPeer.StartAsync();
        _registry = new PeerRegistry(_time);
        _client = new PeerClient(() => "Home", () => "0.1.0.0", _time, _registry) { EnforceAddressPolicy = false };
        var trust = new PeerTrust(_client, _registry, _time);
        var endpoint = new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri(_peer.BaseUrl), "shoal-a1b2c3d4e5f6-4b0c1d2e", FakeJellyfinPeer.DefaultToken)
            .Configured(PeerNetwork.Tailnet, null, false, _peer.ServerId);
        _registry.SetStatus(StateSamples.PeerKeyA, PeerStatus.Online);
        _registry.RecordPinVerified(StateSamples.PeerKeyA);
        _registry.RecordAuthVerified(StateSamples.PeerKeyA);
        _slots = new StreamSlots(_time, () => 4);
        _grants = new GrantStore(_time, _slots);
        var endpoints = new FakeEndpoints(endpoint);
        _info = new PlaybackInfoHandler(
            _grants,
            new RateLimiter(_time, 600, 100),
            endpoints,
            _client,
            trust,
            _registry,
            catalog,
            _state,
            new FakeDevices(),
            new NoLimits(),
            new FakeNotice(),
            new FakeAdmins(),
            _slots,
            () => true,
            _time,
            NullLogger<PlaybackInfoHandler>.Instance,
            null,
            _resolver);
        _static = new StaticStreamHandler(catalog, endpoints, _client, trust, _registry, _slots, new RateLimiter(_time, 1200, 100), _state, new FakeNotice(), NullLogger<StaticStreamHandler>.Instance, _resolver);
        _peer.PlaybackInfoReply = (_, _) => Reply.Json(PeerAnswer());
        _peer.StreamFileSize = 1000;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _state.Dispose();
        await _peer.DisposeAsync();
        _dir.Dispose();
    }

    // --- T-ALT-6: PlaybackInfo

    [Fact]
    public async Task T_ALT_6_APlaybackInfoWithoutAMediaSource_PlaysTheRouteItemsOwnVersion()
    {
        var (status, text) = await RunAsync(await _info.HandleAsync(InfoRequest(Primary, null), default));

        Assert.Equal(200, status);
        Assert.Equal(PeerSourceA.ToString("N"), PeerBody().GetProperty("MediaSourceId").GetString());
        Assert.Equal(Primary.ToString("N"), SourceId(text));
        var grant = Assert.Single(_grants.ForUser(User));
        Assert.Equal(Primary, grant.EffectiveVersionItemId);
        Assert.Equal(PeerSourceA, grant.PeerMediaSourceId);
        Assert.Equal("k3m9q2x7v1tz8c4n", grant.SetId);
    }

    [Fact]
    public async Task T_ALT_6_APlaybackInfoThatNamesTheSecondVersion_ForwardsItsPeerSource_AndHandsBackItsOwnId()
    {
        var (status, text) = await RunAsync(await _info.HandleAsync(InfoRequest(Primary, Extra.ToString("N")), default));

        Assert.Equal(200, status);
        Assert.Equal(PeerSourceB.ToString("N"), PeerBody().GetProperty("MediaSourceId").GetString()); // T-SEC-20: the body is 6.2.1 for that version
        Assert.Equal(PeerItem.ToString("N"), _peer.Requests.Last(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal)).Path.Split('/')[2]);
        Assert.Equal(Extra.ToString("N"), SourceId(text)); // the version's own local id
        var grant = Assert.Single(_grants.ForUser(User));
        Assert.Equal(Primary, grant.LocalItemId); // the grant key stays (user, route item, device)
        Assert.Equal(Extra, grant.EffectiveVersionItemId);
        Assert.Equal(Extra, grant.LocalMediaSourceId);
        Assert.Equal(PeerSourceB, grant.PeerMediaSourceId);
    }

    [Fact]
    public async Task T_ALT_6_SwitchingVersionsOnTheSameDevice_ReplacesTheGrant_AndNothingOfThePeerIsInTheAnswer()
    {
        await _info.HandleAsync(InfoRequest(Primary, null), default);
        var (_, text) = await RunAsync(await _info.HandleAsync(InfoRequest(Primary, Extra.ToString("D")), default));

        Assert.Equal(Extra, Assert.Single(_grants.ForUser(User)).EffectiveVersionItemId);
        foreach (var secret in new[] { PeerItem, PeerSourceA, PeerSourceB })
        {
            Assert.DoesNotContain(secret.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(FakeJellyfinPeer.DefaultToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_ALT_7_APlaybackInfoThatNamesASourceOfAnotherTitle_OrGarbage_IsRefusedBeforeAnyPeerCall()
    {
        foreach (var named in new[] { Stranger.ToString("N"), Guid.NewGuid().ToString("N"), "not-a-guid", PeerSourceB.ToString("N") })
        {
            var before = _peer.Requests.Count;

            var result = await _info.HandleAsync(InfoRequest(Primary, named), default);

            Assert.Equal(400, Status(result));
            Assert.Equal(before, _peer.Requests.Count);
        }

        Assert.Empty(_grants.ForUser(User));
    }

    // --- T-ALT-6: static

    [Fact]
    public async Task T_ALT_6_AStaticStreamPerVersion_AsksThePeerForThatVersion()
    {
        var first = (StaticStreamResult)await _static.HandleAsync(StaticRequest(Primary, Primary.ToString("N")), default);
        var second = (StaticStreamResult)await _static.HandleAsync(StaticRequest(Primary, Extra.ToString("N")), default);

        var asked = _peer.Requests.Where(static r => r.Path == $"/Videos/{PeerItem:N}/stream").Select(static r => r.Query).ToList();
        Assert.Contains(asked, q => q.Contains($"mediaSourceId={PeerSourceA:N}", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(asked, q => q.Contains($"mediaSourceId={PeerSourceB:N}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T_ALT_6_TwoVersionsOfOneTitle_HoldOneSlot()
    {
        var first = (StaticStreamResult)await _static.HandleAsync(StaticRequest(Primary, null), default);
        var second = (StaticStreamResult)await _static.HandleAsync(StaticRequest(Primary, Extra.ToString("N")), default);

        Assert.Equal(1, _slots.ActiveSlots(User)); // alternating versions cannot take extra slots
        Assert.Equal(2, _slots.OpenConnections(User));
        Assert.True(_slots.CanStart(User, SlotKey.Of("k3m9q2x7v1tz8c4n"), StateSamples.PeerKeyA, 4));
    }

    // 2.9.2 K9 (Pilot 25 FAIL, a test lab: a title is on two servers): wired as in production, with BOTH the version resolver and the groups. jellyfin-web
    // direct-plays a grouped title, then Back. The grant of a picked member has no version set (SlotItem = the item), while the static stream used to key
    // its slot by the resolver's set (SlotKey.Of(stub key)), so the stop released a slot nobody held and Status kept counting 1 for the 2 min Grace.
    // The stop is core's PlaybackStopEventArgs as SessionManager.OnPlaybackStopped (v12.1) builds it: Item = the local item, DeviceId = session.DeviceId,
    // PlaySessionId "" (the web client builds a direct play URL itself).
    [Fact]
    public async Task K9_AWebDirectPlayOfAGroupedTitle_StoppedWithAnEmptyPlaySession_FreesItsSlotAtOnce()
    {
        var catalog = new StubCatalog(new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance));
        var endpoints = new FakeEndpoints(new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri(_peer.BaseUrl), "shoal-a1b2c3d4e5f6-4b0c1d2e", FakeJellyfinPeer.DefaultToken).Configured(PeerNetwork.Tailnet, null, false, _peer.ServerId));
        var trust = new PeerTrust(_client, _registry, _time);
        var member = new MemberChoice(StateSamples.PeerKeyA, catalog.Find(StateSamples.PeerKeyA, "Title 0002")!, PeerItemOther, PeerItemOther, false);
        var groups = new OneMember(member);
        var info = new PlaybackInfoHandler(_grants, new RateLimiter(_time, 600, 100), endpoints, _client, trust, _registry, catalog, _state, new FakeDevices(), new NoLimits(), new FakeNotice(), new FakeAdmins(), _slots, () => true, _time, NullLogger<PlaybackInfoHandler>.Instance, null, _resolver, groups: groups);
        var stream = new StaticStreamHandler(catalog, endpoints, _client, trust, _registry, _slots, new RateLimiter(_time, 1200, 100), _state, new FakeNotice(), NullLogger<StaticStreamHandler>.Instance, _resolver, groups: groups, grants: _grants);

        Assert.Equal(200, (await RunAsync(await info.HandleAsync(InfoRequest(Primary, null), default))).Status);
        var grant = Assert.Single(_grants.ForUser(User));
        Assert.Equal(PeerItemOther, grant.PeerItemId); // the picked member plays
        Assert.Equal(206, (await RunAsync(await stream.HandleAsync(StaticRequest(Primary, null), default))).Status);
        Assert.Equal(1, _slots.ActiveSlotsOnPeer(StateSamples.PeerKeyA)); // what Status shows as activeStreams
        Assert.True(_slots.HoldsSlot(grant)); // the stream's slot is the one the grant's books name

        await using var reporter = new PeerSessionReporter(_grants, endpoints, _client, new HlsJobControl(endpoints, _client, NullLogger<HlsJobControl>.Instance), _slots, _time, NullLogger<PeerSessionReporter>.Instance);
        var stop = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Primary },
            Users = [new Jellyfin.Database.Implementations.Entities.User("friend", "auth", "reset") { Id = User }],
            PlaybackPositionTicks = 500_000_000,
            MediaSourceId = Primary.ToString("N"),
            DeviceId = "device-1",
            ClientName = "Jellyfin Web",
            PlaySessionId = string.Empty,
        };
        reporter.OnStop(Assert.IsType<PlaybackNote>(SessionReporterHost.Note(stop)));

        Assert.Empty(_grants.ForUser(User));
        Assert.Equal(0, _slots.ActiveSlotsOnPeer(StateSamples.PeerKeyA)); // at once, not after the Grace
    }

    [Fact]
    public async Task T_ALT_7_AStaticStreamThatNamesASourceOfAnotherTitle_IsRefusedWithoutAPeerCall()
    {
        foreach (var named in new[] { Stranger.ToString("N"), Guid.NewGuid().ToString("N"), "nope" })
        {
            var before = _peer.Requests.Count;

            var result = await _static.HandleAsync(StaticRequest(Primary, named), default);

            Assert.True(Status(result) is 400 or 404, named);
            Assert.Equal(before, _peer.Requests.Count);
        }

        Assert.Equal(0, _slots.ActiveSlots(User));
    }

    [Fact]
    public async Task T_ALT_7_TheFilter_AnswersA404ForASourceOfAnotherTitle_AndLetsAVersionOfTheSameTitleThrough()
    {
        var counters = new InterceptCounters();
        var handlers = new Probe();
        var filter = new ShoalInterceptFilter(_index, null!, handlers, handlers, handlers, handlers, counters, NullLogger<ShoalInterceptFilter>.Instance, null, null, _resolver);

        var cross = await Decide(filter, $"?Static=true&mediaSourceId={Stranger:N}");

        Assert.Equal(404, Status(cross));
        Assert.Empty(handlers.Calls);

        // A version of the same title is not the foreign case: it goes on to the caller check (null here, so the boundary rule answers 500, not 404).
        var sameTitle = await Decide(filter, $"?Static=true&mediaSourceId={Extra:N}");
        Assert.NotEqual(404, Status(sameTitle));
    }

    [Fact]
    public async Task L5_AttachmentAndTrickplayRoutes_ApplyTheForeignMediaSourceRule_Too()
    {
        var counters = new InterceptCounters();
        var handlers = new Probe();
        var filter = new ShoalInterceptFilter(_index, null!, handlers, handlers, handlers, handlers, counters, NullLogger<ShoalInterceptFilter>.Instance, null, null, _resolver);
        var local = Guid.NewGuid(); // an ordinary library item
        var reached = counters.Get(InterceptCounters.CoreHls);

        // A local route id that names a stub as its media source: 404 here, instead of reaching core's handler (the merged-version hazard).
        var attachment = await DecideRoute(filter, "Videos/{videoId}/{mediaSourceId}/Attachments/{index}", "videoId", local, new Dictionary<string, string> { ["mediaSourceId"] = Primary.ToString("N"), ["index"] = "1" }, string.Empty);
        var tile = await DecideRoute(filter, "Videos/{itemId}/Trickplay/{width}/{index}.jpg", "itemId", local, new Dictionary<string, string> { ["width"] = "320", ["index"] = "0" }, $"?mediaSourceId={Primary:N}");
        Assert.Equal(404, Status(attachment));
        Assert.Equal(404, Status(tile));
        Assert.Equal(reached, counters.Get(InterceptCounters.CoreHls)); // answered by the rule, not by a handler

        // A local source named on a local route is core's business; a version of the same title on a federated route goes on to the handler.
        Assert.Null(await DecideRoute(filter, "Videos/{videoId}/{mediaSourceId}/Attachments/{index}", "videoId", local, new Dictionary<string, string> { ["mediaSourceId"] = local.ToString("N"), ["index"] = "1" }, string.Empty));
        var sameTitle = await DecideRoute(filter, "Videos/{videoId}/{mediaSourceId}/Attachments/{index}", "videoId", Primary, new Dictionary<string, string> { ["mediaSourceId"] = Extra.ToString("N"), ["index"] = "1" }, string.Empty);
        Assert.Equal(404, Status(sameTitle)); // no attachment handler registered here: the default answer
        Assert.Equal(reached + 1, counters.Get(InterceptCounters.CoreHls)); // and it was reached
        var crossTitle = await DecideRoute(filter, "Videos/{videoId}/{mediaSourceId}/Attachments/{index}", "videoId", Primary, new Dictionary<string, string> { ["mediaSourceId"] = Stranger.ToString("N"), ["index"] = "1" }, string.Empty);
        Assert.Equal(404, Status(crossTitle));
        Assert.Equal(reached + 1, counters.Get(InterceptCounters.CoreHls)); // refused by the rule before any handler
    }

    private async Task<IActionResult?> DecideRoute(ShoalInterceptFilter filter, string template, string idName, Guid routeItem, Dictionary<string, string> routeValues, string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.QueryString = new QueryString(query);
        http.Request.RouteValues[idName] = routeItem.ToString("N");
        foreach (var (key, value) in routeValues)
        {
            http.Request.RouteValues[key] = value;
        }

        var descriptor = new ActionDescriptor { AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = template } };
        var context = new ResourceExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], []);
        await filter.OnResourceExecutionAsync(context, () => Task.FromResult<ResourceExecutedContext>(null!));
        return context.Result;
    }

    [Fact]
    public async Task TheGrantOfAVersion_IsFoundBySubtitlesOfThatVersionOnly()

    {
        await _info.HandleAsync(InfoRequest(Primary, Extra.ToString("N")), default);
        var grant = Assert.Single(_grants.ForUser(User));

        Assert.Equal(Extra, grant.EffectiveVersionItemId);
        Assert.NotEqual(Primary, grant.EffectiveVersionItemId); // the subtitle route of the primary would be refused against this grant (404)
        Assert.Equal(SlotKey.Of("k3m9q2x7v1tz8c4n"), grant.SlotItem);
    }

    [Fact]
    public async Task T_ALT_6_ASubtitleOfAVersion_GoesToThatVersionsPeerSource_AndAnotherVersionsRouteIsRefusedWithoutAPeerCall()
    {
        await _info.HandleAsync(InfoRequest(Primary, Extra.ToString("N")), default); // the grant is for the second version
        var catalog = new StubCatalog(new CatalogStore(new ShoalPaths(_dir.Path), NullLogger.Instance));
        var subtitles = new SubtitleHandler(
            _grants,
            catalog,
            new FakeEndpoints(new PeerEndpoint(StateSamples.PeerKeyA, "Hops", new Uri(_peer.BaseUrl), "shoal-a1b2c3d4e5f6-4b0c1d2e", FakeJellyfinPeer.DefaultToken).Configured(PeerNetwork.Tailnet, null, false, _peer.ServerId)),
            _client,
            new PeerTrust(_client, _registry, _time),
            _registry,
            _state,
            new FakeNotice(),
            new PeerExtractionGate(_time),
            new RateLimiter(_time, 600, 100),
            _time,
            NullLogger<SubtitleHandler>.Instance,
            _resolver);
        var before = _peer.Requests.Count;

        var refused = await subtitles.HandleAsync(SubtitleRequest(Primary, Primary.ToString("N")), default);
        var stranger = await subtitles.HandleAsync(SubtitleRequest(Primary, Stranger.ToString("N")), default);

        Assert.Equal(404, Status(refused)); // the primary's track list is not this grant's
        Assert.Equal(404, Status(stranger));
        Assert.Equal(before, _peer.Requests.Count);

        await subtitles.HandleAsync(SubtitleRequest(Primary, Extra.ToString("N")), default);

        Assert.Contains(_peer.Requests, r => r.Path.StartsWith($"/Videos/{PeerItem:N}/{PeerSourceB:N}/Subtitles/2/", StringComparison.Ordinal));
    }

    // --- helpers

    private FederatedRequest SubtitleRequest(Guid item, string mediaSource)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["routeMediaSourceId"] = mediaSource;
        http.Request.RouteValues["routeIndex"] = "2";
        http.Request.RouteValues["routeFormat"] = "vtt";
        return new FederatedRequest(http, item, _index.Get(item)!, TheCaller(), "Videos/{itemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}");
    }


    private static object PeerAnswer() => new Dictionary<string, object?>
    {
        ["MediaSources"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["Id"] = "00000000000000000000000000000000",
                ["Protocol"] = "File",
                ["Container"] = "mkv",
                ["Size"] = 1_000_000L,
                ["RunTimeTicks"] = 72_000_000_000L,
                ["SupportsDirectPlay"] = true,
                ["SupportsDirectStream"] = true,
                ["SupportsTranscoding"] = false,
                ["Bitrate"] = 8_000_000,
                ["DefaultAudioStreamIndex"] = 1,
                ["MediaStreams"] = new[]
                {
                    new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264", ["Width"] = 1920, ["Height"] = 1080 },
                    new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "eac3", ["Channels"] = 6 },
                    new Dictionary<string, object?> { ["Index"] = 2, ["Type"] = "Subtitle", ["Codec"] = "srt", ["DeliveryMethod"] = "Embed", ["SupportsExternalStream"] = true },
                },
            },
        },
        ["PlaySessionId"] = "peer-play-session-0123456789",
    };

    private static Caller TheCaller() => new(new CallerUser(User, false, false, true, true, true, false, null), "caller-token-0123456789abcdef", "device-1", true);

    private FederatedRequest InfoRequest(Guid item, string? mediaSource)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(mediaSource is null ? "{}" : JsonSerializer.Serialize(new { MediaSourceId = mediaSource }));
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        return new FederatedRequest(http, item, _index.Get(item)!, TheCaller(), "Items/{itemId}/PlaybackInfo");
    }

    private FederatedRequest StaticRequest(Guid item, string? mediaSource)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.QueryString = new QueryString("?Static=true" + (mediaSource is null ? string.Empty : "&mediaSourceId=" + mediaSource));
        http.Request.Headers.Range = "bytes=0-99";
        return new FederatedRequest(http, item, _index.Get(item)!, TheCaller(), "Videos/{itemId}/stream");
    }

    private JsonElement PeerBody() => JsonDocument.Parse(_peer.Requests.Last(static r => r.Path.EndsWith("/PlaybackInfo", StringComparison.Ordinal)).Body).RootElement;

    private static string SourceId(string json) => JsonDocument.Parse(json).RootElement.GetProperty("MediaSources")[0].GetProperty("Id").GetString()!;

    private static int Status(IActionResult? result) => result is ShoalResult shoal ? AdminTestSupport.Render(shoal).Status : result is ShoalRawJsonResult ? 200 : -1;

    private static async Task<(int Status, string Text)> RunAsync(IActionResult result)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private async Task<IActionResult?> Decide(ShoalInterceptFilter filter, string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.QueryString = new QueryString(query);
        http.Request.RouteValues["itemId"] = Primary.ToString("N");
        var descriptor = new ActionDescriptor { AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = "Videos/{itemId}/stream" } };
        var action = new ActionContext(http, new RouteData(), descriptor);
        var context = new ResourceExecutingContext(action, [], []);
        await filter.OnResourceExecutionAsync(context, () => Task.FromResult<ResourceExecutedContext>(null!));
        return context.Result;
    }

    // A title on two servers whose best member is fixed (the real GroupPlayback needs the group book and the ranking).
    private sealed class OneMember : IGroupPlayback
    {
        private readonly MemberChoice _member;

        public OneMember(MemberChoice member) => _member = member;

        public IReadOnlyList<MemberChoice>? Candidates(string stubPeerKey, CatalogEntry stubEntry, CallerUser user) => [_member];

        public IReadOnlyList<MemberChoice> CandidatesForCopy(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string copyLibKey) => [_member];

        public MemberChoice? Member(string stubPeerKey, CatalogEntry stubEntry, string grantPeerKey, Guid grantPeerItemId) => grantPeerItemId == _member.PeerItemId ? _member : null;

        public IReadOnlyList<Guid> PeerIds(CatalogEntry stubEntry) => [_member.PeerItemId];

        public bool Admits(string stubPeerKey, CatalogEntry stubEntry, MemberChoice member, CallerUser user, string? copyLibKey) => true;
    }

    private sealed class NoLimits : ISystemLimits
    {
        public BitrateLimits Current() => new(0, null);
    }

    private sealed class FixedPaths : IItemPaths
    {
        private readonly IReadOnlyDictionary<Guid, string> _paths;

        public FixedPaths(IReadOnlyDictionary<Guid, string> paths) => _paths = paths;

#pragma warning disable CS0067
        public event EventHandler<ItemPathEventArgs>? Added;

        public event EventHandler<ItemPathEventArgs>? Removed;
#pragma warning restore CS0067

        public string? PathOf(Guid itemId) => _paths.GetValueOrDefault(itemId);

        public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot) => [.. _paths.Select(static p => new ItemPathEventArgs(p.Key, p.Value))];
    }

    private sealed class Probe : IPlaybackInfoHandler, IStaticStreamHandler, ISubtitleHandler, IActiveEncodingsHandler
    {
        public List<string> Calls { get; } = [];

        Task<IActionResult> IPlaybackInfoHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => Seen("playbackinfo");

        Task<IActionResult> IStaticStreamHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => Seen("static");

        Task<IActionResult> ISubtitleHandler.HandleAsync(FederatedRequest request, CancellationToken cancellationToken) => Seen("subtitle");

        Task<IActionResult?> IActiveEncodingsHandler.HandleAsync(HttpContext http, Guid? userId, string? deviceId, string? playSessionId, CancellationToken cancellationToken) => Task.FromResult<IActionResult?>(null);

        private Task<IActionResult> Seen(string what)
        {
            Calls.Add(what);
            return Task.FromResult<IActionResult>(new ShoalResult(200));
        }
    }
}
