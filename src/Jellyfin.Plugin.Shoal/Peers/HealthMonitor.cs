#pragma warning disable SA1600 // internal plumbing; the public surface is documented

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// Keeps the runtime status of every peer true (DESIGN 4.3, 4.4): the identity and the token are re-checked every 60 seconds (the trust window), a
/// Quick Connect peer gets the session anchor, and every 10 minutes the foreign-device watch looks for a second device on the share account.
/// </summary>
public sealed class HealthMonitor
{
    /// <summary>How often a peer is checked.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>How often the foreign-device watch runs.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan AlertInterval = TimeSpan.FromHours(1);

    private readonly IStateStore _state;
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;
    private readonly PeerTrust _trust;
    private readonly PeerRegistry _registry;
    private readonly IAdminNotifier _admins;
    private readonly TimeProvider _time;
    private readonly ILogger<HealthMonitor> _logger;
    private readonly ConcurrentDictionary<string, PeerRun> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthMonitor"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="trust">The trust window.</param>
    /// <param name="registry">The runtime peer table.</param>
    /// <param name="admins">The admin notifier.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public HealthMonitor(IStateStore state, IPeerEndpoints peers, PeerClient client, PeerTrust trust, PeerRegistry registry, IAdminNotifier admins, TimeProvider time, ILogger<HealthMonitor> logger)
    {
        _state = state;
        _peers = peers;
        _client = client;
        _trust = trust;
        _registry = registry;
        _admins = admins;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Checks every enabled peer once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that ends when all peers were checked.</returns>
    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return;
        }

        foreach (var peer in _state.Current.Home.Peers.Where(static p => p.Enabled).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckAsync(peer.PeerKey, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the last checks learned about a peer, for the admin page.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The snapshot, or null before the first check.</returns>
    public PeerHealthSnapshot? Snapshot(string peerKey) =>
        _runs.TryGetValue(peerKey, out var run) && run.LastCheckUtc is { } last ? new PeerHealthSnapshot(last, last + Interval, run.RttMs, run.DeviceLock, run.SessionLimit) : null;

    /// <summary>
    /// Checks one peer: identity (no token), then the token, then the anchor and, when due, the foreign-device watch.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status afterwards.</returns>
    public async Task<PeerStatus> CheckAsync(string peerKey, CancellationToken cancellationToken)
    {
        var state = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey);
        var peer = _peers.Find(peerKey);
        var before = _registry.StatusOf(peerKey);
        if (state is null || !state.Enabled || peer is null || before is PeerStatus.Pairing or PeerStatus.PairingAborted or PeerStatus.Disabled)
        {
            return before;
        }

        if (_registry.IsRotating(peerKey))
        {
            return before; // the token is being replaced: this tick would read the old one (DESIGN v2.6.1)
        }

        var run = _runs.GetOrAdd(peerKey, static _ => new PeerRun());

        // An open breaker lets nothing through but its own half-open probe, and nothing else sends one: without this ping a peer that failed three calls
        // (a paused peer, three PlaybackInfo timeouts) stays unreachable for Home until Home restarts (Pilot round 6, finding 4).
        if (_client.BreakerOf(peer).IsOpen && !await ProbeAsync(peer, cancellationToken).ConfigureAwait(false))
        {
            return Down(peerKey, run);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pin = await _trust.VerifyPinAsync(peer, cancellationToken).ConfigureAwait(false);
        clock.Stop();
        run.LastCheckUtc = _time.GetUtcNow();
        if (pin == PinOutcome.Verified)
        {
            run.RttMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
        }

        switch (pin)
        {
            case PinOutcome.Mismatch:
                await AlertOnceAsync(run, "identity", $"{MetadataSanitizer.DisplayName(peer.DisplayName)} is not the server it was paired with. Shoal stopped talking to it.", cancellationToken).ConfigureAwait(false);
                return PeerStatus.IdentityMismatch;
            case PinOutcome.Unavailable:
                return Down(peerKey, run);
        }

        if (before is PeerStatus.IdentityMismatch or PeerStatus.TlsKeyChanged)
        {
            return before; // zero further calls until the admin acts
        }

        switch (await _trust.VerifyAuthAsync(peer, cancellationToken).ConfigureAwait(false))
        {
            case AuthOutcome.Refused:
                await AlertOnceAsync(run, "auth", $"{MetadataSanitizer.DisplayName(peer.DisplayName)} no longer accepts the pairing. Pair it again.", cancellationToken).ConfigureAwait(false);
                return PeerStatus.AuthFailed;
            case AuthOutcome.Unavailable:
                return Down(peerKey, run);
        }

        _registry.SetStatus(peerKey, PeerStatus.Online);
        var recovered = run.WasDown;
        run.WasDown = false;
        if (state.Pairing?.Method == PairingMethod.Qc)
        {
            await AnchorAsync(peer, run, recovered, cancellationToken).ConfigureAwait(false);
        }

        if (state.Pairing is not null && _time.GetUtcNow() - run.LastWatch >= WatchInterval)
        {
            run.LastWatch = _time.GetUtcNow();
            await WatchAsync(peer, run, cancellationToken).ConfigureAwait(false);
        }

        return PeerStatus.Online;
    }

    // The half-open probe (DESIGN 6.10): a ping once the breaker says it is due. Any answer below 500 closes the breaker; a failure schedules the next probe later.
    private async Task<bool> ProbeAsync(PeerEndpoint peer, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.SendAsync(peer, PeerRequest.Probe(), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
        }
        catch (PeerCallException)
        {
            return false; // not due yet, or the peer did not answer
        }

        return !_client.BreakerOf(peer).IsOpen;
    }

    private PeerStatus Down(string peerKey, PeerRun run)
    {
        run.WasDown = true;
        _registry.SetStatus(peerKey, PeerStatus.Offline);
        return PeerStatus.Offline;
    }

    // The session anchor (6.11): keeps Home's session alive on a stock Peer so MaxActiveSessions=1 refuses a second login. Never sent to a Shoal Peer.
    private async Task AnchorAsync(PeerEndpoint peer, PeerRun run, bool recovered, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.SendAsync(peer, PeerRequest.Post("/Sessions/Capabilities?supportsPersistentIdentifier=true", null).WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess && _time.GetUtcNow() - run.LastAnchorLog >= AlertInterval)
            {
                run.LastAnchorLog = _time.GetUtcNow();
                _logger.LogInformation("The session anchor for peer {Peer} was refused (HTTP {Status})", Redact.Id(peer.PeerKey), (int)response.StatusCode);
            }
        }
        catch (PeerCallException ex)
        {
            if (_time.GetUtcNow() - run.LastAnchorLog >= AlertInterval)
            {
                run.LastAnchorLog = _time.GetUtcNow();
                _logger.LogInformation("The session anchor for peer {Peer} failed ({Kind}, recovered {Recovered})", Redact.Id(peer.PeerKey), ex.Kind, recovered);
            }
        }
    }

    // The foreign-device watch (4.3): core shows a non-admin only its own sessions plus anonymous ones, so keep the share user's and discard the rest.
    private async Task WatchAsync(PeerEndpoint peer, PeerRun run, CancellationToken cancellationToken)
    {
        try
        {
            using (var me = await _client.SendAsync(peer, PeerRequest.Get("/Users/Me").WithTrust(TrustRequirement.None), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false))
            {
                if (me.StatusCode != HttpStatusCode.OK)
                {
                    return;
                }

                using var meDoc = JsonDocument.Parse(await me.ReadBytesAsync(cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = PeerJson.MaxDepth });
                if (meDoc.RootElement.ValueKind != JsonValueKind.Object || !meDoc.RootElement.TryGetProperty("Id", out var id) || !Guid.TryParse(id.GetString(), out var shareUser))
                {
                    return;
                }

                run.ShareUserId = shareUser;
                ReadPolicy(peer, run, meDoc.RootElement);
            }

            using var response = await _client.SendAsync(peer, PeerRequest.Get("/Sessions").WithTrust(TrustRequirement.None), PeerCallProfile.Sessions, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = PeerJson.MaxDepth });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var kept = new List<(string DeviceId, string Name)>();
            foreach (var session in doc.RootElement.EnumerateArray())
            {
                if (session.ValueKind != JsonValueKind.Object
                    || !session.TryGetProperty("UserId", out var user) || !Guid.TryParse(user.GetString(), out var userId) || userId != run.ShareUserId)
                {
                    continue; // another account's, or anonymous: discarded unlogged
                }

                var device = session.TryGetProperty("DeviceId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
                var name = session.TryGetProperty("DeviceName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                kept.Add((device, MetadataSanitizer.DisplayName(name)));
            }

            var foreign = kept.FirstOrDefault(k => !string.Equals(k.DeviceId, peer.DeviceId, StringComparison.Ordinal));
            if (kept.Count > 1 || foreign != default)
            {
                var who = foreign != default ? foreign.Name : kept.Select(static k => k.Name).FirstOrDefault(static n => n.Length > 0) ?? string.Empty;
                await AlertOnceAsync(run, "foreign", $"Another device is using your account on {MetadataSanitizer.DisplayName(peer.DisplayName)}: {who}", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PeerCallException or JsonException)
        {
            _logger.LogInformation("The foreign-device watch for peer {Peer} could not read the sessions ({Type})", Redact.Id(peer.PeerKey), ex.GetType().Name);
        }
    }

    private async Task AlertOnceAsync(PeerRun run, string kind, string text, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (run.LastAlert.TryGetValue(kind, out var last) && now - last < AlertInterval)
        {
            return;
        }

        run.LastAlert[kind] = now;
        await _admins.NotifyAdminsAsync("Shoal", text, cancellationToken).ConfigureAwait(false);
    }

    // DESIGN 4.1 "What Home checks": the device lock is confirmed when the share account may sign in from Home's device only; the session limit when it is 1.
    private static void ReadPolicy(PeerEndpoint peer, PeerRun run, JsonElement user)
    {
        if (!user.TryGetProperty("Policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var allDevices = policy.TryGetProperty("EnableAllDevices", out var all) && all.ValueKind == JsonValueKind.True;
        var devices = policy.TryGetProperty("EnabledDevices", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(static d => d.ValueKind == JsonValueKind.String).Select(static d => d.GetString() ?? string.Empty).ToList()
            : [];
        run.DeviceLock = !allDevices && devices.Count == 1 && string.Equals(devices[0], peer.DeviceId, StringComparison.OrdinalIgnoreCase) ? "confirmed" : "missing";
        run.SessionLimit = policy.TryGetProperty("MaxActiveSessions", out var limit) && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var sessions) && sessions == 1 ? "ok" : "notSet";
    }

    private sealed class PeerRun
    {
        public bool WasDown { get; set; }

        public DateTimeOffset LastWatch { get; set; }

        public DateTimeOffset LastAnchorLog { get; set; }

        public Guid? ShareUserId { get; set; }

        public DateTimeOffset? LastCheckUtc { get; set; }

        public double? RttMs { get; set; }

        public string? DeviceLock { get; set; }

        public string? SessionLimit { get; set; }

        public Dictionary<string, DateTimeOffset> LastAlert { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Runs <see cref="HealthMonitor"/> at startup and then every 60 seconds.
/// </summary>
public sealed class HealthMonitorHost : BackgroundService
{
    private readonly HealthMonitor _monitor;
    private readonly ILogger<HealthMonitorHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthMonitorHost"/> class.
    /// </summary>
    /// <param name="monitor">The monitor.</param>
    /// <param name="logger">The logger.</param>
    public HealthMonitorHost(HealthMonitor monitor, ILogger<HealthMonitorHost> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HealthMonitor.Interval);
        do
        {
            try
            {
                await _monitor.CheckAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // One bad round must never end the monitor; only the type is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning("A health round failed ({Type})", ex.GetType().Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Shows a client a "Peer is offline" message through core's sessions (DESIGN 5.7): at most one per user and peer per five minutes, only when
/// <c>NotifyClientsWhenOffline</c> is on.
/// </summary>
public sealed class ClientOfflineNotice : IOfflineNotice
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly ISessionManager _sessions;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientOfflineNotice> _logger;
    private readonly ConcurrentDictionary<(Guid User, string Peer), DateTimeOffset> _last = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientOfflineNotice"/> class.
    /// </summary>
    /// <param name="sessions">Core's session manager.</param>
    /// <param name="enabled">Whether clients are told (the setting).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ClientOfflineNotice(ISessionManager sessions, Func<bool> enabled, TimeProvider time, ILogger<ClientOfflineNotice> logger)
    {
        _sessions = sessions;
        _enabled = enabled;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public void PeerOffline(Guid userId, string peerKey, string peerName)
    {
        if (!_enabled())
        {
            return;
        }

        var now = _time.GetUtcNow();
        var due = false;
        _last.AddOrUpdate(
            (userId, peerKey),
            _ =>
            {
                due = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < Every)
                {
                    return last;
                }

                due = true;
                return now;
            });
        if (!due)
        {
            return;
        }

        var text = $"{MetadataSanitizer.DisplayName(peerName)} is offline right now. Try again later.";
        _ = SendAsync(userId, text);
    }

    private async Task SendAsync(Guid userId, string text)
    {
        try
        {
            foreach (var session in _sessions.Sessions.Where(s => s.UserId == userId).ToList())
            {
                await _sessions.SendMessageCommand(null, session.Id, new MessageCommand { Header = "Shoal", Text = text, TimeoutMs = 8000 }, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A message is a courtesy; its failure changes nothing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug("The offline message could not be shown ({Type})", ex.GetType().Name);
        }
    }
}

/// <summary>
/// What the health checks know about one peer.
/// </summary>
/// <param name="LastCheckUtc">When the last check ran.</param>
/// <param name="NextCheckUtc">When the next one is due.</param>
/// <param name="RttMs">The round trip of the identity call, or null.</param>
/// <param name="DeviceLock"><c>confirmed</c> or <c>missing</c> (read from the share account's policy), or null when not read yet.</param>
/// <param name="SessionLimit"><c>ok</c> or <c>notSet</c>, or null when not read yet.</param>
public sealed record PeerHealthSnapshot(DateTimeOffset LastCheckUtc, DateTimeOffset NextCheckUtc, double? RttMs, string? DeviceLock, string? SessionLimit);
