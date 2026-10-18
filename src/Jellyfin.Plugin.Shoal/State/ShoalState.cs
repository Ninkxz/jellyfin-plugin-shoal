using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// The network a peer is reached over (DESIGN 4.2, 8.3 T11). Decides which addresses are acceptable.
/// </summary>
public enum PeerNetwork
{
    /// <summary>A Tailscale node: a <c>*.ts.net</c> name or an address in <c>100.64.0.0/10</c> / <c>fd7a:115c:a1e0::/48</c>.</summary>
    Tailnet,

    /// <summary>A private network address (RFC1918, <c>fc00::/7</c>). Plain http needs an acknowledgement.</summary>
    [JsonStringEnumMemberName("LAN")]
    Lan,

    /// <summary>A public address. https is required.</summary>
    Public
}

/// <summary>
/// How a peer was paired (DESIGN 4.2).
/// </summary>
public enum PairingMethod
{
    /// <summary>Quick Connect against a stock peer.</summary>
    [JsonStringEnumMemberName("qc")]
    Qc,

    /// <summary>A one-time invite against a peer that runs Shoal.</summary>
    [JsonStringEnumMemberName("invite")]
    Invite
}

/// <summary>
/// How far a pairing that was started has come (DESIGN 4.2a).
/// </summary>
public enum PairingPhase
{
    /// <summary>The code was asked for and may not be approved yet. After a restart the code is gone: "The code expired".</summary>
    [JsonStringEnumMemberName("beforeApproval")]
    BeforeApproval,

    /// <summary>The friend approved and Home holds a validated token in memory only. After a restart: "Shoal restarted before you confirmed".</summary>
    [JsonStringEnumMemberName("awaitingConfirm")]
    AwaitingConfirm,

    /// <summary>Used by an abort record only: the friend approved, then their server refused to finish (<c>likelySessionLimit</c>, rule a).</summary>
    [JsonStringEnumMemberName("afterApproval")]
    AfterApproval,
}

/// <summary>
/// Who may see a peer library (owner decisions D3 and D4, DESIGN 5.6).
/// </summary>
public enum AccessMode
{
    /// <summary>"Only selected users": exactly the users in the selection.</summary>
    Selected,

    /// <summary>"Everyone on this server", including users created later.</summary>
    Everyone
}

/// <summary>
/// The mode of the Peer-side guard filter (DESIGN 8.4, owner D9). Lives in <c>state.json</c>, not in the plugin XML, because an
/// XML reset to the default would silently downgrade <see cref="Enforce"/> to <see cref="LogOnly"/>.
/// </summary>
public enum HardeningMode
{
    /// <summary>Count what would be blocked; block nothing. One click away from the default (<see cref="Enforce"/> since D18); a state that already stores it keeps it.</summary>
    LogOnly,

    /// <summary>Anonymous requests for shared titles are refused.</summary>
    Enforce,

    /// <summary>Job (a) of the guard filter is off (troubleshooting only).</summary>
    Off
}

/// <summary>
/// The content of <c>state.json</c> (DESIGN 3.2a): Shoal's topology and security settings. This is the source of truth;
/// the plugin XML holds only toggles. Treat an instance returned by <see cref="IStateStore.Current"/> as read-only: change
/// it only inside <see cref="IStateStore.UpdateAsync"/>.
/// </summary>
public sealed class ShoalState
{
    /// <summary>
    /// The schema this build writes. A higher number on disk is never downgraded (DESIGN 3.7). Schema 2 is still read and upgraded in memory
    /// (<see cref="StateUpgrade"/>); the file itself is rewritten only by the next change.
    /// </summary>
    public const int CurrentSchema = 3;

    /// <summary>Gets or sets the schema number.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Gets or sets the write counter; incremented by every write.</summary>
    public long Generation { get; set; }

    /// <summary>Gets or sets when this state was written (UTC).</summary>
    public DateTime WrittenUtc { get; set; }

    /// <summary>
    /// Gets or sets the folder that holds the stubs. Empty means nothing is federated (fail closed, Warden B-5).
    /// </summary>
    public string StubRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the Home role: the peers this server watches.</summary>
    public HomeState Home { get; set; } = new();

    /// <summary>Gets or sets the Peer role: what this server shares.</summary>
    public PeerRoleState PeerRole { get; set; } = new();
}

/// <summary>
/// The Home role part of the state.
/// </summary>
public sealed class HomeState
{
    /// <summary>Gets or sets the peers.</summary>
    public List<PeerState> Peers { get; set; } = [];

    /// <summary>Gets or sets the visibility bookkeeping (DESIGN 5.6).</summary>
    public VisibilityState Visibility { get; set; } = new();

    /// <summary>Gets or sets the Shoal libraries (schema 3, PLAN v2.9 K41). Until K44 the peer mappings stay the source of truth for sync, paths and access.</summary>
    public List<ShoalLibrary> Libraries { get; set; } = [];

    /// <summary>Gets or sets the layout settings (schema 3).</summary>
    public LibraryLayout Layout { get; set; } = new();

    /// <summary>Gets or sets the library keys in the order the admin set; a title shows in the first library a user can open.</summary>
    public List<string> LibraryOrder { get; set; } = [];

    /// <summary>Gets or sets the Moonfin inbox explainer bookkeeping (PLAN v2.9 K49); optional, absent in older files.</summary>
    public InboxState Inbox { get; set; } = new();
}

/// <summary>
/// The Moonfin inbox explainer (PLAN v2.9 K49): the message Moonbase answered for it, so a second post replaces it in place instead of making a duplicate.
/// Holds no key or token.
/// </summary>
public sealed class InboxState
{
    /// <summary>Gets or sets Moonbase's id of the explainer message, or null when it was never posted.</summary>
    public Guid? ExplainerMessageId { get; set; }

    /// <summary>Gets or sets when it was last posted (UTC), or null.</summary>
    public DateTime? ExplainerPostedUtc { get; set; }

    /// <summary>Gets or sets the accounts it was addressed to at the last post (Moonbase's "users" audience reaches only these; a newer account needs a re-post).</summary>
    public List<Guid> TargetUserIds { get; set; } = [];
}

/// <summary>
/// One friend's server. Holds no secret: the token lives in the secret store, under the same <see cref="PeerKey"/>.
/// </summary>
public sealed class PeerState
{
    /// <summary>
    /// Gets or sets the random key of this peer (lower-case hex, 16 to 64 characters). It names the peer's folders on disk
    /// and its entry in the secret store, so it is validated strictly on every load.
    /// </summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the admin sees (text only, never HTML).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets a local name for this server, or empty. The offline card and the offline message use it instead of <see cref="DisplayName"/>, so a friend's own choice of server name is never what Home's users read on the screen.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer base URL, validated by <c>AddressPolicy</c> before use.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the network class of the peer.</summary>
    public PeerNetwork Network { get; set; } = PeerNetwork.Tailnet;

    /// <summary>Gets or sets the optional SOCKS5 proxy (Tailnet peers only, loopback address).</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether this peer is in use.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the server id pinned at pairing (32 hex characters). It is accident detection, not authentication (DESIGN 4.4).
    /// </summary>
    public string? PinnedServerId { get; set; }

    /// <summary>
    /// Gets or sets the optional TLS public-key pin: base64 of the SHA-256 of the certificate's SubjectPublicKeyInfo (opt-in, DESIGN 4.4).
    /// </summary>
    public string? TlsSpkiPin { get; set; }

    /// <summary>Gets or sets a value indicating whether the admin accepted that the token travels unencrypted on the LAN.</summary>
    public bool LanCleartextAck { get; set; }

    /// <summary>Gets or sets how and when the peer was paired.</summary>
    public PairingInfo? Pairing { get; set; }

    /// <summary>
    /// Gets or sets the pairing that is in progress for this peer, or null. It is written when the admin starts a pairing, moved to
    /// <see cref="PairingPhase.AwaitingConfirm"/> when the token has passed validation, and removed at Confirmed or on any abort. A record
    /// that still carries it at startup means Home restarted in the middle: it becomes <c>PairingAborted(interrupted, phase)</c>
    /// (DESIGN 4.2a, v2.2). The token itself is never written.
    /// </summary>
    public PairingProgress? PendingPairing { get; set; }

    /// <summary>Gets or sets the most streams that may run at once through this peer.</summary>
    public int MaxConcurrentStreams { get; set; } = 4;

    /// <summary>Gets or sets how many HLS conversions may run at this peer at once (DESIGN 3.2a, 6.9; owner D15 default 4). Clamped to 1..<see cref="MaxConcurrentStreams"/>.</summary>
    public int MaxConcurrentTranscodes { get; set; } = 4;

    /// <summary>Gets or sets the cross-peer priority (lower wins) used when two peers carry the same title.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Gets or sets the mapped libraries.</summary>
    public List<LibraryMapping> Libraries { get; set; } = [];

    /// <summary>Gets or sets the days between automatic token rotations: 0 (off, the default), 30, 60 or 90 (DESIGN v2.6.1).</summary>
    public int RotationDays { get; set; }

    /// <summary>Gets or sets when the token was last rotated (UTC), or null.</summary>
    public DateTime? LastRotatedUtc { get; set; }

    /// <summary>Gets or sets the priority tier (schema 3); Normal by default. <see cref="Priority"/> stays as a hidden tiebreak.</summary>
    public PriorityTier PriorityTier { get; set; } = PriorityTier.Normal;

    /// <summary>Gets or sets the automatic ranking (schema 3).</summary>
    public AutoRank AutoRank { get; set; } = new();

    /// <summary>Gets or sets the quality limit of this connection (PLAN v2.9 K50); Off by default.</summary>
    public QualityLimit QualityLimit { get; set; } = new();

    /// <summary>Gets or sets the speed-test recommendations the admin dismissed, newest last (the validator keeps the newest 20).</summary>
    public List<DismissedRecommendation> Dismissed { get; set; } = [];

    /// <summary>
    /// The name shown to Home's users on the offline card and message: the alias when there is one, else the display name.
    /// </summary>
    /// <returns>The name.</returns>
    public string ShownName() => Alias.Length > 0 ? Alias : DisplayName;
}

/// <summary>
/// A pairing in progress, as stored on the peer record. Holds no secret.
/// </summary>
public sealed class PairingProgress
{
    /// <summary>Gets or sets how far the pairing has come.</summary>
    public PairingPhase Phase { get; set; }

    /// <summary>Gets or sets when the admin started it (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets the local admin who started it.</summary>
    public Guid StartedByUserId { get; set; }

    /// <summary>Gets or sets the pairing method.</summary>
    public PairingMethod Method { get; set; } = PairingMethod.Invite;
}

/// <summary>
/// How a peer was paired.
/// </summary>
public sealed class PairingInfo
{
    /// <summary>Gets or sets the pairing method.</summary>
    public PairingMethod Method { get; set; }

    /// <summary>Gets or sets the 4-character check the friend compared (Quick Connect only).</summary>
    public string? CheckString { get; set; }

    /// <summary>Gets or sets when the pairing finished (UTC).</summary>
    public DateTime PairedUtc { get; set; }

    /// <summary>Gets or sets the local admin who paired.</summary>
    public Guid PairedByUserId { get; set; }
}

/// <summary>
/// A peer library that is mapped to a local library.
/// </summary>
public sealed class LibraryMapping
{
    /// <summary>Gets or sets the local handle of the mapping (letters, digits, '-' and '_').</summary>
    public string MappingKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name on the peer (peer-supplied: text only).</summary>
    public string RemoteName { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type (only <c>movies</c> in the MVP).</summary>
    public string CollectionType { get; set; } = "movies";

    /// <summary>Gets or sets the name of the local library.</summary>
    public string LocalLibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder below the peer's stub folder: a single, plain path segment.</summary>
    public string Subfolder { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether titles are imported.</summary>
    public bool Import { get; set; } = true;

    /// <summary>Gets or sets who may see the library.</summary>
    public LibraryAccess Access { get; set; } = new();
}

/// <summary>
/// The access mode and selection of one library (DESIGN 5.6).
/// </summary>
public sealed class LibraryAccess
{
    /// <summary>Gets or sets the mode; <see cref="AccessMode.Selected"/> is the default (owner D3).</summary>
    public AccessMode Mode { get; set; } = AccessMode.Selected;

    /// <summary>
    /// Gets or sets the selected local users. Kept unchanged while the mode is <see cref="AccessMode.Everyone"/>, which is how
    /// switching back restores the previous selection (owner D4).
    /// </summary>
    public List<Guid> SelectedUserIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether profiles with parental limits are admitted in <see cref="AccessMode.Everyone"/> mode.</summary>
    public bool IncludeRestricted { get; set; }

    /// <summary>Gets or sets when the access was last changed (UTC).</summary>
    public DateTime ChangedUtc { get; set; }

    /// <summary>Gets or sets who changed it last.</summary>
    public Guid ChangedByUserId { get; set; }
}

/// <summary>
/// Visibility bookkeeping (DESIGN 5.6).
/// </summary>
public sealed class VisibilityState
{
    /// <summary>
    /// Gets or sets the accounts whose "all libraries" setting Shoal replaced with an explicit list, so Shoal can keep them
    /// current and undo exactly its own change.
    /// </summary>
    public Dictionary<Guid, ManagedUser> ManagedUsers { get; set; } = [];

    /// <summary>
    /// Gets or sets the servers whose libraries are hidden while they are offline (<c>ParkOfflinePeers</c>), with the exact (account, library) pairs Shoal removed,
    /// so only those are put back. Survives a restart.
    /// </summary>
    public Dictionary<string, ParkedPeer> Parked { get; set; } = [];

    /// <summary>
    /// Gets or sets the <c>BlockedTags</c> entries Shoal added to each account (PLAN v2.9 K45, <c>managedBlockedTagUsers</c>): library tags, alternate-copy tags and the
    /// hide-all tag. Shoal removes only what it recorded here, so a tag an admin set by hand is never taken away.
    /// </summary>
    public Dictionary<Guid, List<string>> ManagedBlockedTags { get; set; } = [];

    /// <summary>Gets or sets the accounts that hide friends' titles ("Hide friends' titles", the global <c>Shoal</c> tag); playback refuses them too.</summary>
    public List<Guid> HiddenFriendTitles { get; set; } = [];
}

/// <summary>
/// A server whose libraries are hidden.
/// </summary>
public sealed class ParkedPeer
{
    /// <summary>Gets or sets when the libraries were hidden (UTC).</summary>
    public DateTime ParkedUtc { get; set; }

    /// <summary>Gets or sets the (account, library) pairs Shoal removed from an explicit library list.</summary>
    public List<ParkedPair> Pairs { get; set; } = [];
}

/// <summary>
/// One library removed from one account's list.
/// </summary>
public sealed class ParkedPair
{
    /// <summary>Gets or sets the account.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the library's folder id.</summary>
    public Guid LibraryId { get; set; }
}

/// <summary>
/// An account whose library list Shoal maintains.
/// </summary>
public sealed class ManagedUser
{
    /// <summary>Gets or sets when Shoal took the account over (UTC).</summary>
    public DateTime Since { get; set; }

    /// <summary>Gets or sets a hash of the folder list Shoal last wrote; a difference means the admin edited the account.</summary>
    public string LastWrittenFoldersHash { get; set; } = string.Empty;
}

/// <summary>
/// The Peer role part of the state (a Shoal server that shares its libraries).
/// </summary>
public sealed class PeerRoleState
{
    /// <summary>Gets or sets the guard filter mode.</summary>
    public HardeningMode HardeningMode { get; set; } = HardeningMode.Enforce;

    /// <summary>Gets or sets when the mode was last changed (UTC).</summary>
    public DateTime HardeningSinceUtc { get; set; }

    /// <summary>Gets or sets the shares.</summary>
    public List<ShareState> Shares { get; set; } = [];
}

/// <summary>
/// One friend this server shares with.
/// </summary>
public sealed class ShareState
{
    /// <summary>Gets or sets the handle of the share.</summary>
    public string ShareId { get; set; } = string.Empty;

    /// <summary>Gets or sets the friend's name (text only).</summary>
    public string FriendName { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user created for the friend.</summary>
    public Guid ShareUserId { get; set; }

    /// <summary>Gets or sets the shared libraries (local library ids).</summary>
    public List<Guid> LibraryIds { get; set; } = [];

    /// <summary>Gets or sets the device ids the share user may have; any other device is signed out.</summary>
    public List<string> ExpectedDeviceIds { get; set; } = [];

    /// <summary>Gets or sets an optional bitrate limit per stream, in Mbps.</summary>
    public int? MaxBitrateMbps { get; set; }

    /// <summary>Gets or sets the most transcodes that may run at once for this share.</summary>
    public int MaxConcurrentTranscodes { get; set; } = 4;

    /// <summary>Gets or sets the name of the sharing account (<c>shoal-&lt;friend&gt;-&lt;4 hex&gt;</c>; a name, not a secret).</summary>
    public string ShareUserName { get; set; } = string.Empty;

    /// <summary>Gets or sets when the share was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the share is active (false = paused).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the first 12 hex characters of the Home server id, from the invite (DESIGN v2.6.2). Display only: it links a share to a peer record and no decision reads it. Empty for older shares.</summary>
    public string HomeServerIdPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets when the share's token was last rotated (UTC), or null (DESIGN v2.6.1).</summary>
    public DateTime? LastRotatedUtc { get; set; }

    /// <summary>Gets or sets how many times the share's token was rotated.</summary>
    public int RotationCount { get; set; }

    /// <summary>Gets or sets when a rotation last failed and left the share without a device (UTC), or null.</summary>
    public DateTime? RotationFailedUtc { get; set; }
}
