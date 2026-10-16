using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// The time limits of a pairing (DESIGN 4.2a, 6.9, 6.10). The defaults are the designed values; tests scale them down.
/// </summary>
public sealed class PairingOptions
{
    /// <summary>Gets the time between two polls (measured from the start of one poll to the start of the next).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the deadlines of the other steps (3 s).</summary>
    public PeerCallProfile StepProfile { get; init; } = PeerCallProfile.PairStep;

    /// <summary>Gets how long the peer may stay silent before the pairing stops as <see cref="PairingErrorKind.Network"/>.</summary>
    public TimeSpan NetworkAbortAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the number of failed polls in a row after which the session reads "waiting for the server".</summary>
    public int SilentAfterFailures { get; init; } = 3;

    /// <summary>Gets how long a probe handle lives.</summary>
    public TimeSpan ProbeLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long a confirmed pairing stays on the page as the last session before it ends by itself (sooner once libraries were chosen for the server).</summary>
    public TimeSpan ConfirmedLife { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the most pairings that may be started per server in <see cref="StartWindow"/>.</summary>
    public int StartLimit { get; init; } = 5;

    /// <summary>Gets the window of <see cref="StartLimit"/>.</summary>
    public TimeSpan StartWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets a replacement for the address check (tests that talk to a server on the loopback address); null uses <see cref="AddressPolicy.Validate"/>.</summary>
    internal Func<string?, PeerNetwork, bool, string?, AddressCheck>? AddressValidator { get; init; }
}

/// <summary>
/// The Home side of pairing a friend's Shoal server by invite (DESIGN 4.1b, 4.2a; D17).
/// <para>
/// One live session at a time, started only by an explicit call (<see cref="BeginAsync"/>): nothing else, not a probe, a reload, a retry or a
/// restart, ever starts a pairing. The session runs in the background and keeps polling without any page open. The token
/// is held in memory until the friend's admin has approved the safety code; only then is it stored, and until then nothing is saved
/// (no secret, no library, no sync). Every value the peer sends is validated before it is shown, stored or put in a header, and none of it,
/// nor the invite, the safety code or the token, is ever logged.
/// </para>
/// Aborts are durable (<c>health.json</c>) and split into safety kinds (pop-up to every admin) and stopped kinds (no pop-up).
/// </summary>
public sealed partial class PairingService : IPairingGate, IHostedService, IDisposable
{
    private const int MaxProbeHandles = 16;

    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly PeerHealthStore _health;
    private readonly IHomeIdentity _home;
    private readonly IAdminNotifier _notifier;
    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly PairingOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProbeEntry> _probes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _starts = new(StringComparer.Ordinal);

    private Run? _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table of peers.</param>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="home">Home's identity.</param>
    /// <param name="notifier">The admin pop-ups.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public PairingService(
        IStateStore state,
        ISecretStore secrets,
        PeerClient client,
        PeerRegistry registry,
        PeerHealthStore health,
        IHomeIdentity home,
        IAdminNotifier notifier,
        ShoalPaths paths,
        ILogger<PairingService> logger)
        : this(state, secrets, client, registry, health, home, notifier, paths, logger, TimeProvider.System, new PairingOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingService"/> class (tests: own clock and time limits).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="registry">The runtime table of peers.</param>
    /// <param name="health">The per-peer health files.</param>
    /// <param name="home">Home's identity.</param>
    /// <param name="notifier">The admin pop-ups.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The time limits.</param>
    internal PairingService(
        IStateStore state,
        ISecretStore secrets,
        PeerClient client,
        PeerRegistry registry,
        PeerHealthStore health,
        IHomeIdentity home,
        IAdminNotifier notifier,
        ShoalPaths paths,
        ILogger logger,
        TimeProvider time,
        PairingOptions options)
    {
        _state = state;
        _secrets = secrets;
        _client = client;
        _registry = registry;
        _health = health;
        _home = home;
        _notifier = notifier;
        _paths = paths;
        _logger = logger;
        _time = time;
        _options = options;
    }

    /// <summary>
    /// Gets the task of the current session, for tests that wait for it to finish.
    /// </summary>
    internal Task? CurrentCompletion
    {
        get
        {
            lock (_lock)
            {
                return _run?.Completion;
            }
        }
    }

    /// <inheritdoc />
    public bool IsLive(string peerKey)
    {
        lock (_lock)
        {
            return _run is { IsLive: true } run && string.Equals(run.PeerKey, peerKey, StringComparison.Ordinal);
        }
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(string peerKey, CancellationToken cancellationToken = default) => IsLive(peerKey) ? CancelAsync(cancellationToken) : Task.FromResult(false);

    /// <summary>
    /// Looks at a server before pairing (DESIGN 4.2 steps 1 and 2): checks the address, asks <c>/System/Info/Public</c> and returns a
    /// single-use handle (valid for 10 minutes) that <see cref="BeginAsync"/> needs. It never starts a pairing and never
    /// sends a token.
    /// </summary>
    /// <param name="request">What the admin typed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result; a refused address or an unreachable server is a result, not an exception.</returns>
    public async Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ProbeCoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            // The reason is a fixed word (never the address, the invite or a peer's text), so a refused check leaves a trace.
            _logger.LogInformation("A pairing check was refused: {Reason} (invite given: {HasInvite})", result.Failure ?? result.AddressProblem.ToString(), !string.IsNullOrWhiteSpace(request.Invite));
        }

        return result;
    }

    /// <summary>
    /// Starts a pairing: the admin's "Start pairing" click. Writes the peer record (a new one, or the existing one for a re-pair) with its
    /// <see cref="PairingProgress"/> and begins the session in the background.
    /// </summary>
    /// <param name="adminUserId">The local admin who clicked.</param>
    /// <param name="probeHandle">The single-use handle from <see cref="ProbeAsync"/>.</param>
    /// <param name="displayName">The name the admin gives the server.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The session as it stands.</returns>
    /// <exception cref="PairingConflictException">A session is live, the handle is not valid, the secret store is unreadable, or too many pairings were started for this server.</exception>
    public async Task<PairingSessionInfo> BeginAsync(Guid adminUserId, string probeHandle, string displayName, CancellationToken cancellationToken = default)
    {
        var name = TextClean.Display(displayName, 64);
        if (name.Length == 0)
        {
            throw new PairingConflictException(PairingConflict.InvalidRequest);
        }

        Run run;
        lock (_lock)
        {
            if (_run is { IsLive: true })
            {
                throw new PairingConflictException(PairingConflict.SessionActive);
            }

            PurgeProbes();
            if (probeHandle is null || !_probes.TryGetValue(probeHandle, out var probe))
            {
                throw new PairingConflictException(PairingConflict.ProbeExpired);
            }

            if (probe.ExistingPeerKey is not null && (FindPeer(probe.ExistingPeerKey) is not { } current || !string.Equals(current.BaseUrl, probe.ExistingBaseUrl, StringComparison.Ordinal)))
            {
                _probes.Remove(probeHandle); // the peer's address changed after the probe (W6 L-3): the probe no longer describes it
                throw new PairingConflictException(PairingConflict.ProbeExpired);
            }

            // Every pairing is by invite (D17): a probe that carried no invite can be checked but cannot start one.
            if (probe.InviteSecret is null)
            {
                throw new PairingConflictException(PairingConflict.MethodUnavailable);
            }

            if (probe.Address.IsCleartext && probe.Network != PeerNetwork.Tailnet)
            {
                throw new PairingConflictException(PairingConflict.MethodUnavailable); // checked again here: the probe could have come from anywhere
            }

            if (_secrets.Status != SecretStoreStatus.Ready)
            {
                throw new PairingConflictException(PairingConflict.SecretsUnreadable);
            }

            var serverKey = probe.Address.Host + ":" + probe.Address.BaseUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var now = _time.GetUtcNow();
            if (!_starts.TryGetValue(serverKey, out var starts))
            {
                starts = [];
                _starts[serverKey] = starts;
            }

            starts.RemoveAll(t => now - t >= _options.StartWindow);
            if (starts.Count >= _options.StartLimit)
            {
                var wait = (starts[0] + _options.StartWindow) - now;
                throw new PairingConflictException(PairingConflict.RateLimited, (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds)));
            }

            starts.Add(now);
            _probes.Remove(probeHandle); // single use

            var peerKey = probe.ExistingPeerKey ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            run = new Run(this, peerKey, name, probe, adminUserId, firstPairing: probe.ExistingPeerKey is null || !HasCredential(probe.ExistingPeerKey), now.UtcDateTime);
            _run = run;
        }

        try
        {
            await run.PersistStartAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever failed, the half-started session must be dropped so the admin can click again.
        catch (Exception)
#pragma warning restore CA1031
        {
            lock (_lock)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }

            throw;
        }

        _client.Forget(run.PeerKey);
        _registry.SetStatus(run.PeerKey, PeerStatus.Pairing);
        run.Start();
        return run.Snapshot();
    }

    /// <summary>
    /// Gets the current session (live, or the last one until the next start), or null.
    /// </summary>
    /// <returns>The session as it stands.</returns>
    public PairingSessionInfo? GetSession()
    {
        lock (_lock)
        {
            // A pairing that is done ends by itself: once its libraries were chosen, or two minutes after it was confirmed, the page is not told "still in progress" any more.
            if (_run is { IsConfirmed: true } done && done.ConfirmedUtc is { } at
                && (_time.GetUtcNow().UtcDateTime - at >= _options.ConfirmedLife || (FindPeer(done.PeerKey)?.Libraries.Count ?? 0) > done.LibrariesAtConfirm))
            {
                _run = null;
                return null;
            }

            return _run?.Snapshot();
        }
    }

    /// <summary>
    /// Cancels the live session (the admin's Cancel). Nothing is stored: a first pairing's record is removed, a re-pair goes back to its previous state, and a held token is signed out.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a live session was cancelled.</returns>
    public async Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        Run run;
        lock (_lock)
        {
            if (_run is not { IsLive: true } live)
            {
                _run = null;
                return false;
            }

            run = live;
        }

        run.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (ReferenceEquals(_run, run))
            {
                _run = null;
            }
        }

        return true;
    }

    /// <summary>
    /// Clears a stored abort (DESIGN 4.2a, 9.6 <c>DismissPairing</c>). A peer that never finished a pairing is removed (it has no secret, library or stub
    /// folder yet); a re-paired peer goes back to its previous state and its old token is judged by the next health check.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="PairingConflictException">No abort is stored for that peer, or a pairing is live for it.</exception>
    public async Task<DismissResult> DismissPairingAsync(string peerKey, CancellationToken cancellationToken = default)
    {
        if (!ShoalPaths.IsValidPeerKey(peerKey))
        {
            throw new PairingConflictException(PairingConflict.UnknownPeer);
        }

        if (IsLive(peerKey))
        {
            throw new PairingConflictException(PairingConflict.NothingToDismiss);
        }

        var peer = FindPeer(peerKey) ?? throw new PairingConflictException(PairingConflict.UnknownPeer);
        if (_health.GetAbort(peerKey) is null)
        {
            throw new PairingConflictException(PairingConflict.NothingToDismiss);
        }

        var neverPaired = !HasCredential(peerKey) && peer.Pairing is null && peer.Libraries.Count == 0;
        if (neverPaired)
        {
            await _state.UpdateAsync(state => state.Home.Peers.RemoveAll(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)), cancellationToken).ConfigureAwait(false);
            DeletePeerFolder(peerKey);
            _registry.Forget(peerKey);
            _client.Forget(peerKey);
            return new DismissResult(Removed: true, Restored: false, Status: null);
        }

        _health.ClearAbort(peerKey);
        _registry.SetStatus(peerKey, PeerStatus.Offline);
        return new DismissResult(Removed: false, Restored: true, PeerStatus.Offline);
    }

    /// <summary>
    /// At startup: a peer record that still carries a pairing in progress means Home restarted in the middle. It becomes
    /// <c>PairingAborted(interrupted, phase)</c> (DESIGN 4.2a), and the runtime status of every peer with a stored abort is set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the records are updated.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Recovery must never stop the server from starting; the error type is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError("Shoal could not check for interrupted pairings: {Error}", ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Run? run;
        lock (_lock)
        {
            run = _run;
        }

        run?.Shutdown(); // the pending record stays on disk: the next start reports it as interrupted
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _run?.Shutdown();
        }
    }

    private static string DeviceIdFor(string homeServerId, string peerKey) =>
        "shoal-" + homeServerId[..12] + "-" + peerKey[..8];

    private static string Hex12Of(string value)
    {
        if (PeerTrust.TryNormalizeServerId(value, out var normalized))
        {
            return normalized;
        }

        // A server id that is not a GUID (it should be): derive a stable 32-hex string from it.
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..32];
    }

    private string DeviceIdFor(string peerKey) => DeviceIdFor(Hex12Of(_home.ServerId), peerKey);

    private PeerState? FindPeer(string peerKey) =>
        _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal));

    private bool HasCredential(string peerKey) => _secrets.PeerKeys?.Contains(peerKey) == true;

    private void PurgeProbes()
    {
        var now = _time.GetUtcNow();
        foreach (var expired in _probes.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToList())
        {
            _probes.Remove(expired);
        }
    }

    private void DeletePeerFolder(string peerKey)
    {
        try
        {
            var directory = _paths.PeerDir(peerKey);
            if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, "catalog.json")))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the folder of a dismissed peer {Peer}: {Error}", Redact.Id(peerKey), ex.GetType().Name);
        }
    }

    private async Task<ProbeResult> ProbeCoreAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        var url = request.Url;
        var network = request.Network;
        var proxy = request.ProxyUrl;
        var ack = request.LanAck;
        string? inviteSecret = null;
        if (!string.IsNullOrWhiteSpace(request.Invite))
        {
            // An invite names the server it is for (the first 8 hex of its id); a malformed one is refused before any request is made.
            if (!Jellyfin.Plugin.Shoal.Sharing.InviteCrypto.TryParse(request.Invite, out _, out inviteSecret))
            {
                return new ProbeResult { Ok = false, Failure = "inviteInvalid" };
            }
        }

        PeerState? existing = null;
        if (!string.IsNullOrEmpty(request.PeerHandle))
        {
            existing = FindPeer(request.PeerHandle);
            if (existing is null)
            {
                return new ProbeResult { Ok = false, Failure = "unknownPeer" };
            }

            // The stored address is used: while a pairing lives, and for a re-pair, the address does not change.
            (url, network, proxy, ack) = (existing.BaseUrl, existing.Network, existing.ProxyUrl, existing.LanCleartextAck);
        }

        var check = (_options.AddressValidator ?? AddressPolicy.Validate)(url, network, ack, proxy);
        if (!check.Ok)
        {
            return new ProbeResult { Ok = false, AddressProblem = check.Problem };
        }

        var address = check.Address!;

        // An invite's secret and the token that comes back travel over this address: cleartext is only for a Tailnet (DESIGN 4.2 step 1; Warden M2).
        if (inviteSecret is not null && address.IsCleartext && network != PeerNetwork.Tailnet)
        {
            return new ProbeResult { Ok = false, Failure = "inviteLanHttp" };
        }

        var probeKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var endpoint = PeerEndpoint.WithoutToken(probeKey, "probe", address.BaseUri, DeviceIdFor(probeKey), null).Configured(network, proxy, ack, null, existing?.TlsSpkiPin);
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await _client.SendAsync(endpoint, PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new ProbeResult { Ok = false, Failure = "unreachable" };
            }

            var info = await response.ReadJsonAsync<SystemInfoWire>(cancellationToken).ConfigureAwait(false);
            if (!PeerTrust.TryNormalizeServerId(info.Id, out var serverId))
            {
                return new ProbeResult { Ok = false, Failure = "notJellyfin" };
            }

            if (inviteSecret is not null && Jellyfin.Plugin.Shoal.Sharing.InviteCrypto.TryParse(request.Invite, out var invitePrefix, out _) && !string.Equals(invitePrefix, serverId[..8], StringComparison.Ordinal))
            {
                return new ProbeResult { Ok = false, Failure = "inviteWrongServer" };
            }

            var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var name = TextClean.Display(info.ServerName, 64);
            var version = TextClean.Display(info.Version, 32);
            lock (_lock)
            {
                PurgeProbes();
                if (_probes.Count >= MaxProbeHandles)
                {
                    var oldest = _probes.OrderBy(p => p.Value.ExpiresUtc).First().Key;
                    _probes.Remove(oldest);
                }

                _probes[handle] = new ProbeEntry(address, network, proxy, ack, serverId, existing?.PeerKey, _time.GetUtcNow() + _options.ProbeLifetime, existing?.TlsSpkiPin, existing?.BaseUrl, inviteSecret);
            }

            var ping = await ShoalPing.DetectAsync(_client, endpoint, cancellationToken).ConfigureAwait(false);
            return new ProbeResult
            {
                Ok = true,
                ProbeHandle = handle,
                ServerName = name.Length == 0 ? null : name,
                Version = version.Length == 0 ? null : version,
                ServerIdPrefix = serverId[..8],
                LatencyMs = (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue),
                Address = address.BaseUri.AbsoluteUri.TrimEnd('/'),
                HasShoal = ping == PingOutcome.Yes,
                ShoalPingThrottled = ping == PingOutcome.Throttled,
                AlsoWatchesYou = _state.Status == StateStatus.Ready && Jellyfin.Plugin.Shoal.Sharing.FriendLinks.AlsoWatchesYou(_state.Current, serverId)
            };
        }
        catch (PeerCallException ex) when (ex.Kind is PeerFailureKind.BadResponse or PeerFailureKind.TooLarge)
        {
            return new ProbeResult { Ok = false, Failure = "notJellyfin" };
        }
        catch (PeerCallException)
        {
            return new ProbeResult { Ok = false, Failure = "unreachable" };
        }
        finally
        {
            _client.Forget(probeKey);
        }
    }

    // D17: Quick Connect pairing is gone. What an old state still holds is cleaned up at start, silently. A server that was paired that way is paused (it keeps its
    // libraries, stubs and catalog; the titles stop playing like those of any paused server) until the admin pairs it again by invite. A pairing that was in flight
    // the old way is dropped, and a stopped one with a legacy reason is cleared; a server that never finished pairing and holds no key is removed as Dismiss does.
    private async Task MigrateLegacyAsync(CancellationToken cancellationToken)
    {
        if (_secrets.Status != SecretStoreStatus.Ready)
        {
            return; // the keys cannot be read, so "never paired" cannot be told: nothing is removed or paused now, the next start tries again
        }

        var legacyKinds = new[] { PairingErrorKind.RequestReplaced, PairingErrorKind.CodesDidNotMatch, PairingErrorKind.QuickConnectOff, PairingErrorKind.QuickConnectUnreachable, PairingErrorKind.LikelySessionLimit };
        var pause = new List<string>();
        var drop = new List<string>();
        var clearPending = new List<string>();
        foreach (var peer in _state.Current.Home.Peers)
        {
            var paired = HasCredential(peer.PeerKey);
            var legacyPending = peer.PendingPairing is { Method: PairingMethod.Qc };
            var legacyAbort = _health.GetAbort(peer.PeerKey) is { } abort && legacyKinds.Contains(abort.Kind);
            if (legacyPending || legacyAbort)
            {
                if (legacyAbort)
                {
                    _health.ClearAbort(peer.PeerKey);
                }

                if (legacyPending)
                {
                    clearPending.Add(peer.PeerKey);
                }

                if (!paired && peer.Pairing is null && peer.Libraries.Count == 0)
                {
                    drop.Add(peer.PeerKey); // the test Dismiss uses: no pairing, no mapping, no key
                    continue;
                }
            }

            if (peer.Pairing?.Method == PairingMethod.Qc && peer.Enabled)
            {
                pause.Add(peer.PeerKey);
            }
        }

        if (pause.Count == 0 && drop.Count == 0 && clearPending.Count == 0)
        {
            return;
        }

        await _state.UpdateAsync(
            state =>
            {
                state.Home.Peers.RemoveAll(p => drop.Contains(p.PeerKey));
                foreach (var peer in state.Home.Peers)
                {
                    if (clearPending.Contains(peer.PeerKey))
                    {
                        peer.PendingPairing = null;
                    }

                    if (pause.Contains(peer.PeerKey))
                    {
                        peer.Enabled = false;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var key in drop)
        {
            DeletePeerFolder(key);
            _registry.Forget(key);
        }

        foreach (var key in pause)
        {
            await SignOutLegacyAsync(key).ConfigureAwait(false);
            _logger.LogWarning("Shoal paused server {Peer}: it was paired the old way (Quick Connect, no longer supported). Pair it again with an invite; its libraries and titles stay", Redact.Id(key));
        }
    }

    // The old Quick Connect session stays on the friend's server until it is ended: sign it out once, best effort (3 s, never fatal), as the server is paused.
    private async Task SignOutLegacyAsync(string peerKey)
    {
        try
        {
            if (FindPeer(peerKey) is not { } peer || _secrets.TryGet(peerKey) is not { } credential || !Uri.TryCreate(peer.BaseUrl, UriKind.Absolute, out var uri))
            {
                return;
            }

            var endpoint = new PeerEndpoint(peerKey, peer.DisplayName, uri, credential.DeviceId, credential.Token).Configured(peer.Network, peer.ProxyUrl, peer.LanCleartextAck, peer.PinnedServerId, peer.TlsSpkiPin);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _client.SendAsync(endpoint, PeerRequest.Post("/Sessions/Logout", null).WithTrust(TrustRequirement.None).WithoutBreaker(), _options.StepProfile, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the friend can delete the old sharing account.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing to do.
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return;
        }

        await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);

        var interrupted = _state.Current.Home.Peers.Where(p => p.PendingPairing is not null).Select(p => (p.PeerKey, Phase: p.PendingPairing!.Phase)).ToList();
        if (interrupted.Count > 0)
        {
            foreach (var (peerKey, phase) in interrupted)
            {
                _health.SetAbort(peerKey, new PairingAbortRecord { Kind = PairingErrorKind.Interrupted, Phase = phase, AtUtc = _time.GetUtcNow().UtcDateTime });
                _logger.LogWarning("A pairing with peer {Peer} was interrupted by a restart ({Phase})", Redact.Id(peerKey), phase);
            }

            await _state.UpdateAsync(
                state =>
                {
                    foreach (var peer in state.Home.Peers)
                    {
                        peer.PendingPairing = null;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var peer in _state.Current.Home.Peers)
        {
            if (_health.GetAbort(peer.PeerKey) is not null)
            {
                _registry.SetStatus(peer.PeerKey, HasCredential(peer.PeerKey) ? PeerStatus.Offline : PeerStatus.PairingAborted);
            }
        }
    }

    private sealed record ProbeEntry(PeerAddress Address, PeerNetwork Network, string? ProxyUrl, bool LanAck, string ServerId, string? ExistingPeerKey, DateTimeOffset ExpiresUtc, string? TlsPin = null, string? ExistingBaseUrl = null, string? InviteSecret = null);

#pragma warning disable CA1032, CA1064 // A private control-flow exception that never leaves this class.
    private sealed class AbortException : Exception
    {
        public AbortException(PairingErrorKind kind, PairingPhase? phase = null)
            : base("The pairing stopped (" + kind + ").")
        {
            Kind = kind;
            Phase = phase;
        }

        public PairingErrorKind Kind { get; }

        public PairingPhase? Phase { get; }
    }
#pragma warning restore CA1032, CA1064

#pragma warning disable CA1001 // The token source lives as long as the session and is released with it.
    private sealed partial class Run
    {
        private readonly PairingService _service;
        private readonly ProbeEntry _probe;
        private readonly Guid _adminId;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly string _deviceId;
        private readonly DateTime _startedUtc;
        private readonly string _displayName;
        private string? _token;
        private DateTime? _expiresUtc;
        private DateTime? _confirmedUtc;
        private bool _silent;
        private bool _shuttingDown;
        private PairingErrorKind? _error;
        private PairingPhase? _errorPhase;
        private IReadOnlyList<AccountWarning> _warnings = [];
        private IReadOnlyList<AccountNote> _notes = [];
        private PolicyWire _policy = new();
        private DateTimeOffset _lastPollStart;
        private volatile PairingStep _step = PairingStep.Preflight;
        private volatile bool _finished;
        private volatile bool _confirmed;

        public Run(PairingService service, string peerKey, string displayName, ProbeEntry probe, Guid adminId, bool firstPairing, DateTime startedUtc)
        {
            _service = service;
            PeerKey = peerKey;
            _displayName = displayName;
            _probe = probe;
            _adminId = adminId;
            FirstPairing = firstPairing;
            _startedUtc = startedUtc;
            _expiresUtc = startedUtc + Jellyfin.Plugin.Shoal.Sharing.InviteBook.InviteLife; // a deadline from the first moment: Home own window, shortened when the Peer says how long is left
            _deviceId = service.DeviceIdFor(peerKey);
        }

        public string PeerKey { get; }

        public bool FirstPairing { get; }

        public bool IsLive => !_finished;

        public bool IsConfirmed => _confirmed;

        public int LibrariesAtConfirm { get; private set; }

        public DateTime? ConfirmedUtc
        {
            get
            {
                lock (_gate)
                {
                    return _confirmedUtc;
                }
            }
        }

        public PairingStep Step => _step;

        public Task Completion => _completion.Task;

        public async Task PersistStartAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_service._paths.PeersDir);
            SecureDirectory.Ensure(_service._paths.PeerDir(PeerKey));
            await _service._state.UpdateAsync(
                state =>
                {
                    var peer = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal));
                    if (peer is null)
                    {
                        peer = new PeerState
                        {
                            PeerKey = PeerKey,
                            DisplayName = _displayName,
                            BaseUrl = _probe.Address.BaseUri.AbsoluteUri.TrimEnd('/'),
                            Network = _probe.Network,
                            ProxyUrl = _probe.ProxyUrl,
                            LanCleartextAck = _probe.LanAck,
                            PinnedServerId = _probe.ServerId,
                            Enabled = false
                        };
                        state.Home.Peers.Add(peer);
                    }

                    peer.PendingPairing = new PairingProgress { Phase = PairingPhase.BeforeApproval, StartedUtc = _startedUtc, StartedByUserId = _adminId, Method = PairingMethod.Invite };
                },
                cancellationToken).ConfigureAwait(false);
        }

        public void Start() => _ = Task.Run(ExecuteAsync);

        public PairingSessionInfo Snapshot()
        {
            lock (_gate)
            {
                var showCode = !_finished && _step is PairingStep.AwaitingApproval or PairingStep.Validating;
                return new PairingSessionInfo
                {
                    Method = PairingMethod.Invite,
                    PeerHandle = PeerKey,
                    Step = _step,
                    State = _confirmed ? "confirmed" : _error is not null ? "aborted" : "running",
                    SafetyCode = showCode ? _sas : null,
                    ExpiresUtc = _expiresUtc,
                    Error = _error,
                    Phase = _errorPhase,
                    PeerSilent = _silent && !_finished,
                    Paired = !FirstPairing,
                    AccountWarnings = _warnings,
                    AccountNotes = _notes,
                    StartedUtc = _startedUtc
                };
            }
        }

        public void Cancel() => _cts.Cancel();

        public void Shutdown()
        {
            _shuttingDown = true;
            _cts.Cancel();
        }

        private PeerEndpoint Unauthenticated() =>
            PeerEndpoint.WithoutToken(PeerKey, _displayName, _probe.Address.BaseUri, _deviceId, null).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);

        private PeerEndpoint Authenticated() =>
            new PeerEndpoint(PeerKey, _displayName, _probe.Address.BaseUri, _deviceId, _token!).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);

        private void SetStep(PairingStep step)
        {
            lock (_gate)
            {
                _step = step;
            }
        }

        private async Task ExecuteAsync()
        {
            try
            {
                await RunInviteAsync().ConfigureAwait(false);
            }
            catch (AbortException abort)
            {
                await AbortAsync(abort.Kind, abort.Phase).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                await CancelledAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The session must always end in a known state; only the exception type is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _service._logger.LogError("A pairing with peer {Peer} failed unexpectedly: {Error}", Redact.Id(PeerKey), ex.GetType().Name);
                await AbortAsync(PairingErrorKind.Server, null).ConfigureAwait(false);
            }
            finally
            {
                _finished = true;
                Complete();
            }
        }

        private void Complete() => _completion.TrySetResult();

        private async Task<PeerResponse> CallAsync(PeerEndpoint endpoint, PeerRequest request, PeerCallProfile profile) =>
            await _service._client.SendAsync(endpoint, request.WithTrust(TrustRequirement.None).WithoutBreaker(), profile, _cts.Token).ConfigureAwait(false);

        private void SetSilent(bool value)
        {
            lock (_gate)
            {
                _silent = value;
            }
        }

        private async Task DelayUntilAsync(DateTimeOffset moment)
        {
            var wait = moment - _service._time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _service._time, _cts.Token).ConfigureAwait(false);
            }
        }

        private async Task ValidateAsync()
        {
            SetStep(PairingStep.Validating);
            PolicyWire policy;
            try
            {
                using (var me = await CallAsync(Authenticated(), PeerRequest.Get("/Users/Me"), PeerCallProfile.Info).ConfigureAwait(false))
                {
                    if (me.StatusCode != HttpStatusCode.OK)
                    {
                        throw new AbortException((int)me.StatusCode >= 500 ? PairingErrorKind.Network : PairingErrorKind.BadPeerResponse);
                    }

                    var user = await me.ReadJsonAsync<UserWire>(_cts.Token).ConfigureAwait(false);
                    if (!PairingValidation.TryGuid(user.Id, out var meId) || user.Policy?.IsAdministrator is null)
                    {
                        throw new AbortException(PairingErrorKind.BadPeerResponse);
                    }

                    policy = user.Policy;
                }

                if (policy.IsAdministrator == true)
                {
                    throw new AbortException(PairingErrorKind.AdminAccount); // the abort signs the token out
                }

                using var info = await CallAsync(Authenticated(), PeerRequest.Get("/System/Info"), PeerCallProfile.Info).ConfigureAwait(false);
                if (info.StatusCode != HttpStatusCode.OK)
                {
                    throw new AbortException((int)info.StatusCode >= 500 ? PairingErrorKind.Network : PairingErrorKind.BadPeerResponse);
                }

                var system = await info.ReadJsonAsync<SystemInfoWire>(_cts.Token).ConfigureAwait(false);
                if (!PeerTrust.TryNormalizeServerId(system.Id, out var serverId))
                {
                    throw new AbortException(PairingErrorKind.BadPeerResponse);
                }

                if (!string.Equals(serverId, _probe.ServerId, StringComparison.Ordinal))
                {
                    throw new AbortException(PairingErrorKind.ServerChanged);
                }
            }
            catch (PeerCallException ex)
            {
                throw new AbortException(ex.Kind is PeerFailureKind.BadResponse or PeerFailureKind.TooLarge ? PairingErrorKind.BadPeerResponse : PairingErrorKind.Network);
            }

            _policy = policy;
            var (warnings, notes) = AccountPolicyReport.Evaluate(policy, pairingConfirmed: false);
            await _service._state.UpdateAsync(
                state =>
                {
                    if (state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal)) is { PendingPairing: { } pending })
                    {
                        pending.Phase = PairingPhase.AwaitingConfirm;
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _warnings = warnings;
                _notes = notes;
            }
        }

        private async Task FinalizeAsync()
        {
            var service = _service;
            var credential = new PeerCredential(_token!, _deviceId, service._time.GetUtcNow().UtcDateTime, PairingMethod.Invite);
            await LogoutOldQuickConnectTokenAsync().ConfigureAwait(false);
            try
            {
                await service._secrets.PutAsync(PeerKey, credential, CancellationToken.None).ConfigureAwait(false);
                await service._state.UpdateAsync(
                    state =>
                    {
                        var peer = state.Home.Peers.First(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal));
                        peer.Enabled = true;
                        peer.PinnedServerId = _probe.ServerId;
                        peer.Pairing = new PairingInfo { Method = PairingMethod.Invite, PairedUtc = credential.PairedUtc, PairedByUserId = _adminId };
                        peer.PendingPairing = null;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Whatever failed, the pairing did not finish; the token is signed out and the admin can try again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                service._logger.LogError("Could not store the pairing with peer {Peer}: {Error}", Redact.Id(PeerKey), ex.GetType().Name);
                throw new AbortException(PairingErrorKind.Server);
            }

            service._health.ClearAbort(PeerKey);
            service._registry.SetStatus(PeerKey, PeerStatus.Offline);
            service._registry.RecordPinVerified(PeerKey);
            service._registry.RecordAuthVerified(PeerKey);
            var (warnings, notes) = AccountPolicyReport.Evaluate(_policy, pairingConfirmed: true);
            lock (_gate)
            {
                _token = null;
                _confirmed = true;
                _confirmedUtc = service._time.GetUtcNow().UtcDateTime;
                LibrariesAtConfirm = service.FindPeer(PeerKey)?.Libraries.Count ?? 0;
                _expiresUtc = null;
                _warnings = warnings;
                _notes = notes;
                _step = PairingStep.Confirmed;
                _finished = true;
            }

            service._logger.LogInformation("Paired with peer {Peer} by {Method}", Redact.Id(PeerKey), PairingMethod.Invite);
        }

        // A server that was paired the old way (Quick Connect) still holds that session on the friend's server. Just before the new credential replaces it, Home signs the old
        // token out, best effort: 3 seconds, and whatever happens never stops the pairing (the friend can delete the old sharing account).
        private async Task LogoutOldQuickConnectTokenAsync()
        {
            if (FirstPairing || _service._secrets.TryGet(PeerKey) is not { Method: PairingMethod.Qc } old)
            {
                return;
            }

            try
            {
                var endpoint = new PeerEndpoint(PeerKey, _displayName, _probe.Address.BaseUri, old.DeviceId, old.Token).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await _service._client.SendAsync(endpoint, PeerRequest.Post("/Sessions/Logout", null).WithTrust(TrustRequirement.None).WithoutBreaker(), _service._options.StepProfile, timeout.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort: the old token is replaced whether or not the friend's server heard us.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Nothing to do.
            }
        }

        private async Task LogoutTokenAsync()
        {
            var token = _token;
            if (token is null)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await _service._client.SendAsync(Authenticated(), PeerRequest.Post("/Sessions/Logout", null).WithTrust(TrustRequirement.None).WithoutBreaker(), _service._options.StepProfile, timeout.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort: the token is discarded whether or not the peer heard us.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Nothing to do: the sharing account can be deleted by the friend (DESIGN 4.5).
            }
        }

        private async Task AbortAsync(PairingErrorKind kind, PairingPhase? phase)
        {
            if (_finished && _error is not null)
            {
                return;
            }

            var service = _service;
            if (_shuttingDown)
            {
                return; // the pending record stays: the next start reports it as interrupted
            }

            await LogoutTokenAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _token = null;
                _expiresUtc = null;
                _error = kind;
                _errorPhase = phase;
                _step = PairingStep.Aborted;
                _finished = true;
            }

            try
            {
                service._health.SetAbort(PeerKey, new PairingAbortRecord { Kind = kind, Phase = phase, AtUtc = service._time.GetUtcNow().UtcDateTime });
                await service._state.UpdateAsync(
                    state =>
                    {
                        if (state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal)) is { } peer)
                        {
                            peer.PendingPairing = null;
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The abort is still reported in memory; only the type is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                service._logger.LogError("Could not record that the pairing with peer {Peer} stopped: {Error}", Redact.Id(PeerKey), ex.GetType().Name);
            }

            service._registry.SetStatus(PeerKey, FirstPairing ? PeerStatus.PairingAborted : PeerStatus.Offline);
            service._logger.LogInformation("The pairing with peer {Peer} ended without a credential: {Kind} (phase {Phase}, {Method})", Redact.Id(PeerKey), kind, phase?.ToString() ?? "unknown", PairingMethod.Invite);
            if (PairingErrors.IsSafety(kind))
            {
                await service._notifier.NotifyAdminsAsync("Pairing stopped for your safety", PairingMessages.SafetyText(kind, _displayName)).ConfigureAwait(false);
            }
        }

        private async Task CancelledAsync()
        {
            if (_shuttingDown)
            {
                return;
            }

            var service = _service;
            await LogoutTokenAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _token = null;
                _finished = true;
            }

            try
            {
                await service._state.UpdateAsync(
                    state =>
                    {
                        if (FirstPairing)
                        {
                            state.Home.Peers.RemoveAll(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal));
                        }
                        else if (state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal)) is { } peer)
                        {
                            peer.PendingPairing = null;
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Only the type is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                service._logger.LogError("Could not clean up the cancelled pairing with peer {Peer}: {Error}", Redact.Id(PeerKey), ex.GetType().Name);
            }

            if (FirstPairing)
            {
                service.DeletePeerFolder(PeerKey);
                service._registry.Forget(PeerKey);
                service._client.Forget(PeerKey);
            }
            else
            {
                service._registry.SetStatus(PeerKey, PeerStatus.Offline);
            }
        }
    }
}

/// <summary>
/// The fixed texts of the pop-up for the three safety kinds. Only the admin-typed server name is included, cleaned.
/// </summary>
internal static class PairingMessages
{
    /// <summary>
    /// The text of the pop-up.
    /// </summary>
    /// <param name="kind">A safety kind.</param>
    /// <param name="serverName">The server name the admin typed.</param>
    /// <returns>Plain text.</returns>
    public static string SafetyText(PairingErrorKind kind, string serverName)
    {
        var name = Redact.Peer(serverName);
        return kind switch
        {
            PairingErrorKind.ServerChanged => name + ": the server that answered is not the one you checked. Nothing was saved.",
            PairingErrorKind.AdminAccount => name + ": your friend approved an administrator account. Home refused it and signed it out.",
            PairingErrorKind.BadPeerResponse => name + ": the server sent an answer that Jellyfin would not send. Nothing was saved.",
            _ => name + ": the pairing stopped."
        };
    }
}
