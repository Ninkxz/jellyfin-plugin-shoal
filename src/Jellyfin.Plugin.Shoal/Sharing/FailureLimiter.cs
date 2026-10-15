using System;
using System.Collections.Generic;
using System.Net;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// How callers are told apart by the limiters: an IPv4 address as it is, an IPv4-mapped IPv6 address as its IPv4 form, and any other IPv6
/// address by its /64 (one network owner has a whole /64, so per-address buckets would give it unlimited ones).
/// </summary>
public static class AddressKey
{
    /// <summary>Makes the key for a caller given as text.</summary>
    /// <param name="address">The remote address as text.</param>
    /// <returns>The key; <c>unknown</c> when it does not parse.</returns>
    public static string Of(string? address) => IPAddress.TryParse(address, out var ip) ? Of(ip) : "unknown";

    /// <summary>Makes the key for a caller.</summary>
    /// <param name="address">The remote address.</param>
    /// <returns>The key; <c>unknown</c> when there is none.</returns>
    public static string Of(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8)) + "::/64";
    }
}

/// <summary>
/// Counts failures in fixed buckets with constant work and bounded memory (Warden H1): the current minute's total, the current minute's
/// count per caller (at most 1,024 callers; the rest share one bucket) and a 60-slot history for the last hour. A request costs a few
/// integer operations whatever the traffic.
/// </summary>
public sealed class FailureLimiter
{
    private const int MaxKeys = 1024;
    private const string Overflow = "*overflow*";

    private readonly object _lock = new();
    private readonly int _perKey;
    private readonly int _global;
    private readonly Dictionary<string, int> _perKeyCounts = new(StringComparer.Ordinal);
    private readonly int[] _slots = new int[60];
    private readonly long[] _slotMinute = new long[60];
    private long _minute = -1;
    private int _total;

    /// <summary>
    /// Initializes a new instance of the <see cref="FailureLimiter"/> class.
    /// </summary>
    /// <param name="perKeyPerMinute">The failures one caller may make in a minute.</param>
    /// <param name="globalPerMinute">The failures all callers together may make in a minute.</param>
    public FailureLimiter(int perKeyPerMinute, int globalPerMinute)
    {
        _perKey = perKeyPerMinute;
        _global = globalPerMinute;
    }

    /// <summary>Gets the number of callers currently tracked (never more than the cap plus one).</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_lock)
            {
                return _perKeyCounts.Count;
            }
        }
    }

    /// <summary>Records one failure.</summary>
    /// <param name="key">The caller (see <see cref="AddressKey"/>).</param>
    /// <param name="now">The time.</param>
    public void Record(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            Roll(now);
            _total++;
            var bucket = _perKeyCounts.ContainsKey(key) || _perKeyCounts.Count < MaxKeys ? key : Overflow;
            _perKeyCounts[bucket] = _perKeyCounts.GetValueOrDefault(bucket) + 1;
            var slot = (int)(_minute % 60);
            _slots[slot]++;
        }
    }

    /// <summary>Says whether a caller has used up its failures (or all callers have) in this minute.</summary>
    /// <param name="key">The caller.</param>
    /// <param name="now">The time.</param>
    /// <returns>True when a failure should be answered 429.</returns>
    public bool Throttled(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            Roll(now);
            var bucket = _perKeyCounts.ContainsKey(key) ? key : _perKeyCounts.Count < MaxKeys ? key : Overflow;
            return _total >= _global || _perKeyCounts.GetValueOrDefault(bucket) >= _perKey;
        }
    }

    /// <summary>Counts the failures of the last 60 minutes.</summary>
    /// <param name="now">The time.</param>
    /// <returns>The count.</returns>
    public int LastHour(DateTimeOffset now)
    {
        lock (_lock)
        {
            Roll(now);
            var minute = now.ToUnixTimeSeconds() / 60;
            var sum = 0;
            for (var i = 0; i < 60; i++)
            {
                if (minute - _slotMinute[i] < 60)
                {
                    sum += _slots[i];
                }
            }

            return sum;
        }
    }

    private void Roll(DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        if (minute == _minute)
        {
            return;
        }

        _minute = minute;
        _total = 0;
        _perKeyCounts.Clear();
        var slot = (int)(minute % 60);
        _slots[slot] = 0;
        _slotMinute[slot] = minute;
    }
}
