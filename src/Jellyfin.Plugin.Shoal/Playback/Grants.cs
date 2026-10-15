using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// How a federated title is being played.
/// </summary>
public enum PlayMethod
{
    /// <summary>The raw file is streamed through Home.</summary>
    DirectPlay,

    /// <summary>The Peer transcodes and Home proxies the HLS stream.</summary>
    Transcode,
}

/// <summary>
/// The outcome of creating a grant.
/// </summary>
public enum GrantOutcome
{
    /// <summary>A new grant was stored.</summary>
    Created,

    /// <summary>A slot-free grant of the same key was replaced.</summary>
    Replaced,

    /// <summary>The user's oldest slot-free grant was evicted to make room.</summary>
    EvictedOwn,

    /// <summary>The user already holds the most grants and none is slot-free.</summary>
    RefusedUser,

    /// <summary>The server holds the most grants.</summary>
    RefusedGlobal,
}

/// <summary>
/// Tells whether a grant holds a stream slot (DESIGN 3.5, 6.9). K15 and K17 own the slots; until then no grant holds one.
/// </summary>
public interface ISlotLedger
{
    /// <summary>
    /// Whether the grant holds a slot: its own HLS slot is live, or the static slot of its (user, item) pair is.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <returns>True when a stream is running on it.</returns>
    bool HoldsSlot(Grant grant);

    /// <summary>
    /// Whether the grant holds a slot of its own: its HLS slot. The static slot of a (user, item) pair belongs to the stream, not to a grant, so a new
    /// PlaybackInfo of the same device (an audio, subtitle or quality switch during direct play) may replace the grant that plays it.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <returns>True when an HLS slot is held under this grant.</returns>
    bool HoldsOwnSlot(Grant grant) => HoldsSlot(grant);

    /// <summary>
    /// Whether a stream for this user and item could start now: the user and the peer have room (the pre-check at PlaybackInfo; nothing is reserved).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The local item.</param>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerMax">The peer's <c>MaxConcurrentStreams</c>.</param>
    /// <returns>True when a slot is available.</returns>
    bool CanStart(Guid userId, Guid itemId, string peerKey, int peerMax) => true;

    /// <summary>
    /// Whether all of the peer's conversion slots are held.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="peerMaxConversions">The peer's <c>MaxConcurrentTranscodes</c>.</param>
    /// <returns>True when no conversion can start.</returns>
    bool ConversionsFull(string peerKey, int peerMaxConversions) => false;
}

/// <summary>
/// A sanitized stream of a title (DESIGN 6.2.3): what the grant keeps for HLS subtitle names and the subtitle index checks.
/// </summary>
/// <param name="Index">The stream index, 0 to 127.</param>
/// <param name="Type">Video, Audio or Subtitle.</param>
/// <param name="Codec">The codec, or null.</param>
/// <param name="Language">The language, or null.</param>
/// <param name="Title">The sanitized title, or null.</param>
/// <param name="IsText">Whether a subtitle stream is a text stream.</param>
/// <param name="IsExternal">Whether the Peer delivers the stream as an external file.</param>
/// <param name="IsDefault">Whether it is the default stream.</param>
/// <param name="IsForced">Whether it is forced.</param>
public sealed record GrantTrack(int Index, string Type, string? Codec, string? Language, string? Title, bool IsText, bool IsExternal, bool IsDefault, bool IsForced);

/// <summary>
/// What PlaybackInfo agreed with the Peer for one (user, item, device): the only source of every later Peer request for this play (DESIGN 3.5).
/// Memory only; a Home restart drops all grants.
/// </summary>
public sealed class Grant
{
    /// <summary>The most font bytes one grant serves in total (DESIGN v2.6.6).</summary>
    public const long MaxAttachmentBytes = 128L * 1024 * 1024;

    private int _inFlightSegments;
    private long _attachmentBytes;

    /// <summary>Gets the grant id: 128 random bits, base64url.</summary>
    public required string GrantId { get; init; }

    /// <summary>Gets the local user who owns the grant. Every request on it re-checks this.</summary>
    public required Guid LocalUserId { get; init; }

    /// <summary>Gets the local item id the client asked for (the route item; with alternate versions it may be another version's title item).</summary>
    public required Guid LocalItemId { get; init; }

    /// <summary>Gets the local item id of the version that is played (DESIGN v2.6.4), or the empty id for a title with one version (then it is <see cref="LocalItemId"/>).</summary>
    public Guid VersionItemId { get; init; }

    /// <summary>Gets the media source id the client was given: the version's local item id (N form), or the empty id for a title with one version.</summary>
    public Guid LocalMediaSourceId { get; init; }

    /// <summary>Gets the id of the version set (the primary's stub key), or empty when the title has one version.</summary>
    public string SetId { get; init; } = string.Empty;

    /// <summary>Gets the version that is played: <see cref="VersionItemId"/>, or <see cref="LocalItemId"/> when the grant has no version.</summary>
    public Guid EffectiveVersionItemId => VersionItemId == Guid.Empty ? LocalItemId : VersionItemId;

    /// <summary>Gets the id the stream slots are keyed by: one per version set, so two versions of a title cannot hold two slots (DESIGN v2.6.4).</summary>
    public Guid SlotItem => SetId.Length == 0 ? LocalItemId : SlotKey.Of(SetId);

    /// <summary>Gets the capped device id of the caller (a convenience key within one user, never an identity).</summary>
    public required string CallerDeviceId { get; init; }

    /// <summary>Gets the random play session id Home gave the client (32 hex characters).</summary>
    public required string LocalPlaySessionId { get; init; }

    /// <summary>Gets the peer.</summary>
    public required string PeerKey { get; init; }

    /// <summary>Gets the peer's item id.</summary>
    public required Guid PeerItemId { get; init; }

    /// <summary>Gets the peer's media source id.</summary>
    public required Guid PeerMediaSourceId { get; init; }

    /// <summary>Gets the peer's play session id, or empty when the Peer sent none.</summary>
    public string PeerPlaySessionId { get; init; } = string.Empty;

    /// <summary>Gets the Peer's own master query (credentials removed), for an HLS transcode.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> PeerMasterQuery { get; init; } = [];

    /// <summary>Gets or sets the variant query learned from the master playlist (K17).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> PeerVariantQuery { get; set; } = [];

    /// <summary>Gets the sanitized streams.</summary>
    public IReadOnlyList<GrantTrack> Streams { get; init; } = [];

    /// <summary>Gets the audio stream index the Peer chose, or null.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the subtitle stream index the Peer chose, or null.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the runtime in ticks.</summary>
    public long RuntimeTicks { get; init; }

    /// <summary>Gets how the title is played.</summary>
    public PlayMethod Method { get; init; }

    /// <summary>Gets or sets a value indicating whether the Peer started an ffmpeg job for this grant (set at the first forwarded HLS segment).</summary>
    public bool PeerJobStarted { get; set; }

    /// <summary>Gets or sets how many segments the accepted media playlist listed, or null while no media playlist was accepted (H-1: no segment is served before it).</summary>
    public int? SegmentCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the accepted media playlist carried an initialization segment (<c>-1.mp4</c>).</summary>
    public bool HasInitSegment { get; set; }

    /// <summary>Gets when the grant was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets or sets when the grant was last used (UTC).</summary>
    public DateTimeOffset LastAccessUtc { get; set; }

    /// <summary>Gets or sets when steps 5 to 7 of CallerContext last ran for this grant (UTC).</summary>
    public DateTimeOffset LastAuthorizedUtc { get; set; }

    /// <summary>Gets or sets how often a client asked for another audio or subtitle track than the grant's (an admin-only debug aid, never labelled with values).</summary>
    public int ClientIndexMismatch { get; set; }

    /// <summary>Gets the attachment (font) indexes the rebuilt PlaybackInfo listed; only these may be fetched (DESIGN v2.6.6).</summary>
    public IReadOnlyList<int> AttachmentIndexes { get; init; } = [];

    /// <summary>Gets the key (user, item, device) the grant replaces and is replaced by.</summary>
    public (Guid User, Guid Item, string Device) Key => (LocalUserId, LocalItemId, CallerDeviceId);

    /// <summary>
    /// Takes one of the grant's in-flight segment places.
    /// </summary>
    /// <param name="max">The most segment calls one grant may have at the Peer at once.</param>
    /// <returns>True when a place was free.</returns>
    public bool TryEnterSegment(int max)
    {
        if (Interlocked.Increment(ref _inFlightSegments) <= max)
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlightSegments);
        return false;
    }

    /// <summary>
    /// Gives back an in-flight segment place.
    /// </summary>
    public void ExitSegment() => Interlocked.Decrement(ref _inFlightSegments);

    /// <summary>
    /// Counts font bytes about to be served against the grant's total.
    /// </summary>
    /// <param name="bytes">The size of the font.</param>
    /// <returns>False when the grant has served its 128 MB.</returns>
    public bool TryChargeAttachmentBytes(long bytes)
    {
        if (Interlocked.Add(ref _attachmentBytes, bytes) <= MaxAttachmentBytes)
        {
            return true;
        }

        Interlocked.Add(ref _attachmentBytes, -bytes);
        return false;
    }
}

/// <summary>
/// The grants (DESIGN 3.5): at most 8 per user and 200 in total. The order of the capacity checks is fixed: (1) a slot-free grant with the same key is
/// replaced (net zero); else (2) if the user already has 8, that user's oldest slot-free grant is evicted, and when none is slot-free the new grant is
/// refused; else (3) the global cap, checked last, so a user who would only replace or evict their own grant is never refused because the server is
/// full. A grant that holds a slot is never replaced or evicted.
/// </summary>
public sealed class GrantStore
{
    /// <summary>The most grants one user holds.</summary>
    public const int MaxPerUser = 8;

    /// <summary>The most grants the server holds.</summary>
    public const int MaxTotal = 200;

    /// <summary>The absolute lifetime of a grant.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    /// <summary>The idle expiry of a direct-play grant.</summary>
    public static readonly TimeSpan DirectPlayIdle = TimeSpan.FromHours(2);

    /// <summary>The idle expiry of an HLS grant.</summary>
    public static readonly TimeSpan HlsIdle = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _byId = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ISlotLedger _slots;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrantStore"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="slots">Which grants hold a stream slot.</param>
    public GrantStore(TimeProvider time, ISlotLedger slots)
    {
        _time = time;
        _slots = slots;
    }

    /// <summary>Gets the number of grants.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>
    /// Makes a new grant id: 128 random bits as base64url.
    /// </summary>
    /// <returns>The id.</returns>
    public static string NewGrantId() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Makes a new local play session id: 128 random bits as 32 hex characters.
    /// </summary>
    /// <returns>The id.</returns>
    public static string NewPlaySessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Checks the capacity in the same order <see cref="TryAdd"/> uses, before any Peer call.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="deviceId">The capped device id.</param>
    /// <returns>True when a grant for this key would be accepted.</returns>
    public bool HasRoom(Guid userId, Guid itemId, string deviceId)
    {
        lock (_gate)
        {
            PruneLocked();
            return Plan(userId, itemId, deviceId, out _) is not (GrantOutcome.RefusedUser or GrantOutcome.RefusedGlobal);
        }
    }

    /// <summary>
    /// Stores a grant, atomically, in the capacity order above.
    /// </summary>
    /// <param name="grant">The new grant.</param>
    /// <returns>What happened; a refused grant is not stored.</returns>
    public GrantOutcome TryAdd(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            PruneLocked();
            var outcome = Plan(grant.LocalUserId, grant.LocalItemId, grant.CallerDeviceId, out var victim);
            if (outcome is GrantOutcome.RefusedUser or GrantOutcome.RefusedGlobal)
            {
                return outcome;
            }

            if (victim is not null)
            {
                _byId.Remove(victim.GrantId);
            }

            _byId[grant.GrantId] = grant;
            return outcome;
        }
    }

    /// <summary>
    /// Finds a grant.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <returns>The grant, or null when it does not exist or has expired.</returns>
    public Grant? Get(string grantId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _byId.GetValueOrDefault(grantId);
        }
    }

    /// <summary>
    /// Finds a grant by the play session id the client holds.
    /// </summary>
    /// <param name="userId">The owner; another user's grant is never returned.</param>
    /// <param name="playSessionId">The local play session id.</param>
    /// <returns>The grant, or null.</returns>
    public Grant? FindByPlaySession(Guid userId, string playSessionId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _byId.Values.FirstOrDefault(g => g.LocalUserId == userId && string.Equals(g.LocalPlaySessionId, playSessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Lists a user's grants.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<Grant> ForUser(Guid userId)
    {
        lock (_gate)
        {
            PruneLocked();
            return [.. _byId.Values.Where(g => g.LocalUserId == userId)];
        }
    }

    /// <summary>
    /// Removes a grant.
    /// </summary>
    /// <param name="grantId">The grant id.</param>
    /// <returns>True when it existed.</returns>
    public bool Remove(string grantId)
    {
        lock (_gate)
        {
            return _byId.Remove(grantId);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private GrantOutcome Plan(Guid userId, Guid itemId, string deviceId, out Grant? victim)
    {
        victim = null;
        var mine = _byId.Values.Where(g => g.LocalUserId == userId).ToList();
        var same = mine.FirstOrDefault(g => g.LocalItemId == itemId && string.Equals(g.CallerDeviceId, deviceId, StringComparison.Ordinal));
        if (same is not null && !_slots.HoldsOwnSlot(same))
        {
            victim = same; // (1) replace: net zero, whatever the caps say
            return GrantOutcome.Replaced;
        }

        if (mine.Count >= MaxPerUser)
        {
            // A grant that holds nothing goes first; a direct-play grant (static slot only) can go too, the oldest first, but never one with an HLS slot.
            victim = mine.Where(g => !_slots.HoldsOwnSlot(g)).OrderBy(g => _slots.HoldsSlot(g) ? 1 : 0).ThenBy(static g => g.LastAccessUtc).FirstOrDefault();
            return victim is null ? GrantOutcome.RefusedUser : GrantOutcome.EvictedOwn; // (2)
        }

        return _byId.Count >= MaxTotal ? GrantOutcome.RefusedGlobal : GrantOutcome.Created; // (3), last
    }

    private void PruneLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var grant in _byId.Values.ToList())
        {
            var idle = grant.Method == PlayMethod.Transcode ? HlsIdle : DirectPlayIdle;
            if (now - grant.CreatedUtc > AbsoluteLifetime || (now - grant.LastAccessUtc > idle && !_slots.HoldsSlot(grant)))
            {
                _byId.Remove(grant.GrantId);
            }
        }
    }
}

/// <summary>
/// The default ledger until K15 and K17: no grant holds a stream slot.
/// </summary>
public sealed class NoSlots : ISlotLedger
{
    /// <inheritdoc />
    public bool HoldsSlot(Grant grant) => false;
}

/// <summary>
/// A token bucket per key, on the injected clock: <c>burst</c> calls at once, refilled at <c>perMinute</c> per minute.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (double Tokens, DateTimeOffset At)> _buckets = [];
    private readonly TimeProvider _time;
    private readonly int _burst;
    private readonly double _perSecond;
    private DateTimeOffset _lastPrune;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimiter"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="perMinute">The sustained rate.</param>
    /// <param name="burst">The bucket size.</param>
    public RateLimiter(TimeProvider time, int perMinute, int burst)
    {
        _time = time;
        _burst = burst;
        _perSecond = perMinute / 60.0;
    }

    /// <summary>
    /// Gets the number of keys the limiter remembers (tests).
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>
    /// Takes a token for a key.
    /// </summary>
    /// <param name="key">The key (the user).</param>
    /// <returns>True when the call may proceed.</returns>
    public bool TryTake(Guid key)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            var (tokens, at) = _buckets.TryGetValue(key, out var state) ? state : (_burst, now);
            tokens = Math.Min(_burst, tokens + ((now - at).TotalSeconds * _perSecond));
            if (tokens < 1)
            {
                _buckets[key] = (tokens, now);
                return false;
            }

            _buckets[key] = (tokens - 1, now);
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastPrune = now;
        foreach (var pair in _buckets.ToList())
        {
            var idle = now - pair.Value.At;
            if (idle > TimeSpan.FromMinutes(2) && pair.Value.Tokens + (idle.TotalSeconds * _perSecond) >= _burst)
            {
                _buckets.Remove(pair.Key);
            }
        }
    }
}
