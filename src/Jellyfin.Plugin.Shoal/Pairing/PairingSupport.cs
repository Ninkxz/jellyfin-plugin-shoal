using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// Tells Home's own place in the pairing: its server id and name.
/// </summary>
public interface IHomeIdentity
{
    /// <summary>Gets the server id (32 hex characters).</summary>
    string ServerId { get; }

    /// <summary>Gets the server's friendly name.</summary>
    string ServerName { get; }
}

/// <summary>
/// Sends a pop-up to every administrator who is signed in (DESIGN 5.7 mechanism, 4.2a safety kinds).
/// </summary>
public interface IAdminNotifier
{
    /// <summary>
    /// Shows a message to the sessions of every administrator. Failures are swallowed: a missing pop-up must never break a pairing.
    /// </summary>
    /// <param name="header">The title.</param>
    /// <param name="text">The text (plain text).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the messages were handed to the sessions.</returns>
    Task NotifyAdminsAsync(string header, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells whether a pairing session is live for a peer. While one is, the peer's address is immutable (DESIGN 4.2a).
/// </summary>
public interface IPairingGate
{
    /// <summary>
    /// Whether a live pairing session works on <paramref name="peerKey"/>.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>True while the session lives.</returns>
    bool IsLive(string peerKey);

    /// <summary>
    /// Cancels the live pairing session of this peer, if there is one (removing the server ends its pairing).
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a session was cancelled.</returns>
    Task<bool> CancelAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// A pairing that stopped, as stored in <c>peers/&lt;peerKey&gt;/health.json</c> (DESIGN 4.2a). It survives a restart and a page reload
/// and clears only on Dismiss or on a successful pairing.
/// </summary>
public sealed class PairingAbortRecord
{
    /// <summary>Gets or sets why it stopped.</summary>
    [JsonPropertyName("kind")]
    public PairingErrorKind Kind { get; set; }

    /// <summary>Gets or sets how far it had come, for <see cref="PairingErrorKind.Interrupted"/>.</summary>
    [JsonPropertyName("phase")]
    public PairingPhase? Phase { get; set; }

    /// <summary>Gets or sets when it stopped (UTC).</summary>
    [JsonPropertyName("atUtc")]
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// The per-peer <c>health.json</c> (DESIGN 3.1). Only the durable pairing abort lives here for now; the health monitor adds its own members later.
/// </summary>
public sealed class PeerHealthStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        MaxDepth = 8,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerHealthStore"/> class.
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public PeerHealthStore(ShoalPaths paths, ILogger<PeerHealthStore> logger)
        : this(paths, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerHealthStore"/> class.
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    internal PeerHealthStore(ShoalPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Reads the stored abort of a peer.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>The record, or null when none is stored or the file cannot be read.</returns>
    public PairingAbortRecord? GetAbort(string peerKey)
    {
        lock (_lock)
        {
            return Read(peerKey)?.PairingAbort;
        }
    }

    /// <summary>
    /// Stores the abort of a peer (replacing an older one).
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <param name="record">The record.</param>
    public void SetAbort(string peerKey, PairingAbortRecord record)
    {
        lock (_lock)
        {
            var document = Read(peerKey) ?? new HealthDocument();
            document.PairingAbort = record;
            Write(peerKey, document);
        }
    }

    /// <summary>
    /// Removes the stored abort of a peer.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>True when one was stored.</returns>
    public bool ClearAbort(string peerKey)
    {
        lock (_lock)
        {
            var document = Read(peerKey);
            if (document?.PairingAbort is null)
            {
                return false;
            }

            document.PairingAbort = null;
            Write(peerKey, document);
            return true;
        }
    }

    /// <summary>
    /// Reads why the last token rotation of a peer failed.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>The record, or null when the last rotation did not fail.</returns>
    public RotationErrorRecord? GetRotationError(string peerKey)
    {
        lock (_lock)
        {
            return Read(peerKey)?.RotationError;
        }
    }

    /// <summary>
    /// Stores why a token rotation failed (a word, never a token).
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <param name="record">The record, or null to clear it.</param>
    public void SetRotationError(string peerKey, RotationErrorRecord? record)
    {
        lock (_lock)
        {
            var document = Read(peerKey) ?? new HealthDocument();
            if (document.RotationError is null && record is null)
            {
                return;
            }

            document.RotationError = record;
            Write(peerKey, document);
        }
    }

    private HealthDocument? Read(string peerKey)
    {
        var path = Path.Combine(_paths.PeerDir(peerKey), "health.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HealthDocument>(File.ReadAllBytes(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("health.json of peer {Peer} could not be read: {Error}", Redact.Id(peerKey), ex.GetType().Name);
            return null;
        }
    }

    private void Write(string peerKey, HealthDocument document)
    {
        var directory = _paths.PeerDir(peerKey);
        SecureDirectory.Ensure(directory);
        AtomicFile.Write(Path.Combine(directory, "health.json"), JsonSerializer.SerializeToUtf8Bytes(document, Options));
    }

    private sealed class HealthDocument
    {
        [JsonPropertyName("pairingAbort")]
        public PairingAbortRecord? PairingAbort { get; set; }

        [JsonPropertyName("rotationError")]
        public RotationErrorRecord? RotationError { get; set; }
    }
}

/// <summary>
/// Why the last token rotation of a peer failed (health.json): <c>rejected</c>, <c>lostResponse</c>, <c>badResponse</c> or <c>storeFailed</c>.
/// </summary>
public sealed class RotationErrorRecord
{
    /// <summary>Gets or sets the kind word.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets when it happened (UTC).</summary>
    [JsonPropertyName("atUtc")]
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// <see cref="IHomeIdentity"/> from the running server.
/// </summary>
public sealed class ServerHostIdentity : IHomeIdentity
{
    private readonly IServerApplicationHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerHostIdentity"/> class.
    /// </summary>
    /// <param name="host">The application host.</param>
    public ServerHostIdentity(IServerApplicationHost host) => _host = host;

    /// <inheritdoc />
    public string ServerId => _host.SystemId;

    /// <inheritdoc />
    public string ServerName => _host.FriendlyName;
}

/// <summary>
/// <see cref="IAdminNotifier"/> through <see cref="ISessionManager"/>: a <c>DisplayMessage</c> command to each session of an administrator.
/// </summary>
public sealed class SessionAdminNotifier : IAdminNotifier
{
    private const int TimeoutMs = 15000;

    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly ILogger<SessionAdminNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionAdminNotifier"/> class.
    /// </summary>
    /// <param name="sessions">The session manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public SessionAdminNotifier(ISessionManager sessions, IUserManager users, ILogger<SessionAdminNotifier> logger)
    {
        _sessions = sessions;
        _users = users;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAdminsAsync(string header, string text, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var session in _sessions.Sessions)
            {
                if (session.UserId == Guid.Empty || _users.GetUserById(session.UserId) is not { } user || !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
                {
                    continue;
                }

                var command = new MessageCommand { Header = header, Text = text, TimeoutMs = TimeoutMs };
                await _sessions.SendMessageCommand(null, session.Id, command, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A pop-up is a courtesy; its failure must never change the outcome of a pairing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("Could not show the pairing message to the administrators: {Error}", ex.GetType().Name);
        }
    }
}
