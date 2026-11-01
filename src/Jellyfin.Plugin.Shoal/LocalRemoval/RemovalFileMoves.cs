using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Deterministic transaction boundaries used by fault-injection tests.</summary>
internal enum RemovalMovePoint
{
    /// <summary>Original bytes are still at the source name.</summary>
    BeforeRename,

    /// <summary>The no-overwrite rename completed; both known paths must remain recoverable.</summary>
    AfterRename,

    /// <summary>The renamed file is flushed and both directory flushes are still required.</summary>
    BeforeDirectoryFlush,
}

/// <summary>Strict no-overwrite, same-mount moves. Unsupported mutation platforms refuse before touching files.</summary>
internal sealed partial class RemovalFiles
{
    private readonly Action<RemovalMovePoint>? _moveFault;

    /// <summary>Initializes a new instance of the <see cref="RemovalFiles"/> class.</summary>
    /// <param name="moveFault">An optional trusted fault observer; normal registrations leave this null.</param>
    public RemovalFiles(Action<RemovalMovePoint>? moveFault = null) => _moveFault = moveFault;

    /// <summary>Moves one freshly verified file without replacing any destination or discarding bytes.</summary>
    /// <param name="expected">Server-held evidence for the exact source.</param>
    /// <param name="destinationRoot">An existing private directory already admitted by the coordinator.</param>
    /// <param name="destinationName">A basename only; Restore uses the recorded original basename.</param>
    /// <param name="cancellationToken">Cancellation before rename. A committed move finishes validation independently.</param>
    /// <returns>The new full-file evidence at its held or restored name.</returns>
    public async Task<RemovalFileSnapshot> MoveAsync(RemovalFileSnapshot expected, string destinationRoot, string destinationName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Windows read probes are supported. Its rename/directory durability protocol is not yet established.
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new IOException("removalPlatformUnsupported");
        }

        if (!Path.IsPathFullyQualified(destinationRoot) || string.IsNullOrWhiteSpace(destinationName)
            || destinationName is "." or ".." || destinationName.AsSpan().IndexOfAny('\0', '/', '\\') >= 0
            || destinationName.Length > 255 || Path.IsPathRooted(destinationName))
        {
            throw Refused();
        }

        var targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var targetPath = Path.Combine(targetRoot, destinationName);
        var fresh = await ProbeAsync(expected.Root, expected.FullPath, cancellationToken).ConfigureAwait(false);
        if (fresh != expected)
        {
            throw new IOException("removalSourceChanged");
        }

        var sourceParent = Path.GetDirectoryName(fresh.FullPath) ?? throw Refused();
        using var source = OpenDirectories(fresh.Root, sourceParent);
        using var target = OpenDirectories(targetRoot, targetRoot);
        var sourceDirectory = source.Pins[^1];
        var targetDirectory = target.Pins[^1];
        if (sourceDirectory.Volume != targetDirectory.Volume)
        {
            throw new IOException("removalDifferentFilesystem");
        }

        var sourceName = Path.GetFileName(fresh.FullPath);
        using var file = OpenNative(sourceDirectory.Handle, sourceName, directory: false);
        var fileIdentity = ReadPin(file, directory: false);
        if (RemovalJournal.PersistentIdentity(fileIdentity.Identity) != RemovalJournal.PersistentIdentity(NativeIdentity(fresh)) || fileIdentity.Volume != sourceDirectory.Volume)
        {
            throw new IOException("removalSourceChanged");
        }

        SyncChecked(file);
        SyncChecked(sourceDirectory.Handle);
        SyncChecked(targetDirectory.Handle);
        _moveFault?.Invoke(RemovalMovePoint.BeforeRename);
        VerifyDirectories(source, fresh.Root, sourceParent);
        VerifyDirectories(target, targetRoot, targetRoot);
        if (ReadPin(file, directory: false).Identity != fileIdentity.Identity)
        {
            throw new IOException("removalSourceChanged");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (RenameNoReplace(sourceDirectory.Handle.DangerousGetHandle().ToInt32(), sourceName, targetDirectory.Handle.DangerousGetHandle().ToInt32(), destinationName, 1) != 0)
            {
                throw new IOException("removalRenameRefused");
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw new IOException("removalPlatformUnsupported");
        }

        // Past this boundary the journal's prepared source and target names must be reconciled on any failure.
        try
        {
            _moveFault?.Invoke(RemovalMovePoint.AfterRename);
            SyncChecked(file);
            _moveFault?.Invoke(RemovalMovePoint.BeforeDirectoryFlush);
            SyncChecked(sourceDirectory.Handle);
            SyncChecked(targetDirectory.Handle);
            VerifyDirectories(source, fresh.Root, sourceParent);
            VerifyDirectories(target, targetRoot, targetRoot);
            file.Dispose();
            var moved = await ProbeAsync(targetRoot, targetPath, CancellationToken.None).ConfigureAwait(false);
            if (moved.Length != fresh.Length || moved.Sha256 != fresh.Sha256 || ObjectIdentity(moved) != ObjectIdentity(fresh))
            {
                throw Refused();
            }

            return moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            // No blind rollback or unlink: another actor may now own the original name.
            throw new IOException("moveNeedsRecovery");
        }
    }

    private static string NativeIdentity(RemovalFileSnapshot snapshot) => snapshot.Identity.Split('|')[0];

    // Linux: l:inode (restart-stable); Windows keeps volume and file ID.
    private static string ObjectIdentity(RemovalFileSnapshot snapshot) => NativeIdentity(snapshot).StartsWith("l:", StringComparison.Ordinal)
        ? string.Join(':', RemovalJournal.PersistentIdentity(NativeIdentity(snapshot)).Split(':').Take(2)) : string.Join(':', NativeIdentity(snapshot).Split(':').Take(5));

    private static string Persisted(SafeFileHandle file) => RemovalJournal.PersistentIdentity(ReadPin(file, directory: false).Identity);

    private static void SyncChecked(SafeFileHandle handle)
    {
        if (SyncFile(handle.DangerousGetHandle().ToInt32()) != 0)
        {
            throw new IOException("removalDurabilityFailed");
        }
    }

    private static void VerifyDirectories(ProbeChain expected, string root, string directory)
    {
        using var actual = OpenDirectories(root, directory);
        if (!expected.Pins.Select(static p => p.Identity).SequenceEqual(actual.Pins.Select(static p => p.Identity), StringComparer.Ordinal))
        {
            throw Refused();
        }
    }

    private static ProbeChain OpenDirectories(string root, string directory)
    {
        var chain = new ProbeChain();
        try
        {
            var current = Path.GetPathRoot(directory) ?? throw Refused();
            chain.Add(OpenNative(null, current, directory: true), current, directory: true);
            foreach (var component in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                chain.Add(OpenNative(chain.Pins[^1].Handle, component, directory: true), current, directory: true);
            }

            var rootIndex = chain.Pins.FindIndex(p => string.Equals(p.Path, root, StringComparison.Ordinal));
            if (rootIndex < 0 || chain.Pins.Skip(rootIndex).Any(p => p.Volume != chain.Pins[rootIndex].Volume))
            {
                throw Refused();
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameNoReplace(int sourceDirectory, string sourceName, int targetDirectory, string targetName, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int SyncFile(int fd);
}
