using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>"Test now" is once a minute per peer, the deep test once every ten minutes (DESIGN 6.9).</summary>
public sealed class SpeedCooldowns
{
    private static readonly TimeSpan Test = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Deep = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedCooldowns"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public SpeedCooldowns(TimeProvider time) => _time = time;

    /// <summary>Starts a cooldown unless one is running.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="deep">True for the deep test.</param>
    /// <param name="retryAfter">How long to wait when refused.</param>
    /// <returns>True when the test may start.</returns>
    public bool TryStart(string peerKey, bool deep, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        var key = Key(peerKey, deep);
        lock (_until)
        {
            if (_until.TryGetValue(key, out var until) && until > now)
            {
                retryAfter = until - now;
                return false;
            }

            _until[key] = now + (deep ? Deep : Test);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Gives a cooldown back (the test did not run after all).</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="deep">True for the deep test.</param>
    public void Release(string peerKey, bool deep) => _until.TryRemove(Key(peerKey, deep), out _);

    /// <summary>When a kind of test is available again.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="deep">True for the deep test.</param>
    /// <returns>The time, or null when it is available now.</returns>
    public DateTimeOffset? Until(string peerKey, bool deep) =>
        _until.TryGetValue(Key(peerKey, deep), out var until) && until > _time.GetUtcNow() ? until : null;

    private static string Key(string peerKey, bool deep) => peerKey + (deep ? ":deep" : ":test");
}

/// <summary>
/// The speed endpoints of the admin page (DESIGN 9.6): the history with its ladder and KPIs, and "Test now".
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminSpeedController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly SpeedHistoryStore _history;
    private readonly SpeedTestService _tests;
    private readonly SpeedCooldowns _cooldowns;
    private readonly ISettingsAccessor _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminSpeedController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminSpeedController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="history">The speed history.</param>
    /// <param name="tests">The speed tests.</param>
    /// <param name="cooldowns">The cooldowns.</param>
    /// <param name="settings">The plugin toggles.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AdminSpeedController(IStateStore state, SpeedHistoryStore history, SpeedTestService tests, SpeedCooldowns cooldowns, ISettingsAccessor settings, TimeProvider time, ILogger<AdminSpeedController> logger)
    {
        _state = state;
        _history = history;
        _tests = tests;
        _cooldowns = cooldowns;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The speed history of one server with the ladder, KPIs and bottleneck.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="range">24h, 7d or 30d.</param>
    /// <returns>The report, 400 for another range, or 404.</returns>
    [HttpGet("Peers/{handle}/Speed")]
    [Produces("application/json")]
    public IActionResult Speed(string handle, [FromQuery] string? range)
    {
        var span = SpeedReport.SpanOf(range ?? "24h");
        if (span is null)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var peer = Find(handle);
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var others = _state.Current.Home.Peers.Where(p => p.Enabled && !string.Equals(p.PeerKey, peer.PeerKey, StringComparison.Ordinal)).ToDictionary(p => p.PeerKey, p => _history.Read(p.PeerKey), StringComparer.Ordinal);
        var report = SpeedReport.Build(_history.Read(peer.PeerKey), span.Value, _time.GetUtcNow(), peer, _settings.Current.AutoFitQualityToLink, _cooldowns.Until(peer.PeerKey, false), _cooldowns.Until(peer.PeerKey, true), _tests.IsRunning(peer.PeerKey), others);
        return AdminReply.Json(StatusCodes.Status200OK, report);
    }

    /// <summary>
    /// "Test now" (<c>deep: false</c>) or the deep test. Returns at once; the page reads the result with the next Speed call.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>202, 404, 409 (recovery, paused, or a test is already running) or 429 with Retry-After.</returns>
    [HttpPost("Peers/{handle}/SpeedTest")]
    public async Task<IActionResult> Start(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = Find(handle);
        if (peer is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        if (!peer.Enabled)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "paused");
        }

        var body = await AdminReply.ReadAsync<SpeedTestBody>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid"); // a missing or damaged body is not a request for a manual test
        }

        var deep = body.Deep;
        if (_tests.IsRunning(peer.PeerKey))
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "running");
        }

        if (!_cooldowns.TryStart(peer.PeerKey, deep, out var retryAfter))
        {
            return AdminReply.TooMany(retryAfter);
        }

        _ = Task.Run(() => RunInBackgroundAsync(peer.PeerKey, deep), CancellationToken.None);
        return AdminReply.Json(StatusCodes.Status202Accepted, new { });
    }

    private async Task RunInBackgroundAsync(string key, bool deep)
    {
        try
        {
            // The cooldown was taken when the request was accepted; if nothing ran (the peer cannot be reached by key, or a test was already
            // running) it is given back, so a 202 that did nothing does not lock the button.
            if (await _tests.RunAsync(key, deep ? SpeedKinds.Deep : SpeedKinds.Manual, CancellationToken.None).ConfigureAwait(false) is null)
            {
                _cooldowns.Release(key, deep);
            }
        }
#pragma warning disable CA1031 // A background test must never take the server down; the result, or its absence, is in the history.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("A speed test of peer {Peer} failed: {Error}", Redact.Id(key), ex.GetType().Name);
        }
    }

    private PeerState? Find(string handle) =>
        _state.Status == StateStatus.Ready ? _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, handle, StringComparison.Ordinal)) : null;
}

/// <summary>The body of the test request.</summary>
public sealed class SpeedTestBody
{
    /// <summary>Gets or sets a value indicating whether this is the deep test.</summary>
    public bool Deep { get; set; }
}
