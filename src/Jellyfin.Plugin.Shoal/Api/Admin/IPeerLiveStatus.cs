using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The live part of the Overview: health, speed, sync and the Peer role's activity. The implementations arrive with the backend
/// tasks (HealthMonitor and catalog sync K11/K20, ShareService K21). Until then <see cref="UnknownPeerLiveStatus"/> answers, so
/// the admin page works against real settings and shows servers as not measured.
/// </summary>
public interface IPeerLiveStatus
{
    /// <summary>
    /// Gets what is known about a peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>What is known; never null.</returns>
    PeerLiveInfo ForPeer(string peerKey);

    /// <summary>
    /// Gets what is known about a friend this server shares with.
    /// </summary>
    /// <param name="shareId">The share id.</param>
    /// <returns>What is known; never null.</returns>
    ShareLiveInfo ForShare(string shareId);

    /// <summary>
    /// Asks for a catalog sync of every enabled peer. Returns at once; the sync runs in the background.
    /// </summary>
    /// <returns>The number of syncs that were started.</returns>
    int StartSyncAll();

    /// <summary>
    /// Asks for a catalog sync of one peer.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>True when a sync was started.</returns>
    bool StartSync(string peerKey);

    /// <summary>
    /// Applies what a peer's guard held back ("Remove them now", "Add them"). Returns at once; a sync runs in the background.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="removals">True for the held removals, false for the held additions.</param>
    /// <returns>True when it was started.</returns>
    bool StartRelease(string peerKey, bool removals) => false;
}

/// <summary>
/// What is known about one peer right now. Everything that comes from the peer is optional: this server never assumes a value
/// it has not measured.
/// </summary>
public sealed record PeerLiveInfo
{
    /// <summary>Gets the status: Online, Degraded, Offline, AuthFailed, IdentityMismatch, TlsKeyChanged, Pairing or PairingAborted. Null means "not known yet".</summary>
    public string? Status { get; init; }

    /// <summary>Gets the pairing error kind (PairingAborted).</summary>
    public string? PairingError { get; init; }

    /// <summary>Gets how far a stopped pairing had come (beforeApproval or awaitingConfirm).</summary>
    public string? PairingPhase { get; init; }

    /// <summary>Gets when the pairing stopped.</summary>
    public DateTime? PairingStoppedUtc { get; init; }

    /// <summary>Gets the path: direct, derp, derp?, proxy or unknown.</summary>
    public string? Path { get; init; }

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

    /// <summary>Gets the one-line verdict summary (1080p, 4K, remux), empty before the first speed test.</summary>
    public IReadOnlyList<Jellyfin.Plugin.Shoal.Diagnostics.SummaryRow> VerdictSummary { get; init; } = [];

    /// <summary>Gets since when the peer is offline.</summary>
    public DateTime? OfflineSinceUtc { get; init; }

    /// <summary>Gets when the peer is checked next.</summary>
    public DateTime? NextCheckUtc { get; init; }

    /// <summary>Gets the device lock read from the share account (<c>confirmed</c> or <c>missing</c>), or null when not known.</summary>
    public string? DeviceLock { get; init; }

    /// <summary>Gets the session limit read from the share account (<c>ok</c> or <c>notSet</c>), or null when not known.</summary>
    public string? SessionLimit { get; init; }

    /// <summary>Gets a value indicating whether a sync is running.</summary>
    public bool Syncing { get; init; }

    /// <summary>Gets the number of titles imported from this peer.</summary>
    public int TitleCount { get; init; }

    /// <summary>Gets the number of things that need attention.</summary>
    public int AlertCount { get; init; }

    /// <summary>Gets the number of removals on hold.</summary>
    public int HeldRemovals { get; init; }

    /// <summary>Gets the number of additions on hold.</summary>
    public int HeldAdditions { get; init; }

    /// <summary>Gets the streams running through the peer now.</summary>
    public int ActiveStreams { get; init; }

    /// <summary>Gets the last sync, or null before the first.</summary>
    public PeerSyncInfo? LastSync { get; init; }

    /// <summary>Gets the storage and name budgets, or null before the first sync.</summary>
    public PeerUsageInfo? Usage { get; init; }

    /// <summary>Gets the alerts the last sync raised (kind words of ui-spec 8.3.2, severity, count, example titles).</summary>
    public IReadOnlyList<PeerAlertInfo> Alerts { get; init; } = [];
}

/// <summary>The last sync of a peer.</summary>
/// <param name="AtUtc">When it ended.</param>
/// <param name="Result">ok, partial, failed or skipped.</param>
/// <param name="Added">Stubs added.</param>
/// <param name="Removed">Stubs removed.</param>
public sealed record PeerSyncInfo(DateTime AtUtc, string Result, int Added, int Removed);

/// <summary>What Shoal stores for a peer.</summary>
/// <param name="DiskBytes">Bytes written (stubs and images).</param>
/// <param name="QuotaBytes">The limit.</param>
/// <param name="Stubs">Titles with a stub.</param>
/// <param name="People">People names kept.</param>
/// <param name="Genres">Genre names kept.</param>
/// <param name="Studios">Studio names kept.</param>
public sealed record PeerUsageInfo(long DiskBytes, long QuotaBytes, int Stubs, int People, int Genres, int Studios);

/// <summary>One alert for the server detail.</summary>
/// <param name="Kind">The kind word (heldRemovals, heldAdditions, diskQuota, entryCap, entityCap, stubFolder).</param>
/// <param name="Severity">warn or bad.</param>
/// <param name="Count">How many titles, for the held kinds.</param>
/// <param name="Examples">Up to three titles (sanitized text).</param>
public sealed record PeerAlertInfo(string Kind, string Severity, int? Count = null, IReadOnlyList<string>? Examples = null);

/// <summary>
/// What is known about one friend right now.
/// </summary>
public sealed record ShareLiveInfo
{
    /// <summary>Gets a value indicating whether the friend's sharing account was disabled or deleted outside Shoal.</summary>
    public bool AccessRemoved { get; init; }

    /// <summary>Gets the devices known for the account.</summary>
    public int DeviceCount { get; init; }

    /// <summary>Gets the devices that were signed out because they are not the friend's server.</summary>
    public int UnexpectedDeviceCount { get; init; }

    /// <summary>Gets the last activity.</summary>
    public DateTime? LastSeenUtc { get; init; }

    /// <summary>Gets the streams running now.</summary>
    public int PlayingNow { get; init; }

    /// <summary>Gets how many of them transcode.</summary>
    public int TranscodingNow { get; init; }
}

/// <summary>
/// The placeholder until the health monitor and the sync service exist: nothing has been measured, nothing is syncing.
/// </summary>
public sealed class UnknownPeerLiveStatus : IPeerLiveStatus
{
    private static readonly PeerLiveInfo NoInfo = new();
    private static readonly ShareLiveInfo NoShareInfo = new();

    /// <inheritdoc />
    public PeerLiveInfo ForPeer(string peerKey) => NoInfo;

    /// <inheritdoc />
    public ShareLiveInfo ForShare(string shareId) => NoShareInfo;

    /// <inheritdoc />
    public int StartSyncAll() => 0;

    /// <inheritdoc />
    public bool StartSync(string peerKey) => false;
}
