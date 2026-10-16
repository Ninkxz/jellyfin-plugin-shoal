using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// An https fake peer with a self-signed certificate.
/// </summary>
public sealed class TlsPeerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public X509Certificate2 Certificate { get; private set; } = null!;

    public string BaseUrl { get; private set; } = string.Empty;

    public int Requests => _requests;

    /// <summary>Gets the Authorization header of the last request, or null when it had none.</summary>
    public string? LastAuthorization { get; private set; }

    private int _requests;

    public async Task InitializeAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=hops.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("hops.example");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate)));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _requests);
            LastAuthorization = context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null;
            await next();
        });
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();
        _app = app;
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        Certificate?.Dispose();
    }
}

/// <summary>
/// A minimal SOCKS5 server (no authentication, CONNECT only) that records what it was asked for and forwards to a fixed target.
/// </summary>
public sealed class SocksProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SocksProxy(IPEndPoint target) => _target = target;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<(byte AddressType, string Host, int Port)> Requests { get; } = new();

    public int Connections { get; private set; }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Connections++;
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, _stop.Token);
                await stream.ReadExactlyAsync(new byte[greeting[1]], _stop.Token);
                await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);

                var head = new byte[4];
                await stream.ReadExactlyAsync(head, _stop.Token);
                string host;
                switch (head[3])
                {
                    case 3:
                        var length = new byte[1];
                        await stream.ReadExactlyAsync(length, _stop.Token);
                        var name = new byte[length[0]];
                        await stream.ReadExactlyAsync(name, _stop.Token);
                        host = Encoding.ASCII.GetString(name);
                        break;
                    case 1:
                        var v4 = new byte[4];
                        await stream.ReadExactlyAsync(v4, _stop.Token);
                        host = new IPAddress(v4).ToString();
                        break;
                    default:
                        return;
                }

                var portBytes = new byte[2];
                await stream.ReadExactlyAsync(portBytes, _stop.Token);
                Requests.Enqueue((head[3], host, (portBytes[0] << 8) | portBytes[1]));

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(_target, _stop.Token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                var up = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(up, _stop.Token), up.CopyToAsync(stream, _stop.Token));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>
/// K4 acceptance (DESIGN 11.2, 8.3 T11, T13): the address policy as PeerClient applies it, rebinding blocked in direct mode, proxy mode,
/// the TLS public-key pin, and the peer directory failing closed on a doctored state.
/// </summary>
public sealed class PeerNetworkPolicyTests : IClassFixture<PeerServerFixture>, IClassFixture<TlsPeerFixture>
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string DeviceId = "shoal-1a2b3c4d5e6f-4b0c1d2e";

    private static int _keyCounter;
    private readonly PeerServerFixture _server;
    private readonly TlsPeerFixture _tls;

    public PeerNetworkPolicyTests(PeerServerFixture server, TlsPeerFixture tls)
    {
        _server = server;
        _tls = tls;
    }

    private static string NewKey() => Convert.ToHexStringLower(BitConverter.GetBytes(Interlocked.Increment(ref _keyCounter))).PadLeft(32, 'a');

    private static PeerClient Client(PeerRegistry? registry = null, bool enforce = true) =>
        new(() => "Home", () => "0.1.0.0", new ManualTimeProvider(), registry) { EnforceAddressPolicy = enforce };

    private static Task<PeerResponse> Send(PeerClient client, PeerEndpoint peer, string path = "/ok-json") =>
        client.SendAsync(peer, PeerRequest.GetAnonymous(path), PeerCallProfile.Info, CancellationToken.None);

    [Theory]
    [InlineData("http://127.0.0.1:1", PeerNetwork.Tailnet)]
    [InlineData("http://127.0.0.1:1", PeerNetwork.Lan)]
    [InlineData("http://169.254.169.254", PeerNetwork.Public)]
    [InlineData("http://100.100.100.200", PeerNetwork.Tailnet)]
    [InlineData("http://[::1]:8096", PeerNetwork.Tailnet)]
    [InlineData("https://example.com", PeerNetwork.Tailnet)]
    [InlineData("http://example.com", PeerNetwork.Public)]
    [InlineData("http://192.168.1.5", PeerNetwork.Lan)]
    public async Task AnUnacceptableAddress_IsRefusedBeforeAnythingIsSent(string url, PeerNetwork network)
    {
        using var client = Client();
        var peer = new PeerEndpoint(NewKey(), "p", new Uri(url), DeviceId, Token) { Network = network };

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer));

        Assert.Equal(PeerFailureKind.AddressRefused, failure.Kind);
        Assert.Equal(0, client.BreakerOf(peer).ConsecutiveFailures);
    }

    [Fact]
    public async Task T_SEC_8_ANameThatResolvesToABlockedAddress_IsRefusedAtConnectTime_AndNothingIsSent()
    {
        using var client = Client();
        var calls = new List<string>();
        client.Resolver = (host, _) =>
        {
            calls.Add(host);
            return Task.FromResult(new[] { IPAddress.Parse("100.64.0.7"), IPAddress.Loopback }); // rebinding: one good answer, one bad
        };
        var before = _server.Requests.Count;
        var peer = new PeerEndpoint(NewKey(), "p", new Uri("http://" + "hops.tail1234.ts.net:" + new Uri(_server.BaseUrl).Port), DeviceId, Token) { Network = PeerNetwork.Tailnet };

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer));

        Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
        Assert.Equal(["hops.tail1234.ts.net"], calls);
        Assert.Equal(before, _server.Requests.Count);
        Assert.DoesNotContain("hops", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    [InlineData("100.100.100.200")]
    public async Task T_SEC_8_EveryResolvedAddressMustSuitTheNetworkType(string resolved)
    {
        using var client = Client();
        client.Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolved) });
        var before = _server.Requests.Count;
        var peer = new PeerEndpoint(NewKey(), "p", new Uri("http://hops.tail1234.ts.net:" + new Uri(_server.BaseUrl).Port), DeviceId, Token) { Network = PeerNetwork.Tailnet };

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer));

        Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public async Task TheResolverIsUsed_ForNames_AndNeverForAddressLiterals()
    {
        using var client = Client(enforce: false);
        var calls = new List<string>();
        client.Resolver = (host, _) =>
        {
            calls.Add(host);
            return Task.FromResult(new[] { IPAddress.Loopback });
        };
        var port = new Uri(_server.BaseUrl).Port;

        using (var named = await Send(client, new PeerEndpoint(NewKey(), "p", new Uri("http://hops.tail1234.ts.net:" + port), DeviceId, Token)))
        {
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        }

        using (var literal = await Send(client, new PeerEndpoint(NewKey(), "p", new Uri(_server.BaseUrl), DeviceId, Token)))
        {
            Assert.Equal(HttpStatusCode.OK, literal.StatusCode);
        }

        Assert.Equal(["hops.tail1234.ts.net"], calls);
    }

    [Fact]
    public async Task ProxyMode_SendsTheNameToTheProxy_AndNeverResolvesItLocally()
    {
        var fixturePort = new Uri(_server.BaseUrl).Port;
        await using var proxy = new SocksProxy(new IPEndPoint(IPAddress.Loopback, fixturePort));
        proxy.Start();
        using var client = Client();
        var resolverCalls = 0;
        client.Resolver = (_, _) =>
        {
            Interlocked.Increment(ref resolverCalls);
            return Task.FromResult(new[] { IPAddress.Loopback });
        };
        var peer = new PeerEndpoint(NewKey(), "p", new Uri("http://hops.tail1234.ts.net:" + fixturePort), DeviceId, Token)
        {
            Network = PeerNetwork.Tailnet,
            ProxyUrl = "socks5://127.0.0.1:" + proxy.Port
        };
        var before = _server.Count("/ok-json");

        using (var response = await Send(client, peer))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"a\":1}", await response.ReadUtf8Async());
        }

        var asked = Assert.Single(proxy.Requests);
        Assert.Equal((byte)3, asked.AddressType); // a domain name, resolved by the proxy (Tailscale)
        Assert.Equal("hops.tail1234.ts.net", asked.Host);
        Assert.Equal(fixturePort, asked.Port);
        Assert.Equal(0, resolverCalls);
        Assert.Equal(1, _server.Count("/ok-json") - before);
    }

    [Theory]
    [InlineData(PeerNetwork.Lan, "http://192.168.1.5:8096")]
    [InlineData(PeerNetwork.Public, "https://example.com")]
    public async Task ProxyMode_IsRefusedForPeersThatAreNotOnTheTailnet_WithoutTouchingTheProxy(PeerNetwork network, string url)
    {
        await using var proxy = new SocksProxy(new IPEndPoint(IPAddress.Loopback, 9));
        proxy.Start();
        using var client = Client();
        var peer = new PeerEndpoint(NewKey(), "p", new Uri(url), DeviceId, Token) { Network = network, LanCleartextAck = true, ProxyUrl = "socks5://127.0.0.1:" + proxy.Port };

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer));

        Assert.Equal(PeerFailureKind.AddressRefused, failure.Kind);
        Assert.Equal(0, proxy.Connections);
    }

    [Fact]
    public async Task ProxyMode_RefusesAProxyThatIsNotOnTheLoopback()
    {
        using var client = Client();
        var peer = new PeerEndpoint(NewKey(), "p", new Uri("http://hops.tail1234.ts.net:8096"), DeviceId, Token) { Network = PeerNetwork.Tailnet, ProxyUrl = "socks5://10.0.0.9:1055" };

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer));

        Assert.Equal(PeerFailureKind.AddressRefused, failure.Kind);
    }

    private PeerEndpoint TlsPeer(string? pin) =>
        new(NewKey(), "p", new Uri(_tls.BaseUrl), DeviceId, Token) { Network = PeerNetwork.Public, TlsSpkiPin = pin };

    [Fact]
    public async Task ASelfSignedCertificate_IsRefused_WithoutAPin()
    {
        using var client = Client(enforce: false);
        var before = _tls.Requests;

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, TlsPeer(null), "/ok"));

        Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
        Assert.Equal(before, _tls.Requests);
    }

    [Fact]
    public async Task ThePinnedKey_IsTheTrustAnchor_SoASelfSignedCertificateWorks()
    {
        using var client = Client(enforce: false);
        var pin = TlsPin.Compute(_tls.Certificate);

        using var response = await Send(client, TlsPeer(pin), "/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(TlsPin.IsValid(pin));
    }

    [Fact]
    public async Task APinMismatch_IsTlsKeyChanged_AndEverythingStopsUntilTheAdminActs()
    {
        var registry = new PeerRegistry(new ManualTimeProvider());
        using var client = Client(registry, enforce: false);
        var wrong = Convert.ToBase64String(SHA256.HashData("another key"u8));
        var peer = TlsPeer(wrong);
        var before = _tls.Requests;

        var failure = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer, "/ok"));

        Assert.Equal(PeerFailureKind.Unreachable, failure.Kind);
        Assert.Equal(PeerStatus.TlsKeyChanged, registry.StatusOf(peer.PeerKey));
        Assert.Equal(before, _tls.Requests);
        var again = await Assert.ThrowsAsync<PeerCallException>(() => Send(client, peer, "/ok"));
        Assert.Equal(PeerFailureKind.Blocked, again.Kind);

        // "Re-trust": the new pin is stored, the status is cleared, and the peer works again.
        var trusted = TlsPeer(TlsPin.Compute(_tls.Certificate));
        var trustedPeer = new PeerEndpoint(peer.PeerKey, "p", trusted.BaseUri, DeviceId, Token) { Network = PeerNetwork.Public, TlsSpkiPin = trusted.TlsSpkiPin };
        registry.SetStatus(peer.PeerKey, PeerStatus.Offline);
        using var response = await Send(client, trustedPeer, "/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void TlsPin_IsTheBase64Sha256OfTheSubjectPublicKeyInfo()
    {
        var pin = TlsPin.Compute(_tls.Certificate);

        Assert.Equal(44, pin.Length);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(_tls.Certificate.PublicKey.ExportSubjectPublicKeyInfo())), pin);
        Assert.True(TlsPin.Matches(_tls.Certificate, pin));
        Assert.False(TlsPin.Matches(_tls.Certificate, Convert.ToBase64String(new byte[32])));
        Assert.False(TlsPin.Matches(_tls.Certificate, "nonsense"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void TlsPin_IsValid_OnlyForBase64Of32Bytes(string value, bool valid)
    {
        Assert.Equal(valid, TlsPin.IsValid(value));
    }

    [Fact]
    public async Task ADoctoredState_NeverYieldsAnEndpoint_ThePeerDirectoryRechecksTheAddressFromItsText()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Token, DeviceId, DateTime.UtcNow, PairingMethod.Qc));
        var directory = new PeerDirectory(state, secrets);
        Assert.NotNull(directory.Find(StateSamples.PeerKeyA));

        foreach (var doctored in new[] { "http://2130706433:8096", "http://127.0.0.1:8096", "http://169.254.169.254", "http://100.100.100.200", "http://user:pw@hops.tail1234.ts.net", "http://hops.tail1234.ts.net?x=1", "http://example.com", "http://0x7f.1" })
        {
            await state.UpdateAsync(s => s.Home.Peers[0].BaseUrl = doctored);
            Assert.Null(directory.Find(StateSamples.PeerKeyA));
        }

        await state.UpdateAsync(s => s.Home.Peers[0].BaseUrl = "http://hops.tail1234.ts.net:8096");
        await state.UpdateAsync(s => s.Home.Peers[0].TlsSpkiPin = "not a pin");
        Assert.Null(directory.Find(StateSamples.PeerKeyA));
        await state.UpdateAsync(s => s.Home.Peers[0].TlsSpkiPin = null);
        await state.UpdateAsync(s => s.Home.Peers[0].Network = PeerNetwork.Public);
        Assert.Null(directory.Find(StateSamples.PeerKeyA)); // a Tailnet name is not a public server, and http is not allowed there
    }

    [Fact]
    public async Task ThePeerDirectory_CarriesTheNetworkSettingsToTheEndpoint()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.Home.Peers[0].ProxyUrl = "socks5://127.0.0.1:1055";
            s.Home.Peers[0].TlsSpkiPin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        });
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Token, DeviceId, DateTime.UtcNow, PairingMethod.Qc));

        var endpoint = new PeerDirectory(state, secrets).Find(StateSamples.PeerKeyA)!;

        Assert.Equal(PeerNetwork.Tailnet, endpoint.Network);
        Assert.Equal("socks5://127.0.0.1:1055", endpoint.ProxyUrl);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", endpoint.TlsSpkiPin);
        Assert.Equal("0123456789abcdef0123456789abcdef", endpoint.PinnedServerId);
        Assert.Null(endpoint.Check); // a device-name check belonged to Quick Connect (D17): a server paired that way is paused
    }
}
