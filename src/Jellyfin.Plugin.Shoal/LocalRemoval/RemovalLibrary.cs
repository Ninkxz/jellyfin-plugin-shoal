using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Authoritative movie and complete same-user history operations.</summary>
internal interface IRemovalLibrary
{
    /// <summary>Rechecks a current local administrator immediately before irreversible acceptance.</summary>
    /// <param name="admin">Authenticated local account identity.</param>
    void VerifyPurgeAdministrator(Guid admin) => throw new InvalidOperationException("administratorRequired");

    /// <summary>Reads fresh replacement access and complete personal state after original deindexing.</summary>
    /// <param name="target">The journaled original and checked origin.</param>
    /// <param name="sharedItemId">The journaled shared replacement.</param>
    /// <param name="baseline">The originally affected local users.</param>
    /// <returns>Private immutable evidence. Implementations without this proof fail closed.</returns>
    RemovalPurgeState VerifyPurgeState(RemovalTarget target, Guid sharedItemId, IReadOnlyList<RemovalUserState> baseline) => throw new InvalidOperationException("purgeUnavailable");

    /// <summary>Discovers bounded current real-file movie matches.</summary>
    /// <param name="peerKey">The selected configured friend.</param>
    /// <returns>Unhashed internal candidates.</returns>
    IReadOnlyList<RemovalTarget> Discover(string peerKey);

    /// <summary>Rechecks a server-held selection against current core and grants.</summary>
    /// <param name="target">The selected movie.</param>
    /// <returns>The unchanged current target or a refusal.</returns>
    RemovalTarget Revalidate(RemovalTarget target);

    /// <summary>Captures every eligible visible local user's complete supported personal state.</summary>
    /// <param name="itemId">The currently indexed item.</param>
    /// <returns>Private bounded history records.</returns>
    IReadOnlyList<RemovalUserState> Capture(Guid itemId);

    /// <summary>Requires every captured user to retain current source and mapped replacement access.</summary>
    /// <param name="target">The selected movie and mapping.</param>
    /// <param name="history">The complete affected-user capture.</param>
    void VerifyAccess(RemovalTarget target, IReadOnlyList<RemovalUserState> history);

    /// <summary>Removes only the core index entry after the source file was held.</summary>
    /// <param name="target">The exact absent source.</param>
    void Deindex(RemovalTarget target);

    /// <summary>Imports through normal guarded Shoal sync, without changing personal state.</summary>
    /// <param name="target">The held source.</param>
    /// <param name="history">The durable original personal state.</param>
    /// <param name="cancellationToken">Operation cancellation.</param>
    /// <returns>The unique admitted shared replacement.</returns>
    Task<Guid> ImportAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> history, CancellationToken cancellationToken);

    /// <summary>Transfers original personal state after the replacement identity is durably recorded.</summary>
    /// <param name="target">The held source.</param>
    /// <param name="itemId">The previously proved replacement.</param>
    /// <param name="history">The complete original personal state.</param>
    void Handoff(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history);

    /// <summary>Captures an admitted shared replacement before Restore can retire it.</summary>
    /// <param name="target">The original selection.</param>
    /// <param name="itemId">The previous admitted replacement.</param>
    /// <param name="baseline">The previously affected local users.</param>
    /// <returns>Current shared history, or no records when the replacement is gone.</returns>
    IReadOnlyList<RemovalUserState> CaptureShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> baseline);

    /// <summary>Rechecks exact source membership, duration and every affected user's replacement access.</summary>
    /// <param name="target">The held local movie.</param>
    /// <param name="itemId">The imported replacement.</param>
    /// <param name="history">Affected users.</param>
    void VerifyShared(RemovalTarget target, Guid itemId, IReadOnlyList<RemovalUserState> history);

    /// <summary>Reindexes restored bytes and preserves newer shared or local history.</summary>
    /// <param name="target">The restored original source.</param>
    /// <param name="baseline">History captured before quarantine.</param>
    /// <param name="shared">Current replacement history captured before restoration.</param>
    /// <param name="cancellationToken">Operation cancellation.</param>
    /// <returns>The restored local core item.</returns>
    Task<Guid> RestoreAsync(RemovalTarget target, IReadOnlyList<RemovalUserState> baseline, IReadOnlyList<RemovalUserState> shared, CancellationToken cancellationToken);
}

/// <summary>Private current-state proof; never serialize into an admin response.</summary>
/// <param name="Fingerprint">Digest of current source, topology and complete same-user history.</param>
/// <param name="History">The bounded, freshly admitted user states.</param>
internal sealed record RemovalPurgeState(string Fingerprint, IReadOnlyList<RemovalUserState> History);

/// <summary>Private durable selection. Never serialize into an admin response.</summary>
internal sealed record RemovalTarget
{
    /// <summary>Gets the configured origin.</summary>
    public required string PeerKey { get; init; }

    /// <summary>Gets the current imported mapping.</summary>
    public required string MappingKey { get; init; }

    /// <summary>Gets the peer movie identity.</summary>
    public Guid PeerItemId { get; init; }

    /// <summary>Gets the original local core identity.</summary>
    public Guid LocalItemId { get; init; }

    /// <summary>Gets its physical collection folder.</summary>
    public Guid LibraryId { get; init; }

    /// <summary>Gets display-only title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the authoritative source root.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the original movie path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the derived unscanned recovery root.</summary>
    public required string RecoveryRoot { get; init; }

    /// <summary>Gets the current strong provider identities.</summary>
    public required IReadOnlyDictionary<string, string> ProviderIds { get; init; }

    /// <summary>Gets the actual indexed duration.</summary>
    public long RuntimeTicks { get; init; }

    /// <summary>Gets current source bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets current core/configuration generation.</summary>
    public required string Generation { get; init; }
}

/// <summary>Complete supported personal movie state, confined to one local user.</summary>
/// <param name="UserId">The owning local user.</param>
/// <param name="Observed">Whether core hydrated an existing personal row.</param>
/// <param name="Played">Watched status.</param>
/// <param name="Position">Exact resume ticks.</param>
/// <param name="PlayCount">Completed play count.</param>
/// <param name="LastPlayedUtc">Last played date.</param>
/// <param name="Favorite">Favorite status.</param>
/// <param name="Rating">Personal rating.</param>
/// <param name="Likes">Personal liked status.</param>
/// <param name="AudioIndex">Selected audio track.</param>
/// <param name="SubtitleIndex">Selected subtitle track.</param>
internal sealed record RemovalUserState(Guid UserId, bool Observed, bool Played, long Position, int PlayCount, DateTime? LastPlayedUtc, bool Favorite, double? Rating, bool? Likes, int? AudioIndex, int? SubtitleIndex);

/// <summary>Conservative history conflict rules, including explicit zero/reset rows.</summary>
internal static class RemovalHistory
{
    /// <summary>Chooses a handoff value without replacing existing different shared history.</summary>
    /// <param name="baseline">The original complete capture.</param>
    /// <param name="current">Current shared state.</param>
    /// <returns>The desired same-user state.</returns>
    public static RemovalUserState Handoff(RemovalUserState baseline, RemovalUserState current)
    {
        if (baseline.UserId != current.UserId)
        {
            throw new InvalidOperationException("historyUserChanged");
        }

        if (SameValues(baseline, current))
        {
            return current;
        }

        if (current.Observed || current.Played || current.Position != 0 || current.PlayCount != 0 || current.LastPlayedUtc.HasValue || current.Favorite || current.Rating.HasValue || current.Likes.HasValue || current.AudioIndex.HasValue || current.SubtitleIndex.HasValue)
        {
            throw new InvalidOperationException("historyConflict");
        }

        return baseline;
    }

    /// <summary>Keeps edits to the restored local row; otherwise carries current shared state, including resets.</summary>
    /// <param name="baseline">The original pre-quarantine capture.</param>
    /// <param name="current">The reindexed original's current state.</param>
    /// <param name="shared">Current shared state, or none when unavailable.</param>
    /// <returns>The desired same-user state.</returns>
    public static RemovalUserState Restore(RemovalUserState baseline, RemovalUserState current, RemovalUserState? shared)
    {
        if (baseline.UserId != current.UserId || (shared is not null && shared.UserId != current.UserId))
        {
            throw new InvalidOperationException("historyUserChanged");
        }

        return SameValues(current, baseline) || Pristine(current) ? shared ?? baseline : current;
    }

    /// <summary>Compares personal values independently of whether a matching row was already materialized.</summary>
    /// <param name="left">One user's capture.</param>
    /// <param name="right">The same user's capture.</param>
    /// <returns>Whether every supported personal value agrees.</returns>
    public static bool SameValues(RemovalUserState left, RemovalUserState right) => left with { Observed = false } == right with { Observed = false };

    private static bool Pristine(RemovalUserState value) => !value.Observed && !value.Played && value.Position == 0 && value.PlayCount == 0 && !value.LastPlayedUtc.HasValue && !value.Favorite && !value.Rating.HasValue && !value.Likes.HasValue && !value.AudioIndex.HasValue && !value.SubtitleIndex.HasValue;
}

/// <summary>Prevents a verified origin from authorizing an unchecked alternative cut.</summary>
internal static class RemovalSelection
{
    /// <summary>Requires exactly one recorded origin, equal to the fresh checked source.</summary>
    /// <param name="members">Current recorded origins.</param>
    /// <param name="peer">The checked configured friend.</param>
    /// <param name="item">The checked remote movie.</param>
    /// <returns>Whether the sole source is the checked origin.</returns>
    public static bool UniqueOrigin(IReadOnlyList<GroupMember> members, string peer, Guid item) => members.Count == 1 && members[0].PeerKey == peer && Guid.TryParse(members[0].PeerItemId, out var id) && id == item;
}
