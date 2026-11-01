using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Pure eligibility rules for recoverable single-file movie removal. Never touches files.</summary>
internal static class RemovalPolicy
{
    private static readonly string[] Providers = ["Imdb", "Tmdb", "Tvdb"];

    /// <summary>Gets the supported single-file size cap; larger files remain ineligible.</summary>
    public static long MaxFileSizeBytes { get; } = 32L * 1024 * 1024 * 1024;

    /// <summary>Gets the maximum time an adapter may spend hashing one file.</summary>
    public static TimeSpan HashingDeadline { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the maximum lifetime of an admin confirmation.</summary>
    public static TimeSpan PlanLifetime { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the lifetime of a discovered candidate handle (2.10.3 K1): long enough for a queue of many Checks, each of which still re-proves everything.</summary>
    public static TimeSpan CandidateLifetime { get; } = TimeSpan.FromMinutes(60);

    /// <summary>Gets the most candidate handles kept in memory across list reads; the oldest go first.</summary>
    public static int MaxCandidates { get; } = 300;

    /// <summary>
    /// Gets how long a move waits for Jellyfin to index the shared copy, and a Restore for the restored original (2.10.3: was a fixed 60 s, which
    /// Jellyfin's own change-report delay and Shoal's one-scan-in-five-minutes limit routinely outlast). On timeout nothing is weakened: a move rolls
    /// back to the verified original as before, a Restore keeps the original in place and reports that it needs attention.
    /// </summary>
    public static TimeSpan IndexWait { get; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the pause between two looks at the index while waiting.</summary>
    public static TimeSpan IndexPoll { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets how many looks fit in <see cref="IndexWait"/>.</summary>
    public static int IndexPolls => (int)(IndexWait.Ticks / IndexPoll.Ticks);

    /// <summary>Gets how many looks pass between two catalog syncs while a move waits (one every 10 s, not one every 2.5 s).</summary>
    public static int IndexPollsPerSync { get; } = 20;

    /// <summary>Gets how many looks (30 s) a restore gives core to settle the restored movie and a running sync to free the gate before the stub is left to the next sync.</summary>
    public static int RetireLooks { get; } = 60;

    /// <summary>Gets the maximum age of locally observed authorization and source proof.</summary>
    public static TimeSpan EvidenceLifetime { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets the largest file "delete like Jellyfin" handles (2.10.4, owner 2026-10-28: 100 GiB, up from the 32 GiB of recoverable removal).
    /// The legacy move, purge, journal and native probe keep <see cref="MaxFileSizeBytes"/>.
    /// </summary>
    public static long MaxDeleteFileBytes { get; } = 100L * 1024 * 1024 * 1024;

    /// <summary>Gets the fixed part of the delete hashing deadline (open, stat, the last read and slack on a busy share).</summary>
    public static TimeSpan DeleteHashingBase { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the time allowed per started GiB: 75 s is a floor of about 14.3 MB/s (a slow SMB share or a busy disk).</summary>
    public static TimeSpan DeleteHashingPerGib { get; } = TimeSpan.FromSeconds(75);

    /// <summary>Gets the hard maximum of one delete hash, local or on the friend: the deadline of a 100 GiB file, 130 minutes.</summary>
    public static TimeSpan DeleteHashingMaximum => DeleteHashingDeadlineFor(MaxDeleteFileBytes);

    /// <summary>Whether a file length can be deleted like Jellyfin (1 byte to exactly 100 GiB).</summary>
    /// <param name="bytes">The file length.</param>
    /// <returns>True inside the range.</returns>
    public static bool DeleteSizeAllowed(long bytes) => bytes > 0 && bytes <= MaxDeleteFileBytes;

    /// <summary>
    /// The hashing deadline for "delete like Jellyfin" (portable probe on Home and on the friend, and the friend call's wait): 5 minutes plus 75 s per
    /// started GiB, capped at the 100 GiB figure. At 17 MB/s a 100 GiB file needs about 105 minutes; the cap is 130 minutes.
    /// </summary>
    /// <param name="bytes">The file length; zero or less gets the base, above 100 GiB the cap.</param>
    /// <returns>A deadline between 5 and 130 minutes.</returns>
    public static TimeSpan DeleteHashingDeadlineFor(long bytes) => bytes <= 0
        ? DeleteHashingBase : DeleteHashingBase + (DeleteHashingPerGib * ((Math.Min(bytes, MaxDeleteFileBytes) + (1L << 30) - 1) >> 30));

    /// <summary>Scales the hashing deadline by size: the base plus one minute per started GiB (at least about 17 MB/s).</summary>
    /// <param name="bytes">The file length; zero or less gets the base deadline, above MaxFileSizeBytes the cap (2.10.4, Scout 04 C3: was the base 2 minutes).</param>
    /// <returns>A deadline between 2 and 34 minutes.</returns>
    public static TimeSpan HashingDeadlineFor(long bytes) => bytes <= 0
        ? HashingDeadline : HashingDeadline + TimeSpan.FromMinutes((Math.Min(bytes, MaxFileSizeBytes) + (1L << 30) - 1) >> 30);

    /// <summary>Returns null only when every independent requirement is established.</summary>
    /// <param name="local">Current physical local movie evidence.</param>
    /// <param name="remote">Current physical friend movie evidence.</param>
    /// <param name="context">Fresh owner, access and lifecycle facts.</param>
    /// <param name="now">The current Home clock.</param>
    /// <returns>A stable refusal code, or null when recoverable quarantine may be planned.</returns>
    public static string? Refusal(RemovalMediaEvidence local, RemovalMediaEvidence remote, RemovalContext context, DateTimeOffset now)
    {
        if (!context.Enabled)
        {
            return "disabled";
        }

        if (!context.StateAndCatalogsReady)
        {
            return "recovery";
        }

        if (!context.PeerAdmitted || !context.GrantCurrent)
        {
            return "sourceNotAdmitted";
        }

        if (context.Busy)
        {
            return "busy";
        }

        if (context.EvidenceUtc > now || now - context.EvidenceUtc > EvidenceLifetime)
        {
            return "staleEvidence";
        }

        if (local.ItemId == Guid.Empty || remote.ItemId == Guid.Empty)
        {
            return "invalidItem";
        }

        if (!local.IsMovie || !remote.IsMovie)
        {
            return "moviesOnly";
        }

        if (!local.IsPhysicalFile || !remote.IsPhysicalFile)
        {
            return "physicalOriginRequired";
        }

        if (!local.IsSingleVersion || !remote.IsSingleVersion)
        {
            return "unsupportedVersions";
        }

        if (!local.FileIdentityVerified || !remote.FileIdentityVerified)
        {
            return "fileIdentityUnverified";
        }

        if (!StrongMatch(local.ProviderIds, remote.ProviderIds))
        {
            return "identityUnverified";
        }

        if (local.SizeBytes <= 0 || local.SizeBytes > MaxFileSizeBytes || local.SizeBytes != remote.SizeBytes
            || !IsSha256(local.ContentSha256) || !IsSha256(remote.ContentSha256)
            || !string.Equals(local.ContentSha256, remote.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "contentNotEquivalent";
        }

        if (!context.IndependentOriginVerified)
        {
            return "originUnverified";
        }

        if (!context.AllUsersAdmitted)
        {
            return "userAccessUnverified";
        }

        if (!context.HistoryHandoffVerified)
        {
            return "historyUnverified";
        }

        if (!context.ReplacementPlaybackVerified)
        {
            return "playbackUnverified";
        }

        if (!context.RecoverableStorageVerified)
        {
            return "recoveryStorageUnverified";
        }

        return null;
    }

    /// <summary>Revalidates an existing server-held plan against caller, clock and current fact generation.</summary>
    /// <param name="plan">The stored immutable plan.</param>
    /// <param name="adminId">The currently elevated local administrator.</param>
    /// <param name="factsSha256">The freshly recomputed fact generation digest.</param>
    /// <param name="now">The Home clock.</param>
    /// <returns>A stable refusal code, or null if this plan is currently usable.</returns>
    public static string? PlanRefusal(RemovalPlan plan, Guid adminId, string factsSha256, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(plan.PlanId, "N", out var id) || id == Guid.Empty || plan.LocalItemId == Guid.Empty)
        {
            return "invalidPlan";
        }

        if (adminId == Guid.Empty || plan.AdminId != adminId)
        {
            return "wrongAdministrator";
        }

        if (plan.Consumed)
        {
            return "planAlreadyUsed";
        }

        if (plan.CreatedUtc > now || plan.ExpiresUtc <= now || plan.ExpiresUtc <= plan.CreatedUtc
            || plan.ExpiresUtc - plan.CreatedUtc > PlanLifetime)
        {
            return "expiredPlan";
        }

        if (!IsSha256(plan.FactsSha256) || !IsSha256(factsSha256)
            || !string.Equals(plan.FactsSha256, factsSha256, StringComparison.Ordinal))
        {
            return "planChanged";
        }

        return null;
    }

    /// <summary>Checks a journal transition. Restored and Purged are immutable terminal states.</summary>
    /// <param name="from">The durable current phase.</param>
    /// <param name="to">The proposed next phase.</param>
    /// <returns>Whether the transition is permitted by the recoverable protocol.</returns>
    public static bool CanTransition(RemovalPhase from, RemovalPhase to) => (from, to) switch
    {
        (RemovalPhase.Prepared, RemovalPhase.Quarantined or RemovalPhase.Restored or RemovalPhase.NeedsRecovery) => true,
        (RemovalPhase.Quarantined, RemovalPhase.ReplacementReady or RemovalPhase.Restored or RemovalPhase.NeedsRecovery) => true,
        (RemovalPhase.ReplacementReady, RemovalPhase.Restored or RemovalPhase.NeedsRecovery or RemovalPhase.PurgePrepared) => true,
        (RemovalPhase.NeedsRecovery, RemovalPhase.Quarantined or RemovalPhase.Restored) => true,

        // ReplacementReady here is only the proved pre-commit rollback; RemovalJournal.ValidateSuccessor binds it.
        (RemovalPhase.PurgePrepared, RemovalPhase.PurgeCommitting or RemovalPhase.PurgeOutcomeUnknown or RemovalPhase.ReplacementReady) => true,

        // 2.10.3 startup sweep (W20-5, P30-KILL-STUCK): a PurgeCommitting job whose stage or held name still holds the exact object after a restart
        // was never unlinked (an unlink removes the name atomically, and the process that could have issued it is gone); RemovalJournal.ValidateSuccessor
        // binds this return exactly like the PurgePrepared pre-commit rollback.
        (RemovalPhase.PurgeCommitting, RemovalPhase.Purged or RemovalPhase.PurgeOutcomeUnknown or RemovalPhase.ReplacementReady) => true,

        // 2.10.3 W22-2: an outcome-unknown delete is probed again at the next start; only the exact object still at exactly one of its two names
        // (never unlinked: an unlink removes the name) returns it, bound by RemovalJournal.ValidateSuccessor like the rollbacks above.
        (RemovalPhase.PurgeOutcomeUnknown, RemovalPhase.ReplacementReady) => true,
        _ => false,
    };

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool StrongMatch(IReadOnlyDictionary<string, string>? local, IReadOnlyDictionary<string, string>? remote)
    {
        if (local is null || remote is null)
        {
            return false;
        }

        var shared = false;
        foreach (var provider in Providers)
        {
            var a = Values(local, provider);
            var b = Values(remote, provider);
            if (a.Length > 1 || b.Length > 1)
            {
                return false;
            }

            var left = a.Length == 0 ? null : Canonical(a[0], provider);
            var right = b.Length == 0 ? null : Canonical(b[0], provider);
            if ((a.Length > 0 && left is null) || (b.Length > 0 && right is null))
            {
                return false;
            }

            if (left is not null && right is not null)
            {
                if (!string.Equals(left, right, StringComparison.Ordinal))
                {
                    return false;
                }

                shared = true;
            }
        }

        return shared;
    }

    private static string[] Values(IReadOnlyDictionary<string, string> ids, string provider) => ids
        .Where(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Select(static p => p.Value).ToArray();

    private static string? Canonical(string value, string provider)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (provider == "Imdb")
        {
            return trimmed.Length is >= 3 and <= 12 && trimmed.StartsWith("tt", StringComparison.Ordinal)
                && trimmed.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0 ? trimmed : null;
        }

        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
}
