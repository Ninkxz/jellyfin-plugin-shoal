#pragma warning disable SA1600 // Wire types mirror the members of Jellyfin DTOs; their names are the documentation.
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;

namespace Jellyfin.Plugin.Shoal.Pairing;

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
    public static bool TryGuid(string? value, out Guid guid)
    {
        guid = Guid.Empty;
        return value is not null && value.Length is 32 or 36 && Guid.TryParse(value, out guid);
    }
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
