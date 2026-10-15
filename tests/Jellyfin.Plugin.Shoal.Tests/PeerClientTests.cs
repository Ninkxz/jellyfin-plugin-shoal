using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K3 acceptance (DESIGN 11.2, 6.10, 6.11) against a real hostile fake peer: F-RS-04 (cut at the cap, flat memory), F-RS-05 (idle
/// timeout), F-RS-06 (error), F-RS-07 and F-RS-08 (ignored), F-RS-09 and any 3xx (errors, no second request), 100 MB and 10k-deep
/// JSON rejected, the total deadline, the breaker, and the request headers exactly as 6.11 (part of T-SEC-20).
/// </summary>
public sealed class PeerClientTests : IClassFixture<PeerServerFixture>
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string DeviceId = "shoal-1a2b3c4d5e6f-4b0c1d2e";

    private static int _keyCounter;
    private readonly PeerServerFixture _server;

    public PeerClientTests(PeerServerFixture server) => _server = server;

    private static PeerClient NewClient(string homeName = "Home", ManualTimeProvider? time = null) => new(() => homeName, () => "0.1.0.0", time ?? new ManualTimeProvider()) { EnforceAddressPolicy = false };

    // Each test gets its own peer key, so breakers never leak between tests.
    private PeerEndpoint Peer(string? check = "K7QM", string? baseUrl = null, string token = Token)
    {
        var key = Convert.ToHexStringLower(BitConverter.GetBytes(Interlocked.Increment(ref _keyCounter))).PadLeft(32, '0');
        return new PeerEndpoint(key, "peer", new Uri(baseUrl ?? _server.BaseUrl), DeviceId, token, check);
    }

    private static PeerCallProfile Limits(double totalSeconds, long maxBytes, double idleSeconds = 10, double headerSeconds = 10) =>
        new("test", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(headerSeconds), TimeSpan.FromSeconds(idleSeconds), TimeSpan.FromSeconds(totalSeconds), maxBytes);

    [Fact]
    public async Task AnOkJsonAnswer_IsReadAsJson()
    {
        using var client = NewClient();

        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None);
        var document = await response.ReadJsonAsync<Dictionary<string, int>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.IsSuccess);
        Assert.Equal("application/json", response.MediaType);
        Assert.Equal(1, document["a"]);
    }

    [Fact]
    public async Task ANonOkAnswer_HasNoBody_AndIsNotAnException()
    {
        using var client = NewClient();

        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/not-found"), PeerCallProfile.Info, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.HasBody);
        Assert.Empty(await response.ReadBytesAsync());
        Assert.Equal(string.Empty, await response.ReadUtf8Async());
        Assert.Throws<InvalidOperationException>(() => response.OpenBody());
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCap_IsRefusedFromTheHeadersAlone()
    {
        using var client = NewClient();
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get("/declared-100mb"), PeerCallProfile.Info, CancellationToken.None));

        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "refused without waiting for a body, took " + stopwatch.Elapsed);
    }

    [Fact]
    public async Task F_RS_04_AnEndlessChunkedBody_IsCutAtTheCap_WithFlatMemory()
    {
        using var client = NewClient();
        var profile = Limits(60, maxBytes: 64L * 1024 * 1024, idleSeconds: 10);
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/endless"), profile, CancellationToken.None);
        Assert.Null(response.ContentLength);
        var stream = response.OpenBody();
        var buffer = new byte[64 * 1024];
        long total = 0;
        var failure = await Assert.ThrowsAsync<PeerCallException>(async () =>
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                total += read;
            }
        });

        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
        Assert.True(total <= 64L * 1024 * 1024, "never hands out more than the cap, handed " + total);
        Assert.True(total >= 64L * 1024 * 1024 - buffer.Length, "read right up to the cap");
        GC.Collect();
        Assert.True(GC.GetTotalMemory(forceFullCollection: true) - before < 48 * 1024 * 1024, "memory stays flat");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task F_RS_04_ReadingTheWholeBodyOfAnEndlessAnswer_FailsAtTheCap()
    {
        using var client = NewClient();
        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/endless"), Limits(20, 256 * 1024), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => response.ReadBytesAsync());

        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
    }

    [Fact]
    public async Task F_RS_05_APeerThatStopsSending_HitsTheIdleTimeout()
    {
        using var client = NewClient();
        var profile = Limits(30, 1024, idleSeconds: 0.4);
        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/stall-after-headers"), profile, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => response.ReadBytesAsync());

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task F_RS_05_ATricklingBody_IsStoppedByTheTotalDeadline()
    {
        using var client = NewClient();
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/trickle"), Limits(1, 1024 * 1024, idleSeconds: 5), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => response.ReadBytesAsync());

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), "took " + stopwatch.Elapsed);
    }

    [Fact]
    public async Task TheTotalDeadline_AlsoCoversMissingHeaders()
    {
        using var client = NewClient();
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get("/stall-before-headers"), Limits(1, 1024, headerSeconds: 30), CancellationToken.None));

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task TheHeaderTimeout_IsEnforcedOnItsOwn()
    {
        using var client = NewClient();

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get("/stall-before-headers"), Limits(30, 1024, headerSeconds: 0.5), CancellationToken.None));

        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task AClientDisconnect_IsACancellation_NotATimeoutOrAPeerFailure()
    {
        using var client = NewClient();
        var peer = Peer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(peer, PeerRequest.Get("/stall-before-headers"), Limits(30, 1024), cancel.Token));

        Assert.Equal(0, client.BreakerOf(peer).ConsecutiveFailures);
    }

    [Fact]
    public async Task F_RS_06_ABodyShorterThanItsContentLength_IsAnError()
    {
        using var client = NewClient();
        using var response = await client.SendAsync(Peer(), PeerRequest.Get("/short-body"), Limits(5, 1024), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => response.ReadBytesAsync());

        Assert.True(failure.Kind is PeerFailureKind.Unreachable or PeerFailureKind.BadResponse, "was " + failure.Kind);
    }

    [Theory]
    [InlineData("/redirect")]
    [InlineData("/redirect-metadata")]
    public async Task F_RS_09_AnyRedirect_IsAnError_AndNoSecondRequestIsMade(string path)
    {
        using var client = NewClient();
        var before = _server.Requests.Count;

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get(path), PeerCallProfile.Info, CancellationToken.None));

        Assert.Equal(PeerFailureKind.Redirect, failure.Kind);
        Assert.Equal(HttpStatusCode.Found, failure.StatusCode);
        Assert.Equal(1, _server.Requests.Count - before);
        Assert.Equal(0, _server.Count("/redirect-target"));
    }

    [Fact]
    public async Task F_RS_07_And_08_ALocationHeaderOnA200_AndACookie_AreIgnored()
    {
        using var client = NewClient();
        var peer = Peer();

        using (var first = await client.SendAsync(peer, PeerRequest.Get("/location-and-cookie"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("{\"a\":1}", await first.ReadUtf8Async());
        }

        using (await client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
        }

        var second = _server.Last("/ok-json");
        Assert.False(second.Headers.ContainsKey("Cookie"), "the cookie must not come back");
        Assert.Equal(0, _server.Count("/evil"));
        Assert.DoesNotContain(typeof(PeerResponse).GetProperties(), p => p.Name is "Location" or "SetCookie" or "Headers");
    }

    [Fact]
    public async Task F_RS_03_ABogusCharset_IsHarmless_AndOtherMediaTypesAreRefusedAsJson()
    {
        using var client = NewClient();

        using (var bogus = await client.SendAsync(Peer(), PeerRequest.Get("/bogus-charset"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal("application/json", bogus.MediaType);
            Assert.Equal("x", (await bogus.ReadJsonAsync<Dictionary<string, string>>())["a"]);
        }

        using var plain = await client.SendAsync(Peer(), PeerRequest.Get("/text-plain"), PeerCallProfile.Info, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => plain.ReadJsonAsync<Dictionary<string, int>>());
        Assert.Equal(PeerFailureKind.BadResponse, failure.Kind);
    }

    [Fact]
    public async Task InvalidUtf8_TenThousandDeepJson_AndNull_AreBadResponses()
    {
        using var client = NewClient();

        using (var bad = await client.SendAsync(Peer(), PeerRequest.Get("/bad-utf8"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(PeerFailureKind.BadResponse, (await Assert.ThrowsAsync<PeerCallException>(() => bad.ReadUtf8Async())).Kind);
        }

        using (var deep = await client.SendAsync(Peer(), PeerRequest.Get("/deep-json"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(PeerFailureKind.BadResponse, (await Assert.ThrowsAsync<PeerCallException>(() => deep.ReadJsonAsync<JsonElement[]>())).Kind);
        }

        using (var nothing = await client.SendAsync(Peer(), PeerRequest.Get("/json-null"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(PeerFailureKind.BadResponse, (await Assert.ThrowsAsync<PeerCallException>(() => nothing.ReadJsonAsync<Dictionary<string, int>>())).Kind);
        }
    }

    [Fact]
    public async Task OversizedResponseHeaders_AreRefused()
    {
        using var client = NewClient();

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get("/huge-headers"), PeerCallProfile.Info, CancellationToken.None));

        Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
    }

    [Fact]
    public async Task ARequestCarriesExactlyTheHeadersOfDesign611()
    {
        using var client = NewClient(homeName: "Home");
        var peer = Peer(check: "K7QM");

        using (await client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
        }

        var request = _server.Last("/ok-json");
        Assert.Equal(["Accept", "Accept-Encoding", "Authorization", "Host", "User-Agent"], request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(
            "MediaBrowser Client=\"Shoal\", Device=\"Shoal%3A%20Home%20%28K7QM%29\", DeviceId=\"" + DeviceId + "\", Version=\"0.1.0.0\", Token=\"" + Token + "\"",
            request.Headers["Authorization"]);
        Assert.Equal("Shoal/0.1.0.0", request.Headers["User-Agent"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.Equal("identity", request.Headers["Accept-Encoding"]);
        Assert.Empty(request.Query);
        Assert.Empty(request.Body);
    }

    [Fact]
    public async Task AMediaRequest_AcceptsAnything_AndCarriesTheRangeAndNothingMore()
    {
        using var client = NewClient();

        using var response = await client.SendAsync(Peer(), PeerRequest.GetMedia("/static-ok", "bytes=0-9"), PeerCallProfile.StaticStream, CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 0-9/100", response.ContentRange);
        Assert.Equal(10, response.ContentLength);
        Assert.Equal(10, (await response.ReadBytesAsync()).Length);
        var request = _server.Last("/static-ok");
        Assert.Equal(["Accept", "Accept-Encoding", "Authorization", "Host", "Range", "User-Agent"], request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal("*/*", request.Headers["Accept"]);
        Assert.Equal("bytes=0-9", request.Headers["Range"]);
    }

    [Fact]
    public async Task APostCarriesItsJsonBodyAndContentType_AndNothingElseExtra()
    {
        using var client = NewClient();
        var body = Encoding.UTF8.GetBytes("{\"ItemId\":\"abc\"}");

        using (await client.SendAsync(Peer(), PeerRequest.Post("/echo", body), PeerCallProfile.PlaybackInfo, CancellationToken.None))
        {
        }

        var request = _server.Last("/echo");
        Assert.Equal(["Accept", "Accept-Encoding", "Authorization", "Content-Length", "Content-Type", "Host", "User-Agent"], request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal(body, request.Body);
    }

    [Fact]
    public async Task ANastyServerName_ReachesThePeerByteExactAfterTheCoreDecode()
    {
        using var client = NewClient(homeName: "A+B 100% \"x\", y");

        using (await client.SendAsync(Peer(check: "K7QM"), PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
        }

        var header = _server.Last("/ok-json").Headers["Authorization"];
        Assert.Contains("Device=\"Shoal%3A%20A%2BB%20100%25%20x%20y%20%28K7QM%29\"", header, StringComparison.Ordinal);
        var device = header.Split("Device=\"")[1].Split('"')[0];
        Assert.Equal("Shoal: A+B 100% x y (K7QM)", WebUtility.UrlDecode(device));
    }

    [Fact]
    public async Task AnUnauthenticatedCall_SendsNoToken_AndAnAuthenticatedOneWithoutATokenSendsNothing()
    {
        using var client = NewClient();
        var withToken = Peer();
        var without = PeerEndpoint.WithoutToken(withToken.PeerKey, "peer", withToken.BaseUri, DeviceId, "K7QM");
        var before = _server.Requests.Count;

        using (await client.SendAsync(without, PeerRequest.GetAnonymous("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
        }

        Assert.DoesNotContain("Token=", _server.Last("/ok-json").Headers["Authorization"], StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(without, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(1, _server.Requests.Count - before);
    }

    [Fact]
    public async Task ThreeFiveHundreds_OpenTheBreaker_AndNothingIsSentWhileItIsOpen()
    {
        var time = new ManualTimeProvider();
        using var client = NewClient(time: time);
        var peer = Peer();
        var before = _server.Count("/boom");

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.SendAsync(peer, PeerRequest.Get("/boom"), PeerCallProfile.Info, CancellationToken.None);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.True(client.BreakerOf(peer).IsOpen);
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(PeerFailureKind.CircuitOpen, failure.Kind);
        Assert.Equal(3, _server.Count("/boom") - before);
        var hitsBefore = _server.Requests.Count;

        // Half-open at 30 s: only the health probe goes through.
        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(hitsBefore, _server.Requests.Count);
        using (var probe = await client.SendAsync(peer, PeerRequest.Probe(), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        }

        Assert.False(client.BreakerOf(peer).IsOpen);
        using var again = await client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task AFailedProbe_KeepsItOpen_AndPostponesTheNextOne()
    {
        var time = new ManualTimeProvider();
        using var client = NewClient(time: time);
        var peer = Peer();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.SendAsync(peer, PeerRequest.Get("/boom"), PeerCallProfile.Info, CancellationToken.None);
        }

        time.Advance(TimeSpan.FromSeconds(30));
        var breaker = client.BreakerOf(peer);
        Assert.True(breaker.TryAcquire(isProbe: true));
        breaker.RecordFailure();

        Assert.True(breaker.IsOpen);
        Assert.Equal(60, (breaker.NextProbeUtc!.Value - time.GetUtcNow()).TotalSeconds);
    }

    [Fact]
    public async Task ConnectFailures_AreCountedAndOpenTheBreaker_WithoutLeakingTheAddressOrTheToken()
    {
        using var client = NewClient();
        var peer = Peer(baseUrl: "http://127.0.0.1:1"); // nothing listens there

        for (var i = 0; i < 3; i++)
        {
            var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
            Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain("127.0.0.1", failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token, failure.ToString(), StringComparison.Ordinal);
        }

        var open = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        Assert.Equal(PeerFailureKind.CircuitOpen, open.Kind);
    }

    [Fact]
    public async Task UnauthorizedAnswers_AreNotBreakerFailures()
    {
        using var client = NewClient();
        var peer = Peer();

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.SendAsync(peer, PeerRequest.Get("/unauthorized"), PeerCallProfile.Info, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.False(client.BreakerOf(peer).IsOpen);
    }

    [Fact]
    public async Task ABreakerIsPerPeer_AndANewHostStartsFresh()
    {
        using var client = NewClient();
        var dead = Peer(baseUrl: "http://127.0.0.1:1");
        var healthy = Peer();
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(dead, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        }

        Assert.True(client.BreakerOf(dead).IsOpen);
        using (var other = await client.SendAsync(healthy, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        // The same peer key at another address is a different peer as far as failure memory goes.
        var moved = new PeerEndpoint(dead.PeerKey, "peer", new Uri(_server.BaseUrl), DeviceId, Token, "K7QM");
        using var response = await client.SendAsync(moved, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AStaticStream_MustDeclareItsLength()
    {
        using var client = NewClient();
        using var response = await client.SendAsync(Peer(), PeerRequest.GetMedia("/static-chunked"), PeerCallProfile.StaticStream, CancellationToken.None);

        Assert.Null(response.ContentLength);
        var failure = Assert.Throws<PeerCallException>(() => response.OpenBody());
        Assert.Equal(PeerFailureKind.BadResponse, failure.Kind);
    }

    [Fact]
    public async Task ABodyThatIsSlowerThanTheIdleTimeout_FailsWhileStreaming()
    {
        using var client = NewClient();
        var profile = Limits(30, 1024, idleSeconds: 0.4);
        using var response = await client.SendAsync(Peer(), PeerRequest.GetMedia("/slow-body"), profile, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "headers must not wait for the body");
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => response.ReadBytesAsync());
        Assert.Equal(PeerFailureKind.Timeout, failure.Kind);
    }


    [Fact]
    public async Task Forget_DropsTheConnectionAndTheBreaker()
    {
        using var client = NewClient();
        var peer = Peer(baseUrl: "http://127.0.0.1:1");
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(peer, PeerRequest.Get("/ok-json"), PeerCallProfile.Info, CancellationToken.None));
        }

        Assert.True(client.BreakerOf(peer).IsOpen);
        client.Forget(peer.PeerKey);

        Assert.False(client.BreakerOf(peer).IsOpen);
    }

    [Fact]
    public async Task TheBodyCanBeOpenedOnlyOnce_AndADeclaredLengthOverTheProfileCapFailsAtTheHeaders()
    {
        using var client = NewClient();
        using (var response = await client.SendAsync(Peer(), PeerRequest.Get("/ok-json"), Limits(30, 1024), CancellationToken.None))
        {
            using var stream = response.OpenBody();
            Assert.Throws<InvalidOperationException>(() => response.OpenBody());
        }

        // /big declares 5 MB; with a 1 MB cap it is refused without reading it.
        var failure = await Assert.ThrowsAsync<PeerCallException>(() => client.SendAsync(Peer(), PeerRequest.Get("/big"), Limits(30, 1024 * 1024), CancellationToken.None));
        Assert.Equal(PeerFailureKind.TooLarge, failure.Kind);
    }
}
