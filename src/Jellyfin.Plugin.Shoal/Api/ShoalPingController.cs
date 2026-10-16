using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Shoal.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api;

/// <summary>
/// A sliding-window limit for the anonymous ping: per address and for everyone together. Memory is bounded: a full table drops its oldest address instead of
/// refusing new ones, and an IPv6 address counts as its /64 (one host can hold a whole /64, so it must not be able to fill the table).
/// </summary>
public sealed class PingLimiter
{
    /// <summary>The calls one address may make per minute.</summary>
    public const int PerAddress = 20;

    /// <summary>The calls all addresses together may make per minute.</summary>
    public const int Overall = 300;

    private const int DefaultMaxAddresses = 1024;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _byAddress = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly int _maxAddresses;

    /// <summary>
    /// Initializes a new instance of the <see cref="PingLimiter"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public PingLimiter(TimeProvider time)
        : this(time, DefaultMaxAddresses)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PingLimiter"/> class (tests: a small table).
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="maxAddresses">The most addresses remembered.</param>
    internal PingLimiter(TimeProvider time, int maxAddresses)
    {
        _time = time;
        _maxAddresses = maxAddresses;
    }

    /// <summary>
    /// Makes the key a caller is counted under: the address, but an IPv6 address as its /64 network (an IPv4-mapped one as the IPv4 address).
    /// </summary>
    /// <param name="address">The remote address, or null.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        return address.TryWriteBytes(bytes, out _) ? Convert.ToHexString(bytes[..8]) + "/64" : address.ToString();
    }

    /// <summary>
    /// Counts a call.
    /// </summary>
    /// <param name="address">The caller's address.</param>
    /// <returns>True when the call may be answered.</returns>
    public bool TryTake(string address)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Trim(_all, now);
            if (_all.Count >= Overall)
            {
                return false;
            }

            if (!_byAddress.TryGetValue(address, out var queue))
            {
                if (_byAddress.Count >= _maxAddresses)
                {
                    foreach (var key in _byAddress.Keys.ToList())
                    {
                        Trim(_byAddress[key], now);
                        if (_byAddress[key].Count == 0)
                        {
                            _byAddress.Remove(key);
                        }
                    }

                    if (_byAddress.Count >= _maxAddresses)
                    {
                        // Still full of live addresses: the one that was heard from longest ago makes room, so new callers are never locked out by old ones.
                        _byAddress.Remove(_byAddress.MinBy(static p => p.Value.Last()).Key);
                    }
                }

                queue = new Queue<DateTimeOffset>();
                _byAddress[address] = queue;
            }

            Trim(queue, now);
            if (queue.Count >= PerAddress)
            {
                return false;
            }

            queue.Enqueue(now);
            _all.Enqueue(now);
            return true;
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window)
        {
            queue.Dequeue();
        }
    }
}

/// <summary>
/// The second anonymous endpoint of Shoal: <c>GET /Shoal/Ping</c>, so that a friend's Shoal can tell that Shoal is installed here before an admin picks a
/// pairing route. It answers <c>200 {"shoal":true}</c> and nothing else: no version, no id, no name, no state. When the setting "Let friends' servers detect
/// Shoal" is off it answers the one <c>404 {}</c> that every unknown request gets. It is rate limited per address and overall (<c>429</c>). The reflection
/// test whitelists exactly this action and <see cref="PairController.Pair"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("Shoal")]
public class ShoalPingController : ControllerBase
{
    private readonly PingLimiter _limiter;
    private readonly Func<bool> _detectable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalPingController"/> class.
    /// </summary>
    /// <param name="limiter">The rate limit.</param>
    public ShoalPingController(PingLimiter limiter)
        : this(limiter, static () => Plugin.Instance?.Configuration.DetectableByFriends ?? true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalPingController"/> class (tests: the setting).
    /// </summary>
    /// <param name="limiter">The rate limit.</param>
    /// <param name="detectable">Reads the setting.</param>
    internal ShoalPingController(PingLimiter limiter, Func<bool> detectable)
    {
        _limiter = limiter;
        _detectable = detectable;
    }

    /// <summary>
    /// Says that Shoal is here. Anonymous by design: it reveals only that, and only while the setting allows it.
    /// </summary>
    /// <returns><c>200 {"shoal":true}</c>, <c>429</c> when the caller is over its limit, or the one <c>404 {}</c>.</returns>
    [HttpGet("Ping")]
    [AllowAnonymous]
    public IActionResult Ping()
    {
        // With the setting off this is the plain 404 of an unknown address and the limiter is never consulted, so a flood cannot make the server answer in a
        // way a server without Shoal never would (Warden 12 L1).
        if (!_detectable())
        {
            return new ShoalResult(StatusCodes.Status404NotFound, new { });
        }

        if (!_limiter.TryTake(PingLimiter.KeyOf(HttpContext.Connection.RemoteIpAddress)))
        {
            return new ShoalResult(StatusCodes.Status429TooManyRequests, new { }, 5);
        }

        return new ShoalResult(StatusCodes.Status200OK, new PingReply(true));
    }

    private sealed record PingReply(bool Shoal);
}
