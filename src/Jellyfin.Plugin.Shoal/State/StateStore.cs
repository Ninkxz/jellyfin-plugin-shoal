using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// The atomic store for <c>state.json</c> (DESIGN 3.2a, 3.7).
/// <para>
/// Write: one writer at a time; temp file, fsync, replace with a <c>.bak</c>; the first write of each UTC day is also copied to
/// <c>backups/state-YYYYMMDD.json</c> and only the newest seven copies are kept.
/// </para>
/// <para>
/// Read: <c>state.json</c>, then <c>state.json.bak</c> (one write behind); the first one that parses and validates wins, and a
/// corrupt primary is kept byte for byte as <c>state.json.corrupt-&lt;timestamp&gt;</c>. If neither works the store enters recovery
/// mode and never writes a fresh state over the unreadable files; the daily copies are then offered through
/// <see cref="RestoreFromAsync"/> (they can be a day old, so using one is an explicit admin choice). A <c>schema</c> newer than
/// this build understands in <c>state.json</c> itself is recovery mode too (no fallback: that would be a silent downgrade).
/// </para>
/// The constructor never throws.
/// </summary>
public sealed partial class StateStore : IStateStore, IDisposable
{
    private const int KeptDailyCopies = 7;
    private const string PrimaryName = "state.json";
    private const string BackupName = "state.json.bak";

    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action<AtomicStage>? _atomicHook;
    private readonly Action<string, string, string?>? _replaceOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _confirmLock = new();

    private ShoalState _current = NewEmptyState();
    private StateLoadInfo _loadInfo = new() { Status = StateStatus.Recovery, Recovery = RecoveryReason.Unreadable };
    private volatile bool _recovery = true;
    private byte[]? _pendingRepair;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths; the data lives in <c>DataPath/shoal</c>.</param>
    /// <param name="logger">The logger.</param>
    public StateStore(IApplicationPaths applicationPaths, ILogger<StateStore> logger)
        : this(new ShoalPaths(applicationPaths.DataPath), logger, TimeProvider.System, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class (tests: own folder, own clock, crash hook).
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    /// <param name="atomicHook">Test seam to simulate a crash inside a write.</param>
    /// <param name="replaceOverride">Test seam to simulate a file system without hard links.</param>
    internal StateStore(ShoalPaths paths, ILogger logger, TimeProvider time, Action<AtomicStage>? atomicHook, Action<string, string, string?>? replaceOverride = null)
    {
        _paths = paths;
        _logger = logger;
        _time = time;
        _atomicHook = atomicHook;
        _replaceOverride = replaceOverride;
        Load();
    }

    /// <inheritdoc />
    public event EventHandler<StateChangedEventArgs>? Changed;

    /// <inheritdoc />
    public StateStatus Status => _recovery ? StateStatus.Recovery : StateStatus.Ready;

    /// <inheritdoc />
    public ShoalState Current => _current;

    /// <inheritdoc />
    public StateLoadInfo LoadInfo => _loadInfo;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public bool ConfirmRestore()
    {
        lock (_confirmLock)
        {
            if (!_loadInfo.AccessReviewRequired)
            {
                return false;
            }

            _loadInfo = _loadInfo.WithReviewConfirmed();
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<ShoalState> UpdateAsync(Action<ShoalState> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ShoalState next;
        try
        {
            if (_recovery)
            {
                throw new StateRecoveryException(_loadInfo.Recovery);
            }

            RepairPendingPrimary();
            next = StateJson.Deserialize(StateJson.Serialize(_current)); // deep clone; the old snapshot stays untouched for readers
            mutate(next);
            var notes = new List<string>();
            var fatal = StateValidator.Validate(next, notes);
            if (fatal is not null)
            {
                throw new InvalidOperationException("The change was refused: " + fatal + ".");
            }

            WriteLocked(next, _current.Generation);
            foreach (var note in notes)
            {
                _logger.LogInformation("Shoal state adjusted: {Note}", note);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged(next);
        return next;
    }

    /// <inheritdoc />
    public IReadOnlyList<StateCandidate> GetCandidates() => CandidateNames().Select(name => Evaluate(name).Info).ToList();

    /// <inheritdoc />
    public async Task RestoreFromAsync(string candidateFileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateFileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ShoalState restored;
        try
        {
            if (!CandidateNames().Contains(candidateFileName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Unknown state file.");
            }

            if (string.Equals(candidateFileName, PrimaryName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("state.json cannot be restored from itself.");
            }

            var evaluation = Evaluate(candidateFileName);
            if (!evaluation.Info.Usable || evaluation.State is null || evaluation.Bytes is null)
            {
                throw new InvalidOperationException("That state file cannot be used: " + (evaluation.Info.Problem ?? "unknown problem") + ".");
            }

            restored = evaluation.State;
            var notes = evaluation.Notes.Concat(HardenAfterRestore(restored)).ToList();
            var hardened = StateJson.Serialize(restored);
            var quarantined = InstallPrimary(hardened);
            _current = restored;
            _recovery = false;
            _loadInfo = new StateLoadInfo
            {
                Status = StateStatus.Ready,
                Source = candidateFileName == BackupName ? StateSource.Backup : StateSource.Daily,
                RestoredFrom = candidateFileName,
                QuarantinedAs = quarantined,
                AccessReviewRequired = true,
                RestoredUtc = _time.GetUtcNow().UtcDateTime,
                Candidates = GetCandidates(),
                Notes = notes
            };
            WriteDailyIfNeeded(hardened);
            _logger.LogWarning("Shoal restored its settings from {File}", candidateFileName);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged(restored);
    }

    /// <inheritdoc />
    public async Task StartOverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ShoalState fresh;
        try
        {
            var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string? quarantined = null;
            foreach (var path in new[] { _paths.StateFile, _paths.StateBackup })
            {
                if (File.Exists(path))
                {
                    var moved = MoveAside(path, stamp);
                    quarantined ??= Path.GetFileName(moved);
                }
            }

            fresh = NewEmptyState();
            _pendingRepair = null;
            WriteLocked(fresh, 0);
            _loadInfo = new StateLoadInfo
            {
                Status = StateStatus.Ready,
                Source = StateSource.Fresh,
                QuarantinedAs = quarantined,
                Candidates = GetCandidates()
            };
            _logger.LogWarning("Shoal started over with an empty state; the previous files were kept as *.corrupt-*");
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged(fresh);
    }

    private static ShoalState NewEmptyState() => new() { Schema = ShoalState.CurrentSchema };

    // A copy can be older than the last change, and an older state can be wider: a library tightened from Everyone to Selected, a
    // paused share. After any restore other than from state.json itself the safe direction is chosen and the admin is asked to review
    // (Warden N-9). The remembered selection is kept; only the mode and the pause flag change.
    private static List<string> HardenAfterRestore(ShoalState state)
    {
        var notes = new List<string>();
        var libraries = state.Home.Peers.SelectMany(p => p.Libraries).Where(l => l.Access.Mode != AccessMode.Selected).ToList();
        foreach (var library in libraries)
        {
            library.Access.Mode = AccessMode.Selected;
        }

        var shares = state.PeerRole.Shares.Where(s => s.Enabled).ToList();
        foreach (var share in shares)
        {
            share.Enabled = false;
        }

        if (libraries.Count > 0 || shares.Count > 0)
        {
            notes.Add($"Restored from a copy: {libraries.Count} librar{(libraries.Count == 1 ? "y is" : "ies are")} back to \"Only selected users\" and {shares.Count} share(s) paused until access has been reviewed.");
        }

        return notes;
    }

    [GeneratedRegex(@"\Astate-\d{8}\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex DailyNameRegex();

    private DateTime UtcNow()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private string PathOf(string candidateName) =>
        candidateName switch
        {
            PrimaryName => _paths.StateFile,
            BackupName => _paths.StateBackup,
            _ => Path.Combine(_paths.BackupsDir, candidateName["backups/".Length..])
        };

    // The only names that are ever read, restored from or shown: the primary, its .bak and the daily copies (newest first).
    private List<string> CandidateNames()
    {
        var names = new List<string> { PrimaryName, BackupName };
        try
        {
            if (Directory.Exists(_paths.BackupsDir))
            {
                names.AddRange(Directory.EnumerateFiles(_paths.BackupsDir)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && DailyNameRegex().IsMatch(name))
                    .OrderByDescending(name => name, StringComparer.Ordinal)
                    .Select(name => "backups/" + name));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return names;
    }

    private void Load()
    {
        try
        {
            SecureDirectory.Ensure(_paths.Root);
            SecureDirectory.Ensure(_paths.BackupsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Shoal cannot use its data folder ({Error}); it starts in recovery mode with every peer disabled", ex.GetType().Name);
            _loadInfo = new StateLoadInfo { Status = StateStatus.Recovery, Recovery = RecoveryReason.StorageUnavailable };
            return;
        }

        AtomicFile.DeleteStaleTempFiles(_paths.Root, PrimaryName);

        var evaluations = CandidateNames().Select(Evaluate).ToList();
        var candidates = evaluations.Select(e => e.Info).ToList();
        var primary = evaluations[0];

        // Automatic fallback goes one write back, to state.json.bak, and no further. A daily copy can be a day old, and falling
        // back to it by itself could silently undo an access change; it is offered to the admin through RestoreFromAsync instead.
        var automatic = evaluations.Take(2);

        if (primary.NewerSchema)
        {
            _logger.LogError("state.json was written by a newer Shoal; it is not downgraded. Shoal starts in recovery mode with every peer disabled");
            _loadInfo = new StateLoadInfo { Status = StateStatus.Recovery, Recovery = RecoveryReason.NewerSchema, Candidates = candidates };
            return;
        }

        var chosen = automatic.FirstOrDefault(e => e.Info.Usable);
        if (chosen is null || chosen.State is null || chosen.Bytes is null)
        {
            if (evaluations.All(e => !e.Info.Exists))
            {
                _current = NewEmptyState();
                _recovery = false;
                _loadInfo = new StateLoadInfo { Status = StateStatus.Ready, Source = StateSource.Fresh, Candidates = candidates };
                return;
            }

            _logger.LogError("No Shoal state file could be read; Shoal starts in recovery mode with every peer disabled and leaves the files untouched");
            _loadInfo = new StateLoadInfo { Status = StateStatus.Recovery, Recovery = RecoveryReason.Unreadable, Candidates = candidates };
            return;
        }

        string? restoredFrom = null;
        string? quarantined = null;
        var source = StateSource.Primary;
        IReadOnlyList<string> notes = chosen.Notes;
        if (!ReferenceEquals(chosen, primary))
        {
            restoredFrom = chosen.Info.FileName;
            source = chosen.Info.FileName == BackupName ? StateSource.Backup : StateSource.Daily;
            _logger.LogWarning("Shoal restored its settings from {File} because state.json could not be used", restoredFrom);
            notes = [.. notes, .. HardenAfterRestore(chosen.State)];
            var hardened = StateJson.Serialize(chosen.State);
            try
            {
                quarantined = InstallPrimary(hardened);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pendingRepair = hardened;
                _logger.LogWarning("state.json could not be repaired yet ({Error}); the next change will retry", ex.GetType().Name);
            }
        }

        _current = chosen.State;
        _recovery = false;
        _loadInfo = new StateLoadInfo
        {
            Status = StateStatus.Ready,
            Source = source,
            RestoredFrom = restoredFrom,
            QuarantinedAs = quarantined,
            AccessReviewRequired = restoredFrom is not null,
            RestoredUtc = restoredFrom is not null ? _time.GetUtcNow().UtcDateTime : null,
            Candidates = candidates,
            Notes = notes
        };
        foreach (var note in notes)
        {
            _logger.LogInformation("Shoal state adjusted: {Note}", note);
        }
    }

    // Reads and judges one candidate without changing anything. Problem texts are fixed strings, never file content.
    private Evaluation Evaluate(string name)
    {
        var path = PathOf(name);
        try
        {
            if (!File.Exists(path))
            {
                return Evaluation.Missing(name);
            }

            if (new FileInfo(path).Length > StateJson.MaxBytes)
            {
                return Evaluation.Bad(name, "file is too large");
            }

            var bytes = File.ReadAllBytes(path);
            var schema = StateJson.PeekSchema(bytes);
            if (schema is null)
            {
                return Evaluation.Bad(name, "not a state document");
            }

            if (schema.Value > ShoalState.CurrentSchema)
            {
                return Evaluation.Bad(name, "written by a newer Shoal", newerSchema: true);
            }

            if (!StateUpgrade.IsReadable(schema.Value))
            {
                return Evaluation.Bad(name, "unsupported schema");
            }

            ShoalState state;
            try
            {
                state = StateJson.Deserialize(bytes);
            }
            catch (JsonException)
            {
                return Evaluation.Bad(name, "not valid for this schema");
            }

            // A schema 2 file is upgraded in memory only (PLAN v2.9 K41): nothing is written here, and the next change writes schema 3.
            List<string> notes;
            try
            {
                notes = [.. StateUpgrade.Upgrade(state)];
            }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ArgumentException)
            {
                return Evaluation.Bad(name, "state document is malformed");
            }

            var fatal = StateValidator.Validate(state, notes);
            return fatal is null
                ? new Evaluation(new StateCandidate(name, true, true, null, state.Generation, state.WrittenUtc), bytes, state, false, notes)
                : Evaluation.Bad(name, fatal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Evaluation.Bad(name, "file could not be read");
        }
    }

    // Puts known-good bytes in place as state.json. An existing primary that differs is kept byte for byte, never overwritten.
    // Returns the name the old primary was moved to, or null.
    private string? InstallPrimary(byte[] goodBytes)
    {
        string? moved = null;
        if (File.Exists(_paths.StateFile))
        {
            if (File.ReadAllBytes(_paths.StateFile).AsSpan().SequenceEqual(goodBytes))
            {
                return null;
            }

            var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            moved = Path.GetFileName(MoveAside(_paths.StateFile, stamp));
        }

        AtomicFile.Write(_paths.StateFile, goodBytes, backupPath: null, _atomicHook);
        _pendingRepair = null;
        return moved;
    }

    private void RepairPendingPrimary()
    {
        if (_pendingRepair is { } bytes)
        {
            InstallPrimary(bytes);
        }
    }

    private static string MoveAside(string path, string stamp)
    {
        var target = path + ".corrupt-" + stamp;
        for (var i = 1; File.Exists(target); i++)
        {
            target = path + ".corrupt-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);
        }

        File.Move(path, target);
        return target;
    }

    // Must be called inside the gate. Increments the generation, writes, then publishes the snapshot.
    private void WriteLocked(ShoalState next, long baseGeneration)
    {
        next.Schema = ShoalState.CurrentSchema;
        next.Generation = baseGeneration + 1;
        next.WrittenUtc = UtcNow();
        var bytes = StateJson.Serialize(next);
        var outcome = AtomicFile.Write(_paths.StateFile, bytes, _paths.StateBackup, _atomicHook, _replaceOverride);
        if (outcome != AtomicWriteOutcome.Normal)
        {
            _logger.LogWarning("The data folder does not support hard links; state.json.bak was written with {Method}", outcome == AtomicWriteOutcome.BackupByCopy ? "a copy" : "no copy at all");
        }

        WriteDailyIfNeeded(bytes);
        _current = next;
        _recovery = false;
    }

    private void WriteDailyIfNeeded(byte[] bytes)
    {
        try
        {
            var daily = Path.Combine(_paths.BackupsDir, $"state-{_time.GetUtcNow().UtcDateTime:yyyyMMdd}.json");
            if (!File.Exists(daily))
            {
                SecureDirectory.Ensure(_paths.BackupsDir);
                AtomicFile.Write(daily, bytes);
            }

            var stale = Directory.EnumerateFiles(_paths.BackupsDir)
                .Where(path => DailyNameRegex().IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Skip(KeptDailyCopies);
            foreach (var path in stale)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The daily copy of state.json could not be written ({Error})", ex.GetType().Name);
        }
    }

    private void RaiseChanged(ShoalState state)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new StateChangedEventArgs(state);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<StateChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
#pragma warning disable CA1031 // A subscriber must not be able to break the writer or the other subscribers; only the type is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning("A state change handler failed: {Error}", ex.GetType().Name);
            }
        }
    }

    private sealed record Evaluation(StateCandidate Info, byte[]? Bytes, ShoalState? State, bool NewerSchema, IReadOnlyList<string> Notes)
    {
        public static Evaluation Missing(string name) =>
            new(new StateCandidate(name, false, false, null, null, null), null, null, false, []);

        public static Evaluation Bad(string name, string problem, bool newerSchema = false) =>
            new(new StateCandidate(name, true, false, problem, null, null), null, null, newerSchema, []);
    }
}
