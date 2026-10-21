using System;
using System.Collections.Generic;
using System.Linq;

#pragma warning disable SA1600 // internal plumbing; the public surface is documented

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Why a stream slot or connection was refused.
/// </summary>
public enum SlotDenial
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>The user already runs the most streams (<c>MaxStreamsPerUser</c>).</summary>
    User,

    /// <summary>The peer already carries the most streams (<c>MaxConcurrentStreams</c>), or this user would hold more than half of them.</summary>
    Peer,

    /// <summary>The peer already runs the most conversions (<c>MaxConcurrentTranscodes</c>).</summary>
    Conversions,

    /// <summary>The user has the most open static connections (6).</summary>
    Connections,
}

/// <summary>
/// Where a new stream comes from, for the binge handover allowance (DESIGN v2.7.8): the series of an episode and the device that asks. Both empty for anything else.
/// </summary>
/// <param name="SeriesKey">The peer's series id of an episode, or empty.</param>
/// <param name="DeviceId">The caller's device id.</param>
public readonly record struct SlotOrigin(string SeriesKey, string DeviceId)
{
    /// <summary>Gets a value indicating whether the origin names a series (only an episode can be a handover).</summary>
    public bool IsEpisode => !string.IsNullOrEmpty(SeriesKey);
}

/// <summary>
/// A running static stream's claim on a slot. Dispose it when the connection ends; call <see cref="Touch"/> while bytes flow.
/// </summary>
public sealed class StaticLease : IDisposable
{
    private readonly StreamSlots _owner;
    private readonly (Guid User, Guid Item) _key;
    private readonly bool _created;
    private int _disposed;

    internal StaticLease(StreamSlots owner, (Guid User, Guid Item) key, bool created)
    {
        _owner = owner;
        _key = key;
        _created = created;
    }

    /// <summary>
    /// Records that bytes were served (extends the slot).
    /// </summary>
    public void Touch() => _owner.TouchStatic(_key);

    /// <summary>
    /// Gives the slot back at once if this request created it and served nothing (the request failed before any byte).
    /// </summary>
    public void Abandon() => _owner.AbandonStatic(_key, _created);

    /// <inheritdoc />
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.CloseStatic(_key);
        }
    }
}

/// <summary>
/// The key of a stream slot for a version set (DESIGN v2.6.4): a deterministic id made from the set id, so every version of a title shares the same slot.
/// </summary>
public static class SlotKey
{
    /// <summary>
    /// Makes the slot key of a version set.
    /// </summary>
    /// <param name="setId">The set id (the primary's stub key).</param>
    /// <returns>A stable id.</returns>
    public static Guid Of(string setId) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("shoal-set|" + setId)).AsSpan(0, 16));
}

/// <summary>
/// The stream slots (DESIGN 3.5, 6.9): slots, not grants, are the scarce resource. A static slot is keyed by (user, item), taken at the first
/// request, shared by that stream's Range requests and by every live grant of that user and item, and released 2 minutes after the last byte or on
/// <c>PlaybackStopped</c>. It is <b>never</b> released by <c>ActiveEncodings</c> (W4-4). An HLS slot belongs to one grant, is taken at its first
/// segment request and released by the scoped delete, after 2 minutes without a request, or on <c>PlaybackStopped</c>.
/// </summary>
public sealed class StreamSlots : ISlotLedger
{
    /// <summary>The most open static connections per user.</summary>
    public const int MaxConnectionsPerUser = 6;

    /// <summary>How long an HLS slot must have had no segment request before the next episode of the same series on the same device may take its place.</summary>
    public static readonly TimeSpan HlsSupersedeAfter = TimeSpan.FromSeconds(10);

    /// <summary>How long a slot outlives its last byte or request.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();

    private readonly Dictionary<(Guid User, Guid Item), StaticSlot> _static = [];
    private readonly Dictionary<string, HlsSlot> _hls = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _connections = [];
    private readonly TimeProvider _time;
    private readonly Func<int> _maxPerUser;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSlots"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="maxPerUser">The streams one user may run at once (<c>MaxStreamsPerUser</c>, 1 to 4).</param>
    public StreamSlots(TimeProvider time, Func<int> maxPerUser)
    {
        _time = time;
        _maxPerUser = maxPerUser;
    }

    /// <summary>
    /// Raised, under the ledger's lock, with the ids of the HLS grants whose slots the next episode just took over. A listener must only queue work (the reporter sends the
    /// scoped delete to the Peer, best effort) and must never call back into the ledger.
    /// </summary>
#pragma warning disable CA1003 // A plain callback with the ids: this is internal plumbing, not a public event model.
    public event Action<IReadOnlyList<string>>? HlsSuperseded;

    /// <summary>Raised with a grant id after its HLS slot was released (the read-ahead of that stream is dropped). A listener must not call back into the ledger.</summary>
    public event Action<string>? HlsReleased;
#pragma warning restore CA1003

    /// <summary>
    /// Takes (or shares) the static slot of a user and item and opens a connection on it.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The local item id.</param>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerMax">The peer's <c>MaxConcurrentStreams</c>.</param>
    /// <param name="denial">Why it was refused.</param>
    /// <param name="origin">The series and device, for the binge handover allowance.</param>
    /// <returns>The lease, or null when refused.</returns>
    public StaticLease? TryOpenStatic(Guid userId, Guid itemId, string peerKey, int peerMax, out SlotDenial denial, SlotOrigin origin = default)
    {
        lock (_gate)
        {
            PruneLocked();
            if (_connections.GetValueOrDefault(userId) >= MaxConnectionsPerUser)
            {
                denial = SlotDenial.Connections;
                return null;
            }

            var key = (userId, itemId);
            var created = false;
            if (!_static.TryGetValue(key, out var slot))
            {
                denial = CheckLimits(userId, peerKey, peerMax, origin, itemId);
                if (denial != SlotDenial.None)
                {
                    return null;
                }

                DropSuperseded(userId, itemId, origin);
                slot = new StaticSlot { PeerKey = peerKey, Origin = origin };
                _static[key] = slot;
                created = true;
            }

            slot.Connections++;
            slot.LastActivity = _time.GetUtcNow();
            _connections[userId] = _connections.GetValueOrDefault(userId) + 1;
            denial = SlotDenial.None;
            return new StaticLease(this, key, created);
        }
    }

    /// <summary>
    /// Takes the HLS slot of a grant (idempotent for a grant that holds one).
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="peerMax">The peer's <c>MaxConcurrentStreams</c>.</param>
    /// <param name="peerMaxConversions">The peer's <c>MaxConcurrentTranscodes</c>: an HLS stream holds a conversion slot with its stream slot (6.9).</param>
    /// <param name="denial">Why it was refused.</param>
    /// <returns>True when the grant holds the slot.</returns>
    public bool TryTakeHls(Grant grant, int peerMax, int peerMaxConversions, out SlotDenial denial)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            PruneLocked();
            if (_hls.TryGetValue(grant.GrantId, out var existing))
            {
                existing.LastActivity = _time.GetUtcNow();
                denial = SlotDenial.None;
                return true;
            }

            // A static slot of the same user and item is the same stream, so the FIRST HLS slot of that pair needs no second stream slot; any further
            // HLS slot of the pair (another DeviceId) is a stream of its own and meets every limit (Warden 07 M-1).
            var sameStream = _static.ContainsKey((grant.LocalUserId, grant.SlotItem)) && !_hls.Values.Any(h => h.User == grant.LocalUserId && h.Item == grant.SlotItem);
            if (!sameStream)
            {
                denial = CheckLimits(grant.LocalUserId, grant.PeerKey, peerMax, OriginOf(grant), grant.SlotItem);
                if (denial != SlotDenial.None)
                {
                    return false;
                }
            }

            // Every HLS stream is a conversion (6.9): one more than the peer allows is refused, whatever the stream slots say.
            var movedOn = Superseded(grant.LocalUserId, grant.SlotItem, OriginOf(grant));
            if (_hls.Where(p => !movedOn.Hls.Contains(p.Key)).Count(p => p.Value.PeerKey == grant.PeerKey) >= peerMaxConversions)
            {
                denial = SlotDenial.Conversions;
                return false;
            }

            if (!sameStream)
            {
                DropSuperseded(grant.LocalUserId, grant.SlotItem, OriginOf(grant));
            }

            _hls[grant.GrantId] = new HlsSlot { User = grant.LocalUserId, Item = grant.SlotItem, PeerKey = grant.PeerKey, Origin = OriginOf(grant), LastActivity = _time.GetUtcNow() };
            denial = SlotDenial.None;
            return true;
        }
    }

    /// <summary>
    /// Records a request on a grant's HLS slot.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    public void TouchHls(string grantId)
    {
        lock (_gate)
        {
            if (_hls.TryGetValue(grantId, out var slot))
            {
                slot.LastActivity = _time.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Releases a grant's HLS slot (the scoped <c>ActiveEncodings</c> delete was sent, or <c>PlaybackStopped</c>).
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <returns>True when a slot was held.</returns>
    public bool ReleaseHls(string grantId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _hls.Remove(grantId);
        }

        if (removed)
        {
            HlsReleased?.Invoke(grantId);
        }

        return removed;
    }

    /// <summary>
    /// Releases the static slot of a user and item (<c>PlaybackStopped</c>). Never called for <c>ActiveEncodings</c>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The local item id.</param>
    /// <returns>True when a slot was held.</returns>
    public bool ReleaseStatic(Guid userId, Guid itemId)
    {
        lock (_gate)
        {
            if (_static.TryGetValue((userId, itemId), out var slot) && slot.Connections > 0)
            {
                slot.ReleaseOnClose = true; // the body is still streaming: the slot goes when its last connection ends (Warden 07 M-2)
                return false;
            }

            return _static.Remove((userId, itemId));
        }
    }

    /// <summary>
    /// Counts the open static connections of a user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The number of connections.</returns>
    public int OpenConnections(Guid userId)
    {
        lock (_gate)
        {
            return _connections.GetValueOrDefault(userId);
        }
    }

    /// <summary>
    /// Counts the live slots of a user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The number of live static and HLS slots.</returns>
    public int ActiveSlots(Guid userId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _static.Keys.Count(k => k.User == userId) + _hls.Values.Count(h => h.User == userId);
        }
    }

    /// <summary>
    /// Counts the live slots on a peer.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The number of live slots.</returns>
    public int ActiveSlotsOnPeer(string peerKey)
    {
        lock (_gate)
        {
            PruneLocked();
            return _static.Values.Count(s => s.PeerKey == peerKey) + _hls.Values.Count(h => h.PeerKey == peerKey);
        }
    }

    /// <summary>
    /// Counts the conversions (HLS streams) Home holds on a peer.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The number of live conversion slots.</returns>
    public int ConversionsOnPeer(string peerKey)
    {
        lock (_gate)
        {
            PruneLocked();
            return _hls.Values.Count(h => h.PeerKey == peerKey);
        }
    }

    /// <inheritdoc />
    public bool CanStart(Guid userId, Guid itemId, string peerKey, int peerMax) => CanStart(userId, itemId, peerKey, peerMax, default);

    /// <inheritdoc />
    public bool CanStart(Guid userId, Guid itemId, string peerKey, int peerMax, SlotOrigin origin)
    {
        lock (_gate)
        {
            PruneLocked();
            return _static.ContainsKey((userId, itemId)) || CheckLimits(userId, peerKey, peerMax, origin, itemId) == SlotDenial.None;
        }
    }

    /// <inheritdoc />
    public bool ConversionsFull(string peerKey, int peerMaxConversions)
    {
        lock (_gate)
        {
            PruneLocked();
            return _hls.Values.Count(h => h.PeerKey == peerKey) >= peerMaxConversions;
        }
    }

    /// <summary>
    /// Whether all of the peer's conversion slots are held, not counting the HLS slots this user's device has moved on from (2.9.2 K10: the same rule as
    /// <see cref="TryTakeHls"/>, so the PlaybackInfo pre-check does not refuse a retry the segment request would allow).
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerMaxConversions">The peer's <c>MaxConcurrentTranscodes</c>.</param>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The local item (slot key).</param>
    /// <param name="origin">The series and device of the new stream.</param>
    /// <returns>True when no conversion can start.</returns>
    public bool ConversionsFull(string peerKey, int peerMaxConversions, Guid userId, Guid itemId, SlotOrigin origin)
    {
        lock (_gate)
        {
            PruneLocked();
            var movedOn = Superseded(userId, itemId, origin).Hls; // 2.9.2 K10: what TryTakeHls would drop does not count
            return _hls.Count(p => p.Value.PeerKey == peerKey && !movedOn.Contains(p.Key)) >= peerMaxConversions;
        }
    }

    /// <inheritdoc />
    public bool HoldsSlot(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            PruneLocked();
            return _hls.ContainsKey(grant.GrantId) || _static.ContainsKey((grant.LocalUserId, grant.SlotItem));
        }
    }

    /// <inheritdoc />
    public bool HoldsOwnSlot(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            PruneLocked();
            return _hls.ContainsKey(grant.GrantId);
        }
    }

    // When the stream's HLS slot was last used (tests: the read-ahead must not move it).
    internal DateTimeOffset? HlsActivity(string grantId)
    {
        lock (_gate)
        {
            return _hls.TryGetValue(grantId, out var slot) ? slot.LastActivity : null;
        }
    }

    internal void TouchStatic((Guid User, Guid Item) key)
    {
        lock (_gate)
        {
            if (_static.TryGetValue(key, out var slot))
            {
                slot.LastActivity = _time.GetUtcNow();
            }
        }
    }

    internal void AbandonStatic((Guid User, Guid Item) key, bool created)
    {
        lock (_gate)
        {
            if (created && _static.TryGetValue(key, out var slot) && slot.Connections <= 1)
            {
                _static.Remove(key);
            }
        }
    }

    internal void CloseStatic((Guid User, Guid Item) key)
    {
        lock (_gate)
        {
            if (_static.TryGetValue(key, out var slot))
            {
                slot.Connections = Math.Max(0, slot.Connections - 1);
                slot.LastActivity = _time.GetUtcNow();
                if (slot.Connections == 0 && slot.ReleaseOnClose)
                {
                    _static.Remove(key);
                }
            }

            var open = _connections.GetValueOrDefault(key.User) - 1;
            if (open <= 0)
            {
                _connections.Remove(key.User);
            }
            else
            {
                _connections[key.User] = open;
            }
        }
    }

    private static SlotOrigin OriginOf(Grant grant) => new(grant.SeriesKey, grant.CallerDeviceId);

    // The streams a device has moved on from (DESIGN v2.7.8, 2.9.2 K10): the same user starts something on the same device, and an earlier stream of that device is
    // finished sending: a static slot of another title with no open connection (bytes only flow on an open connection, which stays counted), an HLS slot with no
    // segment request for HlsSupersedeAfter, the same title under another grant included (a retry after a failed start). Any title counts, not only the next
    // episode (a user report: a failed play blocked the next one for 2 min). Such a slot is counted as gone in every limit and dropped when the new slot is taken.
    // A stream that still sends is never superseded, so one device id cannot run several streams on one slot (Warden 16 M1): the limits stay hard.
    private (HashSet<(Guid User, Guid Item)> Static, HashSet<string> Hls) Superseded(Guid userId, Guid itemId, SlotOrigin origin)
    {
        if (string.IsNullOrEmpty(origin.DeviceId))
        {
            return ([], []);
        }

        var now = _time.GetUtcNow();
        var device = origin.DeviceId;
        return (
            _static.Where(p => p.Key.User == userId && p.Key.Item != itemId && SameDevice(p.Value.Origin, device) && p.Value.Connections == 0).Select(static p => p.Key).ToHashSet(),
            _hls.Where(p => p.Value.User == userId && SameDevice(p.Value.Origin, device) && now - p.Value.LastActivity >= HlsSupersedeAfter).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal));
    }

    private static bool SameDevice(SlotOrigin held, string device) => string.Equals(held.DeviceId, device, StringComparison.Ordinal);

    private void DropSuperseded(Guid userId, Guid itemId, SlotOrigin origin)
    {
        var gone = Superseded(userId, itemId, origin);
        foreach (var key in gone.Static)
        {
            _static.Remove(key);
        }

        foreach (var id in gone.Hls)
        {
            _hls.Remove(id);
        }

        if (gone.Hls.Count > 0)
        {
            try
            {
                HlsSuperseded?.Invoke([.. gone.Hls]);
            }
#pragma warning disable CA1031 // A listener must never stop the new episode from starting.
            catch (Exception)
#pragma warning restore CA1031
            {
                // best effort
            }
        }
    }

    private SlotDenial CheckLimits(Guid userId, string peerKey, int peerMax, SlotOrigin origin, Guid itemId)
    {
        var movedOn = Superseded(userId, itemId, origin);
        var userSlots = _static.Keys.Count(k => k.User == userId && !movedOn.Static.Contains(k)) + _hls.Where(p => p.Value.User == userId && !movedOn.Hls.Contains(p.Key)).Count();
        if (userSlots >= Math.Clamp(_maxPerUser(), 1, 4))
        {
            return SlotDenial.User;
        }

        var onPeer = _static.Where(p => p.Value.PeerKey == peerKey && !movedOn.Static.Contains(p.Key)).Count() + _hls.Where(p => p.Value.PeerKey == peerKey && !movedOn.Hls.Contains(p.Key)).Count();
        var mineOnPeer = _static.Where(p => p.Key.User == userId && p.Value.PeerKey == peerKey && !movedOn.Static.Contains(p.Key)).Count() + _hls.Where(p => p.Value.User == userId && p.Value.PeerKey == peerKey && !movedOn.Hls.Contains(p.Key)).Count();
        var half = (peerMax + 1) / 2;

        // Binge handover (DESIGN v2.7.8): the user already plays another episode of this series on this device at this peer, so the next one may exceed the
        // half-of-the-peer rule by one. The peer total and the per-user limit above stay hard.
        if (origin.IsEpisode && mineOnPeer >= half && HoldsSameSeriesOnDevice(userId, peerKey, origin))
        {
            half++;
        }

        return onPeer >= peerMax || mineOnPeer >= half ? SlotDenial.Peer : SlotDenial.None;
    }

    private bool HoldsSameSeriesOnDevice(Guid userId, string peerKey, SlotOrigin origin) =>
        _static.Any(p => p.Key.User == userId && p.Value.PeerKey == peerKey && p.Value.Origin == origin)
        || _hls.Values.Any(h => h.User == userId && h.PeerKey == peerKey && h.Origin == origin);

    private void PruneLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _static.Where(p => p.Value.Connections == 0 && now - p.Value.LastActivity >= Grace).ToList())
        {
            _static.Remove(pair.Key);
        }

        foreach (var pair in _hls.Where(p => now - p.Value.LastActivity >= Grace).ToList())
        {
            _hls.Remove(pair.Key);
            HlsReleased?.Invoke(pair.Key); // an idle stream keeps no read-ahead either (the listener only drops its own memory)
        }
    }

    private sealed class StaticSlot
    {
        public string PeerKey { get; init; } = string.Empty;

        public SlotOrigin Origin { get; init; }

        public int Connections { get; set; }

        public bool ReleaseOnClose { get; set; }

        public DateTimeOffset LastActivity { get; set; }
    }

    private sealed class HlsSlot
    {
        public Guid User { get; init; }

        public Guid Item { get; init; }

        public string PeerKey { get; init; } = string.Empty;

        public SlotOrigin Origin { get; init; }

        public DateTimeOffset LastActivity { get; set; }
    }
}
