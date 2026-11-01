using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Irreversible outcome information is retained even when a post-unlink check fails.</summary>
internal enum RemovalPurgeResult
{
    /// <summary>No unlink succeeded. Staged bytes still need conservative reconciliation.</summary>
    NoUnlink,

    /// <summary>The authorized name was unlinked and its containing directory flushed.</summary>
    UnlinkedAndFlushed,

    /// <summary>Unlink succeeded but later durability or verification was uncertain.</summary>
    UnlinkOutcomeUnknown,
}

/// <summary>Trusted fault boundaries, never configured by peers or administrators.</summary>
internal enum RemovalPurgePoint
{
    /// <summary>The held name is still present.</summary>
    BeforeRename,

    /// <summary>The fixed private staged name exists.</summary>
    AfterRename,

    /// <summary>The staged directory has not yet been flushed.</summary>
    BeforeStageFlush,

    /// <summary>Fresh full hash and name/object checks run after this observer.</summary>
    BeforeUnlink,

    /// <summary>Unlink succeeded; failures now mean an unknown durability outcome.</summary>
    AfterUnlink,

    /// <summary>The directory flush is still outstanding.</summary>
    BeforeDirectoryFlush,

    /// <summary>A pre-commit rollback is about to re-check the staged name.</summary>
    BeforeUnstage,

    /// <summary>The staged name was renamed back; nothing is flushed or re-verified yet.</summary>
    AfterUnstageRename,

    /// <summary>The rolled-back directory entry still needs its checked flush.</summary>
    BeforeUnstageFlush,
}

/// <summary>Whether a pre-commit rollback proved the retained original is back under its held name.</summary>
internal enum RemovalUnstageResult
{
    /// <summary>Not proved. The caller must keep the job outcome-unknown; nothing was unlinked.</summary>
    NotRestored,

    /// <summary>The exact held object is under its held name, flushed and re-hashed.</summary>
    Restored,
}

/// <summary>A rollback result plus the fresh held evidence (a rename changes only the inode change time).</summary>
/// <param name="Result">Whether restoration was proved.</param>
/// <param name="Held">Fresh held evidence when restored, otherwise null.</param>
internal sealed record RemovalUnstageOutcome(RemovalUnstageResult Result, RemovalFileSnapshot? Held)
{
    /// <summary>Gets the fail-closed result.</summary>
    internal static RemovalUnstageOutcome NotRestored { get; } = new(RemovalUnstageResult.NotRestored, null);
}

/// <summary>A live staged claim retaining native descriptors until the coordinator finishes.</summary>
internal sealed class RemovalPurgeStage : IDisposable
{
    private readonly Func<RemovalJob, Action, CancellationToken, Task<RemovalPurgeResult>> _commit;
    private readonly Func<RemovalJob, CancellationToken, Task<RemovalUnstageOutcome>>? _unstage;
    private readonly Action _dispose;
    private int _used;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="RemovalPurgeStage"/> class.</summary>
    /// <param name="snapshot">Verified staged evidence.</param>
    /// <param name="commit">Bound native operation, or a controlled test seam.</param>
    /// <param name="dispose">Release every pinned descriptor.</param>
    /// <param name="unstage">Bound pre-commit rollback; absent means rollback is never proved.</param>
    internal RemovalPurgeStage(RemovalFileSnapshot snapshot, Func<RemovalJob, Action, CancellationToken, Task<RemovalPurgeResult>> commit, Action dispose, Func<RemovalJob, CancellationToken, Task<RemovalUnstageOutcome>>? unstage = null)
    {
        Snapshot = snapshot;
        _commit = commit;
        _dispose = dispose;
        _unstage = unstage;
    }

    /// <summary>Gets the verified fixed staged name and complete byte evidence.</summary>
    internal RemovalFileSnapshot Snapshot { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }

    /// <summary>Consumes a live stage once; disposing or retrying never replays unlink.</summary>
    /// <param name="job">The durable committing authority.</param>
    /// <param name="authorizeImmediatelyBeforeUnlink">Mandatory fresh synchronous authority check after native hashing.</param>
    /// <param name="token">Cancellation before the irreversible boundary.</param>
    /// <returns>An honest native outcome.</returns>
    internal Task<RemovalPurgeResult> CommitAsync(RemovalJob job, Action authorizeImmediatelyBeforeUnlink, CancellationToken token)
    {
        if (authorizeImmediatelyBeforeUnlink is null || Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _used, 1) != 0)
        {
            return Task.FromResult(RemovalPurgeResult.NoUnlink);
        }

        return _commit(job, authorizeImmediatelyBeforeUnlink, token);
    }

    /// <summary>Consumes the same one-use claim as <see cref="CommitAsync"/>: once commit was attempted, rollback is refused and vice versa.</summary>
    /// <param name="job">The exact PurgePrepared authority that staged this claim.</param>
    /// <param name="token">Rollback cancellation; cancellation leaves the outcome unknown.</param>
    /// <returns>Restored only when the held object is proved back under its held name.</returns>
    internal Task<RemovalUnstageOutcome> UnstageAsync(RemovalJob job, CancellationToken token)
    {
        if (_unstage is null || job is null || job.Phase != RemovalPhase.PurgePrepared || Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _used, 1) != 0)
        {
            return Task.FromResult(RemovalUnstageOutcome.NotRestored);
        }

        return _unstage(job, token);
    }
}

/// <summary>Linux-only staged retained-original deletion; never accepts a browser path.</summary>
internal sealed partial class RemovalFiles
{
    /// <summary>Stages only the exact durably authorized held object, retaining its descriptor.</summary>
    /// <param name="job">Validated PurgePrepared authority already committed to the journal.</param>
    /// <param name="cancellationToken">Cancellation before staging; later failure preserves all names.</param>
    /// <param name="fault">Optional trusted fault observer.</param>
    /// <returns>A disposable live native claim. Restart cannot reconstruct permission to unlink.</returns>
    internal async Task<RemovalPurgeStage> StagePurgeAsync(RemovalJob job, CancellationToken cancellationToken, Action<RemovalPurgePoint>? fault = null)
    {
        RequirePurgePlatform();
        RemovalJournal.Validate(job);
        if (job.Phase != RemovalPhase.PurgePrepared || job.Purge is null || job.Held is null)
        {
            throw new IOException("purgeAuthorityRequired");
        }

        var held = job.Held;
        var authority = job.Purge;
        var fresh = await ProbeAsync(held.Root, held.FullPath, cancellationToken).ConfigureAwait(false);
        if (!RemovalJournal.SameHeld(held, fresh))
        {
            throw new IOException("removalSourceChanged");
        }

        var originalParent = Path.GetDirectoryName(job.Original.FullPath) ?? throw Refused();
        var original = OpenDirectories(job.Original.Root, originalParent);
        ProbeChain? recovery = null;
        SafeFileHandle? file = null;
        try
        {
            recovery = OpenDirectories(held.Root, held.Root);
            var directory = recovery.Pins[^1].Handle;
            AssertJournalOwner(directory, directory: true);
            if (original.Pins[^1].Volume != recovery.Pins[^1].Volume)
            {
                throw new IOException("removalDifferentFilesystem");
            }

            RequireAbsent(original.Pins[^1].Handle, Path.GetFileName(job.Original.FullPath));
            var heldName = Path.GetFileName(held.FullPath);
            file = OpenNative(directory, heldName, directory: false);
            if (Persisted(file) != RemovalJournal.PersistentIdentity(NativeIdentity(held)))
            {
                throw new IOException("removalSourceChanged");
            }

            SyncChecked(file);
            SyncChecked(directory);
            fault?.Invoke(RemovalPurgePoint.BeforeRename);
            ValidatePurgePins(original, recovery, job);
            RequireNamedObject(directory, heldName, file);
            cancellationToken.ThrowIfCancellationRequested();
            if (RenameNoReplace(directory.DangerousGetHandle().ToInt32(), heldName, directory.DangerousGetHandle().ToInt32(), authority.StageName, 1) != 0)
            {
                throw new IOException("purgeStageRefused");
            }

            fault?.Invoke(RemovalPurgePoint.AfterRename);
            SyncChecked(file);
            fault?.Invoke(RemovalPurgePoint.BeforeStageFlush);
            SyncChecked(directory);
            ValidatePurgePins(original, recovery, job);
            RequireAbsent(directory, heldName);
            RequireNamedObject(directory, authority.StageName, file);
            var staged = await ProbeAsync(held.Root, Path.Combine(held.Root, authority.StageName), cancellationToken).ConfigureAwait(false);
            if (staged.Length != held.Length || staged.Sha256 != held.Sha256 || ObjectIdentity(staged) != ObjectIdentity(held))
            {
                throw new IOException("removalSourceChanged");
            }

            RequireNamedObject(directory, authority.StageName, file);
            var pinnedRecovery = recovery;
            var pinnedFile = file;
            return new(
                staged,
                async (committing, authorizeImmediatelyBeforeUnlink, token) =>
                {
                    var unlinked = false;
                    try
                    {
                        RemovalJournal.Validate(committing);
                        if (committing != job with { Phase = RemovalPhase.PurgeCommitting })
                        {
                            return RemovalPurgeResult.NoUnlink;
                        }

                        fault?.Invoke(RemovalPurgePoint.BeforeUnlink);
                        token.ThrowIfCancellationRequested();
                        ValidatePurgePins(original, pinnedRecovery, job);
                        RequireAbsent(directory, heldName);
                        RequireNamedObject(directory, authority.StageName, pinnedFile);
                        var final = await ProbeAsync(held.Root, staged.FullPath, token).ConfigureAwait(false);
                        if (final != staged || Persisted(pinnedFile) != NativeIdentity(final))
                        {
                            return RemovalPurgeResult.NoUnlink;
                        }

                        // Native hashing can take minutes. Re-read local authority synchronously AFTER it,
                        // with no further await before the final namespace checks and irreversible syscall.
                        try
                        {
                            authorizeImmediatelyBeforeUnlink();
                        }
#pragma warning disable CA1031 // Any trusted authorization callback failure must refuse irreversible unlink.
                        catch (Exception)
                        {
                            return RemovalPurgeResult.NoUnlink;
                        }
#pragma warning restore CA1031

                        ValidatePurgePins(original, pinnedRecovery, job);
                        RequireAbsent(directory, heldName);
                        RequireNamedObject(directory, authority.StageName, pinnedFile);
                        token.ThrowIfCancellationRequested();

                        // Linux has no unlink-by-fd. The checked private0700 namespace excludes other UIDs;
                        // malicious same-UID/root writers remain outside this guarantee.
                        if (UnlinkPurgeName(directory.DangerousGetHandle().ToInt32(), authority.StageName, 0) != 0)
                        {
                            return RemovalPurgeResult.NoUnlink;
                        }

                        unlinked = true;
                        fault?.Invoke(RemovalPurgePoint.AfterUnlink);
                        fault?.Invoke(RemovalPurgePoint.BeforeDirectoryFlush);
                        SyncChecked(directory);
                        ValidatePurgePins(original, pinnedRecovery, job);
                        RequireAbsent(directory, authority.StageName);
                        return RemovalPurgeResult.UnlinkedAndFlushed;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or EntryPointNotFoundException)
                    {
                        return unlinked ? RemovalPurgeResult.UnlinkOutcomeUnknown : RemovalPurgeResult.NoUnlink;
                    }
                },
                () =>
                {
                    pinnedFile.Dispose();
                    pinnedRecovery.Dispose();
                    original.Dispose();
                },
                async (prepared, token) =>
                {
                    try
                    {
                        RemovalJournal.Validate(prepared);
                        return prepared == job ? await UnstageCoreAsync(job, original, pinnedRecovery, pinnedFile, fault, token).ConfigureAwait(false) : RemovalUnstageOutcome.NotRestored;
                    }
                    catch (Exception ex) when (UnprovedRollback(ex))
                    {
                        return RemovalUnstageOutcome.NotRestored;
                    }
                });
        }
        catch
        {
            file?.Dispose();
            recovery?.Dispose();
            original.Dispose();
            throw; // Never unlink or blindly roll back a failed stage.
        }
    }

    /// <summary>Consumes the live native claim only after durable PurgeCommitting intent.</summary>
    /// <param name="job">Exact committing authority.</param>
    /// <param name="stage">The live claim from this process, never reconstructed from disk.</param>
    /// <param name="authorizeImmediatelyBeforeUnlink">Mandatory fresh synchronous authority check after full hashing.</param>
    /// <param name="cancellationToken">Cancellation before unlink; outcomes after unlink stay classified.</param>
    /// <returns>The irreversible outcome, without generic post-unlink exceptions.</returns>
    internal static Task<RemovalPurgeResult> CommitPurgeAsync(RemovalJob job, RemovalPurgeStage stage, Action authorizeImmediatelyBeforeUnlink, CancellationToken cancellationToken) => stage.CommitAsync(job, authorizeImmediatelyBeforeUnlink, cancellationToken);

    /// <summary>Rolls back a pre-commit stage. With a live claim it uses the pinned descriptors; without one (staging threw) it probes fresh.</summary>
    /// <param name="job">The exact PurgePrepared authority; the caller must not have recorded PurgeCommitting.</param>
    /// <param name="stage">The live claim, or null when staging itself failed.</param>
    /// <param name="cancellationToken">Rollback cancellation; any failure is NotRestored.</param>
    /// <returns>Restored only with proof; this path never unlinks or overwrites anything.</returns>
    internal Task<RemovalUnstageOutcome> UnstagePurgeAsync(RemovalJob job, RemovalPurgeStage? stage, CancellationToken cancellationToken)
        => stage is not null ? stage.UnstageAsync(job, cancellationToken) : RecoverPurgeStageAsync(job, cancellationToken);

    /// <summary>Probe for a stage attempt that threw before returning a claim.</summary>
    /// <param name="job">The exact PurgePrepared authority.</param>
    /// <param name="cancellationToken">Cancellation; any failure is NotRestored.</param>
    /// <param name="fault">Optional trusted fault observer.</param>
    /// <returns>Restored when nothing moved, or when the exact object was renamed back; otherwise NotRestored.</returns>
    internal async Task<RemovalUnstageOutcome> RecoverPurgeStageAsync(RemovalJob job, CancellationToken cancellationToken, Action<RemovalPurgePoint>? fault = null)
    {
        ProbeChain? original = null;
        ProbeChain? recovery = null;
        try
        {
            RequirePurgePlatform();
            RemovalJournal.Validate(job);

            // PurgeCommitting and PurgeOutcomeUnknown reach here only from the startup sweep after a restart, or after this process's own commit
            // returned NoUnlink: no commit can be in flight (W20-5, W22-2).
            if (job.Phase is not (RemovalPhase.PurgePrepared or RemovalPhase.PurgeCommitting or RemovalPhase.PurgeOutcomeUnknown) || job.Purge is null || job.Held is null)
            {
                return RemovalUnstageOutcome.NotRestored;
            }

            var held = job.Held;
            original = OpenDirectories(job.Original.Root, Path.GetDirectoryName(job.Original.FullPath) ?? throw Refused());
            recovery = OpenDirectories(held.Root, held.Root);
            var directory = recovery.Pins[^1].Handle;
            AssertJournalOwner(directory, directory: true);
            if (original.Pins[^1].Volume != recovery.Pins[^1].Volume)
            {
                return RemovalUnstageOutcome.NotRestored;
            }

            ValidatePurgePins(original, recovery, job);
            var heldName = Path.GetFileName(held.FullPath);
            var heldPresent = Present(directory, heldName);
            var stagePresent = Present(directory, job.Purge.StageName);
            if (heldPresent && !stagePresent)
            {
                // Nothing moved: the held name must still be the exact journaled object (inode, size, mtime and
                // ctime unchanged) under the same pinned ancestry. No re-hash: a slow disk must not turn an
                // untouched original into an unknown outcome, and any write would have advanced mtime/ctime.
                using var file = OpenNative(directory, heldName, directory: false);
                var identity = Persisted(file);
                if (OldFullFormatHeld(held) ? identity != RemovalJournal.PersistentIdentity(NativeIdentity(held)) : identity + "|" + Ancestry(recovery, identity) != held.Identity)
                {
                    return RemovalUnstageOutcome.NotRestored;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ValidatePurgePins(original, recovery, job);
                RequireAbsent(directory, job.Purge.StageName);
                RequireNamedObject(directory, heldName, file);
                return Persisted(file) == RemovalJournal.PersistentIdentity(NativeIdentity(held))
                    ? new(RemovalUnstageResult.Restored, held) : RemovalUnstageOutcome.NotRestored;
            }

            if (stagePresent && !heldPresent)
            {
                using var file = OpenNative(directory, job.Purge.StageName, directory: false);
                return await UnstageCoreAsync(job, original, recovery, file, fault, cancellationToken).ConfigureAwait(false);
            }

            return RemovalUnstageOutcome.NotRestored; // Both or neither name: never guess.
        }
        catch (Exception ex) when (UnprovedRollback(ex))
        {
            return RemovalUnstageOutcome.NotRestored;
        }
        finally
        {
            recovery?.Dispose();
            original?.Dispose();
        }
    }

    // Renames the private stage name back with RENAME_NOREPLACE. Throws on any doubt; callers map throws to NotRestored.
    private async Task<RemovalUnstageOutcome> UnstageCoreAsync(RemovalJob job, ProbeChain original, ProbeChain recovery, SafeFileHandle file, Action<RemovalPurgePoint>? fault, CancellationToken token)
    {
        var held = job.Held!;
        var stageName = job.Purge!.StageName;
        var heldName = Path.GetFileName(held.FullPath);
        var directory = recovery.Pins[^1].Handle;
        fault?.Invoke(RemovalPurgePoint.BeforeUnstage);
        ValidatePurgePins(original, recovery, job);
        RequireAbsent(directory, heldName);
        RequireNamedObject(directory, stageName, file);

        // The journaled ancestry hash covers every directory pin from the filesystem root to the recovery
        // directory plus the file's own identity (whose ctime a rename advances). Recomputing it from the
        // pinned directories with the journaled file identity proves every directory (inode, mode, mount)
        // is the one recorded, without accepting any drift in them.
        if (!RemovalJournal.SameObjectAcrossRename(ReadPin(file, directory: false).Identity, NativeIdentity(held))
            || (!OldFullFormatHeld(held) && Ancestry(recovery, NativeIdentity(held)) != AncestryOf(held)))
        {
            throw new IOException("removalSourceChanged");
        }

        token.ThrowIfCancellationRequested();
        if (RenameNoReplace(directory.DangerousGetHandle().ToInt32(), stageName, directory.DangerousGetHandle().ToInt32(), heldName, 1) != 0)
        {
            throw new IOException("purgeUnstageRefused");
        }

        fault?.Invoke(RemovalPurgePoint.AfterUnstageRename);
        SyncChecked(file);
        fault?.Invoke(RemovalPurgePoint.BeforeUnstageFlush);
        SyncChecked(directory);
        ValidatePurgePins(original, recovery, job);
        RequireAbsent(directory, stageName);
        RequireNamedObject(directory, heldName, file);
        var restored = await ProbeAsync(held.Root, held.FullPath, token).ConfigureAwait(false);
        RequireNamedObject(directory, heldName, file);
        if (!RemovalJournal.SameHeldAcrossRename(held, restored) || Persisted(file) != NativeIdentity(restored)
            || Ancestry(recovery, NativeIdentity(restored)) != AncestryOf(restored) || (!OldFullFormatHeld(held) && Ancestry(recovery, NativeIdentity(held)) != AncestryOf(held)))
        {
            throw new IOException("removalSourceChanged");
        }

        return new(RemovalUnstageResult.Restored, restored);
    }

    // Same construction as ProbeAsync: directory pins from the filesystem root, then the file identity.
    private static string Ancestry(ProbeChain directories, string fileIdentity) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', directories.Pins.Select(static p => RemovalJournal.PersistentIdentity(p.Identity)).Append(fileIdentity)))));

    // Held evidence journaled before persisted identities dropped device and mount IDs: its ancestry hash
    // embedded them and cannot be re-derived after a restart; the live pins re-verify the chain instead.
    private static bool OldFullFormatHeld(RemovalFileSnapshot held) => RemovalJournal.PersistentIdentity(NativeIdentity(held)) != NativeIdentity(held);

    private static string AncestryOf(RemovalFileSnapshot snapshot) => snapshot.Identity.Split('|') is [_, var ancestry] ? ancestry : string.Empty;

    // Any failure while proving a rollback means NotRestored; only process-fatal errors propagate.
    private static bool UnprovedRollback(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static bool Present(SafeFileHandle directory, string name)
    {
        if (Statx(directory.DangerousGetHandle().ToInt32(), name, 0x100, 0x17ff, out _) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() == 2 ? false : throw new IOException("purgePathUncertain");
    }

    private static void RequirePurgePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new IOException("removalPlatformUnsupported");
        }
    }

    private static void ValidatePurgePins(ProbeChain original, ProbeChain recovery, RemovalJob job)
    {
        AssertJournalOwner(recovery.Pins[^1].Handle, directory: true);
        VerifyDirectories(original, job.Original.Root, Path.GetDirectoryName(job.Original.FullPath)!);
        VerifyDirectories(recovery, job.Held!.Root, job.Held.Root);
        RequireAbsent(original.Pins[^1].Handle, Path.GetFileName(job.Original.FullPath));
    }

    private static void RequireAbsent(SafeFileHandle directory, string name)
    {
        if (Statx(directory.DangerousGetHandle().ToInt32(), name, 0x100, 0x17ff, out _) == 0 || Marshal.GetLastPInvokeError() != 2)
        {
            throw new IOException("purgePathOccupiedOrUncertain");
        }
    }

    private static void RequireNamedObject(SafeFileHandle directory, string name, SafeFileHandle pinned)
    {
        using var current = OpenNative(directory, name, directory: false);
        if (ReadPin(current, directory: false).Identity != ReadPin(pinned, directory: false).Identity)
        {
            throw new IOException("removalSourceChanged");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkPurgeName(int directory, string name, int flags);
}
