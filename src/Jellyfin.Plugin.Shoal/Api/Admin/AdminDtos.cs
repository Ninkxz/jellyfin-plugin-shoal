using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

// Wire shapes of GET Shoal/Admin/Status and the peer detail (docs/ui-spec.md 13, DESIGN 9.3, 9.5). Handles only: a peer is
// identified by its local peer key, a friend by its share id. No token, no invite, no peer-side or user GUID ever appears here.

/// <summary>The last catalog sync of a server.</summary>
/// <param name="AtUtc">When the last real sync finished (when the skip was, if no sync ever ran).</param>
/// <param name="Result">ok, partial, failed or held; skipped only when no sync ever ran.</param>
/// <param name="Added">Titles added.</param>
/// <param name="Removed">Titles removed.</param>
/// <param name="SkippedUtc">When a manual "Sync now" after that sync did not run, or absent.</param>
/// <param name="SkipReason">Why it did not run (tooSoon, scanRunning, busy, layoutJob, ...), or absent.</param>
internal sealed record SyncInfoDto(DateTime AtUtc, string Result, int Added, int Removed, DateTime? SkippedUtc = null, string? SkipReason = null)
{
    /// <summary>The wire shape of a peer's last sync.</summary>
    /// <param name="info">The sync, or null.</param>
    /// <returns>The DTO, or null.</returns>
    public static SyncInfoDto? From(PeerSyncInfo? info) => info is null ? null : new SyncInfoDto(info.AtUtc, info.Result, info.Added, info.Removed, info.SkippedUtc, info.SkipReason);
}

/// <summary>Streams running through one server.</summary>
/// <param name="N">Running now.</param>
/// <param name="Max">The limit.</param>
internal sealed record StreamsDto(int N, int Max);

/// <summary>One line of the verdict summary (DESIGN 7.2).</summary>
/// <param name="Cls">The quality class.</param>
/// <param name="Verdict">smooth, mayBuffer, needsTranscode or willBuffer.</param>
internal sealed record VerdictDto(string Cls, string Verdict);

/// <summary>A hint the link produced (DESIGN 7.3).</summary>
/// <param name="Kind">The hint kind.</param>
/// <param name="Severity">warn or bad.</param>
internal sealed record HintDto(string Kind, string Severity);

/// <summary>The newest title of a server (its stored backdrop is served by core, by local item id).</summary>
/// <param name="LocalItemId">The local item id.</param>
/// <param name="Title">The title (peer-supplied: text only).</param>
/// <param name="Year">The year.</param>
/// <param name="BackdropTag">The backdrop image tag.</param>
/// <param name="PrimaryTag">The poster image tag.</param>
internal sealed record LatestDto(string LocalItemId, string Title, int? Year, string? BackdropTag, string? PrimaryTag);

/// <summary>One friend's server as the Overview shows it.</summary>
internal record ServerDto
{
    /// <summary>Gets the local handle (the peer key).</summary>
    public required string Handle { get; init; }

    /// <summary>Gets the name (text only).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the address the admin entered.</summary>
    public required string Url { get; init; }

    /// <summary>Gets the network class.</summary>
    public required PeerNetwork Network { get; init; }

    /// <summary>Gets a value indicating whether a proxy is used.</summary>
    public bool Proxy { get; init; }

    /// <summary>Gets the status: Online, Degraded, Offline, AuthFailed, IdentityMismatch, TlsKeyChanged, Disabled, Pairing or PairingAborted.</summary>
    public required string Status { get; init; }

    /// <summary>Gets how the server was paired: <c>invite</c>, or <c>qc</c> for one paired the old way, which is paused until it is paired again by invite. Null when never paired.</summary>
    public string? PairingMethod { get; init; }

    /// <summary>Gets a value indicating whether the server must be paired again by invite (it was paired the old way and is paused).</summary>
    public bool NeedsRepair { get; init; }

    /// <summary>Gets a value indicating whether that re-pair cannot work as the server is reached today: an invite is never redeemed over plain http outside a Tailnet (D17). The card then says: switch it to Tailscale or https, then pair again, or remove it.</summary>
    public bool RepairBlockedByHttp { get; init; }

    /// <summary>Gets the pairing error kind for PairingAborted.</summary>
    public string? PairingError { get; init; }

    /// <summary>Gets how far a stopped pairing had come.</summary>
    public string? PairingPhase { get; init; }

    /// <summary>Gets when the pairing stopped.</summary>
    public DateTime? PairingStoppedUtc { get; init; }

    /// <summary>Gets the path: direct, derp, derp?, proxy or unknown.</summary>
    public string Path { get; init; } = "unknown";

    /// <summary>Gets the DERP region.</summary>
    public string? DerpRegion { get; init; }

    /// <summary>Gets the round trip time in ms.</summary>
    public double? RttMs { get; init; }

    /// <summary>Gets the throughput in Mbps.</summary>
    public double? Mbps { get; init; }

    /// <summary>Gets the jitter in ms.</summary>
    public double? JitterMs { get; init; }

    /// <summary>Gets the packet loss in percent.</summary>
    public double? LossPct { get; init; }

    /// <summary>Gets since when the server is offline.</summary>
    public DateTime? OfflineSinceUtc { get; init; }

    /// <summary>Gets when it is checked next.</summary>
    public DateTime? NextCheckUtc { get; init; }

    /// <summary>Gets the last sync.</summary>
    public SyncInfoDto? LastSync { get; init; }

    /// <summary>Gets a value indicating whether a sync is running.</summary>
    public bool Syncing { get; init; }

    /// <summary>Gets the number of titles shared by this server.</summary>
    public int TitleCount { get; init; }

    /// <summary>Gets how many titles of this server have no stub because another server or Home carries them (0 when none).</summary>
    public int HiddenByDedup { get; init; }

    /// <summary>Gets the name of the server that carries most of them, or null (Home itself, or not known).</summary>
    public string? DedupWinner { get; init; }

    /// <summary>Gets the number of things that need the admin's attention.</summary>
    public int AlertCount { get; init; }

    /// <summary>Gets the number of removals on hold.</summary>
    public int HeldRemovals { get; init; }

    /// <summary>Gets the number of additions on hold.</summary>
    public int HeldAdditions { get; init; }

    /// <summary>Gets the streams.</summary>
    public StreamsDto ActiveStreams { get; init; } = new(0, 4);

    /// <summary>Gets the verdict summary.</summary>
    public IReadOnlyList<VerdictDto> VerdictSummary { get; init; } = [];

    /// <summary>Gets the hints.</summary>
    public IReadOnlyList<HintDto> Hints { get; init; } = [];

    /// <summary>Gets the newest title.</summary>
    public LatestDto? Latest { get; init; }
}

/// <summary>One friend who watches from this server (DESIGN 9.3).</summary>
/// <param name="Handle">The share id.</param>
/// <param name="Name">The friend's name (text only).</param>
/// <param name="Status">Active, Paused or AccessRemoved.</param>
/// <param name="LibraryCount">Shared libraries.</param>
/// <param name="DeviceCount">Known devices.</param>
/// <param name="UnexpectedDeviceCount">Devices that were signed out because they are not the friend's server.</param>
/// <param name="LastSeenUtc">Last activity.</param>
/// <param name="PlayingNow">Streams running now.</param>
/// <param name="TranscodingNow">Of those, transcoding.</param>
internal sealed record FriendDto(string Handle, string Name, string Status, int LibraryCount, int DeviceCount, int UnexpectedDeviceCount, DateTime? LastSeenUtc, int PlayingNow, int TranscodingNow);

/// <summary>The "you watch them" half of a friend (DESIGN v2.6.2).</summary>
/// <param name="PeerHandle">The server's handle.</param>
/// <param name="Status">The server's status word.</param>
/// <param name="Titles">Titles shared by that server.</param>
/// <param name="ActiveStreams">Streams of this server's users running through it now.</param>
internal sealed record WatchSideDto(string PeerHandle, string Status, int Titles, int ActiveStreams);

/// <summary>The "they watch you" half of a friend (DESIGN v2.6.2).</summary>
/// <param name="ShareHandle">The share id.</param>
/// <param name="Status">Active, Paused or AccessRemoved.</param>
/// <param name="LibraryCount">Shared libraries.</param>
/// <param name="DeviceCount">Known devices.</param>
/// <param name="UnexpectedDeviceCount">Devices signed out for not being the friend's server.</param>
/// <param name="PlayingNow">Streams running now.</param>
/// <param name="TranscodingNow">Of those, transcoding.</param>
/// <param name="LastSeenUtc">Last activity.</param>
internal sealed record ShareSideDto(string ShareHandle, string Status, int LibraryCount, int DeviceCount, int UnexpectedDeviceCount, int PlayingNow, int TranscodingNow, DateTime? LastSeenUtc);

/// <summary>
/// One friend as a person: the server this server watches and the friend who watches this server, linked by the first 12 characters of the server id (display only;
/// DESIGN v2.6.2). Either half may be missing; <paramref name="Linked"/> is true when both exist.
/// </summary>
/// <param name="FriendHandle">A stable local handle (a hash of the two record keys).</param>
/// <param name="Name">The server's name when there is one, else the friend's.</param>
/// <param name="Status">The worse of the two halves: AuthFailed, IdentityMismatch, TlsKeyChanged, PairingAborted, Offline, Degraded, Paused or Online.</param>
/// <param name="Linked">Whether both halves exist.</param>
/// <param name="Watch">What this server watches, or null.</param>
/// <param name="Share">Who watches this server, or null.</param>
internal sealed record FriendLinkDto(string FriendHandle, string Name, string Status, bool Linked, WatchSideDto? Watch, ShareSideDto? Share);

/// <summary>An item recently shared with this server.</summary>
/// <param name="LocalItemId">The local item id.</param>
/// <param name="Title">The title (text only).</param>
/// <param name="Year">The year.</param>
/// <param name="RuntimeMin">The runtime in minutes.</param>
/// <param name="ServerHandle">The server's handle.</param>
/// <param name="ServerName">The server's name.</param>
/// <param name="PrimaryTag">The poster tag.</param>
/// <param name="Kind">shared or yourCopy.</param>
internal sealed record RecentDto(string LocalItemId, string Title, int? Year, int? RuntimeMin, string ServerHandle, string ServerName, string? PrimaryTag, string Kind);

/// <summary>The copy the settings were restored from (ui-spec 8.8).</summary>
/// <param name="File">The file name.</param>
/// <param name="AtUtc">When that copy was written.</param>
/// <param name="NeedsConfirm">True until an admin confirms the restore (POST State/ConfirmRestore).</param>
internal sealed record RestoredFromDto(string File, DateTime AtUtc, bool NeedsConfirm);

/// <summary>What state the settings are in (banners, ui-spec 8.8).</summary>
/// <param name="Mode">Normal or Recovery.</param>
/// <param name="Reason">unreadable or newerSchema, in recovery mode.</param>
/// <param name="RestoredFrom">Set when the settings were restored from a copy.</param>
/// <param name="GuardLastGood">In recovery mode: ok when the last-known share list was read, unreadable when it was not, else null.</param>
internal sealed record StateBannerDto(string Mode, string? Reason, RestoredFromDto? RestoredFrom, string? GuardLastGood = null);

/// <summary>Wrong invites seen on this server lately (ui-spec 8.1.1 item 7).</summary>
/// <param name="LastHour">How many in the last hour.</param>
/// <param name="Notice">True when the page should show its calm notice.</param>
internal sealed record InviteFailuresDto(int LastHour, bool Notice);

/// <summary>Cooldowns the UI shows as "Available again in ...".</summary>
/// <param name="SyncAllUntilUtc">When "Sync now" works again.</param>
internal sealed record CooldownsDto(DateTime? SyncAllUntilUtc);

/// <summary>The intercept counters (DESIGN v2.6.3).</summary>
/// <param name="SinceUtc">When counting started (the counters start again with the server).</param>
/// <param name="Counters">Every counter, zero ones included, in a fixed order.</param>
internal sealed record InterceptDto(DateTime SinceUtc, IReadOnlyList<InterceptCounterDto> Counters);

/// <summary>The image safety check (DESIGN v2.6.7).</summary>
/// <param name="State">active, unavailable or off.</param>
/// <param name="Limits">full or partial.</param>
/// <param name="Reason">Why it is unavailable (ffmpegMissing, selfTestFailed), or null.</param>
/// <param name="Rejected">Images it refused since the server started.</param>
internal sealed record ImageSandboxDto(string State, string Limits, string? Reason, long Rejected);

/// <summary>One intercept counter.</summary>
/// <param name="Name">The counter name.</param>
/// <param name="Count">How often it was hit.</param>
internal sealed record InterceptCounterDto(string Name, long Count);

/// <summary>A leftover sharing account.</summary>
/// <param name="Id">The account id.</param>
/// <param name="Name">The account name.</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
internal sealed record OrphanAccountDto(Guid Id, string Name, bool IsDisabled);

/// <summary>GET Shoal/Admin/Status.</summary>
internal sealed record StatusDto
{
    /// <summary>Gets the servers.</summary>
    public required IReadOnlyList<ServerDto> Servers { get; init; }

    /// <summary>Gets the recently shared items.</summary>
    public IReadOnlyList<RecentDto> RecentlyShared { get; init; } = [];

    /// <summary>Gets the friends who watch from this server (the v1 list; each row keeps its shape).</summary>
    public required IReadOnlyList<FriendDto> Friends { get; init; }

    /// <summary>
    /// Gets every friend as a person with the two halves linked (DESIGN v2.6.2). The wire name is <c>friendLinks</c>: <c>friends</c> already carries the v1 list of
    /// shares and the page reads it.
    /// </summary>
    public IReadOnlyList<FriendLinkDto> FriendLinks { get; init; } = [];

    /// <summary>Gets the cooldowns.</summary>
    public required CooldownsDto Cooldowns { get; init; }

    /// <summary>Gets the state of the settings.</summary>
    public required StateBannerDto State { get; init; }

    /// <summary>Gets a value indicating whether the saved keys cannot be unlocked.</summary>
    public bool SecretsUnreadable { get; init; }

    /// <summary>Gets where the key comes from: env, envFile, dpapi or file.</summary>
    public string? KeySource { get; init; }

    /// <summary>Gets why the keys cannot be unlocked: badKey, bothKeys or decryptFailed (only when they cannot).</summary>
    public string? SecretsReason { get; init; }

    /// <summary>Gets this server's name.</summary>
    public string? HomeName { get; init; }

    /// <summary>Gets the wrong-invite counter.</summary>
    public InviteFailuresDto? InviteFailures { get; init; }

    /// <summary>Gets the sharing accounts that no share manages (Warden 08 M7): the admin may delete them; Shoal never does.</summary>
    public IReadOnlyList<OrphanAccountDto> OrphanAccounts { get; init; } = [];

    /// <summary>Gets the main libraries holding friends' titles whose NFO saver is on (Pilot 14: turn it off for them, or "Replace all metadata" overwrites friends' titles).</summary>
    public IReadOnlyList<string> MainLibrariesSavingNfo { get; init; } = [];

    /// <summary>Gets the libraries that hold a Shoal library folder beside other folders (Warden 12 L2): Shoal does not lock their options, so the admin should.</summary>
    public IReadOnlyList<string> ShoalLibrariesNotLocked { get; init; } = [];

    /// <summary>Gets the libraries in a main library that no Jellyfin library holds any more (Warden 13 L5; Shoal's names): their titles show nowhere.</summary>
    public IReadOnlyList<string> MainLibrariesMissing { get; init; } = [];

    /// <summary>Gets the state of the image safety check (DESIGN v2.6.7), or null when it is not wired.</summary>
    public ImageSandboxDto? ImageSandbox { get; init; }

    /// <summary>Gets the intercept counters Pilot reads for the device matrix (DESIGN v2.6.3); memory only, no URL, id or user agent.</summary>
    public InterceptDto? Intercept { get; init; }
}

/// <summary>Storage and limits of one server (ui-spec 8.3.5).</summary>
/// <param name="DiskBytes">Bytes used by pictures and library files.</param>
/// <param name="QuotaBytes">The limit.</param>
/// <param name="Stubs">Titles.</param>
/// <param name="People">Cast names.</param>
/// <param name="Genres">Genres.</param>
/// <param name="Studios">Studios.</param>
internal sealed record UsageDto(long DiskBytes, long QuotaBytes, int Stubs, int People, int Genres, int Studios);

/// <summary>The per-server settings the admin edits (ui-spec 8.3.7).</summary>
/// <param name="MaxConcurrentStreams">Streams at once.</param>
/// <param name="Priority">Lower wins when two servers carry a title.</param>
/// <param name="ProxyUrl">The proxy, when set.</param>
/// <param name="TlsPinned">Whether the certificate key is pinned.</param>
/// <param name="Alias">The local name for the offline card and message, or empty (the display name is used).</param>
internal sealed record PeerSettingsDto(int MaxConcurrentStreams, int Priority, string? ProxyUrl, bool TlsPinned, string Alias = "");

/// <summary>GET Shoal/Admin/Peers/{handle}: the card plus the detail screen's data.</summary>
internal sealed record ServerDetailDto : ServerDto
{
    /// <summary>Initializes a new instance of the <see cref="ServerDetailDto"/> class.</summary>
    /// <param name="card">The card.</param>
    [SetsRequiredMembers]
    public ServerDetailDto(ServerDto card)
        : base(card)
    {
    }

    /// <summary>Gets the first 8 characters of the pinned server id (accident detection, never a code).</summary>
    public string? ServerIdPrefix { get; init; }

    /// <summary>Gets when the server was paired.</summary>
    public DateTime? PairedUtc { get; init; }

    /// <summary>Gets the libraries Shoal made from this server.</summary>
    public IReadOnlyList<object> Libraries { get; init; } = [];

    /// <summary>Gets the local accounts the access summaries name.</summary>
    public IReadOnlyList<object> Users { get; init; } = [];

    /// <summary>Gets the alerts.</summary>
    public IReadOnlyList<object> Alerts { get; init; } = [];

    /// <summary>Gets the usage.</summary>
    public UsageDto? Usage { get; init; }

    /// <summary>Gets the settings.</summary>
    public PeerSettingsDto? Settings { get; init; }

    /// <summary>Gets the streams running now.</summary>
    public IReadOnlyList<object> Streams { get; init; } = [];

    /// <summary>Gets when the data was last refreshed.</summary>
    public DateTime? UpdatedUtc { get; init; }

    /// <summary>Gets what token rotation can do for this server: whether it is supported, the schedule, the last and next time, the last failure (DESIGN v2.6.1).</summary>
    public Jellyfin.Plugin.Shoal.Peers.RotationInfo? Rotation { get; init; }

    /// <summary>Gets the friend this server is linked to, when the friend also watches this server (DESIGN v2.6.2), or null.</summary>
    public FriendLinkDto? Link { get; init; }
}
