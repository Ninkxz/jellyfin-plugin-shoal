using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The only place that talks to peers (DESIGN 2 #7, 6.10, 6.11). One <see cref="SocketsHttpHandler"/> and one
/// <see cref="CircuitBreaker"/> per peer, never <c>IHttpClientFactory</c> (which logs request URLs).
/// <list type="bullet">
/// <item>Headers are built here and nowhere else (<see cref="PeerAuthorization"/>); a <see cref="PeerRequest"/> cannot carry a client header, cookie or query.</item>
/// <item>No redirect is followed: a 3xx answer is an error and no second request is made. Cookies and automatic decompression are off.</item>
/// <item>Every call has its deadlines from a <see cref="PeerCallProfile"/> (connect, headers, idle read, total) and every body is read through <see cref="BoundedReadStream"/>.</item>
/// <item>Three consecutive connect, timeout or 5xx failures open the breaker; while open only the due health probe is sent and every other call fails at once with <see cref="PeerFailureKind.CircuitOpen"/>.</item>
/// <item>Failures are <see cref="PeerCallException"/>s with fixed text: no URL, header or token ever appears in an exception message, and no inner exception is chained.</item>
/// </list>
/// </summary>
public sealed class PeerClient : IDisposable
{
    /// <summary>The key under which the connect timeout of a call travels to the connect callback.</summary>
    internal static readonly HttpRequestOptionsKey<TimeSpan> ConnectTimeoutKey = new("shoal.connectTimeout");

    private readonly Func<string> _homeName;
    private readonly Func<string> _version;
    private readonly TimeProvider _time;
    private readonly PeerRegistry? _registry;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new(StringComparer.Ordinal);
    private readonly object _createLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerClient"/> class.
    /// </summary>
    /// <param name="appHost">The application host (the server name that is shown on the peer).</param>
    /// <param name="registry">The runtime table of peers (trust window, stop statuses).</param>
    public PeerClient(IServerApplicationHost appHost, PeerRegistry registry)
        : this(() => appHost.FriendlyName, () => Plugin.Instance?.Version.ToString() ?? "0.0.0.0", TimeProvider.System, registry)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerClient"/> class without any Jellyfin dependency (tests).
    /// </summary>
    /// <param name="homeName">Supplies the Home server's name for the device name.</param>
    /// <param name="version">Supplies the plugin version.</param>
    /// <param name="time">The clock (circuit breaker).</param>
    /// <param name="registry">The runtime table of peers, or null to send without a trust window.</param>
    internal PeerClient(Func<string> homeName, Func<string> version, TimeProvider time, PeerRegistry? registry = null)
    {
        _homeName = homeName;
        _version = version;
        _time = time;
        _registry = registry;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the address policy is applied (DESIGN 8.3 T11): the peer's address is checked before every
    /// call and every address a name resolves to is checked at connect time. Always true in production; tests that talk to a server on
    /// the loopback address switch it off.
    /// </summary>
    internal bool EnforceAddressPolicy { get; set; } = true;

    /// <summary>
    /// Gets or sets a replacement for opening the TCP connection (tests: an in-memory peer on a simulated clock). Never set in production.
    /// </summary>
    internal Func<System.Net.DnsEndPoint, CancellationToken, ValueTask<Stream>>? ConnectOverride { get; set; }

    /// <summary>
    /// Gets or sets the name resolver (a seam for the rebinding tests).
    /// </summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolver { get; set; } = (host, token) => Dns.GetHostAddressesAsync(host, token);

    /// <summary>
    /// Gets the breaker of a peer (created on first use).
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <returns>The breaker.</returns>
    public CircuitBreaker BreakerOf(PeerEndpoint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return ConnectionFor(peer).Breaker;
    }

    /// <summary>
    /// Drops everything held for a peer (its connections and its breaker), for example when the peer is removed.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    public void Forget(string peerKey)
    {
        if (_connections.TryRemove(peerKey, out var connection))
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Sends a request and returns as soon as the response headers arrive; the body is read through the returned response.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="request">The request.</param>
    /// <param name="profile">The deadlines and cap.</param>
    /// <param name="cancellationToken">The caller's cancellation (client disconnect, the end of a sync).</param>
    /// <returns>The response. Dispose it.</returns>
    /// <exception cref="PeerCallException">The circuit is open, the peer is unreachable or too slow, or it answered with a redirect or a declared length over the cap.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<PeerResponse> SendAsync(PeerEndpoint peer, PeerRequest request, PeerCallProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        if (request.Authenticated && !peer.HasToken)
        {
            throw new InvalidOperationException("An authenticated call needs a token.");
        }

        if (EnforceAddressPolicy && !AddressPolicy.Validate(peer.BaseUri.AbsoluteUri, peer.Network, peer.LanCleartextAck, peer.ProxyUrl).Ok)
        {
            throw new PeerCallException(PeerFailureKind.AddressRefused);
        }

        if (_registry is not null)
        {
            var denial = _registry.Check(peer.PeerKey, request.Authenticated, request.Trust);
            if (denial != TrustDenial.None)
            {
                throw new PeerCallException(denial switch { TrustDenial.Blocked => PeerFailureKind.Blocked, TrustDenial.Rotating => PeerFailureKind.CircuitOpen, _ => PeerFailureKind.TrustLapsed });
            }
        }

        var connection = ConnectionFor(peer);
        var breaker = request.UsesBreaker ? connection.Breaker : new CircuitBreaker(_time);
        if (!breaker.TryAcquire(request.IsProbe))
        {
            throw new PeerCallException(PeerFailureKind.CircuitOpen);
        }

        var budget = new CallBudget(profile.Total, cancellationToken);
        var outcomeRecorded = false;
        var ownsBudget = true;
        try
        {
            HttpResponseMessage message;
            try
            {
                using var outgoing = BuildMessage(peer, request, profile);
                using var headerPhase = budget.StartPhase(profile.Headers, cancellationToken);
                message = await connection.Client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, headerPhase.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                outcomeRecorded = true;
                if (profile.CountsTowardBreaker)
                {
                    breaker.RecordFailure();
                }
                else
                {
                    breaker.ReleaseProbe();
                }

                throw new PeerCallException(PeerFailureKind.Timeout);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or AuthenticationException)
            {
                // The text of these carries the URL and the address; none of it is passed on.
                outcomeRecorded = true;
                breaker.RecordFailure();
                throw new PeerCallException(PeerFailureKind.Unreachable);
            }

            outcomeRecorded = true;
            var status = (int)message.StatusCode;
            if (status is >= 300 and < 400)
            {
                var redirected = message.StatusCode;
                message.Dispose();
                breaker.RecordSuccess(); // the peer answered; it is just not acceptable
                throw new PeerCallException(PeerFailureKind.Redirect, redirected);
            }

            if (!profile.CountsTowardBreaker)
            {
                breaker.ReleaseProbe(); // the outcome says nothing about whether the peer is up (W4-1)
            }
            else if (status >= 500)
            {
                breaker.RecordFailure();
            }
            else
            {
                breaker.RecordSuccess();
            }

            var expectBody = !HttpMethod.Head.Equals(request.Method) && status < 500;
            var response = new PeerResponse(message, budget, profile, expectBody, exception => CountBodyFailure(profile.CountsTowardBreaker ? breaker : new CircuitBreaker(_time), exception));
            ownsBudget = false; // the response owns it now
            if (response.HasBody && response.ContentLength > profile.MaxBytes)
            {
                await response.DisposeAsync().ConfigureAwait(false);
                throw new PeerCallException(PeerFailureKind.TooLarge);
            }

            return response;
        }
        finally
        {
            if (!outcomeRecorded)
            {
                breaker.ReleaseProbe();
            }

            if (ownsBudget)
            {
                budget.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
    }

    private static void CountBodyFailure(CircuitBreaker breaker, PeerCallException exception)
    {
        // Transport failures count towards the breaker; a hostile or oversized answer does not (the peer is reachable).
        if (exception.Kind is PeerFailureKind.Timeout or PeerFailureKind.Unreachable)
        {
            breaker.RecordFailure();
        }
    }

    private HttpRequestMessage BuildMessage(PeerEndpoint peer, PeerRequest request, PeerCallProfile profile)
    {
        var baseUri = peer.BaseUri;
        var uri = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/') + request.PathAndQuery);
        var message = new HttpRequestMessage(request.Method, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        message.Options.Set(ConnectTimeoutKey, profile.Connect);

        var deviceName = PeerAuthorization.DeviceName(_homeName(), peer.Check);
        message.Headers.TryAddWithoutValidation("Authorization", PeerAuthorization.Build(deviceName, peer.DeviceId, _version(), request.Authenticated ? peer.Token : null));
        message.Headers.TryAddWithoutValidation("User-Agent", "Shoal/" + _version());
        message.Headers.TryAddWithoutValidation("Accept", request.AcceptJson ? "application/json" : "*/*");
        message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (request.Range is not null)
        {
            message.Headers.TryAddWithoutValidation("Range", request.Range);
        }

        if (request.JsonBody is not null)
        {
            message.Content = new ReadOnlyMemoryContent(request.JsonBody.Value);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return message;
    }

    private static string SignatureOf(PeerEndpoint peer) =>
        string.Join('|', peer.BaseUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), peer.Network, peer.ProxyUrl ?? string.Empty, peer.TlsSpkiPin ?? string.Empty);

    private PeerConnection ConnectionFor(PeerEndpoint peer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var signature = SignatureOf(peer);
        if (_connections.TryGetValue(peer.PeerKey, out var existing) && string.Equals(existing.Signature, signature, StringComparison.Ordinal))
        {
            return existing;
        }

        lock (_createLock)
        {
            if (_connections.TryGetValue(peer.PeerKey, out existing) && string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                return existing;
            }

            // A new host, network type, proxy or pin is a new peer as far as connections and failure memory go.
            existing?.Dispose();
            var created = new PeerConnection(peer, signature, new CircuitBreaker(_time), this);
            _connections[peer.PeerKey] = created;
            return created;
        }
    }

    // Opens the TCP connection under the call's connect timeout. Resolution happens here, not in the handler, so every address a name
    // resolved to can be checked before any is used (DNS rebinding, DESIGN 8.3 T11). TLS is layered on top by the handler.
    private async ValueTask<Stream> ConnectAsync(PeerNetwork network, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var timeout = context.InitialRequestMessage is { } initial && initial.Options.TryGetValue(ConnectTimeoutKey, out var configured) ? configured : TimeSpan.FromSeconds(5);
        using var connectPhase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectPhase.CancelAfter(timeout);

        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Resolver(host, connectPhase.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new HttpRequestException("Name resolution failed.");
        }

        if (addresses.Length == 0 || (EnforceAddressPolicy && !AddressPolicy.CheckResolved(network, addresses)))
        {
            throw new HttpRequestException("The peer address is not allowed.");
        }

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), connectPhase.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The peer could not be reached.", last);
    }

    // Public-key pin (DESIGN 4.4): with a pin the key is the trust anchor (a self-signed certificate is fine, chain errors are ignored)
    // and any other key is refused and recorded as TlsKeyChanged; without a pin the normal chain and host name validation applies.
    private bool ValidateServerCertificate(string peerKey, string? pin, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (pin is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        using var presented = new X509Certificate2(certificate);
        if (TlsPin.Matches(presented, pin))
        {
            return true;
        }

        _registry?.SetStatus(peerKey, PeerStatus.TlsKeyChanged);
        return false;
    }

    private sealed class PeerConnection : IDisposable
    {
#pragma warning disable CA5398 // DESIGN 6.10 fixes the minimum at TLS 1.2 on purpose, whatever the operating system defaults to.
        private const SslProtocols TlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398

        public PeerConnection(PeerEndpoint peer, string signature, CircuitBreaker breaker, PeerClient owner)
        {
            Signature = signature;
            Breaker = breaker;
            var peerKey = peer.PeerKey;
            var pin = peer.TlsSpkiPin;
            var network = peer.Network;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 16,
                MaxResponseHeadersLength = 32, // KiB
#pragma warning disable CA5398 // see TlsProtocols
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = TlsProtocols,
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) => owner.ValidateServerCertificate(peerKey, pin, certificate, errors)
                }
#pragma warning restore CA5398
            };

            if (AddressPolicy.TryParseProxy(peer.ProxyUrl, out var proxy))
            {
                // Proxy mode (Tailnet userspace networking): the proxy resolves the name, so the rebinding check cannot run here; only Tailnet
                // literals and *.ts.net names get this far (AddressPolicy).
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxy);
            }
            else
            {
                handler.ConnectCallback = (context, token) => owner.ConnectOverride is { } simulated ? simulated(context.DnsEndPoint, token) : owner.ConnectAsync(network, context, token);
            }

            // Every call applies its own deadlines (CallBudget); the client-wide timeout would also cut a movie that streams for hours.
            Client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public string Signature { get; }

        public CircuitBreaker Breaker { get; }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }
}
