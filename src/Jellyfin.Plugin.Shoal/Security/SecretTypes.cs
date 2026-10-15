using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// Whether the secrets could be read (DESIGN 8.2).
/// </summary>
public enum SecretStoreStatus
{
    /// <summary>The store can be read and written (it may simply be empty).</summary>
    Ready,

    /// <summary>
    /// <c>secrets.enc</c> exists but cannot be decrypted (tampered, wrong key, key missing). The Home role refuses to use any
    /// peer, and the key is never regenerated: that would orphan the secrets.
    /// </summary>
    Unreadable
}

/// <summary>
/// Why the store is unreadable.
/// </summary>
public enum SecretUnreadableReason
{
    /// <summary>Not unreadable.</summary>
    None,

    /// <summary>The file exists but the key (<c>master.key</c>) does not.</summary>
    MasterKeyMissing,

    /// <summary>Authentication of the file failed: it was changed, or the key is not the one that wrote it.</summary>
    Tampered,

    /// <summary>The file decrypted but its content is not a secrets document, or it is not a Shoal secrets file.</summary>
    Corrupt,

    /// <summary>
    /// The key given in <c>JELLYFIN_SHOAL_KEY</c> or the file named by <c>JELLYFIN_SHOAL_KEY_FILE</c> is not exactly 32 random
    /// bytes as base64url or hex (or the key file cannot be read). There is no fallback to <c>master.key</c>.
    /// </summary>
    BadKey,

    /// <summary>Both <c>JELLYFIN_SHOAL_KEY</c> and <c>JELLYFIN_SHOAL_KEY_FILE</c> are set; startup is refused as ambiguous.</summary>
    AmbiguousKey,

    /// <summary>The key file exists but could not be unwrapped (for example a DPAPI blob of another user).</summary>
    KeyProtectionFailed,

    /// <summary>The data folder or a file could not be read.</summary>
    StorageUnavailable,

    /// <summary>This platform has no AES-GCM (a minimal container image); secrets cannot be protected, so none are stored.</summary>
    CryptoUnavailable
}

/// <summary>
/// Where the encryption key comes from (DESIGN 8.2).
/// </summary>
public enum KeySource
{
    /// <summary>No key has been needed or created yet.</summary>
    None,

    /// <summary>The environment variable <c>JELLYFIN_SHOAL_KEY</c> (no key file on disk).</summary>
    Environment,

    /// <summary>
    /// A file named by <c>JELLYFIN_SHOAL_KEY_FILE</c> (recommended: the key stays out of <c>docker inspect</c> and
    /// <c>/proc/&lt;pid&gt;/environ</c>; no <c>master.key</c> on disk).
    /// </summary>
    EnvironmentFile,

    /// <summary>A random key in <c>master.key</c>, wrapped with Windows DPAPI (scope <c>CurrentUser</c>).</summary>
    Dpapi,

    /// <summary>A random key in <c>master.key</c>, protected by file permissions (0600).</summary>
    KeyFile
}

/// <summary>
/// What the Settings screen shows about the secret store. Never contains a secret.
/// </summary>
public sealed class SecretStoreInfo
{
    /// <summary>Gets the status.</summary>
    public SecretStoreStatus Status { get; init; }

    /// <summary>Gets why the store is unreadable, or <see cref="SecretUnreadableReason.None"/>.</summary>
    public SecretUnreadableReason Reason { get; init; }

    /// <summary>Gets where the key comes from.</summary>
    public KeySource KeySource { get; init; }

    /// <summary>Gets the number of stored credentials, or 0 when unreadable.</summary>
    public int Entries { get; init; }

    /// <summary>
    /// Gets a warning when the data folder cannot protect its files ("treat secrets.enc and master.key as plaintext"), or null.
    /// </summary>
    public string? VolumeWarning { get; init; }

    /// <summary>
    /// Gets the peer keys whose entry exists but cannot be used while the rest of the store is fine (the entry was changed, or the
    /// peer's host was edited without a re-pair). Those peers are refused; the others keep working.
    /// </summary>
    public IReadOnlyList<string> RefusedPeerKeys { get; init; } = [];

    /// <summary>
    /// Gets a fixed sentence that tells the admin how to fix the problem (for example how to make a good key), or null. Never contains a value.
    /// </summary>
    public string? Hint { get; init; }
}

/// <summary>
/// The credential Home holds for one peer. The token is a secret: it is never logged, never put in a URL or an exception, and
/// never returned by an admin API. <see cref="ToString"/> and the debugger view do not show it.
/// </summary>
[DebuggerDisplay("PeerCredential [secret hidden]")]
public sealed partial class PeerCredential
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCredential"/> class.
    /// </summary>
    /// <param name="token">The access token of the share user on the peer.</param>
    /// <param name="deviceId">The device id Home presents to the peer (constant per pairing).</param>
    /// <param name="pairedUtc">When the pairing finished (UTC).</param>
    /// <param name="method">How the peer was paired.</param>
    public PeerCredential(string token, string deviceId, DateTime pairedUtc, PairingMethod method)
    {
        if (!IsValidToken(token))
        {
            throw new ArgumentException("The access token must be 32 lower-case hex characters.", nameof(token));
        }

        if (!IsValidDeviceId(deviceId))
        {
            throw new ArgumentException("The device id must look like shoal-<12 hex>-<8 hex>.", nameof(deviceId));
        }

        Token = token;
        DeviceId = deviceId;
        PairedUtc = pairedUtc;
        Method = method;
    }

    /// <summary>Gets the access token.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    public string Token { get; }

    /// <summary>Gets the device id.</summary>
    public string DeviceId { get; }

    /// <summary>Gets when the pairing finished (UTC).</summary>
    public DateTime PairedUtc { get; }

    /// <summary>Gets how the peer was paired.</summary>
    public PairingMethod Method { get; }

    /// <inheritdoc />
    public override string ToString() => "PeerCredential [secret hidden]";

    /// <summary>
    /// Whether <paramref name="value"/> is a Jellyfin access token as Home accepts it: 32 lower-case hex characters (a GUID in "N" form).
    /// </summary>
    /// <param name="value">The candidate.</param>
    /// <returns>True when it has the shape.</returns>
    public static bool IsValidToken(string? value) => value is not null && TokenShape().IsMatch(value);

    /// <summary>
    /// Whether <paramref name="value"/> is a device id as Home derives it: <c>shoal-&lt;12 hex&gt;-&lt;8 hex&gt;</c>.
    /// </summary>
    /// <param name="value">The candidate.</param>
    /// <returns>True when it has the shape.</returns>
    public static bool IsValidDeviceId(string? value) => value is not null && DeviceIdShape().IsMatch(value);

    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"\Ashoal-[0-9a-f]{12}-[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdShape();
}
