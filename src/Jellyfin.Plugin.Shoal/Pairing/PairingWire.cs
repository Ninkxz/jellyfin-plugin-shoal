#pragma warning disable SA1600 // Wire types mirror the members of Jellyfin DTOs; their names are the documentation.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// The Quick Connect state Jellyfin returns from <c>Initiate</c> and <c>Connect</c>. Only the members Home uses are read.
/// </summary>
internal sealed class QuickConnectWire
{
    public bool? Authenticated { get; set; }

    public string? Secret { get; set; }

    public string? Code { get; set; }
}

/// <summary>
/// The answer of <c>AuthenticateWithQuickConnect</c>.
/// </summary>
internal sealed class AuthenticationWire
{
    public string? AccessToken { get; set; }

    public string? ServerId { get; set; }

    public UserRefWire? User { get; set; }
}

/// <summary>
/// The user reference inside <see cref="AuthenticationWire"/>.
/// </summary>
internal sealed class UserRefWire
{
    public string? Id { get; set; }
}

/// <summary>
/// <c>GET /Users/Me</c>.
/// </summary>
internal sealed class UserWire
{
    public string? Id { get; set; }

    public PolicyWire? Policy { get; set; }
}

/// <summary>
/// The policy flags of the sharing account that Home looks at (DESIGN 4.1).
/// </summary>
internal sealed class PolicyWire
{
    public bool? IsAdministrator { get; set; }

    public bool? EnableAllFolders { get; set; }

    public bool? EnableContentDeletion { get; set; }

    public bool? EnableContentDownloading { get; set; }

    public bool? EnableLiveTvAccess { get; set; }

    public bool? EnableRemoteControlOfOtherUsers { get; set; }

    public bool? EnableAllDevices { get; set; }

    public int? MaxActiveSessions { get; set; }

    public bool? EnableUserPreferenceAccess { get; set; }

    public bool? EnableMediaConversion { get; set; }

    public bool? EnableSyncTranscoding { get; set; }

    public bool? EnablePublicSharing { get; set; }
}

/// <summary>
/// <c>GET /System/Info</c> (authenticated) and <c>/System/Info/Public</c>.
/// </summary>
internal sealed class SystemInfoWire
{
    public string? Id { get; set; }

    public string? ServerName { get; set; }

    public string? Version { get; set; }
}

/// <summary>
/// The format checks of DESIGN 4.2a "Response validation". A failure means the peer is not behaving like Jellyfin (<see cref="PairingErrorKind.BadPeerResponse"/>);
/// the offending value is never shown or logged.
/// </summary>
internal static partial class PairingValidation
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The check Home puts in its device name: 4 Crockford base32 characters from <see cref="RandomNumberGenerator"/>.</summary>
    /// <returns>For example <c>K7QM</c>.</returns>
    public static string NewCheck()
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CrockfordAlphabet[RandomNumberGenerator.GetInt32(CrockfordAlphabet.Length)];
        }

        return new string(chars);
    }

    public static bool IsCheck(string? value) => value is not null && CheckShape().IsMatch(value);

    public static bool IsCode(string? value) => value is not null && CodeShape().IsMatch(value);

    public static bool IsSecret(string? value) => value is not null && SecretShape().IsMatch(value);

    /// <summary>
    /// Checks a Quick Connect state. <paramref name="expectedSecret"/> is null for the answer of <c>Initiate</c> and the stored secret for polls.
    /// </summary>
    /// <param name="wire">The state.</param>
    /// <param name="expectedSecret">The secret the poll must repeat (ordinal comparison), or null.</param>
    /// <param name="expectedCode">The code the poll must repeat, or null.</param>
    /// <returns>True when the values have the right shape and repeat what they must.</returns>
    public static bool IsValid(QuickConnectWire? wire, string? expectedSecret, string? expectedCode) =>
        wire is not null
        && IsCode(wire.Code)
        && IsSecret(wire.Secret)
        && (expectedSecret is null || string.Equals(wire.Secret, expectedSecret, StringComparison.Ordinal))
        && (expectedCode is null || string.Equals(wire.Code, expectedCode, StringComparison.Ordinal));

    /// <summary>
    /// Checks the authentication result against the pinned server id. The user id is compared with <c>/Users/Me</c> later.
    /// </summary>
    /// <param name="wire">The result.</param>
    /// <param name="pinnedServerId">The pinned server id (32 hex).</param>
    /// <param name="token">The access token when the result is <see cref="AuthRead.Ok"/>.</param>
    /// <param name="userId">The user id when the result is <see cref="AuthRead.Ok"/>.</param>
    /// <returns>Whether the values are well formed, and whether the server id is the pinned one.</returns>
    public static AuthRead ReadAuthentication(AuthenticationWire? wire, string pinnedServerId, out string token, out Guid userId)
    {
        token = string.Empty;
        userId = Guid.Empty;
        if (wire is null
            || !PeerCredential.IsValidToken(wire.AccessToken)
            || !TryGuid(wire.User?.Id, out userId)
            || !PeerTrust.TryNormalizeServerId(wire.ServerId, out var serverId))
        {
            return AuthRead.Malformed;
        }

        if (!string.Equals(serverId, pinnedServerId, StringComparison.Ordinal))
        {
            return AuthRead.ServerChanged;
        }

        token = wire.AccessToken!;
        return AuthRead.Ok;
    }

    public static bool TryGuid(string? value, out Guid guid)
    {
        guid = Guid.Empty;
        return value is not null && value.Length is 32 or 36 && Guid.TryParse(value, out guid);
    }

    [GeneratedRegex(@"\A[0-9A-HJKMNP-TV-Z]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CheckShape();

    [GeneratedRegex(@"\A[0-9]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretShape();
}

/// <summary>
/// Sorts the sharing account's policy into warnings and notes (DESIGN 4.1 "What Home checks"). The same function serves the pairing and the 60 s health check.
/// </summary>
internal static class AccountPolicyReport
{
    /// <summary>
    /// Evaluates a policy.
    /// </summary>
    /// <param name="policy">The policy of <c>/Users/Me</c>.</param>
    /// <param name="pairingConfirmed">True once the pairing is confirmed: only then is a session limit other than 1 a warning (the friend sets it in step 9).</param>
    /// <returns>The warnings (things the friend can fix) and the notes (things that can never clear).</returns>
    public static (IReadOnlyList<AccountWarning> Warnings, IReadOnlyList<AccountNote> Notes) Evaluate(PolicyWire policy, bool pairingConfirmed)
    {
        var warnings = new List<AccountWarning>();
        var notes = new List<AccountNote>();
        if (policy.EnableAllFolders == true)
        {
            warnings.Add(AccountWarning.AllLibraries);
        }

        if (policy.EnableContentDeletion == true)
        {
            warnings.Add(AccountWarning.ContentDeletion);
        }

        if (policy.EnableContentDownloading == true)
        {
            warnings.Add(AccountWarning.ContentDownloading);
        }

        if (policy.EnableLiveTvAccess == true)
        {
            warnings.Add(AccountWarning.LiveTv);
        }

        if (policy.EnableRemoteControlOfOtherUsers == true)
        {
            warnings.Add(AccountWarning.RemoteControl);
        }

        if (policy.EnableAllDevices == true)
        {
            warnings.Add(AccountWarning.AllDevices);
        }

        if (pairingConfirmed && policy.MaxActiveSessions is { } limit && limit != 1)
        {
            warnings.Add(AccountWarning.SessionLimit);
        }

        if (policy.EnableUserPreferenceAccess == true)
        {
            notes.Add(AccountNote.UserPreferenceAccess);
        }

        if (policy.EnableMediaConversion == true)
        {
            notes.Add(AccountNote.MediaConversion);
        }

        if (policy.EnableSyncTranscoding == true)
        {
            notes.Add(AccountNote.SyncTranscoding);
        }

        if (policy.EnablePublicSharing == true)
        {
            notes.Add(AccountNote.PublicSharing);
        }

        return (warnings, notes);
    }
}
