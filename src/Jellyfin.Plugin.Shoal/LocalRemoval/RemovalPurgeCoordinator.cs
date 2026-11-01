using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Explicit permanent deletion after fresh review of a journaled retained original.</summary>
public sealed partial class RemovalCoordinator
{
    // Pre-commit failures already shown to the admin as ordinary retryable refusals; anything else becomes removalChanged.
    private static readonly HashSet<string> RollbackRefusals = new(StringComparer.Ordinal)
    {
        "planChanged", "disabled", "administratorRequired", "unsupportedPlatform", "contentNotEquivalent", "originUnavailable", "originRateLimited", "historyChanged", "accessNotShared",
    };

    private readonly Dictionary<string, PendingPurge> _purgePlans = new(StringComparer.Ordinal);
    private int _swept; // 1 once the startup sweep of interrupted deletes has run in this process

    /// <summary>Saves the independent default-off irreversible-operation toggle.</summary>
    /// <param name="enabled">Explicit local administrator choice.</param>
    /// <returns>The saved toggle.</returns>
    internal object EnablePermanentDeletion(bool enabled)
    {
        var settings = _settings.Current;
        settings.PermanentDeletionEnabled = enabled;
        _settings.Save(settings);
        if (!enabled)
        {
            lock (_memory)
            {
                _purgePlans.Clear();
            }
        }

        return new { enabled };
    }

    /// <summary>Creates a fresh short-lived review without changing retained media.</summary>
    /// <param name="admin">The currently elevated local administrator.</param>
    /// <param name="jobId">An opaque journaled job identifier.</param>
    /// <param name="cancellationToken">Read-only review cancellation.</param>
    /// <returns>The safe display summary and fixed confirmation phrase.</returns>
    internal async Task<object> CreatePurgePlanAsync(Guid admin, string jobId, CancellationToken cancellationToken)
    {
        EnsurePurgeEnabled(admin);
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Refused("busy");
        }

        try
        {
            using var hold = _sync.TryHold() ?? throw Refused("busy");
            var evidence = await VerifyPurgeAsync(jobId, cancellationToken).ConfigureAwait(false);
            EnsurePurgeEnabled(admin);
            var id = Guid.NewGuid().ToString("N");
            var until = _time.GetUtcNow() + RemovalPolicy.PlanLifetime;
            lock (_memory)
            {
                foreach (var expired in _purgePlans.Where(p => p.Value.Until <= _time.GetUtcNow()).Select(p => p.Key).ToArray())
                {
                    _purgePlans.Remove(expired);
                }

                if (_purgePlans.Count >= 100)
                {
                    throw Refused("busy");
                }

                _purgePlans[id] = new(admin, until, evidence);
            }

            return new { planId = id, title = evidence.Job.Title, year = evidence.Context.Target.Year, logicalBytes = evidence.Held.Length, expiresUtc = until, confirmation = "DELETE ORIGINAL FOREVER" };
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>Consumes one reviewed consent, stages the exact retained file and records an honest outcome.</summary>
    /// <param name="admin">The currently elevated local administrator.</param>
    /// <param name="planId">A same-admin one-use review handle.</param>
    /// <param name="cancellationToken">Cancellation before accepting the operation.</param>
    /// <returns>The authoritative durable job projection.</returns>
    internal async Task<object> ConfirmPurgeAsync(Guid admin, string planId, CancellationToken cancellationToken)
    {
        EnsurePurgeEnabled(admin);
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Refused("busy");
        }

        try
        {
            PendingPurge plan;
            lock (_memory)
            {
                if (!_purgePlans.TryGetValue(planId, out plan!) || plan.Admin != admin)
                {
                    throw Refused("planChanged");
                }

                _purgePlans.Remove(planId);
            }

            if (plan.Until <= _time.GetUtcNow())
            {
                throw Refused("planExpired");
            }

            // Up to four size-scaled local hashes (review, stage, final) plus the peer's own hash run under this token.
            using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(10) + (4 * RemovalPolicy.HashingDeadlineFor(plan.Evidence.Held.Length)));
            using var hold = _sync.TryHold() ?? throw Refused("busy");
            var fresh = await VerifyPurgeAsync(plan.Evidence.Job.JobId, operation.Token).ConfigureAwait(false);
            if (PurgeFacts(fresh) != PurgeFacts(plan.Evidence))
            {
                throw Refused("planChanged");
            }

            EnsurePurgeEnabled(admin);
            var operationId = Guid.NewGuid().ToString("N");
            var authority = new RemovalPurgeAuthority(operationId, ".purge-" + fresh.Job.JobId + "-" + operationId + ".media", admin, _time.GetUtcNow(), PurgeFacts(fresh));
            var job = fresh.Job with { Phase = RemovalPhase.PurgePrepared, Purge = authority };
            await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
            var prepared = job;
            var committing = false;
            var rolledBack = false; // set only once a NoUnlink commit was proved rolled back: its refusal must not reach the outcome-unknown fallback
            RemovalPurgeStage? stage = null;
            try
            {
                stage = await _io.PurgeStage(job, operation.Token).ConfigureAwait(false);
                var remote = await _origin.ProveAsync(fresh.Context.Target, operation.Token).ConfigureAwait(false);
                Equivalent(stage.Snapshot, remote);
                var current = _library.VerifyPurgeState(fresh.Context.Target, fresh.Context.SharedItemId, fresh.Context.Baseline);
                if (current.Fingerprint != fresh.Shared.Fingerprint || Fingerprint(remote) != Fingerprint(fresh.Origin))
                {
                    throw Refused("planChanged");
                }

                EnsurePurgeEnabled(admin);

                // Set before the save: a save that throws may still have made PurgeCommitting durable,
                // and from then on rollback is never attempted.
                committing = true;
                job = job with { Phase = RemovalPhase.PurgeCommitting };
                await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);

                // No awaits between the last local observation/opt-in check and
                // native commit except the native full-file verification itself.
                EnsurePurgeEnabled(admin);
                if (_library.VerifyPurgeState(fresh.Context.Target, fresh.Context.SharedItemId, fresh.Context.Baseline).Fingerprint != fresh.Shared.Fingerprint)
                {
                    throw Refused("planChanged");
                }

                Action authorize = () =>
                {
                    EnsurePurgeEnabled(admin);
                    if (_library.VerifyPurgeState(fresh.Context.Target, fresh.Context.SharedItemId, fresh.Context.Baseline).Fingerprint != fresh.Shared.Fingerprint)
                    {
                        throw Refused("planChanged");
                    }
                };
                var outcome = await _io.PurgeCommit(job, stage, authorize, operation.Token).ConfigureAwait(false);
                if (outcome == RemovalPurgeResult.NoUnlink)
                {
                    // 2.10.3 W22-2: the commit refused, was cancelled or failed before any unlink, so the file is intact under its stage name. Return it
                    // with the same native probe the start-up sweep uses (exact object at exactly one name, renamed back without overwrite); any doubt
                    // keeps the old outcome-unknown record below.
                    stage.Dispose();
                    using var undo = new CancellationTokenSource(RemovalPolicy.HashingDeadlineFor(prepared.Held!.Length) + TimeSpan.FromMinutes(1));
                    if (await TryRollBackPurgeAsync(job, null, undo.Token).ConfigureAwait(false))
                    {
                        rolledBack = true;
                        throw Refused("removalChanged"); // the same refusal as a rollback before commit; the job is recoverable again
                    }
                }

                job = job with { Phase = outcome == RemovalPurgeResult.UnlinkedAndFlushed ? RemovalPhase.Purged : RemovalPhase.PurgeOutcomeUnknown };
                await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
                return Public(job);
            }
            catch (Exception ex) when (!rolledBack && (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException
                || (!committing && ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))))
            {
                // Never call the ordinary removal rollback: a successful unlink
                // or failed final directory/journal flush cannot promise Restore.
                // Before PurgeCommitting nothing can have been unlinked, so a proved
                // rename back to the held name returns the job to ReplacementReady.
                // Any exception type qualifies before commit, so a library or parser
                // failure cannot strand the original under its private stage name.
                if (!committing)
                {
                    using var rollback = new CancellationTokenSource(RemovalPolicy.HashingDeadlineFor(prepared.Held!.Length) + TimeSpan.FromMinutes(1));
                    if (await TryRollBackPurgeAsync(prepared, stage, rollback.Token).ConfigureAwait(false))
                    {
                        throw Refused(ex is InvalidOperationException && RollbackRefusals.Contains(ex.Message) ? ex.Message : "removalChanged");
                    }
                }

                // The fallback has its own budget so a slow rollback cannot leave the job PurgePrepared.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var latest = (await _journal.LoadAsync(recovery.Token).ConfigureAwait(false)).Single(j => j.JobId == job.JobId);
                if (latest.Phase == RemovalPhase.Purged)
                {
                    return Public(latest);
                }

                job = latest with { Phase = RemovalPhase.PurgeOutcomeUnknown };
                await _journal.SaveAsync(job, recovery.Token).ConfigureAwait(false);
                return Public(job);
            }
            finally
            {
                stage?.Dispose();
            }
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>
    /// 2.10.3 startup sweep (W20-5, Pilot P30-KILL-STUCK): classifies each delete a previous process left PurgePrepared or PurgeCommitting. The native
    /// probe (the same one the pre-commit rollback uses after a lost stage claim) decides: the exact object still at the held name, or only at the stage
    /// name and renamed back without overwrite, returns the job to ReplacementReady, so Restore works again; both names, neither, or any doubt records
    /// PurgeOutcomeUnknown for that job alone. Nothing is ever deleted here. Runs once per process, under the single run lock.
    /// Warden 22: a cancelled sweep (shutdown, or an aborted Restore request) saves nothing, so cancellation never makes a job outcome-unknown (W22-1);
    /// a probe that found no proof is asked once more, so a rename back whose re-hash failed is found at the held name (W22-1); an outcome-unknown job
    /// is probed again and returned only on the same proof, never re-marked (W22-2); the sweep counts as done only when every save went through (W22-3).
    /// </summary>
    /// <param name="cancellationToken">Cancellation; a job not yet classified stays as it was and the next start tries again.</param>
    /// <param name="fromRestore">A Restore asked: a live removal holding the run lock answers busy, a journal that cannot be read recoveryRequired (W22-4).</param>
    /// <returns>How many jobs were classified.</returns>
    internal async Task<int> SweepInterruptedAsync(CancellationToken cancellationToken, bool fromRestore = false)
    {
        if (Volatile.Read(ref _swept) != 0 || !_io.Supported())
        {
            return 0;
        }

        if (fromRestore)
        {
            if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw Refused("busy");
            }
        }
        else
        {
            await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (Volatile.Read(ref _swept) != 0)
            {
                return 0;
            }

            IReadOnlyList<RemovalJob> jobs;
            try
            {
                jobs = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (fromRestore && ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw Refused("recoveryRequired");
            }

            var classified = 0;
            var complete = true;
            foreach (var job in jobs.Where(static j => j.Phase is RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.PurgeOutcomeUnknown))
            {
                var outcome = await ProbeInterruptedAsync(job, cancellationToken).ConfigureAwait(false);
                if (outcome is null && job.Phase == RemovalPhase.PurgeOutcomeUnknown)
                {
                    continue; // still no proof: it stays as recorded, nothing is written
                }

                var next = outcome is not null
                    ? job with { Phase = RemovalPhase.ReplacementReady, Purge = null, Held = outcome }
                    : job with { Phase = RemovalPhase.PurgeOutcomeUnknown };
                try
                {
                    await _journal.SaveAsync(next, cancellationToken).ConfigureAwait(false);
                    classified++;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // The journal refused (for example an uncommitted record is present): leave the job as recorded; a Restore or the next start tries again.
                    complete = false;
                }
            }

            if (complete)
            {
                Volatile.Write(ref _swept, 1);
            }

            return classified;
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>The proved held snapshot of an interrupted delete, or null for any doubt; throws when cancelled so nothing is recorded.</summary>
    private async Task<RemovalFileSnapshot?> ProbeInterruptedAsync(RemovalJob job, CancellationToken cancellationToken)
    {
        for (var look = 0; look < 2; look++)
        {
            RemovalUnstageOutcome outcome;
            try
            {
                outcome = await _io.PurgeUnstage(job, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or OperationCanceledException))
            {
                outcome = RemovalUnstageOutcome.NotRestored; // Any doubt: never guess.
            }

            // The native probe turns a cancelled re-hash into NotRestored; that is no evidence about the file.
            cancellationToken.ThrowIfCancellationRequested();
            if (outcome.Result == RemovalUnstageResult.Restored && outcome.Held is not null)
            {
                return outcome.Held;
            }
        }

        return null;
    }

    private async Task<bool> TryRollBackPurgeAsync(RemovalJob prepared, RemovalPurgeStage? stage, CancellationToken token)
    {
        try
        {
            // Only the exact PurgePrepared generation may be rolled back; anything later means commit may have begun.
            if ((await _journal.LoadAsync(token).ConfigureAwait(false)).SingleOrDefault(j => j.JobId == prepared.JobId) != prepared)
            {
                return false;
            }

            var outcome = await _io.PurgeUnstage(prepared, stage, token).ConfigureAwait(false);
            if (outcome.Result != RemovalUnstageResult.Restored || outcome.Held is null)
            {
                return false;
            }

            try
            {
                await _journal.SaveAsync(prepared with { Phase = RemovalPhase.ReplacementReady, Purge = null, Held = outcome.Held }, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // A failed flush may still have made the rollback record durable.
                using var check = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return (await _journal.LoadAsync(check.Token).ConfigureAwait(false)).SingleOrDefault(j => j.JobId == prepared.JobId)?.Phase == RemovalPhase.ReplacementReady;
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return false; // Any doubt keeps the job outcome-unknown.
        }
    }

    private void EnsurePurgeEnabled(Guid admin)
    {
        if (admin == Guid.Empty)
        {
            throw Refused("administratorRequired");
        }

        if (!_io.Supported())
        {
            throw Refused("unsupportedPlatform");
        }

        if (!_settings.Current.LocalRemovalEnabled || !_settings.Current.PermanentDeletionEnabled)
        {
            throw Refused("disabled");
        }

        _library.VerifyPurgeAdministrator(admin);
    }

    private async Task<PurgeEvidence> VerifyPurgeAsync(string jobId, CancellationToken token)
    {
        await _journal.EnsureCanPurgeAsync(jobId, token).ConfigureAwait(false);
        var jobs = await _journal.LoadAsync(token).ConfigureAwait(false);
        var job = jobs.SingleOrDefault(j => j.JobId == jobId) ?? throw Refused("notFound");
        if (job.Phase != RemovalPhase.ReplacementReady || job.Purge is not null || job.Held is null)
        {
            throw Refused("planChanged");
        }

        var context = JsonSerializer.Deserialize<RemovalContextData>(job.ContextJson, ContextOptions) ?? throw Refused("recoveryRequired");
        ValidateInventory(context.Target, jobs);
        var held = await _io.Probe(context.Target.RecoveryRoot, Path.Combine(context.Target.RecoveryRoot, job.JobId + ".media"), token).ConfigureAwait(false);
        if (!RemovalJournal.SameHeld(job.Held, held))
        {
            throw Refused("recoveryRequired");
        }

        var shared = _library.VerifyPurgeState(context.Target, context.SharedItemId, context.Baseline);
        var origin = await _origin.ProveAsync(context.Target, token).ConfigureAwait(false);
        Equivalent(held, origin);
        if (_library.VerifyPurgeState(context.Target, context.SharedItemId, context.Baseline).Fingerprint != shared.Fingerprint)
        {
            throw Refused("historyChanged");
        }

        return new(job, context, held, shared, origin);
    }

    private static string PurgeFacts(PurgeEvidence evidence) => Fingerprint(evidence);

    private sealed record PurgeEvidence(RemovalJob Job, RemovalContextData Context, RemovalFileSnapshot Held, RemovalPurgeState Shared, RemovalOriginDto Origin);

    private sealed record PendingPurge(Guid Admin, DateTimeOffset Until, PurgeEvidence Evidence);
}
