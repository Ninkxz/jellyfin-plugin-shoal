using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>The durable phases of recoverable removal and explicitly approved retained-file deletion.</summary>
internal enum RemovalPhase
{
    /// <summary>Evidence and rollback information are durable; media has not moved.</summary>
    Prepared,

    /// <summary>Original bytes are held and replacement handoff has not completed.</summary>
    Quarantined,

    /// <summary>The admitted shared replacement and local history have been verified.</summary>
    ReplacementReady,

    /// <summary>Original bytes were restored without overwriting another file.</summary>
    Restored,

    /// <summary>An interrupted operation requires conservative reconciliation.</summary>
    NeedsRecovery,

    /// <summary>Durable consent authorizes staging only; never replay deletion after restart.</summary>
    PurgePrepared,

    /// <summary>Unlink may have begun. No optimistic Restore or automatic retry is allowed.</summary>
    PurgeCommitting,

    /// <summary>The exact staged original was unlinked and directory durability confirmed.</summary>
    Purged,

    /// <summary>Deletion or retained-byte durability is uncertain and needs explicit attention.</summary>
    PurgeOutcomeUnknown,
}

/// <summary>Current single-file movie facts. This internal evidence is never an admin reply.</summary>
/// <param name="ItemId">The local core item or selected peer item.</param>
/// <param name="ProviderIds">Current canonical strong identities.</param>
/// <param name="SizeBytes">The actual file length.</param>
/// <param name="ContentSha256">The full-file digest, not a partial or metadata hash.</param>
/// <param name="IsMovie">Whether the item is a movie.</param>
/// <param name="IsPhysicalFile">Whether the selected source is a real file rather than a relay or stub.</param>
/// <param name="IsSingleVersion">Whether exactly one supported single-file version exists.</param>
/// <param name="FileIdentityVerified">Whether current no-follow file identity and ancestry checks passed.</param>
internal sealed record RemovalMediaEvidence(
    Guid ItemId,
    IReadOnlyDictionary<string, string> ProviderIds,
    long SizeBytes,
    string? ContentSha256,
    bool IsMovie,
    bool IsPhysicalFile,
    bool IsSingleVersion,
    bool FileIdentityVerified);

/// <summary>Fresh locally observed authorization and lifecycle evidence. Every flag defaults closed.</summary>
internal sealed record RemovalContext
{
    /// <summary>Gets a value indicating whether the owner explicitly enabled recoverable removal.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether state is ready and every relevant catalog is readable.</summary>
    public bool StateAndCatalogsReady { get; init; }

    /// <summary>Gets a value indicating whether the selected peer remains enabled and invite-paired.</summary>
    public bool PeerAdmitted { get; init; }

    /// <summary>Gets a value indicating whether the selected source remains inside its current grant.</summary>
    public bool GrantCurrent { get; init; }

    /// <summary>Gets a value indicating whether the physical origin is independent of the removed local file.</summary>
    public bool IndependentOriginVerified { get; init; }

    /// <summary>Gets a value indicating whether every affected local user can use the admitted replacement.</summary>
    public bool AllUsersAdmitted { get; init; }

    /// <summary>Gets a value indicating whether history capture and an exact replacement handoff can be completed.</summary>
    public bool HistoryHandoffVerified { get; init; }

    /// <summary>Gets a value indicating whether original bytes can be durably held outside scans on the same filesystem.</summary>
    public bool RecoverableStorageVerified { get; init; }

    /// <summary>Gets a value indicating whether source and replacement have passed current playback verification.</summary>
    public bool ReplacementPlaybackVerified { get; init; }

    /// <summary>Gets a value indicating whether playback or a scan/import is currently using either item.</summary>
    public bool Busy { get; init; }

    /// <summary>Gets the Home clock time at which the bounded proof completed.</summary>
    public DateTimeOffset EvidenceUtc { get; init; }
}

/// <summary>A server-held, admin-bound plan. Never serialize this internal record.</summary>
/// <param name="PlanId">An opaque random 32-character plan identifier.</param>
/// <param name="AdminId">The approving local administrator.</param>
/// <param name="LocalItemId">The selected real local movie.</param>
/// <param name="FactsSha256">The digest of the immutable server-held fact snapshot.</param>
/// <param name="CreatedUtc">The Home creation time.</param>
/// <param name="ExpiresUtc">The short Home expiration time.</param>
/// <param name="Consumed">Whether the store has atomically reserved this confirmation.</param>
internal sealed record RemovalPlan(string PlanId, Guid AdminId, Guid LocalItemId, string FactsSha256, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, bool Consumed = false);

/// <summary>The public-safe confirmation summary; no paths, peer item IDs or credentials.</summary>
/// <param name="PlanId">An opaque identifier for the server-held plan.</param>
/// <param name="Title">Display text only.</param>
/// <param name="BytesHeld">Bytes retained for recovery, not reclaimed space.</param>
/// <param name="ExpiresUtc">When confirmation must be refreshed.</param>
internal sealed record RemovalPlanDto(string PlanId, string Title, long BytesHeld, DateTimeOffset ExpiresUtc);

/// <summary>The public-safe operation status.</summary>
/// <param name="JobId">An opaque identifier.</param>
/// <param name="Phase">The durable recovery phase.</param>
/// <param name="CanRestore">Whether the original remains available to restore.</param>
/// <param name="BytesHeld">Bytes still retained, not reclaimed space.</param>
internal sealed record RemovalJobDto(string JobId, RemovalPhase Phase, bool CanRestore, long BytesHeld);

/// <summary>Immutable private authority for one retained-file deletion. Never a public reply.</summary>
/// <param name="OperationId">Fresh server-generated operation identifier.</param>
/// <param name="StageName">Exact private staging basename bound to job and operation.</param>
/// <param name="AdminId">The local administrator approving irreversible deletion.</param>
/// <param name="ConsentUtc">The time explicit consent was accepted.</param>
/// <param name="FactsSha256">Fresh authorization and content evidence fingerprint.</param>
internal sealed record RemovalPurgeAuthority(string OperationId, string StageName, Guid AdminId, DateTimeOffset ConsentUtc, string FactsSha256);
