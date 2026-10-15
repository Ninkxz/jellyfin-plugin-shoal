using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// U8 acceptance (DESIGN 11.2): under netem <c>rate 8mbit delay 80ms</c> the UI shows 7-8 Mbps and about 160 ms RTT, with the verdicts
/// 720p smooth / 1080p transcode at about 5 Mbps / remux will buffer. The link is shaped in process: a shared pacing clock at 8 Mbit/s for
/// every connection, and 160 ms (80 ms each way) before every answer. Sizes are scaled down; the rate does not depend on the size.
/// </summary>
public sealed class SpeedTestServiceTests
{
    internal const string Key = "4b0c1d2e3f405162738495a6b7c8d9e0";

    [Fact]
    public async Task Netem_8mbit_80ms_Measures7To8Mbps_About160msRtt_AndTheDocumentedVerdicts()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 1_000_000, roundTrip: TimeSpan.FromMilliseconds(160));
        using var rig = await Rig.CreateAsync(link);

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Manual, default);

        Assert.NotNull(sample);
        Assert.Null(sample.Error);
        Assert.InRange(sample.Mbps!.Value, 7.0, 8.3);
        Assert.InRange(sample.RttMedianMs!.Value, 155, 215);
        Assert.Equal(0, sample.LossPct);
        Assert.NotNull(sample.RttP90Ms);
        Assert.NotNull(sample.ConnectMs);

        // The link is shared, so two streams at once get no more than one: the limit is the link, not the connection.
        Assert.InRange(sample.MbpsParallel!.Value / sample.Mbps.Value, 0.7, 1.3);

        var saved = Assert.Single(rig.History.Read(Key));
        Assert.Equal(sample.Mbps, saved.Mbps);
        var t = VerdictEngine.Throughput(rig.History.Read(Key))!.Value;
        var ladder = VerdictEngine.Ladder(t, autoFit: true).ToDictionary(r => r.Cls, r => r.Verdict);
        Assert.Equal("smooth", ladder["720p"]);
        Assert.Equal("needsTranscode", ladder["1080p"]);
        Assert.Equal("willBuffer", ladder["remux1080"]);
        Assert.Equal("willBuffer", ladder["remux4k"]);
        Assert.InRange(VerdictEngine.AutoFitMbps(t), 4.6, 5.6); // "transcodes to about 5 Mbps"
        Assert.Equal("slow", VerdictEngine.LatencyVerdict(sample.RttMedianMs.Value));
    }

    [Fact]
    public async Task APerConnectionLimit_ShowsAsParallelAtLeastOneAndAHalfTimesSingle()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 1_000_000, roundTrip: TimeSpan.FromMilliseconds(20), perConnection: true);
        using var rig = await Rig.CreateAsync(link);

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Deep, default);

        Assert.NotNull(sample);
        Assert.Null(sample.Error);
        Assert.True(sample.MbpsParallel!.Value / sample.Mbps!.Value >= 1.6, $"parallel {sample.MbpsParallel} vs single {sample.Mbps}");
    }

    [Fact]
    public async Task TheLocalApiAnswer_SetsThePath_DirectOrRelayedWithTheRegion()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(2));
        using var direct = await Rig.CreateAsync(link, new FakeTailscale(new TailscalePeerPath(false, null)));
        Assert.Equal("direct", (await direct.Service.RunAsync(Key, SpeedKinds.Light, default))!.Path);

        using var relayed = await Rig.CreateAsync(link, new FakeTailscale(new TailscalePeerPath(true, "fra")));
        var sample = (await relayed.Service.RunAsync(Key, SpeedKinds.Light, default))!;
        Assert.Equal("derp", sample.Path);
        Assert.Equal("fra", sample.DerpRegion);
    }

    [Fact]
    public async Task WithoutTheLocalApi_ALinkThatGotSlowerAndFarther_IsOnlyPossiblyRelayed()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.FromMilliseconds(100));
        using var rig = await Rig.CreateAsync(link);
        rig.History.Append(Key, new SpeedSample { T = DateTimeOffset.UtcNow.AddHours(-3), Kind = SpeedKinds.Light, Mbps = 400, RttMedianMs = 5 });

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Light, default))!;

        Assert.Equal("derp?", sample.Path); // about 100 ms and 8 Mbit against a best of 5 ms and 400 Mbit
        Assert.Null(sample.DerpRegion);
    }

    [Fact]
    public async Task TheDeepTest_AlsoMeasuresThePeersTranscodeSpeed_AndStopsTheJob()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(2));
        using var rig = await Rig.CreateAsync(link, withCatalog: true);

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Deep, default))!;

        Assert.Null(sample.Error);
        Assert.InRange(sample.TranscodeSpeed!.Value, 2.0, 6.5); // 0.6 s of video per about 0.1 s
        Assert.NotNull(sample.SegTtfbMs);
        Assert.Equal(1, link.Kills);
        Assert.InRange(TranscodeProbe.SuggestedConversions(sample.TranscodeSpeed.Value), 1, 4);
    }

    [Fact]
    public async Task L5_TheProbe_HoldsARealConversionSlotWhileItRuns_AndGivesItBack()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(2));
        var slots = new Jellyfin.Plugin.Shoal.Playback.StreamSlots(TimeProvider.System, () => 6);
        using var rig = await Rig.CreateAsync(link, withCatalog: true, slots: slots);
        link.SegmentDelayMs = 250; // nine segments: the probe runs for a couple of seconds

        var run = rig.Service.RunAsync(Key, SpeedKinds.Deep, default);
        var seen = 0;
        while (!run.IsCompleted)
        {
            seen = Math.Max(seen, slots.ConversionsOnPeer(Key));
            await Task.Delay(10);
        }

        var sample = (await run)!;
        Assert.Equal(1, seen); // one conversion slot, held by the probe
        Assert.NotNull(sample.TranscodeSpeed);
        Assert.Equal(0, slots.ConversionsOnPeer(Key));
        Assert.Equal(0, slots.ActiveSlotsOnPeer(Key));
        Assert.Null(sample.SharedWithStreams); // the probe's own slot does not make the test "shared"
    }

    [Fact]
    public async Task ALightOrManualTest_NeverStartsATranscode()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(2));
        using var rig = await Rig.CreateAsync(link, withCatalog: true);

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;

        Assert.Null(sample.TranscodeSpeed);
        Assert.Equal(0, link.Kills);
    }

    [Fact]
    public async Task M1_TheParallelRuns_AreSizedToTheLink_SoTheyEndInTime()
    {
        await using var link = await ShapedLink.StartAsync(1_000_000, TimeSpan.FromMilliseconds(5));
        using var rig = await Rig.CreateAsync(link);
        rig.Service.Options.ParallelBytes = 16_000_000; // the real size, far more than this link can move in time
        rig.Service.Options.RunSeconds = 1;

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;

        Assert.Null(sample.Error);
        Assert.NotNull(sample.MbpsParallel);
        var lastTwo = link.Sizes.TakeLast(2).ToList();
        Assert.All(lastTwo, size => Assert.InRange(size, 250_000, 700_000)); // about half of the 8 Mbit link for one second each
    }

    [Fact]
    public async Task M1_AParallelRunThatFails_DoesNotCostTheThroughputAlreadyMeasured()
    {
        await using var link = await ShapedLink.StartAsync(2_000_000, TimeSpan.FromMilliseconds(5));
        link.FailAbove = 400_000; // the parallel runs (500 kB) are refused, the single runs are not
        using var rig = await Rig.CreateAsync(link);
        rig.Service.Options.ParallelBytes = 500_000;
        rig.Service.Options.MainBytes = 300_000;
        rig.Service.Options.WarmupBytes = 250_000;
        rig.Service.Options.BigBytes = 300_000;
        rig.Service.Options.FastRun = TimeSpan.Zero;

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;

        Assert.Null(sample.Error);
        Assert.NotNull(sample.Mbps);
        Assert.NotNull(sample.RttMedianMs);
        Assert.Null(sample.MbpsParallel);
        Assert.True(Assert.Single(rig.History.Read(Key)).HasSpeed);
    }

    [Fact]
    public async Task M1_BelowFourMbit_ThereIsNoParallelRun_AndNoTimeout()
    {
        await using var link = await ShapedLink.StartAsync(300_000, TimeSpan.FromMilliseconds(5)); // 2.4 Mbit
        using var rig = await Rig.CreateAsync(link);
        rig.Service.Options.RunSeconds = 1;
        rig.Service.Options.MainBytes = 8_000_000;

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Deep, default))!;

        Assert.Null(sample.Error);
        Assert.InRange(sample.Mbps!.Value, 1.5, 3.2);
        Assert.Null(sample.MbpsParallel);
        Assert.InRange(link.Sizes.Max(), 1, 400_000); // 1 s of a 300 kB/s link
    }

    [Fact]
    public async Task L1_ATestWhileAStreamRuns_IsMarkedShared_AndIsNotUsedForT()
    {
        await using var link = await ShapedLink.StartAsync(5_000_000, TimeSpan.FromMilliseconds(2));
        var slots = new Jellyfin.Plugin.Shoal.Playback.StreamSlots(TimeProvider.System, () => 6);
        using var rig = await Rig.CreateAsync(link, slots: slots);
        var clean = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;
        Assert.Null(clean.SharedWithStreams);

        var grant = new Jellyfin.Plugin.Shoal.Playback.Grant
        {
            GrantId = "streaming-grant-0000000",
            LocalUserId = Guid.NewGuid(),
            LocalItemId = Guid.NewGuid(),
            CallerDeviceId = "d",
            LocalPlaySessionId = Guid.NewGuid().ToString("N"),
            PeerKey = Key,
            PeerItemId = Guid.NewGuid(),
            PeerMediaSourceId = Guid.NewGuid()
        };
        Assert.True(slots.TryTakeHls(grant, 4, 4, out _));
        var shared = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;

        Assert.True(shared.SharedWithStreams);
        Assert.Equal(clean.Mbps, VerdictEngine.Throughput(rig.History.Read(Key))); // only the clean one counts
    }

    [Fact]
    public async Task L2_APeerThatRoundsTheSizeUpToAPowerOfTwo_IsStillMeasured()
    {
        await using var link = await ShapedLink.StartAsync(50_000_000, TimeSpan.Zero);
        link.RoundUpToPowerOfTwo = true; // what Jellyfin 12.1 does
        using var rig = await Rig.CreateAsync(link);
        rig.Service.Options.WarmupBytes = 250_000;
        rig.Service.Options.MainBytes = 1_100_000; // answered with 2,097,152 bytes
        rig.Service.Options.BigBytes = 3_000_000; // answered with 4,194,304
        rig.Service.Options.FastRun = TimeSpan.FromSeconds(10);

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Manual, default))!;

        Assert.Null(sample.Error);
        Assert.NotNull(sample.Mbps);
        Assert.NotNull(sample.MbpsParallel);
    }

    [Fact]
    public async Task L2_APeerThatSendsMuchMoreThanAskedFor_IsCutOff_AsABadAnswer()
    {
        await using var link = await ShapedLink.StartAsync(50_000_000, TimeSpan.Zero);
        link.AnswerSize = 20_000_000;
        using var rig = await Rig.CreateAsync(link);

        var sample = (await rig.Service.RunAsync(Key, SpeedKinds.Light, default))!;

        Assert.Equal("badResponse", sample.Error);
        Assert.Null(sample.Mbps);
    }

    [Fact]
    public async Task ALightTest_HasNoParallelRun()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 5_000_000, roundTrip: TimeSpan.FromMilliseconds(5));
        using var rig = await Rig.CreateAsync(link);

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Light, default);

        Assert.NotNull(sample);
        Assert.Equal(SpeedKinds.Light, sample.Kind);
        Assert.Null(sample.MbpsParallel);
        Assert.NotNull(sample.Mbps);
    }

    [Fact]
    public async Task AFastMainRun_IsFollowedByTheBigOne_AndTheBestRateCounts()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 20_000_000, roundTrip: TimeSpan.FromMilliseconds(1));
        using var rig = await Rig.CreateAsync(link);
        rig.Service.Options.FastRun = TimeSpan.FromSeconds(10); // every main run counts as quick here
        rig.Service.Options.BigBytes = 3_000_000;

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Light, default);

        Assert.NotNull(sample!.Mbps);
        Assert.Contains(sample.Bytes, new long?[] { 2_000_000, 3_000_000 }); // the bigger rate of the two counts
        Assert.Equal(3, link.BitrateRequests); // warm-up, main, big
    }

    [Fact]
    public async Task AnUnreachablePeer_IsASampleWithAnError_NotAnException()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 1_000_000, roundTrip: TimeSpan.Zero);
        using var rig = await Rig.CreateAsync(link);
        await link.DisposeAsync();

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Light, default);

        Assert.NotNull(sample);
        Assert.Contains(sample.Error, new[] { "unreachable", "timeout" }); // a closed loopback port refuses at once on Linux and after the connect timeout on Windows
        Assert.Null(sample.Mbps);
        Assert.False(Assert.Single(rig.History.Read(Key)).HasSpeed);
    }

    [Fact]
    public async Task ARefusedToken_IsASampleWithAnError_AndNoNumbersForTheRate()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 1_000_000, roundTrip: TimeSpan.Zero);
        link.RefuseBitrateTest = true;
        using var rig = await Rig.CreateAsync(link);

        var sample = await rig.Service.RunAsync(Key, SpeedKinds.Light, default);

        Assert.Equal("refused", sample!.Error);
        Assert.Null(sample.Mbps);
        Assert.NotNull(sample.RttMedianMs); // the pings worked and are kept
    }

    [Fact]
    public async Task OneTestAtATimePerPeer_AndAnUnknownOrDisabledPeerIsNotTested()
    {
        await using var link = await ShapedLink.StartAsync(bytesPerSecond: 1_000_000, roundTrip: TimeSpan.FromMilliseconds(50));
        using var rig = await Rig.CreateAsync(link);

        var first = rig.Service.RunAsync(Key, SpeedKinds.Light, default);
        while (!rig.Service.IsRunning(Key))
        {
            await Task.Yield();
        }

        Assert.Null(await rig.Service.RunAsync(Key, SpeedKinds.Light, default));
        Assert.NotNull(await first);
        Assert.False(rig.Service.IsRunning(Key));
        Assert.Null(await rig.Service.RunAsync("00000000000000000000000000000000", SpeedKinds.Light, default));

        await rig.State.UpdateAsync(s => s.Home.Peers[0].Enabled = false);
        Assert.Null(await rig.Service.RunAsync(Key, SpeedKinds.Light, default));
    }

    internal sealed class Rig : IDisposable
    {
        private readonly TempDirectory _dir;
        private readonly PeerClient _client;

        private Rig(TempDirectory dir, StateStore state, SpeedHistoryStore history, PeerClient client, SpeedTestService service)
        {
            _dir = dir;
            State = state;
            History = history;
            _client = client;
            Service = service;
        }

        public StateStore State { get; }

        public SpeedHistoryStore History { get; }

        public SpeedTestService Service { get; }

        public static async Task<Rig> CreateAsync(ShapedLink link, ITailscaleStatus? tailscale = null, bool withCatalog = false, Jellyfin.Plugin.Shoal.Playback.StreamSlots? slots = null)
        {
            var dir = new TempDirectory();
            var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
            await state.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = Key, DisplayName = "Fern", BaseUrl = link.BaseUrl, Network = PeerNetwork.Tailnet, Enabled = true }));
            var history = new SpeedHistoryStore(new ShoalPaths(dir.Path), NullLogger.Instance);
            var client = new PeerClient(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
            var endpoint = new PeerEndpoint(Key, "Fern", new Uri(link.BaseUrl), "shoal-test-device", "tok-123");
            CatalogStore? catalogs = null;
            TranscodeProbe? probe = null;
            if (withCatalog)
            {
                catalogs = new CatalogStore(new ShoalPaths(dir.Path), NullLogger<CatalogStore>.Instance);
                catalogs.Save(new CatalogDocument { PeerKey = Key, Entries = [new CatalogEntry { StubKey = "bbbbbbbbbbbbbbbb", StubRelPath = "x/x.strm", PeerItemId = ShapedLink.ItemId.ToString("N"), PeerMediaSourceId = ShapedLink.ItemId.ToString("N"), SizeBytes = 100, RuntimeTicks = 30 * TimeSpan.TicksPerMinute, State = EntryState.Active }] });
                probe = new TranscodeProbe(client, NullLogger.Instance);
            }

            var service = new SpeedTestService(state, new Endpoints(endpoint), client, history, slots, TimeProvider.System, NullLogger.Instance, tailscale, catalogs, probe);
            service.Options.Pings = 10;
            service.Options.PingGap = TimeSpan.FromMilliseconds(10);
            service.Options.WarmupBytes = 250_000;
            service.Options.MainBytes = 2_000_000; // 8 MB in the real test: the same rate in a quarter of the time
            service.Options.BigBytes = 4_000_000;
            service.Options.ParallelBytes = 500_000;
            service.Options.FastRun = TimeSpan.FromMilliseconds(500);
            return new Rig(dir, state, history, client, service);
        }

        public void Dispose()
        {
            _client.Dispose();
            State.Dispose();
            _dir.Dispose();
        }
    }

    private sealed class FakeTailscale(TailscalePeerPath? answer) : ITailscaleStatus
    {
        public Task<TailscalePeerPath?> FindAsync(string host, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private sealed class Endpoints(PeerEndpoint endpoint) : IPeerEndpoints
    {
        public PeerEndpoint? Find(string peerKey) => peerKey == endpoint.PeerKey ? endpoint : null;
    }

    /// <summary>A Jellyfin that answers Ping and BitrateTest over a link with a rate and a delay.</summary>
    internal sealed class ShapedLink : IAsyncDisposable
    {
        private const int Chunk = 16 * 1024;

        private readonly WebApplication _app;
        private readonly long _bytesPerSecond;
        private readonly TimeSpan _roundTrip;
        private readonly bool _perConnection;
        private readonly object _clockLock = new();
        private long _linkFreeAt;
        private int _bitrateRequests;
        private int _kills;

        private ShapedLink(WebApplication app, long bytesPerSecond, TimeSpan roundTrip, bool perConnection)
        {
            _app = app;
            _bytesPerSecond = bytesPerSecond;
            _roundTrip = roundTrip;
            _perConnection = perConnection;
        }

        public static Guid ItemId { get; } = Guid.Parse("11111111-2222-3333-4444-555555555555");

        public int Kills => Volatile.Read(ref _kills);

        public System.Collections.Concurrent.ConcurrentQueue<long> Sizes { get; } = new();

        public long FailAbove { get; set; } = long.MaxValue;

        public long AnswerSize { get; set; }

        public bool RoundUpToPowerOfTwo { get; set; }

        public int SegmentDelayMs { get; set; } = 100;

        public string BaseUrl { get; private set; } = string.Empty;

        public bool RefuseBitrateTest { get; set; }

        public int BitrateRequests => Volatile.Read(ref _bitrateRequests);

        public static async Task<ShapedLink> StartAsync(long bytesPerSecond, TimeSpan roundTrip, bool perConnection = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var link = new ShapedLink(app, bytesPerSecond, roundTrip, perConnection);
            app.MapGet("/System/Ping", async ctx =>
            {
                await Task.Delay(link._roundTrip, ctx.RequestAborted);
                await ctx.Response.WriteAsync("Jellyfin Server");
            });
            app.MapGet("/Playback/BitrateTest", link.BitrateTestAsync);
            app.MapPost("/Items/{itemId}/PlaybackInfo", async ctx =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($"{{\"MediaSources\":[{{\"Id\":\"{ItemId:N}\",\"SupportsDirectPlay\":false,\"TranscodingUrl\":\"/videos/{ItemId:N}/master.m3u8?DeviceId=x&PlaySessionId=probe-sess-1\"}}],\"PlaySessionId\":\"probe-sess-1\"}}");
            });
            app.MapGet("/Videos/{itemId}/master.m3u8", async ctx =>
            {
                ctx.Response.ContentType = "application/x-mpegURL";
                await ctx.Response.WriteAsync("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000\nmain.m3u8?DeviceId=x&PlaySessionId=probe-sess-1\n");
            });
            app.MapGet("/Videos/{itemId}/main.m3u8", async ctx =>
            {
                ctx.Response.ContentType = "application/x-mpegURL";
                await ctx.Response.WriteAsync("#EXTM3U\n#EXT-X-TARGETDURATION:1\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"#EXTINF:0.600,\nhls1/main/{i}.ts?x=1\n")) + "#EXT-X-ENDLIST\n");
            });
            app.MapGet("/Videos/{itemId}/hls1/main/{segment}", async ctx =>
            {
                await Task.Delay(link.SegmentDelayMs, ctx.RequestAborted);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.ContentLength = 500;
                await ctx.Response.Body.WriteAsync(new byte[500]);
            });
            app.MapDelete("/Videos/ActiveEncodings", ctx =>
            {
                Interlocked.Increment(ref link._kills);
                ctx.Response.StatusCode = 204;
                return Task.CompletedTask;
            });
            await app.StartAsync();
            link.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return link;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private async Task BitrateTestAsync(HttpContext ctx)
        {
            Interlocked.Increment(ref _bitrateRequests);
            if (RefuseBitrateTest || !ctx.Request.Headers.ContainsKey("Authorization") && !ctx.Request.Headers.ContainsKey("X-Emby-Authorization"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var size = long.Parse(ctx.Request.Query["size"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Sizes.Enqueue(size);
            if (AnswerSize > 0)
            {
                size = AnswerSize; // a peer that sends more than it was asked for
            }

            if (RoundUpToPowerOfTwo)
            {
                var rounded = 1L;
                while (rounded < size)
                {
                    rounded <<= 1; // Jellyfin 12.1 answers with the next power of two
                }

                size = rounded;
            }

            if (size > FailAbove)
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await Task.Delay(_roundTrip, ctx.RequestAborted); // the request and the first byte each cross the link once
            ctx.Response.ContentLength = size;
            ctx.Response.ContentType = "application/octet-stream";
            var buffer = new byte[Chunk];
            long sent = 0;
            var own = Stopwatch.GetTimestamp();
            while (sent < size)
            {
                var count = (int)Math.Min(Chunk, size - sent);
                await Task.Delay(WaitFor(count, ref own), ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, count), ctx.RequestAborted);
                sent += count;
            }
        }

        // Each chunk takes its slice of the link's time: the link is one pipe for all connections, or one pipe per connection.
        private TimeSpan WaitFor(int bytes, ref long ownFreeAt)
        {
            var slice = Stopwatch.Frequency * bytes / _bytesPerSecond;
            var now = Stopwatch.GetTimestamp();
            lock (_clockLock)
            {
                var freeAt = _perConnection ? ownFreeAt : _linkFreeAt;
                var done = Math.Max(freeAt, now - (Stopwatch.Frequency / 50)) + slice; // up to 20 ms of credit: timers are coarse, the rate over time is exact
                if (_perConnection)
                {
                    ownFreeAt = done;
                }
                else
                {
                    _linkFreeAt = done;
                }

                var wait = done - now;
                return wait <= 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, wait);
            }
        }
    }
}
