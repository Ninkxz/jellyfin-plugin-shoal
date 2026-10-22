using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Where the admin page reads this server's name.
/// </summary>
public interface IHomeNameSource
{
    /// <summary>Gets the server's name, or null when it has none.</summary>
    string? Name { get; }
}

/// <summary>
/// Reads the server name from Jellyfin's configuration.
/// </summary>
public sealed class ServerConfigHomeName : IHomeNameSource
{
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerConfigHomeName"/> class.
    /// </summary>
    /// <param name="config">The server configuration.</param>
    public ServerConfigHomeName(IServerConfigurationManager config) => _config = config;

    /// <inheritdoc />
    public string? Name => _config.Configuration.ServerName;
}

/// <summary>
/// Rate limits the admin actions that cost the friends' servers something, and tells the page when they work again (it shows the
/// answer as "Available again in 4 min"). Singleton, in memory.
/// </summary>
public sealed class AdminCooldowns
{
    /// <summary>How long "Sync now" is unavailable after it was used (ui-spec 8.3.1: 5 minutes).</summary>
    public static readonly TimeSpan SyncAll = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _peerSyncUntil = new(StringComparer.Ordinal);
    private DateTime? _syncAllUntil;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminCooldowns"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public AdminCooldowns(TimeProvider time) => _time = time;

    /// <summary>Gets the time "Sync now" works again, or null when it works now.</summary>
    public DateTime? SyncAllUntilUtc
    {
        get
        {
            lock (_gate)
            {
                return _syncAllUntil is { } until && until > _time.GetUtcNow().UtcDateTime ? until : null;
            }
        }
    }

    /// <summary>
    /// Starts one server's "Sync now" cooldown, unless it is running.
    /// </summary>
    /// <param name="peerKey">The server.</param>
    /// <param name="retryAfter">When it is still running: how long to wait.</param>
    /// <returns>True when the action may run now.</returns>
    public bool TryStartPeerSync(string peerKey, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_peerSyncUntil.TryGetValue(peerKey, out var until) && until > now)
            {
                retryAfter = until - now;
                return false;
            }

            _peerSyncUntil[peerKey] = now + SyncAll;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Starts the "Sync now" cooldown, unless it is running.
    /// </summary>
    /// <param name="retryAfter">When it is still running: how long to wait.</param>
    /// <returns>True when the action may run now.</returns>
    public bool TryStartSyncAll(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_syncAllUntil is { } until && until > now)
            {
                retryAfter = until - now;
                return false;
            }

            _syncAllUntil = now + SyncAll;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

/// <summary>
/// The admin API of the Shoal page (DESIGN 9.6): <c>/Shoal/Admin/*</c>, admin only, JSON, handles only. Nothing here returns a
/// token, an invite after its one display, or a peer-side or user id (docs/ui-spec.md F5; the response shapes are in
/// <c>AdminDtos</c> and carry none).
/// <para>
/// The Home endpoints of the Overview (U2) read the real state. What only the backend tasks can know (health, speed, sync results,
/// the friends' activity) comes from <see cref="IPeerLiveStatus"/>, which answers "not measured" until those services exist.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminController : ControllerBase
{
    /// <summary>The name of the counter that says how often optional data of a peer item (trickplay, attachments) was left out while the title was kept.</summary>
    public const string OptionalDataDroppedCounter = "optional-data-dropped";

    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly IPeerLiveStatus _live;
    private readonly IHomeNameSource _homeName;
    private readonly AdminCooldowns _cooldowns;
    private readonly IGuardCounters _guard;
    private readonly ILibraryView _libraries;
    private readonly IAccountSource? _accounts;
    private readonly Jellyfin.Plugin.Shoal.Playback.InterceptCounters? _intercept;
    private readonly Jellyfin.Plugin.Shoal.Peers.TokenRotation? _rotation;
    private readonly Jellyfin.Plugin.Shoal.Sync.PeerCatalogReader? _reader;
    private readonly Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? _sandbox;
    private readonly Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsGuard? _libraryOptions;
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="live">What is known about the peers and shares right now.</param>
    /// <param name="homeName">This server's name.</param>
    /// <param name="cooldowns">The cooldowns.</param>
    /// <param name="guard">The guard counters.</param>
    /// <param name="libraries">The libraries Shoal made and the accounts.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="accounts">The accounts (to list leftover sharing accounts), or null.</param>
    /// <param name="intercept">The intercept counters (the device matrix), or null.</param>
    /// <param name="rotation">Token rotation (the schedule and the last time shown on the server), or null.</param>
    /// <param name="reader">The catalog reader, whose count of optional data left out is shown with the counters, or null.</param>
    /// <param name="sandbox">The image safety check (its state shown on the Overview and in Settings), or null.</param>
    /// <param name="libraryOptions">Names the main libraries whose NFO saver is on (Pilot 14), or null.</param>
    public AdminController(IStateStore state, ISecretStore secrets, IPeerLiveStatus live, IHomeNameSource homeName, AdminCooldowns cooldowns, IGuardCounters guard, ILibraryView libraries, ILogger<AdminController> logger, IAccountSource? accounts = null, Jellyfin.Plugin.Shoal.Playback.InterceptCounters? intercept = null, Jellyfin.Plugin.Shoal.Peers.TokenRotation? rotation = null, Jellyfin.Plugin.Shoal.Sync.IImageSandboxStatus? sandbox = null, Jellyfin.Plugin.Shoal.Sync.PeerCatalogReader? reader = null, Jellyfin.Plugin.Shoal.Visibility.LibraryOptionsGuard? libraryOptions = null)
    {
        _libraryOptions = libraryOptions;
        _sandbox = sandbox;
        _state = state;
        _secrets = secrets;
        _live = live;
        _homeName = homeName;
        _cooldowns = cooldowns;
        _guard = guard;
        _libraries = libraries;
        _accounts = accounts;
        _intercept = intercept;
        _rotation = rotation;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>
    /// The Overview: every server, every friend, the banners' inputs.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Produces("application/json")]
    public IActionResult GetStatus()
    {
        var status = AdminStatusBuilder.Build(_state, _secrets, _live, _homeName.Name, _cooldowns.SyncAllUntilUtc, _guard);
        if (_libraryOptions is not null)
        {
            status = status with { MainLibrariesSavingNfo = _libraryOptions.MainLibrariesSavingNfo(), MainLibrariesNotMergingSeries = _libraryOptions.MainLibrariesNotMergingSeries(), ShoalLibrariesNotLocked = _libraryOptions.ShoalLibrariesNotLocked(), MainLibrariesMissing = _libraryOptions.MainLibrariesMissing() };
        }

        if (_accounts is not null)
        {
            status = status with { OrphanAccounts = [.. OrphanAccounts.Find(_state, _accounts.List()).Select(static a => new OrphanAccountDto(a.Id, a.Name, a.IsDisabled))] };
        }

        if (_sandbox is not null)
        {
            var sandbox = _sandbox.GetStatus();
            status = status with { ImageSandbox = new ImageSandboxDto(sandbox.State, sandbox.Limits, sandbox.Reason, sandbox.Rejected) };
        }

        if (_intercept is not null)
        {
            var counts = _intercept.Snapshot();
            status = status with
            {
                Intercept = new InterceptDto(
                    _intercept.SinceUtc,
                    [
                        .. Jellyfin.Plugin.Shoal.Playback.InterceptCounters.Names.Select(n => new InterceptCounterDto(n, counts.GetValueOrDefault(n))),
                        .. _reader is null ? [] : new[] { new InterceptCounterDto(OptionalDataDroppedCounter, _reader.OptionalDataDropped) },
                    ]),
            };
        }

        return AdminReply.Json(StatusCodes.Status200OK, status);
    }

    /// <summary>
    /// "Sync now": asks every enabled server for its catalog. Returns at once.
    /// </summary>
    /// <returns>202, or 429 with <c>Retry-After</c> while the cooldown runs, or 409 in recovery mode.</returns>
    [HttpPost("Sync")]
    public IActionResult SyncAll()
    {
        if (_state.Status == StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!_cooldowns.TryStartSyncAll(out var retryAfter))
        {
            return AdminReply.TooMany(retryAfter);
        }

        var started = _live.StartSyncAll();
        _logger.LogInformation("Admin asked for a sync of all servers; {Started} started", started);
        return AdminReply.Json(StatusCodes.Status202Accepted, new { started });
    }

    /// <summary>
    /// One server's detail (U5 fills in the rest; the settings and the identity are real).
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <returns>The detail, or 404.</returns>
    [HttpGet("Peers/{handle}")]
    [Produces("application/json")]
    public IActionResult GetPeer(string handle)
    {
        var peer = Find(handle);
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var card = AdminStatusBuilder.Server(peer, _live.ForPeer(peer.PeerKey));
        var (libraries, users) = _libraries.ForPeer(peer.PeerKey, null);
        var detail = new ServerDetailDto(card)
        {
            Libraries = libraries,
            Users = users,
            ServerIdPrefix = peer.PinnedServerId is { Length: >= 8 } id ? id[..8] : null,
            PairedUtc = peer.Pairing?.PairedUtc,
            Alerts = _live.ForPeer(peer.PeerKey).Alerts.Select(a => (object)a).ToList(),
            Usage = _live.ForPeer(peer.PeerKey).Usage is { } usage ? new UsageDto(usage.DiskBytes, usage.QuotaBytes, usage.Stubs, usage.People, usage.Genres, usage.Studios) : null,
            Settings = new PeerSettingsDto(peer.MaxConcurrentStreams, peer.Priority, peer.ProxyUrl, !string.IsNullOrEmpty(peer.TlsSpkiPin), peer.Alias),
            UpdatedUtc = null,
            Rotation = _rotation?.Describe(peer),
            Link = LinkOf(peer)
        };
        return AdminReply.Json(StatusCodes.Status200OK, detail);
    }

    /// <summary>
    /// Pauses a server: its titles stay listed and fail fast (reversible, no confirmation).
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404, or 409 in recovery mode.</returns>
    [HttpPost("Peers/{handle}/Pause")]
    public Task<IActionResult> Pause(string handle, CancellationToken cancellationToken) => SetEnabled(handle, false, cancellationToken);

    /// <summary>
    /// Resumes a paused server.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404, or 409 in recovery mode.</returns>
    [HttpPost("Peers/{handle}/Resume")]
    public Task<IActionResult> Resume(string handle, CancellationToken cancellationToken) => SetEnabled(handle, true, cancellationToken);

    /// <summary>
    /// "Sync now" for one server: returns at once, 429 with Retry-After while its 5 minutes run.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <returns>202, 404, 409 in recovery mode, or 429.</returns>
    [HttpPost("Peers/{handle}/Sync")]
    public IActionResult SyncPeer(string handle)
    {
        if (_state.Status == StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (Find(handle) is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (!_cooldowns.TryStartPeerSync(handle, out var retryAfter))
        {
            return AdminReply.TooMany(retryAfter);
        }

        return AdminReply.Json(StatusCodes.Status202Accepted, new { started = _live.StartSync(handle) });
    }

    /// <summary>
    /// "Remove them now" / "Add them" (Release) or "Keep them for now" / "Not now" (Keep) for what a server's guard held back.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="kind">removals or additions.</param>
    /// <param name="decision">Release or Keep.</param>
    /// <param name="series">Optional: release only this series group (a key from <c>GET Peers/{h}/Holds</c>).</param>
    /// <returns>202 for Release, 200 for Keep, 400, 404 or 409.</returns>
    [HttpPost("Peers/{handle}/Holds/{kind}/{decision}")]
    public IActionResult Hold(string handle, string kind, string decision, [FromQuery] string? series = null)
    {
        if (_state.Status == StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var removals = string.Equals(kind, "removals", StringComparison.Ordinal);
        if ((!removals && !string.Equals(kind, "additions", StringComparison.Ordinal)) || (!string.Equals(decision, "Release", StringComparison.Ordinal) && !string.Equals(decision, "Keep", StringComparison.Ordinal)))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (Find(handle) is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (series is not null && (series.Length is < 8 or > 32 || !series.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z'))))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (series is not null && string.Equals(decision, "Release", StringComparison.Ordinal))
        {
            return AdminReply.Json(StatusCodes.Status202Accepted, new { started = _live.StartReleaseSeries(handle, removals, series) });
        }

        // Keep changes nothing: the guard keeps holding, and removals release on their own after 3 complete syncs over 24 hours.
        return string.Equals(decision, "Keep", StringComparison.Ordinal)
            ? AdminReply.Json(StatusCodes.Status200OK, new { })
            : AdminReply.Json(StatusCodes.Status202Accepted, new { started = _live.StartRelease(handle, removals) });
    }

    private async Task<IActionResult> SetEnabled(string handle, bool enabled, CancellationToken cancellationToken)
    {
        if (_state.Status == StateStatus.Recovery)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (Find(handle) is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        try
        {
            await _state.UpdateAsync(s => s.Home.Peers.First(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)).Enabled = enabled, cancellationToken).ConfigureAwait(false);
        }
        catch (StateRecoveryException)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The state could not be saved after a pause or resume");
            return AdminReply.Error(StatusCodes.Status500InternalServerError, "server");
        }

        return AdminReply.Json(StatusCodes.Status200OK, new { });
    }

    private FriendLinkDto? LinkOf(PeerState peer)
    {
        var current = _state.Current;
        var servers = current.Home.Peers.Select(p => AdminStatusBuilder.Server(p, _live.ForPeer(p.PeerKey))).ToList();
        var friends = current.PeerRole.Shares.Select(s => AdminStatusBuilder.Friend(s, _live.ForShare(s.ShareId))).ToList();
        return AdminStatusBuilder.Links(current.Home.Peers, servers, current.PeerRole.Shares, friends).FirstOrDefault(l => l.Linked && l.Watch?.PeerHandle == peer.PeerKey);
    }

    private PeerState? Find(string handle) =>
        _state.Status == StateStatus.Recovery ? null : _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal));
}
