using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>
/// Why a pairing stopped (DESIGN 4.2a, 9.5). The first ones that say "Safety" are <b>safety</b> kinds: something may have been wrong or hostile, the
/// Overview says "Pairing stopped for your safety" and every admin gets a pop-up. All others are <b>stopped</b> kinds, ordinary
/// outcomes ("Pairing didn't finish"), with no pop-up.
/// </summary>
public enum PairingErrorKind
{
    /// <summary>Legacy (D17): never produced any more; kept so an old health.json with this value still loads.</summary>
    [JsonStringEnumMemberName("requestReplaced")]
    RequestReplaced,

    /// <summary>Legacy (D17): never produced any more; kept so an old health.json with this value still loads.</summary>
    [JsonStringEnumMemberName("codesDidNotMatch")]
    CodesDidNotMatch,

    /// <summary>Safety: the server answering is not the one that was probed (another server id).</summary>
    [JsonStringEnumMemberName("serverChanged")]
    ServerChanged,

    /// <summary>Safety: the friend approved an administrator account; Home refused it and signed it out.</summary>
    [JsonStringEnumMemberName("adminAccount")]
    AdminAccount,

    /// <summary>Safety: the peer sent something that is not what Jellyfin sends (malformed or inconsistent values).</summary>
    [JsonStringEnumMemberName("badPeerResponse")]
    BadPeerResponse,

    /// <summary>The code or the confirm window ran out.</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>The Peer admin pressed Deny (invite path).</summary>
    [JsonStringEnumMemberName("denied")]
    Denied,

    /// <summary>The Peer admin typed a non-matching safety code three times (invite path).</summary>
    [JsonStringEnumMemberName("codeMismatch")]
    CodeMismatch,

    /// <summary>The invite was not valid (invite path).</summary>
    [JsonStringEnumMemberName("inviteInvalid")]
    InviteInvalid,

    /// <summary>The friend made a newer invite, which replaced this one before it was approved (invite path).</summary>
    [JsonStringEnumMemberName("inviteReplaced")]
    InviteReplaced,

    /// <summary>The friend cancelled the invite, or stopped sharing, before it was approved (invite path).</summary>
    [JsonStringEnumMemberName("inviteCancelled")]
    InviteCancelled,

    /// <summary>The friend approved, but Home did not collect the key within two minutes, so the friend's server signed it out again (invite path).</summary>
    [JsonStringEnumMemberName("tokenNotCollected")]
    TokenNotCollected,

    /// <summary>The Peer could not be reached for 30 seconds.</summary>
    [JsonStringEnumMemberName("network")]
    Network,

    /// <summary>Legacy (D17): never produced any more; kept so an old health.json with this value still loads.</summary>
    [JsonStringEnumMemberName("quickConnectOff")]
    QuickConnectOff,

    /// <summary>Legacy (D17): never produced any more; kept so an old health.json with this value still loads.</summary>
    [JsonStringEnumMemberName("quickConnectUnreachable")]
    QuickConnectUnreachable,

    /// <summary>Home restarted during the pairing (the phase says how far it had come).</summary>
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,

    /// <summary>Too many pairings were started for one server in a short time.</summary>
    [JsonStringEnumMemberName("rateLimited")]
    RateLimited,

    /// <summary>Something failed on this server (for example the secret could not be stored).</summary>
    [JsonStringEnumMemberName("server")]
    Server,

    /// <summary>Legacy (D17): never produced any more; kept so an old health.json with this value still loads.</summary>
    [JsonStringEnumMemberName("likelySessionLimit")]
    LikelySessionLimit
}

/// <summary>
/// Where a pairing session is (DESIGN 4.2a state table).
/// </summary>
public enum PairingStep
{
    /// <summary>Checking that the server is the one that was probed.</summary>
    [JsonStringEnumMemberName("preflight")]
    Preflight,

    /// <summary>Redeeming the invite.</summary>
    [JsonStringEnumMemberName("initiating")]
    Initiating,

    /// <summary>The safety code is shown; waiting for the friend to type it and approve.</summary>
    [JsonStringEnumMemberName("awaitingApproval")]
    AwaitingApproval,

    /// <summary>Checking the token, the account and the server.</summary>
    [JsonStringEnumMemberName("validating")]
    Validating,

    /// <summary>Done: the credential is stored.</summary>
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,

    /// <summary>Stopped; <see cref="PairingSessionInfo.Error"/> says why.</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted
}

/// <summary>
/// What Home found wrong with the friend's sharing account (DESIGN 4.1 "What Home checks"). Warnings are things the friend can fix.
/// </summary>
public enum AccountWarning
{
    /// <summary>The account sees all libraries.</summary>
    [JsonStringEnumMemberName("allLibraries")]
    AllLibraries,

    /// <summary>The account can delete content.</summary>
    [JsonStringEnumMemberName("contentDeletion")]
    ContentDeletion,

    /// <summary>The account can download content.</summary>
    [JsonStringEnumMemberName("contentDownloading")]
    ContentDownloading,

    /// <summary>The account has Live TV access.</summary>
    [JsonStringEnumMemberName("liveTv")]
    LiveTv,

    /// <summary>The account can remote-control other users' sessions.</summary>
    [JsonStringEnumMemberName("remoteControl")]
    RemoteControl,

    /// <summary>The account is not locked to devices: "ask your friend to lock the sharing account to this server".</summary>
    [JsonStringEnumMemberName("allDevices")]
    AllDevices,

    /// <summary>The session limit is not 1 (only reported once the pairing is confirmed): "ask your friend to set the session limit to 1".</summary>
    [JsonStringEnumMemberName("sessionLimit")]
    SessionLimit
}

/// <summary>
/// Facts about the account that can never clear on Jellyfin 12.1; shown on the server detail, never counted as alerts.
/// </summary>
public enum AccountNote
{
    /// <summary>The account may change its own password (<c>EnableUserPreferenceAccess</c>), and the 12.1 dashboard has no switch for it.</summary>
    [JsonStringEnumMemberName("userPreferenceAccess")]
    UserPreferenceAccess,

    /// <summary><c>EnableMediaConversion</c> is true.</summary>
    [JsonStringEnumMemberName("mediaConversion")]
    MediaConversion,

    /// <summary><c>EnableSyncTranscoding</c> is true.</summary>
    [JsonStringEnumMemberName("syncTranscoding")]
    SyncTranscoding,

    /// <summary><c>EnablePublicSharing</c> is true.</summary>
    [JsonStringEnumMemberName("publicSharing")]
    PublicSharing
}

/// <summary>
/// Why a pairing call was refused (the HTTP layer answers 409 with this code, or 429 for <see cref="RateLimited"/>).
/// </summary>
public enum PairingConflict
{
    /// <summary>A pairing session is live (<c>sessionActive</c>).</summary>
    SessionActive,

    /// <summary>The probe handle is unknown, used, or older than 10 minutes (<c>probeExpired</c>).</summary>
    ProbeExpired,

    /// <summary>No session is waiting for the confirmation (<c>notAwaitingConfirm</c>).</summary>
    NotAwaitingConfirm,

    /// <summary>No session exists.</summary>
    NoSession,

    /// <summary>The secret store is unreadable, so a pairing could not be saved (<c>secretsUnreadable</c>).</summary>
    SecretsUnreadable,

    /// <summary>The method is not available yet (the invite path is a later task).</summary>
    MethodUnavailable,

    /// <summary>No abort is stored for that peer, or a pairing is live for it (<c>DismissPairing</c>).</summary>
    NothingToDismiss,

    /// <summary>The peer does not exist.</summary>
    UnknownPeer,

    /// <summary>The server is not reachable like Jellyfin or its address is not acceptable.</summary>
    InvalidRequest,

    /// <summary>Too many pairings were started for one server (<c>429</c> with <c>Retry-After</c>).</summary>
    RateLimited
}
