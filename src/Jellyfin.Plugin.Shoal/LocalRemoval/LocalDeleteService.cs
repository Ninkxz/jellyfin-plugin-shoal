using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>What the batch writes before and after each delete call (K3b: the append-only audit file).</summary>
internal interface ILocalDeleteAudit
{
    /// <summary>Written and flushed before the delete call.</summary>
    /// <param name="entry">The facts known before the call.</param>
    void Started(LocalDeleteAuditEntry entry);

    /// <summary>Written when the item ends (after the settle pass for done items).</summary>
    /// <param name="entry">The facts including the outcome.</param>
    void Finished(LocalDeleteAuditEntry entry);
}

/// <summary>One audit line. The local path is allowed here (local admin file) and nowhere else; never a token or key.</summary>
/// <param name="BatchId">The batch.</param>
/// <param name="ItemKey">The item.</param>
/// <param name="AdminId">The admin user id.</param>
/// <param name="PeerKey">The friend.</param>
/// <param name="Title">Display title.</param>
/// <param name="Year">Production year.</param>
/// <param name="Bytes">File length.</param>
/// <param name="Sha256Prefix">First 16 hex of the local digest.</param>
/// <param name="Path">The local path (audit file only).</param>
/// <param name="Outcome">The item state word, or null for started.</param>
/// <param name="Reason">The reason word, or null.</param>
/// <param name="Shared">present/pending, or null.</param>
/// <param name="History">carried/notCarried, or null.</param>
internal sealed record LocalDeleteAuditEntry(string BatchId, string ItemKey, Guid AdminId, string PeerKey, string Title, int? Year, long Bytes, string Sha256Prefix, string Path, LocalDeleteItemState? Outcome = null, LocalDeleteReason? Reason = null, LocalDeleteShared? Shared = null, LocalDeleteHistory? History = null);

/// <summary>
/// 2.10.4 K3: the server-side deletion batch (plan "Server-side batch", "After the delete"). In memory only: one running batch, at most
/// <see cref="MaxItems"/> items, the last <see cref="KeptBatches"/> ended batches kept for <see cref="KeptFor"/>. Strictly one item at a time.
/// A restart loses the batch; the audit shows "started" without "finished" for the item that was in flight.
/// </summary>
public sealed class LocalDeleteService : IDisposable
{
    /// <summary>The most items in one batch.</summary>
    public const int MaxItems = 50;

    /// <summary>How many ended batches are kept.</summary>
    public const int KeptBatches = 5;

    private static readonly HashSet<LocalDeleteReason> StopsTheBatch =
    [
        LocalDeleteReason.OriginUnavailable, LocalDeleteReason.OriginRateLimited, LocalDeleteReason.Busy, LocalDeleteReason.Disabled,
        LocalDeleteReason.AdministratorRequired, LocalDeleteReason.LibraryBusy, LocalDeleteReason.NotConfirmed,
    ];

    private readonly ILocalDeleteLibrary _library;
    private readonly IRemovalOriginClient _origin;
    private readonly Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> _probe;
    private readonly Func<bool> _enabled;
    private readonly Func<string, CancellationToken, Task> _sync;
    private readonly RemovalGate _gate;
    private readonly ILocalDeleteAudit _audit;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IRemovalJournal? _journal;
    private readonly Func<string, long, int, CancellationToken, Task<byte[]>> _readLocal;
    private readonly CancellationTokenSource _host = new();
    private readonly object _lock = new();
    private readonly List<Batch> _ended = [];
    private readonly Dictionary<string, (RemovalTarget Target, DateTimeOffset Until)> _candidates = new(StringComparer.Ordinal);
    private Batch? _running;

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteService"/> class.</summary>
    /// <param name="library">Core side (rules B to H).</param>
    /// <param name="origin">The friend's fresh proof.</param>
    /// <param name="probe">The local full-file hash (real use: <see cref="PortableFileProbe.ProbeAsync"/>).</param>
    /// <param name="enabled">The switch (<c>LocalDeleteEnabled</c>, K4), read before every item.</param>
    /// <param name="sync">One unthrottled sync of the friend (real use: <c>CatalogSyncService.SyncAsync(peer, SyncTrigger.Scheduled)</c>; gate S5: a manual sync is skipped inside 5 minutes).</param>
    /// <param name="gate">Shared with the legacy Restore.</param>
    /// <param name="audit">The audit lines.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Diagnostics without paths.</param>
    /// <param name="journal">Jobs left by 2.10.2/2.10.3 recoverable removal (read only; the legacy list).</param>
    /// <param name="readLocal">Reads a range of the local file for the challenge (tests replace it).</param>
    internal LocalDeleteService(ILocalDeleteLibrary library, IRemovalOriginClient origin, Func<string, string, CancellationToken, Task<RemovalFileSnapshot>> probe, Func<bool> enabled, Func<string, CancellationToken, Task> sync, RemovalGate gate, ILocalDeleteAudit audit, TimeProvider time, ILogger<LocalDeleteService>? logger = null, IRemovalJournal? journal = null, Func<string, long, int, CancellationToken, Task<byte[]>>? readLocal = null)
    {
        _library = library;
        _origin = origin;
        _probe = probe;
        _enabled = enabled;
        _sync = sync;
        _gate = gate;
        _audit = audit;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _journal = journal;
        _readLocal = readLocal ?? ReadLocalAsync;
    }

    /// <summary>Gets how long an ended batch is kept.</summary>
    public static TimeSpan KeptFor { get; } = TimeSpan.FromHours(1);

    /// <summary>Gets how long the settle pass waits for the friend's copies in total.</summary>
    public static TimeSpan SettleWait { get; } = TimeSpan.FromSeconds(90);

    /// <summary>Gets the pause between two looks in the settle pass.</summary>
    public static TimeSpan SettlePoll { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the least time between two friend proofs in one batch. The friend allows 6 a minute per share (burst 3,
    /// <see cref="RemovalOriginProof"/>); one every 12 s stays below that however small the files are.
    /// </summary>
    public static TimeSpan ProofSpacing { get; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Gets how long one item may wait for a friend that answers 429 before the batch stops with <c>originRateLimited</c>. W24-1: real elapsed
    /// time on the clock: the Retry-After waits, the pacing before a retried proof, and the calls that ended in 429.
    /// </summary>
    public static TimeSpan MaxFriendWait { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the same friend waiting summed over the whole batch (W24-1); beyond it the batch stops with <c>originRateLimited</c>.</summary>
    public static TimeSpan MaxBatchFriendWait { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the run cap's allowance per item on top of its hashing deadline (call margins, pacing, three ranges, the delete).</summary>
    public static TimeSpan RunAllowancePerItem { get; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the share of a proof's hashing deadline after which the friend counts as stalling (sent nothing for most of it).</summary>
    public static double SlowProofShare { get; } = 0.75;

    /// <summary>Gets how many stalling proofs in one batch stop it with <c>originUnavailable</c> (W24-1 stall rule).</summary>
    public static int MaxSlowItems { get; } = 2;

    /// <summary>Gets the wait after a 429 without a usable Retry-After (the friend's own bucket answer is 10 s).</summary>
    public static TimeSpan DefaultRetryAfter { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the longest single wait Home honours from a friend's Retry-After.</summary>
    public static TimeSpan MaxRetryAfter { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the id of the running or settling batch, or null.</summary>
    public string? ActiveBatchId
    {
        get
        {
            lock (_lock)
            {
                return _running?.Id;
            }
        }
    }

    /// <summary>Gets or sets the settle pause (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Gets or sets the pause before a friend call (pacing and Retry-After; tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> FriendDelay { get; set; } = Task.Delay;

    /// <summary>Gets the task of the running batch, for tests and shutdown.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        _host.Cancel();
        try
        {
            Running.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // logged in the batch
        }

        _host.Dispose();
    }

    /// <summary>Starts a batch (route 4). The caller (K4) resolves candidate ids to targets and proves the caller is an elevated admin.</summary>
    /// <param name="admin">The starting admin.</param>
    /// <param name="peerKey">The friend every target belongs to.</param>
    /// <param name="targets">1 to <see cref="MaxItems"/> distinct targets.</param>
    /// <returns>The 202 reply.</returns>
    /// <exception cref="InvalidOperationException"><c>invalidRequest</c>, <c>disabled</c>, <c>administratorRequired</c> or <c>busy</c>.</exception>
    internal LocalDeleteStartDto Start(Guid admin, string peerKey, IReadOnlyList<RemovalTarget> targets)
    {
        if (targets.Count is < 1 or > MaxItems || targets.Any(t => t.PeerKey != peerKey) || targets.Select(static t => t.LocalItemId).Distinct().Count() != targets.Count)
        {
            throw new InvalidOperationException("invalidRequest");
        }

        if (!_enabled())
        {
            throw new InvalidOperationException("disabled");
        }

        _library.VerifyAdministrator(admin);
        lock (_lock)
        {
            if (_running is not null || _host.IsCancellationRequested)
            {
                throw new InvalidOperationException("busy");
            }

            var gate = _gate.TryEnter() ?? throw new InvalidOperationException("busy");
            var batch = new Batch(NewId(), admin, peerKey, _time.GetUtcNow(), targets.Select(t => new Item(NewId(), t)).ToArray(), gate) { RunCap = RunCapFor(targets) };
            _running = batch;
            var reply = new LocalDeleteStartDto(batch.Id, LocalDeleteBatchState.Running, batch.Items.Select(static i => i.Dto()).ToArray()); // every item queued (C0), before the task can move one
            Running = Task.Run(() => RunAsync(batch), CancellationToken.None);
            return reply;
        }
    }

    /// <summary>Jobs left by earlier versions that still hold an original or need attention (restored and purged jobs are hidden).</summary>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>At most 200 rows, newest first; empty off Linux (held originals exist only where the native move worked).</returns>
    internal async Task<IReadOnlyList<LocalDeleteLegacyJobDto>> LegacyJobsAsync(CancellationToken cancellationToken)
    {
        if (_journal is null || !OperatingSystem.IsLinux())
        {
            return [];
        }

        var jobs = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        return jobs.Where(static j => j.Phase is not (RemovalPhase.Restored or RemovalPhase.Purged)).OrderByDescending(static j => j.CreatedUtc).Take(200).Select(static j =>
        {
            var restorable = j.Phase is RemovalPhase.Prepared or RemovalPhase.Quarantined or RemovalPhase.ReplacementReady or RemovalPhase.NeedsRecovery;
            var phase = j.Phase switch
            {
                RemovalPhase.Prepared => "prepared",
                RemovalPhase.Quarantined => "quarantined",
                RemovalPhase.ReplacementReady => "replacementReady",
                RemovalPhase.PurgePrepared => "purgePrepared",
                RemovalPhase.PurgeCommitting => "purgeCommitting",
                RemovalPhase.PurgeOutcomeUnknown => "purgeOutcomeUnknown",
                _ => "needsRecovery",
            };
            return new LocalDeleteLegacyJobDto(j.JobId, j.Title, phase, restorable ? j.Original.Length : 0, restorable, j.Phase is RemovalPhase.Prepared or RemovalPhase.Quarantined or RemovalPhase.ReplacementReady ? null : "recoveryRequired");
        }).ToArray();
    }

    /// <summary>Lists deletable movies for a friend and mints a random 32 hex handle for each (route 1). Handles live 60 minutes; at most 300 are kept.</summary>
    /// <param name="peerKey">The friend.</param>
    /// <returns>The candidates; empty when discovery is not possible right now.</returns>
    internal IReadOnlyList<LocalDeleteCandidateDto> ListCandidates(string peerKey)
    {
        IReadOnlyList<RemovalTarget> found;
        try
        {
            found = _library.Candidates(peerKey);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _candidates.Where(c => c.Value.Until <= now).Select(static c => c.Key).ToArray())
            {
                _candidates.Remove(expired);
            }

            foreach (var oldest in _candidates.OrderBy(static c => c.Value.Until).Take(Math.Max(0, _candidates.Count + found.Count - RemovalPolicy.MaxCandidates)).Select(static c => c.Key).ToArray())
            {
                _candidates.Remove(oldest);
            }

            var result = new List<LocalDeleteCandidateDto>();
            foreach (var target in found.Take(RemovalPolicy.MaxCandidates))
            {
                var id = NewId();
                _candidates[id] = (target, now + RemovalPolicy.CandidateLifetime);
                result.Add(new(id, target.Title, target.Year, target.Bytes));
            }

            return result;
        }
    }

    /// <summary>Forgets every candidate handle (switch off).</summary>
    internal void ForgetCandidates()
    {
        lock (_lock)
        {
            _candidates.Clear();
        }
    }

    /// <summary>Starts a batch from candidate handles (route 4). Unknown or expired handles are dropped; all must belong to one friend.</summary>
    /// <param name="admin">The starting admin.</param>
    /// <param name="candidateIds">1 to 50 distinct handles.</param>
    /// <returns>The 202 reply.</returns>
    /// <exception cref="InvalidOperationException"><c>candidateChanged</c> when no handle resolves, else as <see cref="Start"/>.</exception>
    internal LocalDeleteStartDto StartFromCandidates(Guid admin, IReadOnlyList<string> candidateIds)
    {
        if (candidateIds.Count is < 1 or > MaxItems || candidateIds.Distinct(StringComparer.Ordinal).Count() != candidateIds.Count)
        {
            throw new InvalidOperationException("invalidRequest");
        }

        RemovalTarget[] targets;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            targets = candidateIds.Select(id => _candidates.TryGetValue(id, out var c) && c.Until > now ? c.Target : null).OfType<RemovalTarget>().ToArray();
        }

        if (targets.Length == 0)
        {
            throw new InvalidOperationException("candidateChanged");
        }

        if (targets.Select(static t => t.PeerKey).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidOperationException("invalidRequest");
        }

        return Start(admin, targets[0].PeerKey, targets.DistinctBy(static t => t.LocalItemId).ToArray());
    }

    /// <summary>Route 5.</summary>
    /// <param name="batchId">The batch.</param>
    /// <returns>The batch, or null after expiry or restart.</returns>
    internal LocalDeleteBatchDto? Get(string batchId)
    {
        lock (_lock)
        {
            Prune();
            return Find(batchId)?.Dto();
        }
    }

    /// <summary>Route 6: the running batch, else the latest ended one, else null.</summary>
    /// <returns>The batch or null.</returns>
    internal LocalDeleteBatchDto? Current()
    {
        lock (_lock)
        {
            Prune();
            return (_running ?? _ended.LastOrDefault())?.Dto();
        }
    }

    /// <summary>Route 7: stop after the current item; a friend wait in progress ends at once (W24-2). Idempotent; an ended batch is returned unchanged.</summary>
    /// <param name="batchId">The batch.</param>
    /// <returns>The batch, or null when unknown.</returns>
    internal LocalDeleteBatchDto? Stop(string batchId)
    {
        lock (_lock)
        {
            Prune();
            var batch = Find(batchId);
            if (batch is not null && ReferenceEquals(batch, _running))
            {
                batch.Stopping = true;
                batch.StopWaits.Cancel(); // W24-2: only waits listen to this; a proof, hash or delete in flight finishes
            }

            return batch?.Dto();
        }
    }

    /// <summary>Stops a running batch after its current item (switch off, K4).</summary>
    internal void StopRunning()
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                // W23-1: switch off also ends the item in flight: its hashes are cancelled and the delete step re-reads the switch.
                _running.Stopping = true;
                _running.SwitchedOff = true;
                _running.Cancel.Cancel();
            }
        }
    }

    /// <summary>
    /// The run cap for a batch (W24-1): each item's hashing deadline (5 min + 75 s per GiB, up to 130 min) plus <see cref="RunAllowancePerItem"/>,
    /// plus <see cref="MaxBatchFriendWait"/>. Built from the deadlines, so it never cuts a hash that is within its own deadline. Checked
    /// between items and before every friend wait; an in-flight hash, proof or delete is never cut by it.
    /// </summary>
    /// <param name="targets">The batch's targets.</param>
    /// <returns>The cap.</returns>
    internal static TimeSpan RunCapFor(IReadOnlyList<RemovalTarget> targets)
        => targets.Aggregate(MaxBatchFriendWait, static (sum, t) => sum + RemovalPolicy.DeleteHashingDeadlineFor(t.Bytes) + RunAllowancePerItem);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static long RandomOffset(long max)
    {
        if (max <= 0)
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return (long)(BitConverter.ToUInt64(bytes) % (ulong)(max + 1));
    }

    private static async Task<byte[]> ReadLocalAsync(string path, long offset, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static bool Equivalent(RemovalFileSnapshot local, RemovalOriginDto friend) => local.Length > 0 && local.Length == friend.Length
        && IsDigest(local.Sha256) && IsDigest(friend.ContentSha256) && string.Equals(local.Sha256, friend.ContentSha256, StringComparison.Ordinal);

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // The word for a friend call that failed: 429 after the waits, a valid proof of a different file, or a friend that confirmed nothing.
    private static LocalDeleteReason FriendReason(Exception? ex) => (ex as InvalidOperationException)?.Message switch
    {
        "originRateLimited" => LocalDeleteReason.OriginRateLimited,
        "contentNotEquivalent" => LocalDeleteReason.ContentNotEquivalent,
        _ => LocalDeleteReason.OriginUnavailable,
    };

    private static TimeSpan RetryAfter(InvalidOperationException limited)
    {
        var asked = (limited as RemovalOriginRateLimitedException)?.RetryAfter ?? DefaultRetryAfter;
        return asked < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : asked > MaxRetryAfter ? MaxRetryAfter : asked;
    }

    private static bool Same(IReadOnlyList<RemovalUserState> a, IReadOnlyList<RemovalUserState> b) => a.OrderBy(static h => h.UserId).SequenceEqual(b.OrderBy(static h => h.UserId));

    private static LocalDeleteReason? Word(string message) => message switch
    {
        "originRateLimited" => LocalDeleteReason.OriginRateLimited,
        "originUnavailable" => LocalDeleteReason.OriginUnavailable,
        "administratorRequired" => LocalDeleteReason.AdministratorRequired,
        "busy" => LocalDeleteReason.Busy,
        "disabled" => LocalDeleteReason.Disabled,
        _ => null,
    };

    private bool Administrator(Guid admin)
    {
        try
        {
            _library.VerifyAdministrator(admin);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private Batch? Find(string batchId) => _running?.Id == batchId ? _running : _ended.FirstOrDefault(b => b.Id == batchId);

    private void Prune()
    {
        var now = _time.GetUtcNow();
        _ended.RemoveAll(b => b.Finished is { } at && now - at > KeptFor);
        while (_ended.Count > KeptBatches)
        {
            _ended.RemoveAt(0);
        }
    }

    private async Task RunAsync(Batch batch)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_host.Token, batch.Cancel.Token);
        var token = linked.Token;
        LocalDeleteReason? stop = null;
        var userStopped = false;
        try
        {
            foreach (var item in batch.Items)
            {
                if (stop is not null || userStopped)
                {
                    batch.Set(item, LocalDeleteItemState.NotAttempted, stop);
                    continue;
                }

                if (batch.Stopping || token.IsCancellationRequested)
                {
                    userStopped = true;
                    batch.Set(item, LocalDeleteItemState.NotAttempted, null);
                    continue;
                }

                // Rule A between items: switch off or lost admin rights end the batch before the next item starts. W24-1: so does the run cap.
                stop = !_enabled() ? LocalDeleteReason.Disabled : !Administrator(batch.Admin) ? LocalDeleteReason.AdministratorRequired
                    : _time.GetUtcNow() - batch.Started > batch.RunCap ? LocalDeleteReason.OriginUnavailable : null;
                if (stop is not null)
                {
                    batch.Set(item, LocalDeleteItemState.NotAttempted, stop);
                    continue;
                }

                var reason = await RunItemAsync(batch, item, token).ConfigureAwait(false);
                if (reason is { } r && StopsTheBatch.Contains(r))
                {
                    stop = r;
                }

                // W24-1 stall rule: a friend that sends nothing for most of each proof's deadline stops the batch after MaxSlowItems such items.
                if (item.SlowFriend && ++batch.SlowItems >= MaxSlowItems)
                {
                    stop ??= LocalDeleteReason.OriginUnavailable;
                }
            }

            batch.Settling = true;
            await SettleAsync(batch, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.LogError("Shoal's delete batch ended unexpectedly ({Kind})", ex.GetType().Name);
            foreach (var item in batch.Items.Where(static i => i.State is LocalDeleteItemState.Queued or LocalDeleteItemState.Checking))
            {
                batch.Set(item, LocalDeleteItemState.NotAttempted, null);
            }

            foreach (var item in batch.Items.Where(static i => i.State == LocalDeleteItemState.Deleting))
            {
                batch.Set(item, LocalDeleteItemState.NotConfirmed, LocalDeleteReason.NotConfirmed);
            }

            // W23-6: every item with a started line gets its finished line, also when the batch dies after the delete.
            foreach (var item in batch.Items.Where(static i => i.Audit is not null && !i.AuditClosed))
            {
                Close(item, item.Audit! with { Outcome = item.State, Reason = item.State == LocalDeleteItemState.Done ? null : item.Reason, Shared = item.Shared, History = item.HistoryResult });
            }

            stop ??= LocalDeleteReason.NotConfirmed;
        }
        finally
        {
            lock (_lock)
            {
                batch.Final = stop is not null || userStopped || batch.StoppedInWait ? LocalDeleteBatchState.Stopped : LocalDeleteBatchState.Finished;
                batch.Finished = _time.GetUtcNow();
                batch.Settling = false;
                batch.Stopping = false;
                _running = null;
                _ended.Add(batch);
                Prune();
            }

            batch.Gate.Dispose();
            batch.Cancel.Dispose();
            batch.StopWaits.Dispose();
        }
    }

    // W23-3: a matching hash is the friend's word. Home also compares the first bytes and two ranges it picks at random now, read through the
    // friend's normal static stream, with its own file. A friend without the bytes (or that only knows the digest) fails. A friend that relays
    // Home's own stream live is not stopped by this; the dialog and release notes say the delete trusts the friend.
    private async Task ChallengeAsync(Batch batch, Item item, RemovalTarget target, RemovalFileSnapshot local, CancellationToken cancellationToken)
    {
        var size = (int)Math.Min(RemovalOriginClient.SampleBytes, local.Length);
        foreach (var offset in new[] { 0L, RandomOffset(local.Length - size), RandomOffset(local.Length - size) })
        {
            var mine = await _readLocal(local.FullPath, offset, size, cancellationToken).ConfigureAwait(false);
            byte[] theirs;
            try
            {
                theirs = await FriendAsync(batch, item, paced: false, ct => _origin.ReadRangeAsync(target, offset, size, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                throw new LocalDeleteRefusedException(FriendReason(ex));
            }

            if (!mine.AsSpan().SequenceEqual(theirs))
            {
                throw new LocalDeleteRefusedException(LocalDeleteReason.ContentNotEquivalent);
            }
        }
    }

    // Lab fix (Pilot 2 check 2): one friend call. A proof waits until ProofSpacing has passed since the batch's last proof (stays below the
    // friend's 6 a minute). A 429 is a wait, not a stop: the same call is made again after the friend's Retry-After (1 to 30 s, default 10 s)
    // while the item shows checking + originRateLimited ("waiting for your friend's server"), until the item has waited MaxFriendWait in
    // total; then the 429 is thrown and stops the batch as before. Switch off cancels the wait (W23-1). No proof is skipped or reused.
    // W24-1: the waiting is measured on the clock (a retry's pacing, the 429 call itself, the wait) against the item cap, the batch cap and
    // the run cap. A proof that took more than SlowProofShare of its deadline marks the item slow (stall rule). W24-2: Stop ends a wait.
    private async Task<T> FriendAsync<T>(Batch batch, Item item, bool paced, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var retrying = false;
        while (true)
        {
            var began = _time.GetUtcNow();
            if (paced)
            {
                if (batch.LastProof is { } last && last + ProofSpacing - began is var pause && pause > TimeSpan.Zero)
                {
                    if (retrying && !Fits(batch, item, pause))
                    {
                        throw new RemovalOriginRateLimitedException();
                    }

                    await WaitAsync(batch, pause, cancellationToken).ConfigureAwait(false);
                }

                batch.LastProof = _time.GetUtcNow();
            }

            var asked = _time.GetUtcNow();
            try
            {
                var answer = await call(cancellationToken).ConfigureAwait(false);
                if (paced && _time.GetUtcNow() - asked > RemovalPolicy.DeleteHashingDeadlineFor(item.Picked.Bytes) * SlowProofShare)
                {
                    item.SlowFriend = true;
                }

                return answer;
            }
            catch (InvalidOperationException ex) when (ex.Message == "originRateLimited")
            {
                Count(batch, item, _time.GetUtcNow() - (retrying ? began : asked));
                var wait = RetryAfter(ex);
                if (!Fits(batch, item, wait))
                {
                    throw;
                }

                batch.Set(item, LocalDeleteItemState.Checking, LocalDeleteReason.OriginRateLimited);
                var before = _time.GetUtcNow();
                try
                {
                    await WaitAsync(batch, wait, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Count(batch, item, _time.GetUtcNow() - before);
                    batch.Set(item, LocalDeleteItemState.Checking, null);
                }

                retrying = true;
            }
        }
    }

    // W24-1: whether one more wait stays inside the item cap, the batch cap and the run cap.
    private bool Fits(Batch batch, Item item, TimeSpan wait) => item.FriendWaited + wait <= MaxFriendWait && batch.FriendWaited + wait <= MaxBatchFriendWait
        && _time.GetUtcNow() + wait - batch.Started <= batch.RunCap;

    private static void Count(Batch batch, Item item, TimeSpan spent)
    {
        if (spent > TimeSpan.Zero)
        {
            item.FriendWaited += spent;
            batch.FriendWaited += spent;
        }
    }

    // A pause before a friend call. Switch off and host stop cancel it (OperationCanceledException on the batch token, as before); W24-2: a
    // user Stop cancels it too (OperationCanceledException on StopWaits). Nothing irreversible has happened before a friend call.
    private async Task WaitAsync(Batch batch, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, batch.StopWaits.Token);
        await FriendDelay(wait, either.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        batch.StopWaits.Token.ThrowIfCancellationRequested();
    }

    // One item: rules A to H in plan order. Returns the reason the item did not end done (or null).
    private async Task<LocalDeleteReason?> RunItemAsync(Batch batch, Item item, CancellationToken token)
    {
        batch.Set(item, LocalDeleteItemState.Checking, null);
        RemovalFileSnapshot local;
        try
        {
            if (!_enabled())
            {
                throw new LocalDeleteRefusedException(LocalDeleteReason.Disabled);
            }

            _library.VerifyAdministrator(batch.Admin);
            var target = _library.Resolve(batch.PeerKey, item.Picked.LocalItemId, item.Picked.PeerItemId);
            item.Target = target;
            var history = _library.Capture(target.LocalItemId);
            try
            {
                _library.VerifyAccess(target, history);
            }
            catch (InvalidOperationException ex) when (ex is not LocalDeleteRefusedException)
            {
                // Someone who sees the movie could not see the friend's copy: say so (accessNotShared); any other refusal stays historyChanged.
                throw new LocalDeleteRefusedException(ex.Message == "accessNotShared" ? LocalDeleteReason.AccessNotShared : LocalDeleteReason.HistoryChanged);
            }

            // Rule C: both hashes at once (halves the wait on a big file).
            using var hashes = CancellationTokenSource.CreateLinkedTokenSource(token);
            var localHash = _probe(target.Root, target.Path, hashes.Token);
            var friendHash = FriendAsync(batch, item, paced: true, ct => _origin.ProveAsync(target, ct), hashes.Token);
            if (await Task.WhenAny(localHash, friendHash).ConfigureAwait(false) == friendHash && !friendHash.IsCompletedSuccessfully)
            {
                await hashes.CancelAsync().ConfigureAwait(false); // W23-11: a friend that cannot prove ends the local hash too
            }

            // Wait for both without throwing; each result is read below for the right word.
            await Task.WhenAll(localHash, friendHash).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            if (friendHash.IsCanceled)
            {
                await friendHash.ConfigureAwait(false); // W24-2: Stop ended the friend wait; rethrows its cancellation
            }

            if (!friendHash.IsCompletedSuccessfully)
            {
                throw new LocalDeleteRefusedException(FriendReason(friendHash.Exception?.InnerException));
            }

            if (!localHash.IsCompletedSuccessfully)
            {
                throw new LocalDeleteRefusedException(LocalDeleteReason.RemovalChanged);
            }

            local = await localHash.ConfigureAwait(false);
            if (!Equivalent(local, await friendHash.ConfigureAwait(false)))
            {
                throw new LocalDeleteRefusedException(LocalDeleteReason.ContentNotEquivalent);
            }

            await ChallengeAsync(batch, item, target, local, hashes.Token).ConfigureAwait(false); // W23-3

            if (!Same(history, _library.Capture(target.LocalItemId)))
            {
                throw new LocalDeleteRefusedException(LocalDeleteReason.HistoryChanged);
            }

            item.History = history;
            _library.Inspect(target, batch.Admin, local); // rules E, F, G before the audit line; Delete re-checks under the hold
        }
        catch (LocalDeleteRefusedException ex)
        {
            return batch.End(item, ex.Reason);
        }
        catch (InvalidOperationException ex) when (Word(ex.Message) is { } word)
        {
            return batch.End(item, word);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && batch.StopWaits.IsCancellationRequested)
        {
            batch.StoppedInWait = true; // W24-2: Stop during a friend wait; before the audit line, so nothing was deleted
            batch.Set(item, LocalDeleteItemState.NotAttempted, null);
            return null;
        }
        catch (OperationCanceledException)
        {
            batch.Set(item, LocalDeleteItemState.NotAttempted, LocalDeleteReason.Disabled);
            return LocalDeleteReason.Disabled; // switch off or host stopping: this item and the rest are not tried
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return batch.End(item, LocalDeleteReason.RemovalChanged);
        }

        var entry = new LocalDeleteAuditEntry(batch.Id, item.Key, batch.Admin, batch.PeerKey, item.Picked.Title, item.Picked.Year, local.Length, local.Sha256[..16], local.FullPath);
        item.Audit = entry;

        // W23-1: the switch and the admin are read again right before the audit line (a hash can take half an hour).
        var late = !_enabled() || batch.SwitchedOff ? LocalDeleteReason.Disabled : Administrator(batch.Admin) ? (LocalDeleteReason?)null : LocalDeleteReason.AdministratorRequired;
        if (late is { } lateReason)
        {
            batch.Set(item, LocalDeleteItemState.NotAttempted, lateReason);
            return lateReason;
        }

        _audit.Started(entry); // before the call; a failure here throws and nothing is deleted
        batch.Set(item, LocalDeleteItemState.Deleting, null);
        LocalDeleteOutcome outcome;
        try
        {
            outcome = _library.Delete(item.Target!, batch.Admin, local, () => _enabled() && !batch.SwitchedOff, token);
        }
        catch (LocalDeleteRefusedException ex)
        {
            outcome = new(LocalDeleteItemState.Refused, ex.Reason);
        }
        catch (InvalidOperationException ex) when (Word(ex.Message) is { } word)
        {
            outcome = new(LocalDeleteItemState.Refused, word);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning("Shoal could not confirm a delete ({Kind})", ex.GetType().Name);
            outcome = LocalDeleteOutcome.Unconfirmed; // anything unexpected once the call may have started
        }

        batch.Set(item, outcome.State, outcome.Reason);
        if (outcome.State != LocalDeleteItemState.Done)
        {
            Close(item, entry with { Outcome = outcome.State, Reason = outcome.Reason });
        }

        return outcome.Reason;
    }

    // After the last item: one sync, then up to 90 s for the friend's copies; history goes to each copy that appeared.
    private async Task SettleAsync(Batch batch, CancellationToken token)
    {
        var done = batch.Items.Where(static i => i.State == LocalDeleteItemState.Done).ToList();
        if (done.Count == 0)
        {
            return;
        }

        try
        {
            await _sync(batch.PeerKey, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            _logger.LogWarning("Shoal's sync after a delete failed ({Kind}); the next scheduled sync adds the friend's copies", ex.GetType().Name);
        }

        var until = _time.GetUtcNow() + SettleWait;
        var waiting = done.ToList();
        while (waiting.Count > 0)
        {
            foreach (var item in waiting.ToArray())
            {
                if (_library.FindSharedFor(item.Target!) is { } shared)
                {
                    waiting.Remove(item);
                    var carried = Handoff(item, shared);
                    batch.Settle(item, LocalDeleteShared.Present, carried ? LocalDeleteHistory.Carried : LocalDeleteHistory.NotCarried);
                    Close(item, item.Audit! with { Outcome = LocalDeleteItemState.Done, Shared = item.Shared, History = item.HistoryResult });
                }
            }

            if (waiting.Count == 0 || _time.GetUtcNow() >= until || token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Delay(SettlePoll, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var item in waiting)
        {
            batch.Settle(item, LocalDeleteShared.Pending, LocalDeleteHistory.NotCarried);
            Close(item, item.Audit! with { Outcome = LocalDeleteItemState.Done, Shared = item.Shared, History = item.HistoryResult });
        }
    }

    private void Close(Item item, LocalDeleteAuditEntry entry)
    {
        item.AuditClosed = true;
        _audit.Finished(entry);
    }

    private bool Handoff(Item item, Guid shared)
    {
        try
        {
            _library.Handoff(item.Target!, shared, item.History ?? []);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            return false; // best effort (gate S6: core already keeps user data by provider id)
        }
    }

    private sealed class Item(string key, RemovalTarget picked)
    {
        public string Key { get; } = key;

        public RemovalTarget Picked { get; } = picked;

        public RemovalTarget? Target { get; set; }

        public IReadOnlyList<RemovalUserState>? History { get; set; }

        public LocalDeleteAuditEntry? Audit { get; set; }

        public bool AuditClosed { get; set; }

        // Clock time this item spent on a friend that answered 429 (bounded by MaxFriendWait, W24-1).
        public TimeSpan FriendWaited { get; set; }

        // The friend's proof took more than SlowProofShare of its deadline (stall rule).
        public bool SlowFriend { get; set; }

        public LocalDeleteItemState State { get; set; } = LocalDeleteItemState.Queued;

        public LocalDeleteReason? Reason { get; set; }

        public LocalDeleteShared? Shared { get; set; }

        public LocalDeleteHistory? HistoryResult { get; set; }

        public LocalDeleteItemDto Dto() => new(Key, Picked.Title, Picked.Year, Picked.Bytes, State, Reason, Shared, HistoryResult);
    }

    private sealed class Batch(string id, Guid admin, string peerKey, DateTimeOffset started, Item[] items, IDisposable gate)
    {
        private readonly object _lock = new();

        public string Id { get; } = id;

        public Guid Admin { get; } = admin;

        public string PeerKey { get; } = peerKey;

        public DateTimeOffset Started { get; } = started;

        public Item[] Items { get; } = items;

        public IDisposable Gate { get; } = gate;

        public bool Stopping { get; set; }

        public bool Settling { get; set; }

        public bool SwitchedOff { get; set; }

        // When the last friend proof of this batch was asked for (pacing).
        public DateTimeOffset? LastProof { get; set; }

        public CancellationTokenSource Cancel { get; } = new();

        // W24-2: cancelled by Stop; only friend waits listen to it.
        public CancellationTokenSource StopWaits { get; } = new();

        public bool StoppedInWait { get; set; }

        // W24-1: the run cap, the friend waiting summed over items, and how many items had a stalling friend.
        public TimeSpan RunCap { get; init; } = TimeSpan.MaxValue;

        public TimeSpan FriendWaited { get; set; }

        public int SlowItems { get; set; }

        public LocalDeleteBatchState? Final { get; set; }

        public DateTimeOffset? Finished { get; set; }

        public void Set(Item item, LocalDeleteItemState state, LocalDeleteReason? reason)
        {
            lock (_lock)
            {
                item.State = state;
                item.Reason = reason;
            }
        }

        public LocalDeleteReason End(Item item, LocalDeleteReason reason)
        {
            Set(item, LocalDeleteItemState.Refused, reason);
            return reason;
        }

        public void Settle(Item item, LocalDeleteShared shared, LocalDeleteHistory history)
        {
            lock (_lock)
            {
                item.Shared = shared;
                item.HistoryResult = history;
            }
        }

        public LocalDeleteBatchDto Dto()
        {
            lock (_lock)
            {
                var dtos = Items.Select(static i => i.Dto()).ToArray();
                int Count(LocalDeleteItemState s) => dtos.Count(d => d.State == s);
                var counts = new LocalDeleteCountsDto(Count(LocalDeleteItemState.Done), Count(LocalDeleteItemState.Refused), Count(LocalDeleteItemState.NotConfirmed), Count(LocalDeleteItemState.NotAttempted), dtos.Count(static d => d.State is LocalDeleteItemState.Queued or LocalDeleteItemState.Checking or LocalDeleteItemState.Deleting));
                var state = Final ?? (Settling ? LocalDeleteBatchState.Settling : LocalDeleteBatchState.Running);
                return new(Id, state, Started, Finished, Final is null && Stopping, counts, dtos);
            }
        }
    }
}
