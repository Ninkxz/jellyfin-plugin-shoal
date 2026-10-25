using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Guard;

/// <summary>
/// Item questions the guard asks Jellyfin (DESIGN 8.4 job a). The production class talks to <c>ILibraryManager</c>; tests use a fake.
/// </summary>
public interface IGuardItems
{
    /// <summary>
    /// Says whether an item lies inside one of the shared libraries.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="sharedLibraryIds">The ids of the shared libraries.</param>
    /// <returns>True or false for an item that exists; null for an id that resolves to no item (never cached).</returns>
    bool? IsShared(Guid itemId, IReadOnlySet<Guid> sharedLibraryIds);

    /// <summary>
    /// Says whether the user's own view contains the item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="userId">The user.</param>
    /// <returns>True when the user can see it.</returns>
    bool UserCanSee(Guid itemId, Guid userId);
}

/// <summary>
/// Asks whether a transcode is running for a play session (DESIGN 8.4 job c).
/// </summary>
public interface ITranscodeProbe
{
    /// <summary>
    /// Whether a transcoding job exists for the play session.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <returns>True when a job exists.</returns>
    bool IsTranscoding(string playSessionId);
}

/// <summary>What the guard needs to know about the shares, at one moment.</summary>
/// <param name="ShareUserIds">The sharing accounts.</param>
/// <param name="SharedLibraryIds">The libraries the shares include.</param>
/// <param name="TranscodeLimits">The transcode limit of each sharing account.</param>
public sealed record GuardSnapshot(IReadOnlySet<Guid> ShareUserIds, IReadOnlySet<Guid> SharedLibraryIds, IReadOnlyDictionary<Guid, int> TranscodeLimits)
{
    /// <summary>Gets a value indicating whether any share exists (the guard is inert without one).</summary>
    public bool Any => ShareUserIds.Count > 0;
}

/// <summary>
/// The share list for the guard: from the state, or from <c>peer-role/guard-lastgood.json</c> while the state is in recovery mode (DESIGN 3.7), so a
/// damaged state file never switches the bans off. The snapshot is rebuilt when the state changes (an event, and a reference check as a safety
/// net), never per request; the hardening mode travels with it, so recovery does not quietly drop Enforce to LogOnly (Warden M6, M7).
/// </summary>
public sealed class GuardShares
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 8 };
    private static readonly Built Empty = new(null, new GuardSnapshot(new HashSet<Guid>(), new HashSet<Guid>(), new Dictionary<Guid, int>()), HardeningMode.Enforce); // D18: the same default as a fresh state (an empty snapshot shares nothing, so it refuses nothing)

    private readonly IStateStore _state;
    private readonly string _file;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private volatile Built? _built;
    private string _written = string.Empty;
    private long _nextFileRead;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardShares"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public GuardShares(IStateStore state, ShoalPaths paths, ILogger<GuardShares> logger)
        : this(state, Path.Combine(paths.PeerRoleDir, "guard-lastgood.json"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardShares"/> class (tests: any file and logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="lastGoodFile">The last-known share list file.</param>
    /// <param name="logger">The logger.</param>
    internal GuardShares(IStateStore state, string lastGoodFile, ILogger logger)
    {
        _state = state;
        _file = lastGoodFile;
        _logger = logger;
        _state.Changed += OnChanged;
    }

    /// <summary>Gets the share list as it stands (rebuilt only when the state changed).</summary>
    public GuardSnapshot Current => Resolve().Snapshot;

    /// <summary>Gets the hardening mode: the state's, or the last known one while the state is in recovery mode.</summary>
    public HardeningMode Mode => Resolve().Mode;

    /// <summary>Gets the last snapshot that was built, without looking at the state at all (what job d falls back on).</summary>
    public GuardSnapshot LastKnown => (_built ?? Empty).Snapshot;

    /// <summary>Gets the last hardening mode without reading a possibly unavailable state store.</summary>
    public HardeningMode LastKnownMode => (_built ?? Empty).Mode;

    private Built Resolve()
    {
        if (_state.Status == StateStatus.Ready)
        {
            var state = _state.Current;
            var built = _built;
            return built is not null && ReferenceEquals(built.From, state) ? built : Rebuild(state);
        }

        var last = _built;
        if (last is not null && last.Snapshot.Any)
        {
            return last;
        }

        // Nothing in memory: read the file, but not on every request.
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextFileRead))
        {
            return last ?? Empty;
        }

        Interlocked.Exchange(ref _nextFileRead, now + 30_000);
        var fromFile = ReadLastGood();
        if (fromFile is not null)
        {
            _built = fromFile;
        }

        return fromFile ?? last ?? Empty;
    }

    private void OnChanged(object? sender, StateChangedEventArgs e)
    {
#pragma warning disable CA1031 // A state change must never be failed by the guard.
        try
        {
            Rebuild(e.State);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The guard could not refresh its share list: {Error}", ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    private Built Rebuild(ShoalState state)
    {
        Built built;
        lock (_lock)
        {
            if (_built is { } existing && ReferenceEquals(existing.From, state))
            {
                return existing;
            }

            var shares = state.PeerRole.Shares;
            built = new Built(
                state,
                new GuardSnapshot(
                    shares.Select(s => s.ShareUserId).Where(id => id != Guid.Empty).ToHashSet(),
                    shares.SelectMany(s => s.LibraryIds).ToHashSet(),
                    shares.Where(s => s.ShareUserId != Guid.Empty).GroupBy(s => s.ShareUserId).ToDictionary(g => g.Key, g => g.First().MaxConcurrentTranscodes)),
                state.PeerRole.HardeningMode);
            _built = built;
        }

        Persist(built);
        return built;
    }

    // Once per state change (never per request); any failure is a warning, the in-memory snapshot is what counts.
    private void Persist(Built built)
    {
        var doc = new LastGood
        {
            Users = [.. built.Snapshot.ShareUserIds],
            Libraries = [.. built.Snapshot.SharedLibraryIds],
            Limits = built.Snapshot.TranscodeLimits.ToDictionary(k => k.Key.ToString("N"), k => k.Value),
            Mode = built.Mode.ToString()
        };
        var text = JsonSerializer.Serialize(doc, Options);
        lock (_lock)
        {
            if (text == _written)
            {
                return;
            }

            _written = text; // a failed write is not retried until the list changes again
        }

#pragma warning disable CA1031 // Whatever is wrong with the disk, the guard keeps working from memory.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            AtomicFile.Write(_file, System.Text.Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The guard's last-known share list could not be saved: {Error}", ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    private Built? ReadLastGood()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<LastGood>(File.ReadAllBytes(_file), Options);
            if (doc is null)
            {
                return null;
            }

            var limits = doc.Limits.Where(l => Guid.TryParseExact(l.Key, "N", out _)).ToDictionary(l => Guid.ParseExact(l.Key, "N"), l => l.Value);
            var mode = Enum.TryParse<HardeningMode>(doc.Mode, out var m) ? m : HardeningMode.Enforce;
            return new Built(null, new GuardSnapshot(doc.Users.ToHashSet(), doc.Libraries.ToHashSet(), limits), mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record Built(ShoalState? From, GuardSnapshot Snapshot, HardeningMode Mode);

    private sealed class LastGood
    {
        [JsonPropertyName("users")]
        public List<Guid> Users { get; set; } = [];

        [JsonPropertyName("libraries")]
        public List<Guid> Libraries { get; set; } = [];

        [JsonPropertyName("limits")]
        public Dictionary<string, int> Limits { get; set; } = [];

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }
    }
}

/// <summary>
/// What the guard counted: requests that would have been (or were) refused, by route kind, app and address class, in hourly buckets for 7 days; and
/// the failures, shield hits and transcode refusals shown on the Hardening screen. Nothing here is a URL, an id, a token or a full address.
/// </summary>
public sealed class GuardCounters : IGuardCounters
{
    private const int MaxApps = 64;
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private readonly object _lock = new();
    private readonly Dictionary<(DateTimeOffset Hour, string Kind, string App, string Network), int> _buckets = [];
    private readonly HashSet<string> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _recentAddresses = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly InviteBook? _invites;
    private int _errors;
    private int _transcodeCap;
    private int _requests;
    private DateTimeOffset _pruned;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardCounters"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="invites">The invites (the wrong-invite counter is theirs).</param>
    public GuardCounters(TimeProvider time, InviteBook invites)
    {
        _time = time;
        _invites = invites;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardCounters"/> class (tests: no invite book).
    /// </summary>
    /// <param name="time">The clock.</param>
    internal GuardCounters(TimeProvider time)
    {
        _time = time;
    }

    /// <inheritdoc />
    public IReadOnlyList<GuardCounter> WouldHaveBeenBlocked
    {
        get
        {
            lock (_lock)
            {
                var cutoff = _time.GetUtcNow() - Window;
                return [.. _buckets.Where(b => b.Key.Hour >= cutoff).GroupBy(b => (b.Key.Kind, b.Key.App, b.Key.Network)).Select(g => new GuardCounter(g.Key.App, g.Key.Network, g.Key.Kind, g.Sum(b => b.Value))).OrderByDescending(c => c.Count)];
            }
        }
    }

    /// <inheritdoc />
    public int GuardErrors => Volatile(ref _errors);

    /// <inheritdoc />
    public int TranscodeCapHits => Volatile(ref _transcodeCap);

    /// <inheritdoc />
    public int InviteFailuresLastHour => _invites?.FailuresLastHour ?? 0;

    /// <inheritdoc />
    public bool ProxySuspected
    {
        get
        {
            lock (_lock)
            {
                var total = _recentAddresses.Values.Sum();
                return total >= 50 && _recentAddresses.Any(a => a.Value * 10 >= total * 8 && a.Key.StartsWith("private:", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Counts one request the guard would have refused (LogOnly) or refused.</summary>
    /// <param name="kind">The route kind (stream, subtitle, attachment, hls, image).</param>
    /// <param name="userAgent">The user agent; only its first product token (32 characters) is kept.</param>
    /// <param name="network">The address class: loopback, lan, tailnet or public.</param>
    public void Blocked(string kind, string? userAgent, string network)
    {
        var app = Product(userAgent);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            if (hour != _pruned)
            {
                // Once an hour (not on every request): drop what is older than the window and forget apps that no longer have a bucket.
                _pruned = hour;
                foreach (var old in _buckets.Keys.Where(k => k.Hour < now - Window).ToList())
                {
                    _buckets.Remove(old);
                }

                _apps.Clear();
                foreach (var key in _buckets.Keys)
                {
                    _apps.Add(key.App);
                }
            }

            // A user agent is the caller's to choose: at most 64 distinct names are kept, the rest count as "other" (Warden H2).
            if (!_apps.Contains(app))
            {
                if (_apps.Count >= MaxApps)
                {
                    app = "other";
                }

                _apps.Add(app);
            }

            var bucket = (hour, kind, app, network);
            _buckets[bucket] = _buckets.GetValueOrDefault(bucket) + 1;
        }
    }

    /// <summary>Counts a request's source, for the reverse-proxy warning (private and loopback addresses only; nothing is stored beyond a count).</summary>
    /// <param name="network">The address class.</param>
    /// <param name="address">The address.</param>
    public void Seen(string network, string address)
    {
        lock (_lock)
        {
            var key = (network is "lan" or "loopback" ? "private:" : "other:") + address;
            _recentAddresses[key] = _recentAddresses.GetValueOrDefault(key) + 1;
            if (++_requests >= 200)
            {
                _requests = 0;
                _recentAddresses.Clear();
            }
        }
    }

    /// <summary>Counts a failure the guard let through (fail open).</summary>
    public void Error() => Interlocked.Increment(ref _errors);

    /// <summary>Counts a transcode refused over a friend's limit.</summary>
    public void TranscodeCap() => Interlocked.Increment(ref _transcodeCap);

    private static int Volatile(ref int value) => System.Threading.Volatile.Read(ref value);

    private static string Product(string? userAgent)
    {
        var token = (userAgent ?? string.Empty).Split(' ', '/', ';')[0];
        var clean = new string([.. token.Where(static c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')]);
        return clean.Length == 0 ? "unknown" : clean[..Math.Min(32, clean.Length)];
    }
}

/// <summary>
/// The anonymous item-id routes of the running Jellyfin, computed from its endpoint table and diffed against the checked-in table (DESIGN 8.4 job a).
/// </summary>
public static class GuardRoutes
{
    /// <summary>The routes core 12.1 serves without authorization for an item id (lower case; the checked-in table).</summary>
    public static readonly IReadOnlyList<string> Expected =
    [
        "audio/{itemid}/hls/{segmentid}/stream.aac",
        "audio/{itemid}/hls/{segmentid}/stream.mp3",
        "audio/{itemid}/stream",
        "audio/{itemid}/stream.{container}",
        "items/{itemid}/images/{imagetype}",
        "items/{itemid}/images/{imagetype}/{imageindex}",
        "items/{itemid}/images/{imagetype}/{imageindex}/{tag}/{format}/{maxwidth}/{maxheight}/{percentplayed}/{unplayedcount}",
        "videos/{itemid}/{mediasourceid}/subtitles/{index}/stream.{routeformat}",
        "videos/{itemid}/{mediasourceid}/subtitles/{index}/{startpositionticks}/stream.{routeformat}",
        "videos/{itemid}/hls/{playlistid}/{segmentid}.{segmentcontainer}",
        "videos/{itemid}/stream",
        "videos/{itemid}/stream.{container}",
        "videos/{videoid}/{mediasourceid}/attachments/{index}"
    ];

    private static readonly string[] ItemParameters = ["itemid", "routeitemid", "videoid"];

    /// <summary>
    /// Says whether an endpoint is served without a sign-in: it is marked <c>[AllowAnonymous]</c>, or it carries no authorization at all (Warden M9).
    /// </summary>
    /// <param name="metadata">The endpoint's metadata.</param>
    /// <returns>True when a request needs no sign-in.</returns>
    public static bool IsAnonymous(IEnumerable<object> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var items = metadata as IReadOnlyCollection<object> ?? [.. metadata];
        return items.OfType<IAllowAnonymous>().Any() || !items.OfType<IAuthorizeData>().Any();
    }

    /// <summary>
    /// Lists the actions that are served without a sign-in and take an item id: the very descriptors the guard filter looks at on each request,
    /// so the table and the filter cannot disagree. (The endpoint data sources are not complete while plugins start, which made every route
    /// look missing in the lab.)
    /// </summary>
    /// <param name="actions">The action descriptors.</param>
    /// <returns>The templates, lower case, sorted.</returns>
    public static IReadOnlyList<string> Compute(IEnumerable<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return
        [.. actions
            .Where(a => a.AttributeRouteInfo?.Template is { Length: > 0 } && IsAnonymous(a.EndpointMetadata ?? []))
            .Select(a => a.AttributeRouteInfo!.Template!.TrimStart('/'))
            .Where(static t => ParameterNames(t).Any(p => ItemParameters.Contains(p, StringComparer.Ordinal)))
            .Select(static t => WithoutConstraints(t).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Lists the endpoints that carry no authorization and take an item id.
    /// </summary>
    /// <param name="sources">The endpoint data sources.</param>
    /// <returns>The templates, lower case, sorted.</returns>
    public static IReadOnlyList<string> Compute(IEnumerable<EndpointDataSource> sources) =>
        [.. sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => IsAnonymous(e.Metadata))
            .Where(e => e.RoutePattern.Parameters.Any(p => ItemParameters.Contains(p.Name.ToLowerInvariant(), StringComparer.Ordinal)))
            .Select(e => (e.RoutePattern.RawText ?? string.Empty).TrimStart('/').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Compares the computed table with the checked-in one.
    /// </summary>
    /// <param name="actual">What the running server has.</param>
    /// <param name="expected">The checked-in table.</param>
    /// <returns>The routes that are new (<c>+</c>) and the ones that disappeared (<c>-</c>); empty when they agree.</returns>
    public static IReadOnlyList<string> Diff(IEnumerable<string> actual, IEnumerable<string> expected)
    {
        // Parameter names are not part of a route: core spells the same route differently in two controllers ({itemId} and {routeItemId}).
        var a = actual.Select(NormalizeParameters).ToHashSet(StringComparer.Ordinal);
        var e = expected.Select(NormalizeParameters).ToHashSet(StringComparer.Ordinal);
        return [.. a.Except(e).Order(StringComparer.Ordinal).Select(r => "+" + r), .. e.Except(a).Order(StringComparer.Ordinal).Select(r => "-" + r)];
    }

    /// <summary>
    /// Replaces every <c>{name}</c> (with its constraint or default, if any) by <c>{}</c>, keeping the literal segments.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <returns>The template with anonymous parameters.</returns>
    public static string NormalizeParameters(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var builder = new System.Text.StringBuilder(template.Length);
        var inside = false;
        foreach (var c in template)
        {
            if (!inside)
            {
                builder.Append(c);
                inside = c == '{';
            }
            else if (c == '}')
            {
                inside = false;
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Which kind of media a path is, for the counters.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>stream, subtitle, attachment, hls or image.</returns>
    public static string KindOf(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("/images/", StringComparison.Ordinal) ? "image"
            : p.Contains("/subtitles/", StringComparison.Ordinal) ? "subtitle"
            : p.Contains("/attachments/", StringComparison.Ordinal) ? "attachment"
            : p.Contains("/hls/", StringComparison.Ordinal) ? "hls"
            : "stream";
    }

    // A template with every parameter reduced to its name: {itemId:guid} and {itemId?} become {itemId}, so the table does not depend on constraints.
    private static string WithoutConstraints(string template)
    {
        var builder = new System.Text.StringBuilder(template.Length);
        var inside = false;
        var skipping = false;
        foreach (var c in template)
        {
            if (!inside)
            {
                builder.Append(c);
                inside = c == '{';
                continue;
            }

            if (c == '}')
            {
                inside = false;
                skipping = false;
                builder.Append(c);
            }
            else if (!skipping && (char.IsLetterOrDigit(c) || c is '_' or '*'))
            {
                builder.Append(c);
            }
            else
            {
                skipping = true; // a constraint, a default or an optional mark: left out
            }
        }

        return builder.ToString();
    }

    // The parameter names of a route template, lower case: the text after each opening brace, up to a constraint, default or closing brace.
    private static IEnumerable<string> ParameterNames(string template)
    {
        foreach (var part in template.Split('{').Skip(1))
        {
            var name = new string([.. part.TrimStart('*').TakeWhile(static c => char.IsLetterOrDigit(c) || c == '_')]);
            if (name.Length > 0)
            {
                yield return name.ToLowerInvariant();
            }
        }
    }
}

/// <summary>A bounded cache of "is this existing item in a shared library" answers (60 s, 10,000 entries). An id that is no item is never put in it.</summary>
public sealed class SharedItemCache
{
    private const int Capacity = 10_000;
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, (bool Shared, DateTimeOffset At, LinkedListNode<Guid> Node)> _map = [];
    private readonly LinkedList<Guid> _order = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedItemCache"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public SharedItemCache(TimeProvider time) => _time = time;

    /// <summary>Gets the number of entries.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Looks an answer up.</summary>
    /// <param name="id">The item.</param>
    /// <param name="shared">The cached answer.</param>
    /// <returns>True on a fresh hit.</returns>
    public bool TryGet(Guid id, out bool shared)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var entry) && _time.GetUtcNow() - entry.At < Life)
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                shared = entry.Shared;
                return true;
            }

            shared = false;
            return false;
        }
    }

    /// <summary>Stores an answer for an item that exists.</summary>
    /// <param name="id">The item.</param>
    /// <param name="shared">The answer.</param>
    public void Put(Guid id, bool shared)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var old))
            {
                _order.Remove(old.Node);
            }

            _map[id] = (shared, _time.GetUtcNow(), _order.AddFirst(id));
            while (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value);
            }
        }
    }

    /// <summary>Forgets everything (the shares changed).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
