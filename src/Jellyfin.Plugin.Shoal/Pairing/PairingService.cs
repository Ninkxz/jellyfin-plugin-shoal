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

    /// <summary>Gets the deadlines of a poll and of the redeem (3 s).</summary>
    public PeerCallProfile PollProfile { get; init; } = PeerCallProfile.QuickConnect;

    /// <summary>Gets the deadlines of the other steps (3 s).</summary>
    public PeerCallProfile StepProfile { get; init; } = PeerCallProfile.QuickConnectStep;

    /// <summary>Gets how long the first poll may take to confirm the request before the pairing stops.</summary>
    public TimeSpan BaselineWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the local life of a code, from the moment <c>Initiate</c> is sent. Core expires it at 10 minutes on its own clock; the margin keeps a natural expiry from looking like a replacement.</summary>
    public TimeSpan LocalDeadline { get; init; } = TimeSpan.FromSeconds(570);

    /// <summary>Gets how long the peer may stay silent before the pairing stops as <see cref="PairingErrorKind.Network"/>.</summary>
    public TimeSpan NetworkAbortAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the number of failed polls in a row after which the session reads "waiting for the server".</summary>
    public int SilentAfterFailures { get; init; } = 3;

    /// <summary>Gets how long the admin has to confirm after approval.</summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets how long a probe handle lives.</summary>
    public TimeSpan ProbeLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the most pairings that may be started per server in <see cref="StartWindow"/>.</summary>
    public int StartLimit { get; init; } = 5;

    /// <summary>Gets the window of <see cref="StartLimit"/>.</summary>
    public TimeSpan StartWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets a replacement for the address check (tests that talk to a server on the loopback address); null uses <see cref="AddressPolicy.Validate"/>.</summary>
    internal Func<string?, PeerNetwork, bool, string?, AddressCheck>? AddressValidator { get; init; }
}

/// <summary>
/// The Home side of pairing a friend's stock Jellyfin by Quick Connect (DESIGN 4.2, 4.2a, 4.3; K5).
/// <para>
/// One live session at a time, started only by an explicit call (<see cref="BeginAsync"/>): nothing else, not a probe, a reload, a retry or a
/// restart, ever sends <c>QuickConnect/Initiate</c>. The session runs in the background and keeps polling without any page open. The token
/// is held in memory until <see cref="ConfirmAsync"/> says the check matched; only then is it stored, and until then nothing is saved
/// (no secret, no library, no sync). Every value the peer sends is validated before it is shown, stored or put in a header, and none of it,
/// nor the poll URL, the secret, the code or the token, is ever logged.
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

    private enum PollKind
    {
        Waiting,
        Approved,
        Invalid,
        NonJson,
        NotFound,
        Off,
        Unreachable
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

    /// <summary>
    /// Looks at a server before pairing (DESIGN 4.2 steps 1 and 2): checks the address, asks <c>/System/Info/Public</c> and returns a
    /// single-use handle (valid for 10 minutes) that <see cref="BeginAsync"/> needs. It never sends <c>QuickConnect/Initiate</c> and never
    /// sends a token.
    /// </summary>
    /// <param name="request">What the admin typed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result; a refused address or an unreachable server is a result, not an exception.</returns>
    public async Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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

            return new ProbeResult
            {
                Ok = true,
                ProbeHandle = handle,
                ServerName = name.Length == 0 ? null : name,
                Version = version.Length == 0 ? null : version,
                ServerIdPrefix = serverId[..8],
                LatencyMs = (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue),
                Address = address.BaseUri.AbsoluteUri.TrimEnd('/'),
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

    /// <summary>
    /// Starts a pairing: the admin's "Start pairing" click. Writes the peer record (a new one, or the existing one for a re-pair) with its
    /// <see cref="PairingProgress"/> and begins the session in the background.
    /// </summary>
    /// <param name="adminUserId">The local admin who clicked.</param>
    /// <param name="probeHandle">The single-use handle from <see cref="ProbeAsync"/>.</param>
    /// <param name="displayName">The name the admin gives the server.</param>
    /// <param name="method">The method; only Quick Connect is available here.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The session as it stands.</returns>
    /// <exception cref="PairingConflictException">A session is live, the handle is not valid, the secret store is unreadable, or too many pairings were started for this server.</exception>
    public async Task<PairingSessionInfo> BeginAsync(Guid adminUserId, string probeHandle, string displayName, PairingMethod method, CancellationToken cancellationToken = default)
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

            // The method must match what was probed: an invite starts only from a probe that carried one, Quick Connect only from one that did not.
            if (method != (probe.InviteSecret is null ? PairingMethod.Qc : PairingMethod.Invite))
            {
                throw new PairingConflictException(PairingConflict.MethodUnavailable);
            }

            if (probe.InviteSecret is not null && probe.Address.IsCleartext && probe.Network != PeerNetwork.Tailnet)
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
            return _run?.Snapshot();
        }
    }

    /// <summary>
    /// "The codes match" (<paramref name="match"/> true) or "They don't match" (false). Legal only while the session waits for it
    /// (DESIGN 4.2a): nothing is stored without this call.
    /// </summary>
    /// <param name="match">Whether the check in the friend's Devices list matched.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The session after it has finished.</returns>
    /// <exception cref="PairingConflictException">The session is not waiting for the confirmation.</exception>
    public async Task<PairingSessionInfo> ConfirmAsync(bool match, CancellationToken cancellationToken = default)
    {
        Run run;
        lock (_lock)
        {
            run = _run is { IsLive: true } live && live.Step == PairingStep.AwaitingConfirm ? live : throw new PairingConflictException(PairingConflict.NotAwaitingConfirm);
            if (!run.TryDecide(match))
            {
                throw new PairingConflictException(PairingConflict.NotAwaitingConfirm);
            }
        }

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return run.Snapshot();
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

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return;
        }

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
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly string _deviceId;
        private readonly string _check;
        private readonly DateTime _startedUtc;
        private readonly string _displayName;
        private string? _secret;
        private string? _code;
        private string? _token;
        private Guid _tokenUserId;
        private DateTime? _expiresUtc;
        private bool _silent;
        private bool _shuttingDown;
        private PairingErrorKind? _error;
        private PairingPhase? _errorPhase;
        private int? _sessionLimit;
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
            _check = PairingValidation.NewCheck();
            _deviceId = service.DeviceIdFor(peerKey);
        }

        public string PeerKey { get; }

        public bool FirstPairing { get; }

        public bool IsLive => !_finished;

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

                    peer.PendingPairing = new PairingProgress { Phase = PairingPhase.BeforeApproval, StartedUtc = _startedUtc, StartedByUserId = _adminId, Method = Method };
                },
                cancellationToken).ConfigureAwait(false);
        }

        public void Start() => _ = Task.Run(ExecuteAsync);

        public PairingSessionInfo Snapshot()
        {
            lock (_gate)
            {
                var showCode = !_finished && _step is PairingStep.AwaitingApproval or PairingStep.Redeeming or PairingStep.Validating or PairingStep.AwaitingConfirm;
                return new PairingSessionInfo
                {
                    Method = Method,
                    PeerHandle = PeerKey,
                    Step = _step,
                    State = _confirmed ? "confirmed" : _error is not null ? "aborted" : "running",
                    Code = showCode ? _code : null,
                    SafetyCode = showCode ? (Method == PairingMethod.Invite ? _sas : _check) : null,
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

        public bool TryDecide(bool match) => _step == PairingStep.AwaitingConfirm && !_finished && _decision.TrySetResult(match);

        public void Cancel() => _cts.Cancel();

        public void Shutdown()
        {
            _shuttingDown = true;
            _cts.Cancel();
        }

        private PeerEndpoint Unauthenticated() =>
            PeerEndpoint.WithoutToken(PeerKey, _displayName, _probe.Address.BaseUri, _deviceId, _check).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);

        private PeerEndpoint Authenticated() =>
            new PeerEndpoint(PeerKey, _displayName, _probe.Address.BaseUri, _deviceId, _token!, _check).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);

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
                await RunCoreAsync().ConfigureAwait(false);
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

        private async Task RunCoreAsync()
        {
            if (Method == PairingMethod.Invite)
            {
                await RunInviteAsync().ConfigureAwait(false);
                return;
            }

            var now = _service._time;
            await PreflightAsync().ConfigureAwait(false);
            await ReadSessionLimitAsync().ConfigureAwait(false);

            // Initiating. The local deadline starts when the request is sent.
            SetStep(PairingStep.Initiating);
            var deadline = now.GetUtcNow() + _service._options.LocalDeadline;
            await InitiateAsync().ConfigureAwait(false);

            // Baseline: the first poll must confirm the request before anything is shown.
            SetStep(PairingStep.GettingCode);
            await BaselineAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _expiresUtc = deadline.UtcDateTime;
                _step = PairingStep.AwaitingApproval;
            }

            var minted = await AwaitApprovalAsync(deadline).ConfigureAwait(false);
            _token = minted.Token;
            _tokenUserId = minted.UserId;
            _secret = null;

            await ValidateAsync().ConfigureAwait(false);
            await AwaitConfirmAsync().ConfigureAwait(false);
        }

        private async Task<PeerResponse> CallAsync(PeerEndpoint endpoint, PeerRequest request, PeerCallProfile profile) =>
            await _service._client.SendAsync(endpoint, request.WithTrust(TrustRequirement.None).WithoutBreaker(), profile, _cts.Token).ConfigureAwait(false);

        private async Task PreflightAsync()
        {
            SetStep(PairingStep.Preflight);
            try
            {
                using (var identity = await CallAsync(Unauthenticated(), PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info).ConfigureAwait(false))
                {
                    if (identity.StatusCode != HttpStatusCode.OK)
                    {
                        throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                    }

                    var info = await identity.ReadJsonAsync<SystemInfoWire>(_cts.Token).ConfigureAwait(false);
                    if (!PeerTrust.TryNormalizeServerId(info.Id, out var id))
                    {
                        throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                    }

                    if (!string.Equals(id, _probe.ServerId, StringComparison.Ordinal))
                    {
                        throw new AbortException(PairingErrorKind.ServerChanged);
                    }
                }

                using var enabled = await CallAsync(Unauthenticated(), PeerRequest.GetAnonymous("/QuickConnect/Enabled"), _service._options.StepProfile).ConfigureAwait(false);
                if (enabled.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new AbortException(PairingErrorKind.QuickConnectOff);
                }

                if (enabled.StatusCode != HttpStatusCode.OK)
                {
                    throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                }

                var text = (await enabled.ReadUtf8Async(_cts.Token).ConfigureAwait(false)).Trim();
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AbortException(PairingErrorKind.QuickConnectOff); // no Initiate is sent
                }

                if (!string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                }
            }
            catch (PeerCallException)
            {
                throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
            }
        }

        private async Task InitiateAsync()
        {
            try
            {
                using var response = await CallAsync(Unauthenticated(), PeerRequest.Post("/QuickConnect/Initiate", null, authenticated: false), _service._options.StepProfile).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new AbortException(PairingErrorKind.QuickConnectOff);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                }

                var wire = await response.ReadJsonAsync<QuickConnectWire>(_cts.Token).ConfigureAwait(false);
                if (!PairingValidation.IsValid(wire, null, null) || wire.Authenticated == true)
                {
                    throw new AbortException(PairingErrorKind.BadPeerResponse);
                }

                _secret = wire.Secret;
                _code = wire.Code;
            }
            catch (PeerCallException ex)
            {
                throw new AbortException(ex.Kind is PeerFailureKind.BadResponse or PeerFailureKind.TooLarge ? PairingErrorKind.BadPeerResponse : PairingErrorKind.QuickConnectUnreachable);
            }
        }

        private async Task<(PollKind Kind, bool Authenticated)> PollOnceAsync()
        {
            _lastPollStart = _service._time.GetUtcNow();
            try
            {
                using var response = await CallAsync(Unauthenticated(), PeerRequest.GetAnonymous("/QuickConnect/Connect?secret=" + _secret), _service._options.PollProfile).ConfigureAwait(false);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var wire = await response.ReadJsonAsync<QuickConnectWire>(_cts.Token).ConfigureAwait(false);
                        if (!PairingValidation.IsValid(wire, _secret, _code))
                        {
                            return (PollKind.Invalid, false);
                        }

                        return wire.Authenticated == true ? (PollKind.Approved, true) : (PollKind.Waiting, false);
                    case HttpStatusCode.NotFound:
                        return (PollKind.NotFound, false);
                    case HttpStatusCode.Unauthorized:
                        return (PollKind.Off, false);
                    default:
                        return ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests ? PollKind.Unreachable : PollKind.Invalid, false);
                }
            }
            catch (PeerCallException ex)
            {
                return ex.Kind is PeerFailureKind.Redirect or PeerFailureKind.BadResponse or PeerFailureKind.TooLarge ? (PollKind.NonJson, false) : (PollKind.Unreachable, false);
            }
        }

        private async Task BaselineAsync()
        {
            var now = _service._time;
            var end = now.GetUtcNow() + _service._options.BaselineWindow;
            while (true)
            {
                var started = now.GetUtcNow();
                var (kind, _) = await PollOnceAsync().ConfigureAwait(false);
                switch (kind)
                {
                    case PollKind.Waiting:
                        return;
                    case PollKind.NotFound:
                    case PollKind.NonJson:
                        throw new AbortException(PairingErrorKind.QuickConnectUnreachable); // never "replaced": nothing was confirmed yet
                    case PollKind.Off:
                        throw new AbortException(PairingErrorKind.QuickConnectOff);
                    case PollKind.Approved:
                    case PollKind.Invalid:
                        throw new AbortException(PairingErrorKind.BadPeerResponse);
                    default:
                        if (now.GetUtcNow() >= end)
                        {
                            throw new AbortException(PairingErrorKind.QuickConnectUnreachable);
                        }

                        break;
                }

                await DelayUntilAsync(started + _service._options.PollInterval).ConfigureAwait(false);
            }
        }

        // Rule (b) of 4.2a: a re-pair that runs out unapproved while the friend's account shows a session limit is probably the limit.
        private AbortException DeadlineReached() =>
            !FirstPairing && _sessionLimit is >= 1
                ? new AbortException(PairingErrorKind.LikelySessionLimit, PairingPhase.BeforeApproval)
                : new AbortException(PairingErrorKind.Expired);

        // The Start-time reading of the account (W6 L-1): one GET /Users/Me with the old token of a re-pair. A dead token, a refusal or any
        // failure leaves the reading unknown. Called only after Preflight proved the server id, so the token never goes to a host that failed the pin.
        private async Task ReadSessionLimitAsync()
        {
            if (FirstPairing || _service._secrets.TryGet(PeerKey) is not { } old)
            {
                return;
            }

            try
            {
                var endpoint = new PeerEndpoint(PeerKey, _displayName, _probe.Address.BaseUri, old.DeviceId, old.Token, _check).Configured(_probe.Network, _probe.ProxyUrl, _probe.LanAck, _probe.ServerId, _probe.TlsPin);
                using var me = await CallAsync(endpoint, PeerRequest.Get("/Users/Me"), PeerCallProfile.Info).ConfigureAwait(false);
                if (me.StatusCode == HttpStatusCode.OK && (await me.ReadJsonAsync<UserWire>(_cts.Token).ConfigureAwait(false)).Policy?.MaxActiveSessions is { } limit)
                {
                    _sessionLimit = limit;
                }
            }
            catch (PeerCallException)
            {
                // unknown: the abort then says "expired"
            }
        }

        private async Task<(string Token, Guid UserId)> AwaitApprovalAsync(DateTimeOffset deadline)
        {
            var now = _service._time;
            var failures = 0;
            var lastGood = now.GetUtcNow();
            while (true)
            {
                if (now.GetUtcNow() >= deadline)
                {
                    throw DeadlineReached();
                }

                // One poll every PollInterval, counted from the start of the previous poll (the baseline poll counts as the first).
                await DelayUntilAsync(_lastPollStart + _service._options.PollInterval).ConfigureAwait(false);
                if (now.GetUtcNow() >= deadline)
                {
                    throw DeadlineReached();
                }

                var (kind, _) = await PollOnceAsync().ConfigureAwait(false);
                switch (kind)
                {
                    case PollKind.Waiting:
                        failures = 0;
                        lastGood = now.GetUtcNow();
                        SetSilent(false);
                        break;
                    case PollKind.Approved:
                        SetStep(PairingStep.Redeeming);
                        return await RedeemAsync(afterApproval: true).ConfigureAwait(false) ?? throw new AbortException(PairingErrorKind.Server);
                    case PollKind.Invalid:
                    case PollKind.NonJson:
                        throw new AbortException(PairingErrorKind.BadPeerResponse);
                    case PollKind.Off:
                        throw new AbortException(PairingErrorKind.QuickConnectOff);
                    case PollKind.NotFound:
                        if (now.GetUtcNow() >= deadline)
                        {
                            throw DeadlineReached();
                        }

                        // The entry is gone: another Initiate drew the same code, the Peer restarted, or its clock expired it early.
                        // One redeem attempt tells "approved before the replacement" from "replaced".
                        SetStep(PairingStep.Redeeming);
                        var late = await RedeemAsync(afterApproval: false).ConfigureAwait(false);
                        return late ?? throw new AbortException(PairingErrorKind.RequestReplaced);
                    default:
                        failures++;
                        if (failures >= _service._options.SilentAfterFailures)
                        {
                            SetSilent(true);
                        }

                        if (now.GetUtcNow() - lastGood > _service._options.NetworkAbortAfter)
                        {
                            throw new AbortException(PairingErrorKind.Network);
                        }

                        break;
                }
            }
        }

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

        // One AuthenticateWithQuickConnect attempt. After an approval the answers 401, 403 and 404 are what a session limit looks like from
        // outside (the reason is only in the Peer's log), so they map to LikelySessionLimit without ever looking at message text. In the
        // "entry vanished" path any failure means the request was replaced, and null says so.
        private async Task<(string Token, Guid UserId)?> RedeemAsync(bool afterApproval)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["Secret"] = _secret! });
            try
            {
                using var response = await CallAsync(Unauthenticated(), PeerRequest.Post("/Users/AuthenticateWithQuickConnect", body, authenticated: false), _service._options.PollProfile).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if (!afterApproval)
                    {
                        return null;
                    }

                    throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                        ? new AbortException(PairingErrorKind.LikelySessionLimit, PairingPhase.AfterApproval) // rule (a): they approved, then their server refused
                        : new AbortException(PairingErrorKind.Network);
                }

                var wire = await response.ReadJsonAsync<AuthenticationWire>(_cts.Token).ConfigureAwait(false);
                switch (PairingValidation.ReadAuthentication(wire, _probe.ServerId, out var token, out var userId))
                {
                    case AuthRead.Ok:
                        return (token, userId);
                    case AuthRead.ServerChanged: // the result names another server: reported as a malformed answer (T-PAIR-14)
                    default:
                        throw new AbortException(PairingErrorKind.BadPeerResponse);
                }
            }
            catch (PeerCallException ex)
            {
                if (ex.Kind is PeerFailureKind.BadResponse or PeerFailureKind.TooLarge)
                {
                    throw new AbortException(PairingErrorKind.BadPeerResponse);
                }

                return afterApproval ? throw new AbortException(PairingErrorKind.Network) : null;
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
                    if (!PairingValidation.TryGuid(user.Id, out var meId) || (Method == PairingMethod.Qc && meId != _tokenUserId) || user.Policy?.IsAdministrator is null)
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

        private async Task AwaitConfirmAsync()
        {
            lock (_gate)
            {
                _expiresUtc = (_service._time.GetUtcNow() + _service._options.ConfirmWindow).UtcDateTime;
                _step = PairingStep.AwaitingConfirm;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var timeout = Task.Delay(_service._options.ConfirmWindow, _service._time, timeoutCts.Token);
            var winner = await Task.WhenAny(_decision.Task, timeout).ConfigureAwait(false);
            if (winner != _decision.Task)
            {
                _cts.Token.ThrowIfCancellationRequested();
                throw new AbortException(PairingErrorKind.Expired);
            }

            await timeoutCts.CancelAsync().ConfigureAwait(false);
            if (!await _decision.Task.ConfigureAwait(false))
            {
                throw new AbortException(PairingErrorKind.CodesDidNotMatch);
            }

            await FinalizeAsync().ConfigureAwait(false);
        }

        private async Task FinalizeAsync()
        {
            var service = _service;
            var credential = new PeerCredential(_token!, _deviceId, service._time.GetUtcNow().UtcDateTime, Method);
            try
            {
                await service._secrets.PutAsync(PeerKey, credential, CancellationToken.None).ConfigureAwait(false);
                await service._state.UpdateAsync(
                    state =>
                    {
                        var peer = state.Home.Peers.First(p => string.Equals(p.PeerKey, PeerKey, StringComparison.Ordinal));
                        peer.Enabled = true;
                        peer.PinnedServerId = _probe.ServerId;
                        peer.Pairing = new PairingInfo { Method = Method, CheckString = _check, PairedUtc = credential.PairedUtc, PairedByUserId = _adminId };
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
                _code = null;
                _confirmed = true;
                _expiresUtc = null;
                _warnings = warnings;
                _notes = notes;
                _step = PairingStep.Confirmed;
                _finished = true;
            }

            service._logger.LogInformation("Paired with peer {Peer} by {Method}", Redact.Id(PeerKey), Method);
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
                _secret = null;
                _code = null;
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
            service._logger.LogWarning("The pairing with peer {Peer} stopped: {Kind}", Redact.Id(PeerKey), kind);
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
                _secret = null;
                _code = null;
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
/// The fixed texts of the pop-up for the five safety kinds. Only the admin-typed server name is included, cleaned.
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
            PairingErrorKind.RequestReplaced => name + ": your pairing request was replaced (or the server restarted). Tell your friend NOT to enter the code. If they already did, they should delete the sharing account and you start again with a new one.",
            PairingErrorKind.CodesDidNotMatch => name + ": the codes did not match. Your friend should delete the sharing account and you start again with a new one.",
            PairingErrorKind.ServerChanged => name + ": the server that answered is not the one you checked. Nothing was saved.",
            PairingErrorKind.AdminAccount => name + ": your friend approved an administrator account. Home refused it and signed it out.",
            PairingErrorKind.BadPeerResponse => name + ": the server sent an answer that Jellyfin would not send. Nothing was saved.",
            _ => name + ": the pairing stopped."
        };
    }
}
