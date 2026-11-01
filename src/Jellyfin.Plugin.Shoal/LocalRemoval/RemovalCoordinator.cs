using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Admin-bound recoverable removal and separately opted-in retained-original deletion.</summary>
public sealed partial class RemovalCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions ContextOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16, RespectRequiredConstructorParameters = true };
    private readonly IRemovalLibrary _library;
    private readonly IRemovalOriginClient _origin;
    private readonly IRemovalJournal _journal;
    private readonly ISettingsAccessor _settings;
    private readonly ISyncHold _sync;
    private readonly TimeProvider _time;
    private readonly RemovalIo _io;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly object _memory = new();
    private readonly Dictionary<string, (RemovalTarget Target, DateTimeOffset Until)> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRemoval> _plans = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RemovalCoordinator"/> class.</summary>
    /// <param name="library">Authoritative local core and history operations.</param>
    /// <param name="origin">Fresh scoped peer proof.</param>
    /// <param name="journal">Strict durable recovery records.</param>
    /// <param name="settings">Default-off configuration.</param>
    /// <param name="sync">The global sync transaction hold.</param>
    /// <param name="time">The local plan clock.</param>
    /// <param name="io">Native operations, or controlled test operations.</param>
    internal RemovalCoordinator(IRemovalLibrary library, IRemovalOriginClient origin, IRemovalJournal journal, ISettingsAccessor settings, ISyncHold sync, TimeProvider time, RemovalIo? io = null)
    {
        _library = library;
        _origin = origin;
        _journal = journal;
        _settings = settings;
        _sync = sync;
        _time = time;
        _io = io ?? RemovalIo.Native();
    }

    /// <summary>Gets or sets the single-flight shared with the delete-like-Jellyfin batch (2.10.4; null in legacy tests).</summary>
    internal RemovalGate? Gate { get; set; }

    /// <inheritdoc />
    public void Dispose() => _run.Dispose();

    /// <summary>Returns bounded unselected candidates and every recoverable operation.</summary>
    /// <param name="peer">Optional friend to discover, or none for global recovery.</param>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>Public path-free state.</returns>
    internal async Task<object> StateAsync(string? peer, CancellationToken cancellationToken)
    {
        if (!_io.Supported())
        {
            return new { enabled = _settings.Current.LocalRemovalEnabled, permanentDeleteAvailable = false, permanentDeletionEnabled = _settings.Current.PermanentDeletionEnabled, candidates = Array.Empty<object>(), jobs = Array.Empty<object>() };
        }

        var jobs = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<object>();
        if (_settings.Current.LocalRemovalEnabled && peer is not null)
        {
            try
            {
                var found = _library.Discover(peer);
                lock (_memory)
                {
                    // 2.10.3 K1: older handles stay valid until they expire, so another list read (a second tab, a reload) does not break a
                    // running queue. Expired handles are pruned and the table is capped; the oldest go first.
                    var now = _time.GetUtcNow();
                    var batch = found.Take(100).ToArray();
                    foreach (var expired in _candidates.Where(c => c.Value.Until <= now).Select(c => c.Key).ToArray())
                    {
                        _candidates.Remove(expired);
                    }

                    foreach (var oldest in _candidates.OrderBy(c => c.Value.Until).Take(Math.Max(0, _candidates.Count + batch.Length - RemovalPolicy.MaxCandidates)).Select(c => c.Key).ToArray())
                    {
                        _candidates.Remove(oldest);
                    }

                    foreach (var target in batch)
                    {
                        var id = Guid.NewGuid().ToString("N");
                        _candidates[id] = (target, now + RemovalPolicy.CandidateLifetime);
                        candidates.Add(new { candidateId = id, title = target.Title, year = target.Year, bytes = target.Bytes });
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Recovery stays available even when discovery or a removed peer is unavailable.
            }
        }

        return new { enabled = _settings.Current.LocalRemovalEnabled, permanentDeleteAvailable = true, permanentDeletionEnabled = _settings.Current.PermanentDeletionEnabled, candidates, jobs = jobs.OrderBy(j => j.Phase == RemovalPhase.Restored).ThenByDescending(j => j.CreatedUtc).Take(200).Select(Public).ToArray() };
    }

    /// <summary>Saves explicit local enablement without disabling Restore.</summary>
    /// <param name="enabled">The requested toggle.</param>
    /// <returns>The saved enablement.</returns>
    internal object Enable(bool enabled)
    {
        var settings = _settings.Current;
        settings.LocalRemovalEnabled = enabled;
        _settings.Save(settings);
        if (!enabled)
        {
            lock (_memory)
            {
                _candidates.Clear();
                _purgePlans.Clear();
                foreach (var key in _plans.Where(p => p.Value.JobId is null).Select(p => p.Key).ToArray())
                {
                    _plans.Remove(key);
                }
            }
        }

        return new { enabled };
    }

    /// <summary>Creates one immutable five-minute plan after fresh content/access/storage proof.</summary>
    /// <param name="admin">The current elevated local admin.</param>
    /// <param name="candidate">A server-issued discovery handle.</param>
    /// <param name="cancellationToken">Cancellation before any media move.</param>
    /// <returns>A path-free confirmation.</returns>
    internal async Task<object> PlanAsync(Guid admin, string candidate, CancellationToken cancellationToken)
    {
        if (admin == Guid.Empty || !_io.Supported())
        {
            throw Refused("unsupportedPlatform");
        }

        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Refused("busy");
        }

        try
        {
            EnsureEnabled();
            await _journal.EnsureCanPlanAsync(cancellationToken).ConfigureAwait(false);
            var jobs = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (jobs.Count >= 128 || jobs.Count(j => j.Phase != RemovalPhase.Restored) >= 200)
            {
                throw Refused("recoveryStorageFull");
            }

            RemovalTarget target;
            lock (_memory)
            {
                if (!_candidates.TryGetValue(candidate, out var selected) || selected.Until <= _time.GetUtcNow())
                {
                    throw Refused("candidateChanged");
                }

                target = selected.Target;
                foreach (var expired in _plans.Where(p => p.Value.Until <= _time.GetUtcNow()).Select(p => p.Key).ToArray())
                {
                    _plans.Remove(expired);
                }

                if (_plans.Count >= 100)
                {
                    throw Refused("busy");
                }
            }

            ValidateInventory(target, jobs);
            var evidence = await VerifyAsync(target, cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var until = _time.GetUtcNow() + RemovalPolicy.PlanLifetime;
            lock (_memory)
            {
                _plans[id] = new(admin, until, evidence);
            }

            return new { planId = id, title = target.Title, bytesHeld = evidence.Original.Length, expiresUtc = until };
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>Consumes a bound plan once and completes or safely rolls back a recoverable move.</summary>
    /// <param name="admin">The current approving admin.</param>
    /// <param name="planId">The opaque confirmation handle.</param>
    /// <param name="cancellationToken">Cancellation before accepting the operation.</param>
    /// <returns>The existing or newly completed operation.</returns>
    internal async Task<object> ConfirmAsync(Guid admin, string planId, CancellationToken cancellationToken)
    {
        PendingRemoval plan;
        lock (_memory)
        {
            if (!_plans.TryGetValue(planId, out plan!) || plan.Admin != admin)
            {
                throw Refused("planChanged");
            }
        }

        if (plan.JobId is { } prior)
        {
            return Public((await _journal.LoadAsync(cancellationToken).ConfigureAwait(false)).Single(j => j.JobId == prior));
        }

        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Refused("busy");
        }

        try
        {
            if (plan.JobId is { } existing)
            {
                return Public((await _journal.LoadAsync(cancellationToken).ConfigureAwait(false)).Single(j => j.JobId == existing));
            }

            EnsureEnabled();
            if (plan.Until <= _time.GetUtcNow())
            {
                throw Refused("planExpired");
            }

            using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(10) + RemovalPolicy.IndexWait); // 2.10.3: the longer index wait is added, not taken from the proofs
            await _journal.EnsureCanPlanAsync(operation.Token).ConfigureAwait(false);
            ValidateInventory(plan.Evidence.Target, await _journal.LoadAsync(operation.Token).ConfigureAwait(false));
            var fresh = await VerifyAsync(plan.Evidence.Target, operation.Token).ConfigureAwait(false);
            if (Facts(fresh) != Facts(plan.Evidence))
            {
                throw Refused("planChanged");
            }

            var id = Guid.NewGuid().ToString("N");
            var context = new RemovalContextData(fresh.Target, fresh.History, Guid.Empty, null);
            var job = new RemovalJob(id, RemovalPhase.Prepared, fresh.Target.Title, fresh.Original, null, Serialize(context), _time.GetUtcNow());
            await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
            lock (_memory)
            {
                plan.JobId = id;
            }

            try
            {
                using (var held = _sync.TryHold() ?? throw Refused("busy"))
                {
                    _library.Revalidate(fresh.Target);
                    _library.VerifyAccess(fresh.Target, fresh.History);
                    if (Fingerprint(_library.Capture(fresh.Target.LocalItemId).OrderBy(h => h.UserId).ToArray()) != Fingerprint(fresh.History))
                    {
                        throw Refused("historyChanged");
                    }

                    var moved = await _io.Move(fresh.Original, fresh.Target.RecoveryRoot, id + ".media", operation.Token).ConfigureAwait(false);
                    job = job with { Phase = RemovalPhase.Quarantined, Held = moved };
                    await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
                    var remote = await _origin.ProveAsync(fresh.Target, operation.Token).ConfigureAwait(false);
                    Equivalent(fresh.Original, remote);
                    if (Fingerprint(_library.Capture(fresh.Target.LocalItemId).OrderBy(h => h.UserId).ToArray()) != Fingerprint(fresh.History))
                    {
                        throw Refused("historyChanged");
                    }

                    _library.Deindex(fresh.Target);
                }

                var shared = await _library.ImportAsync(fresh.Target, fresh.History, operation.Token).ConfigureAwait(false);
                context = context with { SharedItemId = shared };
                job = job with { ContextJson = Serialize(context) };
                await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
                using (var held = _sync.TryHold() ?? throw Refused("busy"))
                {
                    _library.Handoff(fresh.Target, shared, fresh.History);
                    _library.VerifyShared(fresh.Target, shared, fresh.History);
                }

                job = job with { Phase = RemovalPhase.ReplacementReady };
                await _journal.SaveAsync(job, operation.Token).ConfigureAwait(false);
                return Public(job);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(5) + RemovalPolicy.IndexWait);
                return Public(await RestoreJobAsync(job, context, recovery.Token).ConfigureAwait(false));
            }
        }
        finally
        {
            _run.Release();
        }
    }

    /// <summary>Restores using local durable facts, even when disabled, offline or approved by a deleted admin.</summary>
    /// <param name="jobId">The opaque recovery handle.</param>
    /// <param name="cancellationToken">Cancellation before accepting recovery.</param>
    /// <returns>The restored or explicitly recoverable operation.</returns>
    internal async Task<object> RestoreAsync(string jobId, CancellationToken cancellationToken)
    {
        // 2.10.3 (W20-5): if the start-up sweep has not run yet in this process (a slow start), it runs first, so an interrupted delete can be restored.
        await SweepInterruptedAsync(cancellationToken, fromRestore: true).ConfigureAwait(false);
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Refused("busy");
        }

        try
        {
            // 2.10.4 K4b: never while a delete-like-Jellyfin batch runs (the two share RemovalGate).
            using var shared = Gate is null ? null : Gate.TryEnter() ?? throw Refused("busy");
            var job = (await _journal.LoadAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(j => j.JobId == jobId) ?? throw Refused("notFound");
            if (job.Phase == RemovalPhase.Restored)
            {
                return Public(job);
            }

            if (!RetainedPhase(job.Phase))
            {
                throw Refused("recoveryRequired");
            }

            var context = JsonSerializer.Deserialize<RemovalContextData>(job.ContextJson, ContextOptions) ?? throw Refused("recoveryRequired");
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(5) + RemovalPolicy.IndexWait);
            return Public(await RestoreJobAsync(job, context, recovery.Token).ConfigureAwait(false));
        }
        finally
        {
            _run.Release();
        }
    }

    private static string Serialize(RemovalContextData context) => JsonSerializer.Serialize(context, ContextOptions);

    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ContextOptions)));

    private static string Facts(RemovalEvidence value) => Fingerprint(value);

    private static InvalidOperationException Refused(string code) => new(code);

    private static void Equivalent(RemovalFileSnapshot local, RemovalOriginDto remote)
    {
        if (local.Length != remote.Length || local.Sha256 != remote.ContentSha256)
        {
            throw Refused("contentNotEquivalent");
        }
    }

    private static bool RetainedPhase(RemovalPhase phase) => phase is RemovalPhase.Prepared or RemovalPhase.Quarantined or RemovalPhase.ReplacementReady or RemovalPhase.NeedsRecovery;

    private object Public(RemovalJob job) => new { jobId = job.JobId, title = job.Title, phase = job.Phase switch { RemovalPhase.Prepared => "prepared", RemovalPhase.Quarantined => "quarantined", RemovalPhase.ReplacementReady => "replacementReady", RemovalPhase.Restored => "restored", RemovalPhase.PurgePrepared => "purgePrepared", RemovalPhase.PurgeCommitting => "purgeCommitting", RemovalPhase.Purged => "purged", RemovalPhase.PurgeOutcomeUnknown => "purgeOutcomeUnknown", _ => "needsRecovery" }, canRestore = RetainedPhase(job.Phase), canReviewPermanentDeletion = job.Phase == RemovalPhase.ReplacementReady && _settings.Current.LocalRemovalEnabled && _settings.Current.PermanentDeletionEnabled && _io.Supported(), outcome = job.Phase == RemovalPhase.Purged ? "purged" : job.Phase == RemovalPhase.Restored ? "restored" : RetainedPhase(job.Phase) ? "retained" : "uncertain", bytesHeld = RetainedPhase(job.Phase) ? job.Original.Length : 0, error = job.Phase is RemovalPhase.NeedsRecovery or RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.PurgeOutcomeUnknown ? "recoveryRequired" : null };

    private void EnsureEnabled()
    {
        if (!_settings.Current.LocalRemovalEnabled)
        {
            throw Refused("disabled");
        }
    }

    private void ValidateInventory(RemovalTarget target, IReadOnlyList<RemovalJob> jobs)
    {
        try
        {
            var bound = jobs.Where(j => j.Phase is not (RemovalPhase.Restored or RemovalPhase.Purged) && JsonSerializer.Deserialize<RemovalContextData>(j.ContextJson, ContextOptions)?.Target.RecoveryRoot == target.RecoveryRoot).ToArray();
            var expected = bound.Select(j => j.JobId).ToHashSet(StringComparer.Ordinal);

            // 2.10.3 (W20-5): the stage name of a delete that never finished belongs to that job only; it is known, so it does not block other removals.
            expected.UnionWith(bound.Where(j => j.Purge is not null).Select(j => j.Purge!.StageName));
            _io.Inventory(target.RecoveryRoot, expected);
        }
        catch (JsonException)
        {
            throw Refused("recoveryRequired");
        }
    }

    private async Task<RemovalEvidence> VerifyAsync(RemovalTarget target, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (!_io.Supported())
        {
            throw Refused("unsupportedPlatform");
        }

        target = _library.Revalidate(target);
        _io.Prepare(target.RecoveryRoot);
        var original = await _io.Probe(target.Root, target.Path, cancellationToken).ConfigureAwait(false);
        _io.Validate(original, target.RecoveryRoot);
        var history = _library.Capture(target.LocalItemId).OrderBy(h => h.UserId).ToArray();
        _library.VerifyAccess(target, history);
        var proof = await _origin.ProveAsync(target, cancellationToken).ConfigureAwait(false);
        Equivalent(original, proof);
        _library.Revalidate(target);
        if (Fingerprint(_library.Capture(target.LocalItemId).OrderBy(h => h.UserId).ToArray()) != Fingerprint(history))
        {
            throw Refused("historyChanged");
        }

        return new(target, original, proof, history);
    }

    private async Task<RemovalJob> RestoreJobAsync(RemovalJob job, RemovalContextData context, CancellationToken cancellationToken)
    {
        if (!RetainedPhase(job.Phase))
        {
            throw Refused("recoveryRequired");
        }

        try
        {
            if (context.Target.Root != job.Original.Root || context.Target.Path != job.Original.FullPath)
            {
                throw Refused("recoveryRequired");
            }

            if (context.SharedItemId != Guid.Empty)
            {
                var latest = _library.CaptureShared(context.Target, context.SharedItemId, context.Baseline);
                context = context with { SharedAtRestore = latest.Count == 0 ? context.SharedAtRestore ?? [] : latest };
                job = job with { ContextJson = Serialize(context) };
                await _journal.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }

            using (var held = _sync.TryHold() ?? throw Refused("busy"))
            {
                var heldPath = Path.Combine(context.Target.RecoveryRoot, job.JobId + ".media");
                var originalExists = _io.Exists(job.Original.FullPath);
                var heldExists = _io.Exists(heldPath);
                if (originalExists && heldExists)
                {
                    throw Refused("recoveryCollision");
                }

                if (!originalExists && !heldExists)
                {
                    throw Refused("recoveryMissing");
                }

                if (heldExists)
                {
                    var actual = await _io.Probe(context.Target.RecoveryRoot, heldPath, cancellationToken).ConfigureAwait(false);
                    if (actual.Length != job.Original.Length || actual.Sha256 != job.Original.Sha256)
                    {
                        throw Refused("recoveryChanged");
                    }

                    await _io.Move(actual, Path.GetDirectoryName(job.Original.FullPath)!, Path.GetFileName(job.Original.FullPath), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var actual = await _io.Probe(job.Original.Root, job.Original.FullPath, cancellationToken).ConfigureAwait(false);
                    if (actual.Length != job.Original.Length || actual.Sha256 != job.Original.Sha256)
                    {
                        throw Refused("recoveryChanged");
                    }
                }
            }

            await _library.RestoreAsync(context.Target, context.Baseline, context.SharedAtRestore ?? [], cancellationToken).ConfigureAwait(false);
            job = job with { Phase = RemovalPhase.Restored, Held = null, ContextJson = Serialize(context) };
            await _journal.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return job;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            job = job with { Phase = RemovalPhase.NeedsRecovery, ContextJson = Serialize(context) };
            try
            {
                await _journal.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Keep all bytes and the last durable generation.
            }

            return job;
        }
    }

    private sealed class PendingRemoval(Guid admin, DateTimeOffset until, RemovalEvidence evidence)
    {
        /// <summary>Gets the approving local admin.</summary>
        internal Guid Admin { get; } = admin;

        /// <summary>Gets the plan expiry.</summary>
        internal DateTimeOffset Until { get; } = until;

        /// <summary>Gets immutable pre-move facts.</summary>
        internal RemovalEvidence Evidence { get; } = evidence;

        /// <summary>Gets or sets the consumed operation handle.</summary>
        internal string? JobId { get; set; }
    }
}

/// <summary>Private immutable pre-move evidence.</summary>
/// <param name="Target">The authoritative selection.</param>
/// <param name="Original">The native whole-file proof.</param>
/// <param name="Origin">The fresh remote proof.</param>
/// <param name="History">Every affected local user's state.</param>
internal sealed record RemovalEvidence(RemovalTarget Target, RemovalFileSnapshot Original, RemovalOriginDto Origin, IReadOnlyList<RemovalUserState> History);

/// <summary>Private schema-one recovery context; no credentials or foreign-user history.</summary>
/// <param name="Target">The immutable bound selection and planned recovery root.</param>
/// <param name="Baseline">Immutable original personal state.</param>
/// <param name="SharedItemId">The previously proved replacement.</param>
/// <param name="SharedAtRestore">Latest state captured before local restoration.</param>
internal sealed record RemovalContextData(RemovalTarget Target, IReadOnlyList<RemovalUserState> Baseline, Guid SharedItemId, IReadOnlyList<RemovalUserState>? SharedAtRestore);

/// <summary>Explicit native seam for deterministic fault tests; ordinary registrations use Native.</summary>
/// <param name="Supported">Whether strict native mutation is supported.</param>
/// <param name="Probe">The fresh native whole-file proof.</param>
/// <param name="Move">The strict same-filesystem no-overwrite rename.</param>
/// <param name="Exists">An existence check that propagates access failures.</param>
/// <param name="Prepare">Private recovery directory preparation.</param>
/// <param name="Validate">The native same-filesystem and durability preflight.</param>
/// <param name="Inventory">The bounded unknown-retained-media preflight.</param>
internal sealed record RemovalIo(Func<bool> Supported, Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> Probe, Func<RemovalFileSnapshot, string, string, CancellationToken, Task<RemovalFileSnapshot>> Move, Func<string, bool> Exists, Action<string> Prepare, Action<RemovalFileSnapshot, string> Validate, Action<string, IReadOnlySet<string>> Inventory)
{
    /// <summary>Gets the separately bound native staged claim; absent implementations fail closed.</summary>
    internal Func<RemovalJob, CancellationToken, Task<RemovalPurgeStage>> PurgeStage { get; init; } = (_, _) => Task.FromException<RemovalPurgeStage>(new InvalidOperationException("purgeUnavailable"));

    /// <summary>Gets the one-use native commit of an already live staged claim.</summary>
    internal Func<RemovalJob, RemovalPurgeStage, Action, CancellationToken, Task<RemovalPurgeResult>> PurgeCommit { get; init; } = RemovalFiles.CommitPurgeAsync;

    /// <summary>Gets the pre-commit rollback (live claim, or a fresh probe when staging threw); absent implementations never prove restoration.</summary>
    internal Func<RemovalJob, RemovalPurgeStage?, CancellationToken, Task<RemovalUnstageOutcome>> PurgeUnstage { get; init; } = (_, _, _) => Task.FromResult(RemovalUnstageOutcome.NotRestored);

    /// <summary>Creates real bounded Linux mutation operations.</summary>
    /// <returns>The ordinary native operations.</returns>
    public static RemovalIo Native()
    {
        var files = new RemovalFiles();
        return new(() => OperatingSystem.IsLinux() && (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64), files.ProbeAsync, files.MoveAsync, ExistsChecked, RemovalFiles.EnsureRecoveryDirectory, RemovalFiles.ValidateRecoveryDirectory, RemovalFiles.ValidateRecoveryInventory) { PurgeStage = (job, token) => files.StagePurgeAsync(job, token), PurgeUnstage = files.UnstagePurgeAsync };
    }

    private static bool ExistsChecked(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
