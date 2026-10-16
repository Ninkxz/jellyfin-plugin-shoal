using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// Which of the two groups a <see cref="PairingErrorKind"/> belongs to.
/// </summary>
public static class PairingErrors
{
    /// <summary>
    /// Whether the kind is a safety kind (shield card, pop-up to every admin).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for the three safety kinds.</returns>
    public static bool IsSafety(PairingErrorKind kind) =>
        kind is PairingErrorKind.ServerChanged or PairingErrorKind.AdminAccount or PairingErrorKind.BadPeerResponse;
}

/// <summary>
/// The pairing session as the admin API shows it (DESIGN 9.5). It never contains the secret, the token or a peer GUID; only the 6-digit code
/// and the safety code, which the admin reads out.
/// </summary>
public sealed class PairingSessionInfo
{
    /// <summary>Gets the direction; always <c>watch</c> here.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "watch";

    /// <summary>Gets the method.</summary>
    [JsonPropertyName("method")]
    public PairingMethod Method { get; init; }

    /// <summary>Gets the handle of the peer this session pairs.</summary>
    [JsonPropertyName("peerHandle")]
    public string PeerHandle { get; init; } = string.Empty;

    /// <summary>Gets where the session is.</summary>
    [JsonPropertyName("step")]
    public PairingStep Step { get; init; }

    /// <summary>Gets <c>running</c>, <c>confirmed</c> or <c>aborted</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = "running";

    /// <summary>Gets the safety code (8 characters) the friend types on their server. Present only while the pairing waits for the friend, and only until it ends.</summary>
    [JsonPropertyName("safetyCode")]
    public string? SafetyCode { get; init; }

    /// <summary>Gets the end of the invite's life.</summary>
    [JsonPropertyName("expiresUtc")]
    public DateTime? ExpiresUtc { get; init; }

    /// <summary>Gets why the session stopped, or null.</summary>
    [JsonPropertyName("error")]
    public PairingErrorKind? Error { get; init; }

    /// <summary>Gets how far the pairing had come when it stopped as <see cref="PairingErrorKind.Interrupted"/>, or null.</summary>
    [JsonPropertyName("phase")]
    public PairingPhase? Phase { get; init; }

    /// <summary>Gets a value indicating whether the peer has missed three polls in a row ("waiting for the server").</summary>
    [JsonPropertyName("peerSilent")]
    public bool PeerSilent { get; init; }

    /// <summary>Gets a value indicating whether this is a re-pair (the server already had a working pairing): "ask your friend to set the session limit to 0 until you're done".</summary>
    [JsonPropertyName("paired")]
    public bool Paired { get; init; }

    /// <summary>Gets what Home found wrong with the account (after validation).</summary>
    [JsonPropertyName("accountWarnings")]
    public IReadOnlyList<AccountWarning> AccountWarnings { get; init; } = [];

    /// <summary>Gets the informational notes about the account (after validation).</summary>
    [JsonPropertyName("accountNotes")]
    public IReadOnlyList<AccountNote> AccountNotes { get; init; } = [];

    /// <summary>Gets when the admin started the session.</summary>
    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; init; }
}

/// <summary>
/// The admin's request to look at a server before pairing (DESIGN 9.6 <c>POST Peers/Probe</c>).
/// </summary>
public sealed class ProbeRequest
{
    /// <summary>Gets or sets the server address.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Gets or sets the network type.</summary>
    [JsonPropertyName("network")]
    public PeerNetwork Network { get; set; } = PeerNetwork.Tailnet;

    /// <summary>Gets or sets the optional loopback SOCKS5 proxy (Tailnet only).</summary>
    [JsonPropertyName("proxyUrl")]
    public string? ProxyUrl { get; set; }

    /// <summary>Gets or sets the invite the friend sent (invite pairing only). It is checked against the server it names and kept in memory with the probe.</summary>
    [JsonPropertyName("invite")]
    public string? Invite { get; set; }

    /// <summary>Gets or sets a value indicating whether the admin accepted plain http on the LAN.</summary>
    [JsonPropertyName("lanAck")]
    public bool LanAck { get; set; }

    /// <summary>
    /// Gets or sets the handle of an existing peer to pair again (a re-pair, or a retry of a stopped first pairing). Its stored address,
    /// network and proxy are used and the other fields are ignored: while a pairing lives the address is immutable.
    /// </summary>
    [JsonPropertyName("peerHandle")]
    public string? PeerHandle { get; set; }
}

/// <summary>
/// What the probe found out.
/// </summary>
public sealed class ProbeResult
{
    /// <summary>Gets a value indicating whether the server was reached and answered like Jellyfin.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Gets what the address policy found wrong, when it refused the address.</summary>
    [JsonPropertyName("addressProblem")]
    public AddressProblem AddressProblem { get; init; }

    /// <summary>Gets a fixed reason when the address was fine but the server could not be used (<c>unreachable</c>, <c>notJellyfin</c>, <c>unknownPeer</c>), or null.</summary>
    [JsonPropertyName("failure")]
    public string? Failure { get; init; }

    /// <summary>Gets the single-use handle (valid for 10 minutes) that starts the pairing.</summary>
    [JsonPropertyName("probeHandle")]
    public string? ProbeHandle { get; init; }

    /// <summary>Gets the server name, cleaned (text only).</summary>
    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    /// <summary>Gets the server version, cleaned.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Gets the first 8 hex characters of the server id ("detects a wrong or reset server; not proof of identity").</summary>
    [JsonPropertyName("serverIdPrefix")]
    public string? ServerIdPrefix { get; init; }

    /// <summary>Gets how long the identity request took.</summary>
    [JsonPropertyName("latencyMs")]
    public int LatencyMs { get; init; }

    /// <summary>Gets the normalized address.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    /// <summary>Gets a value indicating whether a friend with this server's id prefix already watches this server (DESIGN v2.6.2; display only, never an access decision).</summary>
    [JsonPropertyName("alsoWatchesYou")]
    public bool AlsoWatchesYou { get; init; }

    /// <summary>Gets a value indicating whether the probed server answered Shoal's anonymous ping (pairing v2; a hint for the admin, never trusted).</summary>
    [JsonPropertyName("hasShoal")]
    public bool HasShoal { get; init; }

    /// <summary>Gets a value indicating whether the probed server's ping limit answered "later", so whether Shoal is there is not known (distinct from "no Shoal").</summary>
    [JsonPropertyName("shoalPingThrottled")]
    public bool ShoalPingThrottled { get; init; }
}

/// <summary>
/// A pairing call was refused. The message is a fixed text.
/// </summary>
public sealed class PairingConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PairingConflictException"/> class.
    /// </summary>
    public PairingConflictException()
        : this(PairingConflict.InvalidRequest)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingConflictException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public PairingConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingConflictException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public PairingConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingConflictException"/> class.
    /// </summary>
    /// <param name="conflict">Why the call was refused.</param>
    /// <param name="retryAfterSeconds">For <see cref="PairingConflict.RateLimited"/>, the <c>Retry-After</c> value.</param>
    public PairingConflictException(PairingConflict conflict, int retryAfterSeconds = 0)
        : base("The pairing call was refused (" + conflict + ").")
    {
        Conflict = conflict;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Gets why the call was refused.</summary>
    public PairingConflict Conflict { get; }

    /// <summary>Gets the seconds to wait for <see cref="PairingConflict.RateLimited"/>.</summary>
    public int RetryAfterSeconds { get; }
}

/// <summary>
/// The result of <c>PairingService.DismissPairingAsync</c>.
/// </summary>
/// <param name="Removed">True when a never-paired peer's record was removed.</param>
/// <param name="Restored">True when a re-paired peer went back to its previous state.</param>
/// <param name="Status">For a restored peer, the status it resumes with.</param>
public sealed record DismissResult(bool Removed, bool Restored, PeerStatus? Status);
