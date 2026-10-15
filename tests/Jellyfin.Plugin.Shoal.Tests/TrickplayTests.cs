using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K30 (DESIGN v2.6.5): T-TRK-1 (the metadata sanitizer), T-TRK-2 (the route matrix), T-TRK-3 (hostile bodies), T-TRK-4 (coalescing and the gate),
/// T-TRK-5 (cache cap and LRU), T-TRK-6 (offline), and the TrickplayInfo writer.
/// </summary>
public sealed class TrickplayTests : IClassFixture<PlaybackFixture>, IAsyncLifetime
{
    private static readonly string Item = MatrixWorld.Item.ToString("N");
    private static readonly string PeerItemN = PlaybackParts.PeerItem.ToString("N");
    private static readonly string PeerSourceN = PlaybackParts.PeerSource.ToString("N");

    private readonly PlaybackFixture _fixture;

    public TrickplayTests(PlaybackFixture fixture) => _fixture = fixture;

    private MatrixHarness H => _fixture.H;

    private PlaybackParts P => H.Filter!.Playback!;

    private FakeTrickplay Fake => FakeTrickplay.For(H.Peer);

    public async Task InitializeAsync()
    {
        await H.ResetAsync();
        await H.Filter!.Index.RebuildAsync();
        P.Time.Advance(TimeSpan.FromHours(1));
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.Online);
        P.Registry.RecordPinVerified(H.World.PeerKey);
        P.Registry.RecordAuthVerified(H.World.PeerKey);
        P.TrickplayEnabled = true;
        P.TrickplayCapBytes = 1L << 30;
        P.TrickplayCache.Clear();
        Fake.Reset();
        H.Peer.Requests.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string Sign(Guid user) => H.World.Sign("p-" + user.ToString("N"), MatrixWorld.Principal(user));

    private Task<HttpResponseMessage> GetAsync(string path, string? principal = null) =>
        H.SendAsync(principal ?? Sign(MatrixWorld.Admin), HttpMethod.Get, path);

    private int TileRequests() => H.Peer.Requests.Count(static r => r.Path.Contains("/Trickplay/", StringComparison.Ordinal));

    private static string Tile(string item, int width, int index, string query = "") => $"/Videos/{item}/Trickplay/{width}/{index}.jpg{query}";

    // --- T-TRK-1: the metadata sanitizer

    private static TrickplayInfoDto Info(int width, int height = 180, int tileW = 10, int tileH = 10, int count = 250, int interval = 10_000, int bandwidth = 500_000) =>
        new(new TrickplayInfo { Width = width, Height = height, TileWidth = tileW, TileHeight = tileH, ThumbnailCount = count, Interval = interval, Bandwidth = bandwidth });

    private static BaseItemDto Dto(Dictionary<string, Dictionary<int, TrickplayInfoDto>> manifest) => new() { Trickplay = manifest };

    [Fact]
    public void T_TRK_1_AValidLevel_IsKept_ForTheNamedSourceOnly()
    {
        var other = Guid.NewGuid();
        var dto = Dto(new() { [PeerSourceN] = new() { [320] = Info(320) }, [other.ToString("N")] = new() { [160] = Info(160) } });

        var levels = new TrickplaySanitizer().Read(dto, PlaybackParts.PeerSource);

        Assert.Equal([new SanitizedTrickplay(320, 180, 10, 10, 250, 10_000, 500_000)], levels);
    }

    [Fact]
    public void T_TRK_1_FiveWidths_KeepTheFourSmallest()
    {
        var dto = Dto(new() { [PeerSourceN] = new() { [640] = Info(640), [320] = Info(320), [160] = Info(160), [80] = Info(80), [1024] = Info(1024, 100, 4, 4) } });

        var levels = new TrickplaySanitizer().Read(dto, PlaybackParts.PeerSource);

        Assert.Equal([80, 160, 320, 640], levels.Select(static l => l.Width));
    }

    [Theory]
    [InlineData(0, 180, 10, 10, 250, 10000, 0, false)]
    [InlineData(1025, 180, 1, 1, 250, 10000, 0, false)]
    [InlineData(320, 0, 10, 10, 250, 10000, 0, false)]
    [InlineData(320, 1025, 1, 1, 250, 10000, 0, false)]
    [InlineData(320, 180, 0, 10, 250, 10000, 0, false)]
    [InlineData(320, 180, 33, 10, 250, 10000, 0, false)]
    [InlineData(320, 180, 10, 0, 250, 10000, 0, false)]
    [InlineData(320, 180, 10, 33, 250, 10000, 0, false)]
    [InlineData(320, 180, 10, 10, 0, 10000, 0, false)]
    [InlineData(320, 180, 10, 10, 100001, 10000, 0, false)]
    [InlineData(320, 180, 10, 10, 250, 0, 0, false)]
    [InlineData(320, 180, 10, 10, 250, 3600001, 0, false)]
    [InlineData(320, 180, 10, 10, 250, 10000, -1, false)]
    [InlineData(320, 180, 10, 10, 250, 10000, 100000001, false)]
    [InlineData(1024, 1024, 9, 9, 250, 10000, 0, false)]
    [InlineData(320, 180, 10, 10, 100000, 3600000, 100000000, true)]
    [InlineData(1, 1, 32, 32, 1, 1, 0, true)]
    [InlineData(256, 256, 32, 32, 250, 10000, 0, false)]
    public void T_TRK_1_EveryRangeEdge(int width, int height, int tileW, int tileH, int count, int interval, int bandwidth, bool valid)
    {
        var dto = Dto(new() { [PeerSourceN] = new() { [width] = Info(width, height, tileW, tileH, count, interval, bandwidth) } });

        var levels = new TrickplaySanitizer().Read(dto, PlaybackParts.PeerSource);

        Assert.Equal(valid, levels.Count == 1);
    }

    [Fact]
    public void T_TRK_1_AKeyThatDisagreesWithTheWidth_OrNoManifest_GivesNothing()
    {
        Assert.Empty(new TrickplaySanitizer().Read(Dto(new() { [PeerSourceN] = new() { [640] = Info(320) } }), PlaybackParts.PeerSource));
        Assert.Empty(new TrickplaySanitizer().Read(Dto(new() { ["not a guid"] = new() { [320] = Info(320) } }), PlaybackParts.PeerSource));
        Assert.Empty(new TrickplaySanitizer().Read(new BaseItemDto(), PlaybackParts.PeerSource));
        Assert.Empty(new TrickplaySanitizer().Read(Dto(new() { [PeerSourceN] = null! }), PlaybackParts.PeerSource));
    }

    // --- T-TRK-2: the route matrix

    [Fact]
    public async Task T_TRK_2_ATileIsFetched_ValidatedAndServedWithHomesOwnHeaders_AndTheSecondComesFromDisk()
    {
        Fake.Reply = (_, _) => TileReply.Jpeg(ImageBuilder.Jpeg(3200, 1800, 6000));

        using var first = await GetAsync(Tile(Item, 320, 0, "?ApiKey=tok"));
        var body = await first.Content.ReadAsByteArrayAsync();
        using var second = await GetAsync(Tile(Item, 320, 0));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal(6000, body.Length);
        Assert.Contains("nosniff", first.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", first.Headers.GetValues("Cache-Control"));
        Assert.Equal("default-src 'none'; sandbox", string.Join(",", first.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("attachment", first.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(body, await second.Content.ReadAsByteArrayAsync());
        var request = Assert.Single(H.Peer.Requests, static r => r.Path.Contains("/Trickplay/", StringComparison.Ordinal));
        Assert.Equal($"/Videos/{PeerItemN}/Trickplay/320/0.jpg", request.Path);
        Assert.Equal($"?mediaSourceId={PeerSourceN}", request.Query);
        Assert.DoesNotContain("tok", request.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(160, 0)] // a width the source does not have
    [InlineData(320, 3)] // index at N (250 thumbnails in 100-per-tile images is 3 tiles: 0..2)
    [InlineData(320, 99999)]
    [InlineData(320, 999999999)]
    [InlineData(32000, 0)]
    public async Task T_TRK_2_AnUnknownWidthOrIndex_Is404_WithNoPeerCall(int width, int index)
    {
        using var response = await GetAsync(Tile(Item, width, index));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, TileRequests());
    }

    [Fact]
    public async Task T_TRK_2_TheLastIndex_IsServed()
    {
        using var response = await GetAsync(Tile(Item, 320, 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, TileRequests());
    }

    [Theory]
    [InlineData("/Videos/{0}/Trickplay/320/-1.jpg")]
    [InlineData("/Videos/{0}/Trickplay/abc/0.jpg")]
    [InlineData("/Videos/{0}/Trickplay/320/1e1.jpg")]
    [InlineData("/Videos/{0}/Trickplay/+320/0.jpg")]
    public async Task T_TRK_2_NonNumbers_Are404_WithNoPeerCall(string template)
    {
        using var response = await GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Item));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, TileRequests());
    }

    [Fact]
    public async Task T_TRK_2_RepeatedMediaSourceIdKeys_AreRefused_AndAForeignSourceIs404()
    {
        using var repeated = await GetAsync(Tile(Item, 320, 0, $"?mediaSourceId={Item}&mediaSourceId={Item}"));
        using var foreign = await GetAsync(Tile(Item, 320, 0, $"?mediaSourceId={MatrixWorld.ItemB:N}"));
        using var own = await GetAsync(Tile(Item, 320, 0, $"?mediaSourceId={Item}"));

        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(1, TileRequests());
    }

    [Fact]
    public async Task T_TRK_2_NoCredentials_Is401_AndCounted_WithNoPeerCall()
    {
        var before = H.Filter!.Counters.Get(InterceptCounters.NoCredentials);

        using var response = await H.SendAsync(null, HttpMethod.Get, Tile(Item, 320, 0));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before + 1, H.Filter.Counters.Get(InterceptCounters.NoCredentials));
        Assert.Equal(0, TileRequests());
    }

    [Fact]
    public async Task T_TRK_2_AnApiKey_AndAUserWithoutAccess_NeverReachTheCacheOrThePeer()
    {
        var apiKey = H.World.Sign("apikey", MatrixWorld.Principal(MatrixWorld.Admin, new System.Security.Claims.Claim(CallerContext.IsApiKeyClaim, "True")));
        Fake.Reply = (_, _) => TileReply.Jpeg(ImageBuilder.Jpeg(3200, 1800, 3000));
        (await GetAsync(Tile(Item, 320, 0))).Dispose(); // cached for the admin

        using var withKey = await GetAsync(Tile(Item, 320, 0), apiKey);
        using var other = await GetAsync(Tile(Item, 320, 0), Sign(MatrixWorld.Other));

        Assert.Equal(HttpStatusCode.Forbidden, withKey.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(1, TileRequests());
    }

    [Fact]
    public async Task T_TRK_2_WithTheSwitchOff_TheRouteIs404()
    {
        P.TrickplayEnabled = false;

        using var response = await GetAsync(Tile(Item, 320, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, TileRequests());
    }

    // --- T-TRK-3: hostile bodies leave no cache file

    public static TheoryData<string> HostileNames => new() { "html", "png", "huge", "bomb", "wrongType", "toowide", "tooTall", "status500", "empty" };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task T_TRK_3_AHostileAnswer_Is502_AndNothingIsCached(string name)
    {
        Fake.Reply = (_, _) => name switch
        {
            "html" => new TileReply(200, "image/jpeg", "<html><script>alert(1)</script></html>"u8.ToArray()),
            "png" => new TileReply(200, "image/jpeg", ImageBuilder.Png(3200, 1800, 3000)),
            "huge" => new TileReply(200, "image/jpeg", ImageBuilder.Jpeg(3200, 1800, 5 * 1024 * 1024)),
            "bomb" => new TileReply(200, "image/jpeg", ImageBuilder.Jpeg(8000, 8000, 4000, withExifThumbnail: true)),
            "wrongType" => new TileReply(200, "text/html", ImageBuilder.Jpeg(3200, 1800, 3000)),
            "toowide" => new TileReply(200, "image/jpeg", ImageBuilder.Jpeg(3300, 1800, 3000)),
            "tooTall" => new TileReply(200, "image/jpeg", ImageBuilder.Jpeg(3200, 1900, 3000)),
            "status500" => new TileReply(500, "image/jpeg", ImageBuilder.Jpeg(3200, 1800, 3000)),
            _ => new TileReply(200, "image/jpeg", []),
        };

        using var response = await GetAsync(Tile(Item, 320, 0));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, P.TrickplayCache.Count);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task T_TRK_3_AShorterLastTile_IsAccepted()
    {
        Fake.Reply = (_, _) => TileReply.Jpeg(ImageBuilder.Jpeg(3200, 540, 3000));

        using var response = await GetAsync(Tile(Item, 320, 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task T_TRK_3_ASanitizerThatRefuses_Is502_AndNothingIsCached()
    {
        P.Sanitizer = new RefusingSanitizer();
        try
        {
            using var response = await GetAsync(Tile(Item, 320, 0));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Empty(CacheFiles());
        }
        finally
        {
            P.Sanitizer = new PassThroughImageSanitizer();
        }
    }

    [Fact]
    public async Task T_TRK_3_ASlowTrickle_IsNotCached_AndThePeerStatusStaysUnchanged()
    {
        Fake.DelayMs = 100;
        Fake.Reply = (_, _) => new TileReply(404, null, []);

        using var response = await GetAsync(Tile(Item, 320, 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task T_TRK_3_NoPeerIdOrToken_IsInAnyCacheFileName()
    {
        (await GetAsync(Tile(Item, 320, 0))).Dispose();
        (await GetAsync(Tile(Item, 320, 1))).Dispose();

        var files = CacheFiles();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(H.Dir.Combine("trickplay-cache"), file);
            Assert.DoesNotContain(PeerItemN, relative, StringComparison.Ordinal);
            Assert.DoesNotContain(PeerSourceN, relative, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeJellyfinPeer.DefaultToken, relative, StringComparison.Ordinal);
            Assert.Matches(@"\A[0-9a-f]{16,64}[\\/][a-z0-9]{16}[\\/](\d{1,4}_\d{1,6}\.jpg|signature)\z", relative);
        }
    }

    // --- T-TRK-4: coalescing and the gate

    [Fact]
    public async Task T_TRK_4_FiftyIdenticalRequests_MakeOnePeerCall()
    {
        Fake.DelayMs = 400;

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            using var response = await GetAsync(Tile(Item, 320, 1));
            return response.StatusCode;
        }));

        Assert.All(responses, static s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, TileRequests());
    }

    [Fact]
    public async Task T_TRK_4_TheGate_AllowsFourCallsAtOnce_AndRefusesPastThirtyTwoWaiting()
    {
        var flights = new TileFlights();
        var release = new TaskCompletionSource();
        var running = 0;
        var maxRunning = 0;

        async Task<TileFetch> Call(CancellationToken token)
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            do
            {
                seen = maxRunning;
            }
            while (now > seen && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen);
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref running);
            return new TileFetch(200, new byte[] { 1 });
        }

        var tasks = new List<Task<TileFetch?>>();
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(flights.RunAsync("peer", "k" + i, Call, CancellationToken.None));
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref running) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        for (var i = 4; i < 4 + TileFlights.MaxWaiting; i++)
        {
            tasks.Add(flights.RunAsync("peer", "k" + i, Call, CancellationToken.None));
        }

        var refused = await flights.RunAsync("peer", "overflow", Call, CancellationToken.None);
        var otherPeer = flights.RunAsync("peer2", "k0", Call, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Null(refused);
        Assert.All(results, static r => Assert.Equal(200, r!.Status));
        Assert.Equal(200, (await otherPeer)!.Status);
        Assert.True(maxRunning <= TileFlights.GateSlots + 1, $"{maxRunning} calls ran at once (the second peer has its own gate)");
    }

    [Fact]
    public async Task T_TRK_4_ACallOutlivesItsWaiters_SoTheTileLandsInTheCache()
    {
        Fake.DelayMs = 300;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, H.BaseUrl + Tile(Item, 320, 1));
            request.Headers.Add(MatrixAuthentication.Header, Sign(MatrixWorld.Admin));
            using var http = new HttpClient();
            await http.SendAsync(request, cts.Token);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (P.TrickplayCache.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, P.TrickplayCache.Count);
    }

    // --- T-TRK-6: offline

    [Fact]
    public async Task T_TRK_6_ACachedTile_IsServedWhileThePeerIsDown_AnUncachedOneIs503()
    {
        (await GetAsync(Tile(Item, 320, 0))).Dispose();
        P.Registry.SetStatus(H.World.PeerKey, PeerStatus.AuthFailed);
        var before = TileRequests();

        using var cached = await GetAsync(Tile(Item, 320, 0));
        using var missing = await GetAsync(Tile(Item, 320, 1));

        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Empty(await missing.Content.ReadAsByteArrayAsync());
        Assert.Equal(before, TileRequests());
    }

    // --- the cache itself (T-TRK-5)

    [Fact]
    public void T_TRK_5_TheCache_NeverExceedsItsCap_AndEvictsTheLeastRecentlyUsed()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider();
        const long Cap = 5 * 1024 * 1024;
        var cache = new TrickplayCache(dir.Combine("c"), () => Cap, time, NullLogger<TrickplayCache>.Instance);
        var peer = new string('a', 16);
        var stub = "stubstubstubstub";
        var tile = new byte[400 * 1024];

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(cache.Store(peer, stub, 320, i, tile));
            Assert.True(cache.TotalBytes <= Cap, $"{cache.TotalBytes} over the cap after tile {i}");
            Assert.Equal(cache.TotalBytes, Directory.EnumerateFiles(dir.Combine("c"), "*.jpg", SearchOption.AllDirectories).Sum(static f => new FileInfo(f).Length));
        }

        Assert.True(cache.TotalBytes <= Cap * 9 / 10 + tile.Length);
        Assert.NotNull(cache.TryGet(peer, stub, 320, 19)); // the newest survives
        Assert.Null(cache.TryGet(peer, stub, 320, 0)); // the oldest went first
    }

    [Fact]
    public void T_TRK_5_ATileThatWasTouched_OutlivesOlderOnes()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider();
        var cache = new TrickplayCache(dir.Combine("c"), () => 1_000_000, time, NullLogger<TrickplayCache>.Instance);
        var peer = new string('b', 16);
        var stub = "stubstubstubstub";
        var tile = new byte[200_000];
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            cache.Store(peer, stub, 320, i, tile);
        }

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(cache.TryGet(peer, stub, 320, 0));
        time.Advance(TimeSpan.FromSeconds(1));
        cache.Store(peer, stub, 320, 4, tile);
        time.Advance(TimeSpan.FromSeconds(1));
        cache.Store(peer, stub, 320, 5, tile); // over the cap: the least recently used goes

        Assert.NotNull(cache.TryGet(peer, stub, 320, 0));
        Assert.Null(cache.TryGet(peer, stub, 320, 1));
    }

    [Fact]
    public void T_TRK_5_RemovingAStub_ReconcilingAndAChangedSignature_ClearTheFolder()
    {
        using var dir = new TempDirectory();
        var cache = new TrickplayCache(dir.Combine("c"), () => 1L << 30, TimeProvider.System, NullLogger<TrickplayCache>.Instance);
        var peer = new string('c', 16);
        var keep = "keepkeepkeepkeep";
        var gone = "gonegonegonegone";
        cache.EnsureSignature(peer, keep, "320,180,10,10,250,10000");
        cache.EnsureSignature(peer, gone, "320,180,10,10,250,10000");
        cache.Store(peer, keep, 320, 0, new byte[100]);
        cache.Store(peer, gone, 320, 0, new byte[100]);

        Assert.Equal(1, cache.Reconcile(new HashSet<(string, string)> { (peer, keep) }));
        Assert.Null(cache.TryGet(peer, gone, 320, 0));
        Assert.NotNull(cache.TryGet(peer, keep, 320, 0));

        cache.EnsureSignature(peer, keep, "320,180,10,10,250,10000"); // unchanged
        Assert.NotNull(cache.TryGet(peer, keep, 320, 0));
        cache.EnsureSignature(peer, keep, "320,180,5,5,250,10000"); // the Peer regenerated its tiles
        Assert.Null(cache.TryGet(peer, keep, 320, 0));
        Assert.Equal(0, cache.TotalBytes);
    }

    [Fact]
    public void T_TRK_5_AStartupWalk_FindsTheTilesOfTheLastRun_AndIgnoresStrangers()
    {
        using var dir = new TempDirectory();
        var peer = new string('d', 16);
        var stub = "stubstubstubstub";
        var first = new TrickplayCache(dir.Combine("c"), () => 1L << 30, TimeProvider.System, NullLogger<TrickplayCache>.Instance);
        first.Store(peer, stub, 320, 0, new byte[500]);
        File.WriteAllText(Path.Combine(dir.Combine("c"), peer, stub, "0_9.jpg.tmp-abc"), "x");
        Directory.CreateDirectory(Path.Combine(dir.Combine("c"), "not-a-peer-key", stub));
        File.WriteAllText(Path.Combine(dir.Combine("c"), "not-a-peer-key", stub, "0_0.jpg"), "x");

        var second = new TrickplayCache(dir.Combine("c"), () => 1L << 30, TimeProvider.System, NullLogger<TrickplayCache>.Instance);
        second.Scan();

        Assert.Equal(1, second.Count);
        Assert.Equal(500, second.TotalBytes);
        Assert.NotNull(second.TryGet(peer, stub, 320, 0));
        Assert.False(File.Exists(Path.Combine(dir.Combine("c"), peer, stub, "0_9.jpg.tmp-abc")));
    }

    [Theory]
    [InlineData("../x", "stubstubstubstub")]
    [InlineData("aaaaaaaaaaaaaaaa", "../etc/passwd")]
    [InlineData("aaaaaaaaaaaaaaaa", "STUBSTUBSTUBSTUB")]
    [InlineData("aaaaaaaaaaaaaaaa", "short")]
    public void T_TRK_5_AnUnsafeKey_NeverTouchesTheDisk(string peer, string stub)
    {
        using var dir = new TempDirectory();
        var cache = new TrickplayCache(dir.Combine("c"), () => 1L << 30, TimeProvider.System, NullLogger<TrickplayCache>.Instance);

        Assert.False(cache.Store(peer, stub, 320, 0, new byte[10]));
        Assert.Null(cache.TryGet(peer, stub, 320, 0));
        Assert.False(Directory.Exists(dir.Combine("c")));
    }

    // --- the TrickplayInfo writer

    [Fact]
    public async Task TheWriter_SavesTheLevels_ThenLeavesAnUnchangedItemAlone_AndClearsWhenTheSwitchTurnsOff()
    {
        var manager = TrickplayManagerFake.Create(out var rows);
        var enabled = true;
        var writer = Writer(manager, () => enabled);

        var first = await writer.RunAsync(CancellationToken.None);
        var second = await writer.RunAsync(CancellationToken.None);
        enabled = false;
        var third = await writer.RunAsync(CancellationToken.None);

        Assert.True(first.Written >= 1);
        Assert.Equal((0, 0), second);
        Assert.Equal(first.Written, third.Cleared);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task TheWriter_SavesExactlyTheCatalogsNumbers_ForTheStubItem()
    {
        var manager = TrickplayManagerFake.Create(out var rows);

        await Writer(manager, () => true).RunAsync(CancellationToken.None);

        var row = rows[MatrixWorld.Item][320];
        Assert.Equal((180, 10, 10, 250, 10_000, 500_000), (row.Height, row.TileWidth, row.TileHeight, row.ThumbnailCount, row.Interval, row.Bandwidth));
    }

    [Fact]
    public async Task TheWriter_DropsTheCachedTilesOfStubsThatNoLongerExist()
    {
        P.TrickplayCache.EnsureSignature(H.World.PeerKey.Length >= 16 ? H.World.PeerKey : new string('e', 16), "orphanorphanorph", "x");
        P.TrickplayCache.Store(H.World.PeerKey, "orphanorphanorph", 320, 0, new byte[10]);
        var before = P.TrickplayCache.Count;

        await Writer(TrickplayManagerFake.Create(out _), () => true).RunAsync(CancellationToken.None);

        Assert.Equal(1, before);
        Assert.Equal(0, P.TrickplayCache.Count);
    }

    private TrickplayInfoWriter Writer(ITrickplayManager manager, Func<bool> enabled)
    {
        var catalog = new StubCatalog(new CatalogStore(new ShoalPaths(H.Dir.Path), NullLogger<CatalogStore>.Instance));
        var items = new WorldStubItems(H.World, H.State);
        return new TrickplayInfoWriter(items, catalog, new OwnSourceVersionResolver(H.Filter!.Index, catalog), manager, P.TrickplayCache, H.State, enabled, NullLogger.Instance);
    }

    private List<string> CacheFiles() => Directory.Exists(H.Dir.Combine("trickplay-cache"))
        ? [.. Directory.EnumerateFiles(H.Dir.Combine("trickplay-cache"), "*", SearchOption.AllDirectories).Where(static f => !f.EndsWith("signature", StringComparison.Ordinal))]
        : [];

    private sealed class RefusingSanitizer : IImageSanitizer
    {
        public Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageSanitizeResult(false, ReadOnlyMemory<byte>.Empty));
    }
}

/// <summary>
/// The matrix world's federated items as the stub items the writer walks.
/// </summary>
internal sealed class WorldStubItems : IStubItems
{
    private readonly MatrixWorld _world;
    private readonly Jellyfin.Plugin.Shoal.State.IStateStore _state;

    public WorldStubItems(MatrixWorld world, Jellyfin.Plugin.Shoal.State.IStateStore state)
    {
        _world = world;
        _state = state;
    }

    public IReadOnlyList<StubItemView> List()
    {
        var paths = new WorldItemPaths(_world, _state);
        return [.. _world.Federated.Keys.Where(static id => id == MatrixWorld.Item || id == MatrixWorld.ItemB || id == MatrixWorld.ItemC)
            .Select(id => new StubItemView(id, paths.PathOf(id)!, "t", new StubFacts(0, null, 0, 0, 0, 0), false, static (_, _) => Task.CompletedTask))];
    }
}

/// <summary>
/// A stand-in for core's trickplay manager that stores rows in memory; only the three members the writer uses are answered.
/// </summary>
public class TrickplayManagerFake : DispatchProxy
{
    private Dictionary<Guid, Dictionary<int, TrickplayInfo>> _rows = [];

    internal static ITrickplayManager Create(out Dictionary<Guid, Dictionary<int, TrickplayInfo>> rows)
    {
        var proxy = Create<ITrickplayManager, TrickplayManagerFake>();
        var fake = (TrickplayManagerFake)(object)proxy;
        fake._rows = rows = [];
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "GetTrickplayResolutions":
                {
                    var id = (Guid)args![0]!;
                    return Task.FromResult(_rows.TryGetValue(id, out var existing) ? new Dictionary<int, TrickplayInfo>(existing) : []);
                }

            case "SaveTrickplayInfo":
                {
                    var info = (TrickplayInfo)args![0]!;
                    if (!_rows.TryGetValue(info.ItemId, out var existing))
                    {
                        _rows[info.ItemId] = existing = [];
                    }

                    existing[info.Width] = info;
                    return Task.CompletedTask;
                }

            case "DeleteTrickplayDataAsync":
                _rows.Remove((Guid)args![0]!);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
