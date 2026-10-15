using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>Asks the local Tailscale which path leads to a host (DESIGN 7.3 item 1).</summary>
public interface ITailscaleStatus
{
    /// <summary>Looks the host up in Tailscale's status.</summary>
    /// <param name="host">A Tailscale IP or a MagicDNS name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The path, or null when Tailscale cannot be asked or does not know the host.</returns>
    Task<TailscalePeerPath?> FindAsync(string host, CancellationToken cancellationToken);
}

/// <summary>What Tailscale says about the path to one peer.</summary>
/// <param name="Relayed">True when the traffic goes through a DERP relay.</param>
/// <param name="Region">The relay's region, when it is relayed.</param>
public sealed record TailscalePeerPath(bool Relayed, string? Region);

/// <summary>The default: Tailscale is not asked.</summary>
public sealed class NoTailscaleStatus : ITailscaleStatus
{
    /// <inheritdoc />
    public Task<TailscalePeerPath?> FindAsync(string host, CancellationToken cancellationToken) => Task.FromResult<TailscalePeerPath?>(null);
}

/// <summary>
/// Reads <c>GET /localapi/v0/status</c> from the Tailscale daemon's Unix socket (read only, at most 1 MB). The socket is found by the
/// <c>TailscaleSocket</c> setting: <c>auto</c> looks in the usual place on Linux, <c>off</c> never asks, a path is used as it is.
/// </summary>
public sealed class TailscaleLocalApi : ITailscaleStatus
{
    /// <summary>The usual place of the daemon's socket on Linux.</summary>
    public const string DefaultSocket = "/var/run/tailscale/tailscaled.sock";

    private const int MaxBytes = 1024 * 1024;

    private readonly ISettingsAccessor _settings;
    private readonly ILogger<TailscaleLocalApi> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TailscaleLocalApi"/> class.
    /// </summary>
    /// <param name="settings">The plugin toggles.</param>
    /// <param name="logger">The logger.</param>
    public TailscaleLocalApi(ISettingsAccessor settings, ILogger<TailscaleLocalApi> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private enum Match
    {
        None,
        Short,
        Exact
    }

    /// <summary>Reads a node's path out of a status document.</summary>
    /// <param name="json">The status document.</param>
    /// <param name="host">A Tailscale IP or a MagicDNS name.</param>
    /// <returns>The path, or null when the host is not a known, online peer.</returns>
    public static TailscalePeerPath? Parse(ReadOnlySpan<byte> json, string host)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // An exact IP or full name wins; a short machine name counts only when exactly one node has it (a node shared in from another tailnet can
            // carry the same short name, and guessing would put the wrong path on the peer).
            var nodes = peers.EnumerateObject().Select(static p => p.Value).Where(static v => v.ValueKind == JsonValueKind.Object).ToList();
            var exact = nodes.Where(n => Matches(n, host) == Match.Exact).ToList();
            var short_ = nodes.Where(n => Matches(n, host) == Match.Short).ToList();
            var node = exact.Count > 0 ? exact[0] : short_.Count == 1 ? short_[0] : (JsonElement?)null;
            if (node is not { } found || (found.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.False))
            {
                return null;
            }

            var current = found.TryGetProperty("CurAddr", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
            var relay = found.TryGetProperty("Relay", out var rel) && rel.ValueKind == JsonValueKind.String ? rel.GetString() : null;
            return string.IsNullOrEmpty(current) ? new TailscalePeerPath(true, SafeRegion(relay)) : new TailscalePeerPath(false, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TailscalePeerPath?> FindAsync(string host, CancellationToken cancellationToken)
    {
        if (SocketPath() is not { } socket)
        {
            return null;
        }

        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), token).ConfigureAwait(false);
                        return new NetworkStream(client, ownsSocket: true);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                }
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri("http://local-tailscaled.sock/localapi/v0/status"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

#pragma warning disable RS0030 // The local daemon, not a peer: the body is read here under a 1 MB bound.
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore RS0030
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Parse(buffer.ToArray(), host);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug("Tailscale's status could not be read: {Error}", ex.GetType().Name);
            return null;
        }
    }

    // The region word is shown and saved: only a short plain word is accepted (Warden 09 L3).
    private static string? SafeRegion(string? region) =>
        region is { Length: >= 1 and <= 32 } && region.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-') ? region : null;

    private static Match Matches(JsonElement node, string host)
    {
        var wanted = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(wanted, out var ip))
        {
            return node.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                && ips.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.String && IPAddress.TryParse(i.GetString(), out var candidate) && candidate.Equals(ip))
                ? Match.Exact
                : Match.None;
        }

        if (!node.TryGetProperty("DNSName", out var dns) || dns.ValueKind != JsonValueKind.String || dns.GetString() is not { } name)
        {
            return Match.None;
        }

        name = name.TrimEnd('.');
        return string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ? Match.Exact : name.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase) ? Match.Short : Match.None;
    }

    private string? SocketPath()
    {
        var setting = _settings.Current.TailscaleSocket;
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return !OperatingSystem.IsWindows() && File.Exists(DefaultSocket) ? DefaultSocket : null;
        }

        return Path.IsPathRooted(setting) && File.Exists(setting) ? setting : null;
    }
}
