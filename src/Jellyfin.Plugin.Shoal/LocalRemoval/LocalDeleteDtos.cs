using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

// 2.10.4 C0: the public shapes of "delete like Jellyfin" (docs/plans/atlas-delete-like-jellyfin-2.10.4.md, Table 2). Golden files:
// tests/contracts/local-delete.*.json. Every reply is written by Http/ResponseWriter (camelCase, enums by member name).
// Http/ResponseWriter keeps null fields, so an optional field below is always PRESENT and null when it does not apply (stable key sets for
// the UI). No path, token, key or peer item id is in any of these.
// Both servers must run 2.10.4 for the friend-side proof of a file on a network share; an older friend answers like an offline one
// (reason originUnavailable).

/// <summary>Where one ticked movie is in a deletion batch.</summary>
internal enum LocalDeleteItemState
{
    /// <summary>Waiting for its turn.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>Hashing the local file and asking the friend for the same proof.</summary>
    [JsonStringEnumMemberName("checking")]
    Checking,

    /// <summary>Jellyfin is deleting the item and its folder.</summary>
    [JsonStringEnumMemberName("deleting")]
    Deleting,

    /// <summary>Deleted and confirmed gone from disk and the index.</summary>
    [JsonStringEnumMemberName("done")]
    Done,

    /// <summary>A check failed before the delete call; nothing was deleted. <see cref="LocalDeleteItemDto.Reason"/> says why.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>The delete call ran but the outcome could not be confirmed; the admin checks this one by hand. Stops the batch.</summary>
    [JsonStringEnumMemberName("notConfirmed")]
    NotConfirmed,

    /// <summary>Not tried because the batch stopped first.</summary>
    [JsonStringEnumMemberName("notAttempted")]
    NotAttempted,
}

/// <summary>Where a deletion batch is.</summary>
internal enum LocalDeleteBatchState
{
    /// <summary>Items are being checked and deleted, one at a time.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>All items ended; one sync runs and Shoal waits up to 90 s for the friend's copies to appear.</summary>
    [JsonStringEnumMemberName("settling")]
    Settling,

    /// <summary>Every item ran (or was refused) and the settle pass ended.</summary>
    [JsonStringEnumMemberName("finished")]
    Finished,

    /// <summary>Stop, switch off or a stop-the-batch reason ended it early; the rest are notAttempted.</summary>
    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>Why an item was refused or a batch stopped. The UI maps each word to plain copy (Table 2 row 9).</summary>
internal enum LocalDeleteReason
{
    /// <summary>The ticked movie is no longer a candidate (fresh resolve found no match).</summary>
    [JsonStringEnumMemberName("candidateChanged")]
    CandidateChanged,

    /// <summary>The friend's file is not the same length and SHA-256.</summary>
    [JsonStringEnumMemberName("contentNotEquivalent")]
    ContentNotEquivalent,

    /// <summary>Who can see the movie, or their watch state, changed during the check.</summary>
    [JsonStringEnumMemberName("historyChanged")]
    HistoryChanged,

    /// <summary>A person on this server cannot see the friend's copy, so deleting would take the movie away from them. That item only.</summary>
    [JsonStringEnumMemberName("accessNotShared")]
    AccessNotShared,

    /// <summary>The file or item changed between the hash and the delete call.</summary>
    [JsonStringEnumMemberName("removalChanged")]
    RemovalChanged,

    /// <summary>The folder Jellyfin would delete holds other media, a library root or the stub root.</summary>
    [JsonStringEnumMemberName("folderNotDedicated")]
    FolderNotDedicated,

    /// <summary>The admin's Jellyfin account may not delete library content.</summary>
    [JsonStringEnumMemberName("deleteNotPermitted")]
    DeleteNotPermitted,

    /// <summary>Jellyfin could not delete the file (share permissions, read-only mount); the movie file is intact.</summary>
    [JsonStringEnumMemberName("deleteFailed")]
    DeleteFailed,

    /// <summary>Not a single-file local movie (stub, shortcut, versions, external subtitles).</summary>
    [JsonStringEnumMemberName("unsupportedItem")]
    UnsupportedItem,

    /// <summary>The friend's server could not confirm the copy (offline, 404, 409, bad range, or older than 2.10.4 on a network share). Stops the batch.</summary>
    [JsonStringEnumMemberName("originUnavailable")]
    OriginUnavailable,

    /// <summary>The friend's server asked to wait. Stops the batch.</summary>
    [JsonStringEnumMemberName("originRateLimited")]
    OriginRateLimited,

    /// <summary>Another removal or a sync is running. Stops the batch.</summary>
    [JsonStringEnumMemberName("busy")]
    Busy,

    /// <summary>The switch was turned off. Stops the batch.</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,

    /// <summary>The starting admin is no longer an enabled administrator. Stops the batch.</summary>
    [JsonStringEnumMemberName("administratorRequired")]
    AdministratorRequired,

    /// <summary>A library scan is running. Stops the batch.</summary>
    [JsonStringEnumMemberName("libraryBusy")]
    LibraryBusy,

    /// <summary>An earlier item ended notConfirmed, so the rest were not tried.</summary>
    [JsonStringEnumMemberName("notConfirmed")]
    NotConfirmed,
}

/// <summary>Whether the friend's copy was visible after the settle pass.</summary>
internal enum LocalDeleteShared
{
    /// <summary>The friend's copy is in the library.</summary>
    [JsonStringEnumMemberName("present")]
    Present,

    /// <summary>Not visible yet; the next sync adds it. Not a failure.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,
}

/// <summary>Whether watch history reached the friend's copy.</summary>
internal enum LocalDeleteHistory
{
    /// <summary>Watched state, resume point and favourites were copied to the friend's copy.</summary>
    [JsonStringEnumMemberName("carried")]
    Carried,

    /// <summary>The copy appeared too late (or not at all) in the settle pass, so history was not copied.</summary>
    [JsonStringEnumMemberName("notCarried")]
    NotCarried,
}

/// <summary>One deletable movie in <c>GET Peers/{handle}/LocalRemoval</c>.</summary>
/// <param name="CandidateId">Random 32 hex per read, valid 60 minutes.</param>
/// <param name="Title">Display text.</param>
/// <param name="Year">Production year; null when unknown.</param>
/// <param name="Bytes">The local file length (the dialog sums these).</param>
internal sealed record LocalDeleteCandidateDto(string CandidateId, string Title, int? Year, long Bytes);

/// <summary>A job left by 2.10.2/2.10.3 recoverable removal (Restore only).</summary>
/// <param name="JobId">32 hex.</param>
/// <param name="Title">Display text.</param>
/// <param name="Phase">The journal phase word (prepared, quarantined, replacementReady, needsRecovery, purgePrepared, purgeCommitting, purgeOutcomeUnknown).</param>
/// <param name="BytesHeld">Bytes held in the recovery folder.</param>
/// <param name="CanRestore">Whether the Restore button shows.</param>
/// <param name="Error"><c>recoveryRequired</c> for a row that needs attention; null otherwise.</param>
internal sealed record LocalDeleteLegacyJobDto(string JobId, string Title, string Phase, long BytesHeld, bool CanRestore, string? Error);

/// <summary>The removal panel state (routes 1 and 2).</summary>
/// <param name="Enabled">The one switch, <c>LocalDeleteEnabled</c>.</param>
/// <param name="DeleteAvailable">False only when Shoal's state is not ready.</param>
/// <param name="Candidates">Deletable movies (empty for Status or when the switch is off).</param>
/// <param name="LegacyJobs">Held originals from earlier versions; Restore only. Empty for almost every install.</param>
/// <param name="ActiveBatchId">The running or settling batch; null when none.</param>
internal sealed record LocalDeleteStateDto(bool Enabled, bool DeleteAvailable, IReadOnlyList<LocalDeleteCandidateDto> Candidates, IReadOnlyList<LocalDeleteLegacyJobDto> LegacyJobs, string? ActiveBatchId);

/// <summary>One movie in a batch.</summary>
/// <param name="ItemKey">Server-minted 32 hex, stable for the batch.</param>
/// <param name="Title">Display text.</param>
/// <param name="Year">Null when unknown.</param>
/// <param name="Bytes">Local file length.</param>
/// <param name="State">Where the item is.</param>
/// <param name="Reason">Set for refused, notConfirmed and notAttempted. On a <c>checking</c> item, <c>originRateLimited</c> means Home is waiting for the friend's limit and will try the same item again ("Waiting for your friend's server"). Null otherwise.</param>
/// <param name="Shared">Set for done after the settle pass; null before.</param>
/// <param name="History">Set for done after the settle pass (carried or notCarried); null before.</param>
internal sealed record LocalDeleteItemDto(string ItemKey, string Title, int? Year, long Bytes, LocalDeleteItemState State, LocalDeleteReason? Reason = null, LocalDeleteShared? Shared = null, LocalDeleteHistory? History = null);

/// <summary>Item totals. <see cref="Queued"/> counts queued, checking and deleting (not ended yet).</summary>
/// <param name="Done">Deleted.</param>
/// <param name="Refused">Refused, nothing deleted.</param>
/// <param name="NotConfirmed">Check by hand.</param>
/// <param name="NotAttempted">Skipped after a stop.</param>
/// <param name="Queued">Not ended yet.</param>
internal sealed record LocalDeleteCountsDto(int Done, int Refused, int NotConfirmed, int NotAttempted, int Queued);

/// <summary>The 202 reply of <c>POST LocalRemoval/Deletions</c>.</summary>
/// <param name="BatchId">32 hex.</param>
/// <param name="State">Always running.</param>
/// <param name="Items">Every resolved item, all queued.</param>
internal sealed record LocalDeleteStartDto(string BatchId, LocalDeleteBatchState State, IReadOnlyList<LocalDeleteItemDto> Items);

/// <summary>A batch (routes 5, 6 and 7).</summary>
/// <param name="BatchId">32 hex.</param>
/// <param name="State">Where the batch is.</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="FinishedUtc">When it finished or stopped; null while running or settling.</param>
/// <param name="Stopping">True after Stop until the in-flight item ends; false otherwise.</param>
/// <param name="Counts">Item totals.</param>
/// <param name="Items">Every item, in order.</param>
internal sealed record LocalDeleteBatchDto(string BatchId, LocalDeleteBatchState State, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, bool Stopping, LocalDeleteCountsDto Counts, IReadOnlyList<LocalDeleteItemDto> Items);
