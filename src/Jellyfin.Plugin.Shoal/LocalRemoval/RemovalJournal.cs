using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Journal fault boundaries; no fault removes any prior record or media byte.</summary>
internal enum RemovalJournalPoint
{
    /// <summary>No new metadata exists yet.</summary>
    BeforeWrite,

    /// <summary>A complete temporary record has been flushed.</summary>
    AfterFileFlush,

    /// <summary>The new no-overwrite generation name exists.</summary>
    AfterRename,

    /// <summary>The containing directory still needs its checked flush.</summary>
    BeforeDirectoryFlush,
}

/// <summary>Durable job storage seam. Test doubles cannot establish native durability.</summary>
internal interface IRemovalJournal
{
    /// <summary>Loads and validates every generation, refusing uncertain ownership.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The latest validated version of each job.</returns>
    Task<IReadOnlyList<RemovalJob>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Refuses a new plan while an owned uncommitted record needs attention.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Completion only when committed ownership has no pending uncertainty.</returns>
    Task EnsureCanPlanAsync(CancellationToken cancellationToken);

    /// <summary>Refuses purge unless complete bounded intent/outcome capacity is available.</summary>
    /// <param name="jobId">The current ReplacementReady job.</param>
    /// <param name="cancellationToken">Cancellation before any mutation.</param>
    /// <returns>Completion only when the full operation fits. Unknown implementations fail closed.</returns>
    Task EnsureCanPurgeAsync(string jobId, CancellationToken cancellationToken) => Task.FromException(new IOException("purgeJournalUnsupported"));

    /// <summary>Appends a durable, no-overwrite job generation.</summary>
    /// <param name="job">The immutable job plus current phase and history handoff.</param>
    /// <param name="cancellationToken">Cancellation before committing the metadata.</param>
    /// <returns>Completion only after checked file and directory flush.</returns>
    Task SaveAsync(RemovalJob job, CancellationToken cancellationToken);
}

/// <summary>One private, durably recorded recoverable removal.</summary>
/// <param name="JobId">The opaque server-generated job identifier.</param>
/// <param name="Phase">The last durable operation phase.</param>
/// <param name="Title">Display text, never an authority source.</param>
/// <param name="Original">The immutable original file binding.</param>
/// <param name="Held">Current holding evidence, if established.</param>
/// <param name="ContextJson">Private bounded history and core binding; never a public reply.</param>
/// <param name="CreatedUtc">Immutable original creation time.</param>
/// <param name="Purge">Optional strict schema-two irreversible authority.</param>
internal sealed record RemovalJob(string JobId, RemovalPhase Phase, string Title, RemovalFileSnapshot Original, RemovalFileSnapshot? Held, string ContextJson, DateTimeOffset CreatedUtc, RemovalPurgeAuthority? Purge = null);

/// <summary>Strict append-only versioned storage. Corruption never becomes an empty default.</summary>
internal sealed partial class RemovalJournal : IRemovalJournal, IDisposable
{
    private const int MaxRecordBytes = 128 * 1024;
    private const int MaxContextBytes = 64 * 1024;
    private const int MaxRecords = 1024;
    private const int MaxJobs = 128;
    private const int MaxGenerations = 64;
    private const int RestoreHeadroom = 2;
    private const int MaxTotalBytes = 32 * 1024 * 1024;
    private static readonly string[] ContextNames = ["target", "baseline", "sharedItemId", "sharedAtRestore"];
    private static readonly string[] TargetNames = ["peerKey", "mappingKey", "peerItemId", "localItemId", "libraryId", "title", "year", "root", "path", "recoveryRoot", "providerIds", "runtimeTicks", "bytes", "generation"];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _root;
    private readonly Action<RemovalJournalPoint>? _fault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="RemovalJournal"/> class.</summary>
    /// <param name="root">An existing private owned journal directory.</param>
    /// <param name="fault">Optional trusted test fault observer.</param>
    public RemovalJournal(string root, Action<RemovalJournalPoint>? fault = null)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw Corrupt();
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _fault = fault;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemovalJob>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemovalFiles.EnsureRecoveryDirectory(_root);
            return RemovalFiles.InJournalDirectory(_root, handle => Read(handle).Records.GroupBy(static r => r.Job.JobId, StringComparer.Ordinal)
                .Select(static g => g.OrderBy(static e => e.Generation).Last().Job).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureCanPlanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemovalFiles.EnsureRecoveryDirectory(_root);
            _ = RemovalFiles.InJournalDirectory(_root, handle => Read(handle).Pending ? throw new IOException("recoveryRequired") : 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureCanPurgeAsync(string jobId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemovalFiles.EnsureRecoveryDirectory(_root);
            _ = RemovalFiles.InJournalDirectory(_root, handle =>
            {
                var state = Read(handle);
                var current = state.Records.Where(e => e.Job.JobId == jobId).MaxBy(static e => e.Generation);
                if (state.Pending || current?.Job.Phase != RemovalPhase.ReplacementReady)
                {
                    throw new IOException("recoveryRequired");
                }

                // PurgePrepared plus its three reserved outcome records (Committing, outcome, Unknown fallback); a
                // rollback uses one of those, plus at most one Unknown fallback if the rollback record fails. Each
                // rolled-back attempt costs two generations, so repeated attempts stop at MaxGenerations with
                // RestoreHeadroom generations still free for Restore (history refresh, then Restored or NeedsRecovery).
                CheckCapacity(state, current.Job, 4 + RestoreHeadroom, (4 + RestoreHeadroom) * MaxRecordBytes);
                return 0;
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(RemovalJob job, CancellationToken cancellationToken)
    {
        Validate(job);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemovalFiles.EnsureRecoveryDirectory(_root);
            _ = RemovalFiles.InJournalDirectory(_root, handle =>
            {
                var state = Read(handle);
                var prior = state.Records.Where(e => e.Job.JobId == job.JobId).OrderBy(static e => e.Generation).LastOrDefault();
                if (prior?.Job == job)
                {
                    return 0;
                }

                if (prior is not null)
                {
                    ValidateSuccessor(prior.Job, job);
                }
                else if (job.Phase != RemovalPhase.Prepared || state.Records.Select(static e => e.Job.JobId).Distinct(StringComparer.Ordinal).Count() >= MaxJobs)
                {
                    throw Corrupt();
                }

                if (prior is null && state.Pending)
                {
                    throw new IOException("recoveryRequired");
                }

                var generation = (prior?.Generation ?? 0) + 1;
                var bytes = Encode(job, generation);
                if (job.Purge is not null && state.Pending)
                {
                    throw new IOException("recoveryRequired");
                }

                CheckCapacity(state, job, 1 + ReservedRecords(job), bytes.Length + (ReservedRecords(job) * MaxRecordBytes));

                cancellationToken.ThrowIfCancellationRequested();
                _fault?.Invoke(RemovalJournalPoint.BeforeWrite);
                RemovalFiles.WriteJournalRecord(handle, ".pending-" + job.JobId + "-" + Guid.NewGuid().ToString("N") + ".json", Name(job.JobId, generation), bytes, _fault);
                return 0;
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Validates one private job independently of native storage.</summary>
    /// <param name="job">The complete job.</param>
    internal static void Validate(RemovalJob job)
    {
        try
        {
            if (job is null || !Guid.TryParseExact(job.JobId, "N", out var id) || id == Guid.Empty || job.JobId.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !Enum.IsDefined(job.Phase) || string.IsNullOrWhiteSpace(job.Title) || job.Title.Length > 1024
                || job.CreatedUtc <= DateTimeOffset.UnixEpoch || job.CreatedUtc.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(job.ContextJson) || Encoding.UTF8.GetByteCount(job.ContextJson) > MaxContextBytes)
            {
                throw Corrupt();
            }

            ValidateSnapshot(job.Original);
            using var context = JsonDocument.Parse(job.ContextJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = context.RootElement;
            RejectDuplicateNames(root);
            RequireNames(root, ContextNames);
            var target = root.GetProperty("target");
            if (target.ValueKind != JsonValueKind.Object || target.GetProperty("root").GetString() != job.Original.Root
                || target.GetProperty("path").GetString() != job.Original.FullPath || !root.GetProperty("sharedItemId").TryGetGuid(out _)
                || root.GetProperty("baseline").ValueKind != JsonValueKind.Array || root.GetProperty("baseline").GetArrayLength() > 256
                || root.GetProperty("sharedAtRestore").ValueKind is not (JsonValueKind.Null or JsonValueKind.Array)
                || target.EnumerateObject().Any(p => !TargetNames.Contains(p.Name, StringComparer.Ordinal)))
            {
                throw Corrupt();
            }

            if (root.GetProperty("sharedAtRestore").ValueKind == JsonValueKind.Array && root.GetProperty("sharedAtRestore").GetArrayLength() > 256)
            {
                throw Corrupt();
            }

            var recovery = target.GetProperty("recoveryRoot").GetString();
            if (string.IsNullOrWhiteSpace(recovery) || !Path.IsPathFullyQualified(recovery) || recovery != Path.TrimEndingDirectorySeparator(Path.GetFullPath(recovery))
                || ContainsPath(job.Original.Root, recovery) || ContainsPath(recovery, job.Original.Root))
            {
                throw Corrupt();
            }

            if (job.Held is { } held)
            {
                ValidateSnapshot(held);
                if (held.Root != recovery || held.FullPath != Path.Combine(recovery, job.JobId + ".media")
                    || held.Length != job.Original.Length || held.Sha256 != job.Original.Sha256 || job.Phase == RemovalPhase.Prepared)
                {
                    throw Corrupt();
                }
            }
            else if (job.Phase is RemovalPhase.Quarantined or RemovalPhase.ReplacementReady or RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.Purged or RemovalPhase.PurgeOutcomeUnknown)
            {
                throw Corrupt();
            }

            var purgePhase = job.Phase is RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.Purged or RemovalPhase.PurgeOutcomeUnknown;
            if (purgePhase != (job.Purge is not null))
            {
                throw Corrupt();
            }

            if (job.Purge is { } purge && (!IsId(purge.OperationId) || purge.StageName != ".purge-" + job.JobId + "-" + purge.OperationId + ".media"
                || purge.AdminId == Guid.Empty || purge.ConsentUtc < job.CreatedUtc || purge.ConsentUtc.Offset != TimeSpan.Zero
                || purge.FactsSha256 is not { Length: 64 } || purge.FactsSha256.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            {
                throw Corrupt();
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw Corrupt();
        }
    }

    /// <summary>Encodes a bounded strict record, exposed internally for schema tests.</summary>
    /// <param name="job">A private job.</param>
    /// <param name="generation">The next append-only generation.</param>
    /// <returns>The complete UTF8 record.</returns>
    internal static byte[] Encode(RemovalJob job, int generation)
    {
        Validate(job);
        if (generation is < 1 or > MaxGenerations)
        {
            throw Corrupt();
        }

        var bytes = job.Purge is null
            ? JsonSerializer.SerializeToUtf8Bytes(new LegacyEnvelope(1, generation, new LegacyJob(job.JobId, job.Phase, job.Title, job.Original, job.Held, job.ContextJson, job.CreatedUtc)), Options)
            : JsonSerializer.SerializeToUtf8Bytes(new Envelope(2, generation, job), Options);
        return bytes.Length <= MaxRecordBytes ? bytes : throw Corrupt();
    }

    /// <summary>Decodes without silently accepting unknown, missing or malformed fields.</summary>
    /// <param name="bytes">One bounded record.</param>
    /// <returns>The job and exact generation.</returns>
    internal static (RemovalJob Job, int Generation) Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is 0 or > MaxRecordBytes)
            {
                throw Corrupt();
            }

            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            RejectDuplicateNames(document.RootElement);
            var schema = document.RootElement.GetProperty("schema").GetInt32();
            Envelope value;
            if (schema == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyEnvelope>(bytes, Options) ?? throw Corrupt();
                var old = legacy.Job ?? throw Corrupt();
                value = new(1, legacy.Generation, new(old.JobId, old.Phase, old.Title, old.Original, old.Held, old.ContextJson, old.CreatedUtc));
            }
            else if (schema == 2)
            {
                RequireNames(document.RootElement.GetProperty("job"), ["jobId", "phase", "title", "original", "held", "contextJson", "createdUtc", "purge"]);
                value = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw Corrupt();
                if (value.Job?.Purge is null)
                {
                    throw Corrupt();
                }
            }
            else
            {
                throw Corrupt();
            }

            if (value.Generation is < 1 or > MaxGenerations || value.Job is null)
            {
                throw Corrupt();
            }

            Validate(value.Job);
            return (value.Job, value.Generation);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw Corrupt();
        }
    }

    /// <summary>Checks one append-only successor, exposed internally for schema tests.</summary>
    /// <param name="previous">The latest durable generation.</param>
    /// <param name="next">The proposed or recorded next generation.</param>
    internal static void ValidateSuccessor(RemovalJob previous, RemovalJob next)
    {
        // PurgePrepared -> ReplacementReady is the pre-commit rollback: the stage was renamed back before any
        // PurgeCommitting record existed. It must keep every binding except Purge (cleared) and the held inode's
        // change time, which the two renames necessarily advanced. The rolled-back record has no Purge, so it is
        // written as schema one. Downgrade stays fail closed: builds before this rule reject the whole chain as
        // corrupt (an unknown successor), and pre-purge builds already reject the schema-two PurgePrepared record.
        // 2.10.3: the startup sweep returns an interrupted PurgeCommitting job the same way, only after the native probe proved the exact object is still there.
        // 2.10.3 W22-2: and a PurgeCommitting commit that returned NoUnlink, or a PurgeOutcomeUnknown job the next start proved intact.
        var rollback = previous.Phase is RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.PurgeOutcomeUnknown && next.Phase == RemovalPhase.ReplacementReady;
        if (previous.JobId != next.JobId || previous.Original != next.Original || previous.CreatedUtc != next.CreatedUtc || previous.Title != next.Title
            || (previous.Phase == next.Phase ? previous.Phase is RemovalPhase.Restored or RemovalPhase.Purged or RemovalPhase.PurgeOutcomeUnknown : !RemovalPolicy.CanTransition(previous.Phase, next.Phase))
            || (rollback && (previous.Purge is null || next.Purge is not null || previous.ContextJson != next.ContextJson
                || previous.Held is null || next.Held is null || !SameHeldAcrossRename(previous.Held, next.Held)))
            || (!rollback && previous.Purge is not null && (previous.Purge != next.Purge || previous.Held != next.Held || previous.ContextJson != next.ContextJson))
            || (previous.Purge is null && next.Purge is not null && (previous.Phase != RemovalPhase.ReplacementReady || next.Phase != RemovalPhase.PurgePrepared || previous.Held != next.Held || previous.ContextJson != next.ContextJson)))
        {
            throw Corrupt();
        }

        using var before = JsonDocument.Parse(previous.ContextJson);
        using var after = JsonDocument.Parse(next.ContextJson);
        foreach (var binding in new[] { "target", "baseline" })
        {
            if (before.RootElement.GetProperty(binding).GetRawText() != after.RootElement.GetProperty(binding).GetRawText())
            {
                throw Corrupt();
            }
        }
    }

    /// <summary>Compares journaled held evidence with a fresh probe across process lifetimes.</summary>
    /// <remarks>
    /// Persisted identities omit the device number and statx mount ID, which change across container restarts
    /// and reboots; live descriptor pins still compare the full identity inside one operation. Records written
    /// before that change carry device and mount fields and an ancestry hash that embedded them; for those the
    /// file fields are compared without device and mount, and the unrecoverable ancestry hash is not compared
    /// (every operation re-pins and re-verifies the no-follow directory chain live).
    /// </remarks>
    /// <param name="journaled">Held evidence from the journal.</param>
    /// <param name="fresh">A fresh probe of the held name.</param>
    /// <returns>Whether both describe the same unchanged object.</returns>
    internal static bool SameHeld(RemovalFileSnapshot journaled, RemovalFileSnapshot fresh)
    {
        if (journaled == fresh)
        {
            return true;
        }

        var a = journaled.Identity.Split('|');
        var b = fresh.Identity.Split('|');
        return journaled.FullPath == fresh.FullPath && journaled.Root == fresh.Root && journaled.Length == fresh.Length && journaled.Sha256 == fresh.Sha256
            && a.Length == 2 && b.Length == 2 && IsOldFullFormatIdentity(a[0]) && !IsOldFullFormatIdentity(b[0]) && PersistentIdentity(a[0]) == b[0];
    }

    /// <summary>Same held binding, bytes and Linux object, allowing only the inode change time a rename advances.</summary>
    /// <remarks>
    /// The ancestry hash also covers the file's own identity, so a ctime change changes it and the journal
    /// cannot decompose it. Unchanged file identity must keep an unchanged hash. When ctime moved, the native
    /// rollback has already recomputed both hashes from the same pinned directory chain (proving every
    /// directory is the recorded one), and every later review re-probes and compares the new hash exactly.
    /// </remarks>
    /// <param name="before">Journaled held evidence.</param>
    /// <param name="after">Fresh evidence after stage and rollback renames.</param>
    /// <returns>Whether the evidence describes the same unchanged object under the same name.</returns>
    internal static bool SameHeldAcrossRename(RemovalFileSnapshot before, RemovalFileSnapshot after)
    {
        var a = before.Identity.Split('|');
        var b = after.Identity.Split('|');
        return before.FullPath == after.FullPath && before.Root == after.Root && before.Length == after.Length && before.Sha256 == after.Sha256
            && a.Length == 2 && b.Length == 2 && a[1].Length == 64 && b[1].Length == 64 && SameObjectAcrossRename(b[0], a[0])
            && (IsOldFullFormatIdentity(a[0]) || a[0] != b[0] || a[1] == b[1]);
    }

    /// <summary>Compares Linux file identities field by field except the trailing ctime seconds and nanoseconds.</summary>
    /// <param name="current">A freshly read native or persisted identity.</param>
    /// <param name="expected">The journaled identity, current or legacy format.</param>
    /// <returns>Whether only the change time may differ.</returns>
    internal static bool SameObjectAcrossRename(string current, string expected)
    {
        // Persisted file form: l:inode:mode:nlink:size:mtimeS:mtimeNs:ctimeS:ctimeNs
        var x = PersistentIdentity(current).Split(':');
        var y = PersistentIdentity(expected).Split(':');
        return x.Length == 9 && y.Length == 9 && x[0] == "l" && y[0] == "l" && x.AsSpan(0, 7).SequenceEqual(y.AsSpan(0, 7));
    }

    /// <summary>Drops the restart-unstable device and mount fields from a live Linux identity.</summary>
    /// <param name="native">A live identity (l:major:minor:mount:inode:mode[:file fields]) or an already persisted one.</param>
    /// <returns>l:inode:mode[:file fields]; other formats are returned unchanged.</returns>
    internal static string PersistentIdentity(string native) => IsOldFullFormatIdentity(native) ? "l:" + string.Join(':', native.Split(':').Skip(4)) : native;

    // The old full live format (l:major:minor:mount:inode:mode[:file fields]), persisted before 868b82f.
    private static bool IsOldFullFormatIdentity(string native) => native.StartsWith("l:", StringComparison.Ordinal) && native.Split(':').Length is 6 or 12;

    private static IOException Corrupt() => new("removalJournalCorrupt");

    private static string Name(string id, int generation) => id + "." + generation.ToString("D6", CultureInfo.InvariantCulture) + ".json";

    private static bool ContainsPath(string root, string path) => string.Equals(root, path, StringComparison.Ordinal)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void ValidateSnapshot(RemovalFileSnapshot value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Root) || string.IsNullOrWhiteSpace(value.FullPath)
            || !Path.IsPathFullyQualified(value.Root) || !Path.IsPathFullyQualified(value.FullPath)
            || value.Root != Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Root)) || value.FullPath != Path.GetFullPath(value.FullPath)
            || value.Root == value.FullPath || !ContainsPath(value.Root, value.FullPath) || value.Length is <= 0 || value.Length > RemovalPolicy.MaxFileSizeBytes
            || value.Sha256 is not { Length: 64 } || value.Sha256.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || string.IsNullOrWhiteSpace(value.Identity) || value.Identity.Length > 2048 || !NativeName().IsMatch(value.Identity))
        {
            throw Corrupt();
        }
    }

    private static void RequireNames(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Corrupt();
        }

        var actual = value.EnumerateObject().Select(static p => p.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length || actual.Except(names, StringComparer.Ordinal).Any())
        {
            throw Corrupt();
        }
    }

    private static void RejectDuplicateNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw Corrupt();
                }

                RejectDuplicateNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateNames(item);
            }
        }
    }

    private static bool IsId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int ReservedRecords(RemovalJob job) => job.Phase switch
    {
        RemovalPhase.PurgePrepared => 3,
        RemovalPhase.PurgeCommitting => 2,
        _ => 0,
    };

    private static void CheckCapacity(ReadState state, RemovalJob proposed, int records, int bytes)
    {
        var current = state.Records.Where(e => e.Job.JobId == proposed.JobId).MaxBy(static e => e.Generation);
        var otherReservations = state.Records.GroupBy(static e => e.Job.JobId, StringComparer.Ordinal)
            .Where(g => g.Key != proposed.JobId).Sum(g => ReservedRecords(g.MaxBy(static e => e.Generation)!.Job));
        if ((current?.Generation ?? 0) + records > MaxGenerations || state.Records.Count + records + otherReservations > MaxRecords
            || state.Bytes + bytes + (otherReservations * MaxRecordBytes) > MaxTotalBytes)
        {
            throw new IOException("removalJournalFull");
        }
    }

    [GeneratedRegex(@"\A([0-9a-f]{32})\.([0-9]{6})\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecordName();

    [GeneratedRegex(@"\A(?:l|w):[0-9a-fA-F:\-]+\|[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NativeName();

    [GeneratedRegex(@"\A\.pending-[0-9a-f]{32}-[0-9a-f]{32}\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex PendingName();

    private ReadState Read(SafeFileHandle directory)
    {
        var names = Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).Take(MaxRecords + 1).ToArray();
        if (names.Length > MaxRecords)
        {
            throw Corrupt();
        }

        var records = new List<Envelope>();
        var total = 0;
        var pending = false;
        foreach (var name in names)
        {
            var uncommitted = name is not null && PendingName().IsMatch(name);
            if (name is null || (!uncommitted && !RecordName().IsMatch(name)))
            {
                throw Corrupt();
            }

            var bytes = RemovalFiles.ReadJournalRecord(directory, name!, MaxRecordBytes, allowEmpty: uncommitted);
            total += bytes.Length;
            if (total > MaxTotalBytes)
            {
                throw Corrupt();
            }

            if (uncommitted)
            {
                pending = true;
                continue; // Never decode, adopt or discard an uncommitted temporary record.
            }

            var value = Decode(bytes);
            if (name != Name(value.Job.JobId, value.Generation))
            {
                throw Corrupt();
            }

            records.Add(new(1, value.Generation, value.Job));
        }

        var groups = records.GroupBy(static r => r.Job.JobId, StringComparer.Ordinal).ToArray();
        if (groups.Length > MaxJobs)
        {
            throw Corrupt();
        }

        foreach (var group in groups)
        {
            Envelope? prior = null;
            foreach (var record in group.OrderBy(static r => r.Generation))
            {
                if (record.Generation != (prior?.Generation ?? 0) + 1 || (prior is null && record.Job.Phase != RemovalPhase.Prepared))
                {
                    throw Corrupt();
                }

                if (prior is not null)
                {
                    ValidateSuccessor(prior.Job, record.Job);
                }

                prior = record;
            }
        }

        return new(records, total, pending);
    }

    private sealed record Envelope(int Schema, int Generation, RemovalJob Job);

    // An explicit legacy shape preserves required-field and unknown-field rejection for historical schema one.
    private sealed record LegacyEnvelope(int Schema, int Generation, LegacyJob Job);

    private sealed record LegacyJob(string JobId, RemovalPhase Phase, string Title, RemovalFileSnapshot Original, RemovalFileSnapshot? Held, string ContextJson, DateTimeOffset CreatedUtc);

    private sealed record ReadState(List<Envelope> Records, int Bytes, bool Pending);
}
