using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Diagnostics;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The addresses of this server as seen from the network card and the config (pairing v2): private IPv4 addresses and the HTTP port.
/// </summary>
public interface IReachNetwork
{
    /// <summary>Gets the HTTP port the server listens on.</summary>
    int HttpPort { get; }

    /// <summary>Lists the addresses of the network interfaces that are up.</summary>
    /// <returns>The IPv4 addresses.</returns>
    IReadOnlyList<IPAddress> Addresses();
}

/// <summary>
/// <see cref="IReachNetwork"/> from the host and the operating system.
/// </summary>
public sealed class HostReachNetwork : IReachNetwork
{
    private readonly IServerApplicationHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostReachNetwork"/> class.
    /// </summary>
    /// <param name="host">The server.</param>
    public HostReachNetwork(IServerApplicationHost host) => _host = host;

    /// <inheritdoc />
    public int HttpPort => _host.HttpPort;

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> Addresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(static n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .SelectMany(static n => n.GetIPProperties().UnicastAddresses)
                .Select(static a => a.Address)
                .Where(static a => a.AddressFamily == AddressFamily.InterNetwork)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>
/// Suggests the addresses a friend could use to reach this server, for the share wizard (pairing v2 section 11): the Tailscale name from the local
/// Tailscale, the HTTPS address the admin is using right now, and the private addresses of the network cards. Only suggestions for the admin to check and edit;
/// nothing is stored or trusted.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminReachController : ControllerBase
{
    private const int MaxLan = 4;

    private readonly ITailscaleStatus _tailscale;
    private readonly IReachNetwork _network;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminReachController"/> class.
    /// </summary>
    /// <param name="tailscale">The Tailscale status (asked for this node's own name when it can).</param>
    /// <param name="network">The server's addresses.</param>
    public AdminReachController(ITailscaleStatus tailscale, IReachNetwork network)
    {
        _tailscale = tailscale;
        _network = network;
    }

    /// <summary>
    /// Whether an address is a private IPv4 address of a home or office network (10/8, 172.16/12, 192.168/16); not Tailscale's range, not loopback, not link-local.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>True for a private LAN address.</returns>
    public static bool IsPrivateLan(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// Suggests addresses.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>{tailscale?: {dnsName, ip}, https?: {url}, lan?: [{url}]}</c>; members that are not known are left out.</returns>
    [HttpGet("Shares/Reach")]
    [Produces("application/json")]
    public async Task<IActionResult> Reach(CancellationToken cancellationToken)
    {
        var reply = new Dictionary<string, object>();
        if (_tailscale is ITailscaleSelf self && await self.SelfAsync(cancellationToken).ConfigureAwait(false) is { } node)
        {
            reply["tailscale"] = new { dnsName = node.DnsName, ip = node.Ip };
        }

        if (Https() is { } url)
        {
            reply["https"] = new { url };
        }

        var port = _network.HttpPort;
        var lan = _network.Addresses().Where(IsPrivateLan).Select(static a => a.ToString()).Distinct(StringComparer.Ordinal).Take(MaxLan)
            .Select(a => new { url = "http://" + a + ":" + port.ToString(CultureInfo.InvariantCulture) }).ToList();
        if (lan.Count > 0)
        {
            reply["lan"] = lan;
        }

        return AdminReply.Json(StatusCodes.Status200OK, reply);
    }

    // The address the admin reached this page by, when that is a public-looking https name: not an IP literal, not a single-label name, not localhost.
    private string? Https()
    {
        var host = Request.Host;
        if (!Request.IsHttps || !host.HasValue || host.Host.Length is 0 or > 253 || IPAddress.TryParse(host.Host, out _) || !host.Host.Contains('.', StringComparison.Ordinal)
            || host.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || !host.Host.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-'))
        {
            return null;
        }

        return "https://" + host.Host + (host.Port is { } port and not 443 ? ":" + port.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }
}
