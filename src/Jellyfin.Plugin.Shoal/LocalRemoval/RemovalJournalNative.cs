using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Checked native durability and ownership for the private append-only journal.</summary>
internal sealed partial class RemovalFiles
{
    private static readonly SearchValues<char> RecoveryNameCharacters = SearchValues.Create("0123456789abcdef");

    /// <summary>Proves private recovery storage shares the source mount and supports checked durability.</summary>
    /// <param name="source">Fresh server-held source evidence.</param>
    /// <param name="destinationRoot">The outside-library holding directory.</param>
    public static void ValidateRecoveryDirectory(RemovalFileSnapshot source, string destinationRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new IOException("removalPlatformUnsupported");
        }

        var parent = Path.GetDirectoryName(source.FullPath) ?? throw Refused();
        using var from = OpenDirectories(source.Root, parent);
        using var to = OpenDirectories(destinationRoot, destinationRoot);
        AssertJournalOwner(to.Pins[^1].Handle, directory: true);
        if (from.Pins[^1].Volume != to.Pins[^1].Volume)
        {
            throw new IOException("removalDifferentFilesystem");
        }

        SyncChecked(from.Pins[^1].Handle);
        SyncChecked(to.Pins[^1].Handle);
        VerifyDirectories(from, source.Root, parent);
        VerifyDirectories(to, destinationRoot, destinationRoot);
    }

    /// <summary>Refuses unknown or unsafe held media without adopting or deleting any entry.</summary>
    /// <param name="root">The coordinator-selected private recovery root.</param>
    /// <param name="expectedJobIds">Committed non-restored jobs bound to exactly this root.</param>
    internal static void ValidateRecoveryInventory(string root, IReadOnlySet<string> expectedJobIds)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root))
        {
            throw new IOException("recoveryRequired");
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(canonical) ?? throw Refused();
        using var pinned = OpenDirectories(parent, parent);
        var parentHandle = pinned.Pins[^1].Handle;
        if (Statx(parentHandle.DangerousGetHandle().ToInt32(), Path.GetFileName(canonical), 0x100, 0x17ff, out _) != 0)
        {
            if (Marshal.GetLastPInvokeError() != 2)
            {
                throw new IOException("recoveryRequired");
            }

            VerifyDirectories(pinned, parent, parent);
            return; // A truly absent final entry is empty; no recovery directory is created here.
        }

        try
        {
            _ = InJournalDirectory(canonical, directory =>
            {
                var names = Directory.EnumerateFileSystemEntries(canonical).Select(Path.GetFileName).Take(1025).ToArray();
                if (names.Length > 1024)
                {
                    throw new IOException("recoveryRequired");
                }

                var volume = ReadPin(directory, directory: true).Volume;
                foreach (var name in names)
                {
                    // 2.10.3 (W20-5): an interrupted delete's own stage name is a known entry of that one job, so it no longer blocks every other removal.
                    var heldName = name is { Length: 38 } && name.EndsWith(".media", StringComparison.Ordinal)
                        && name.AsSpan(0, 32).IndexOfAnyExcept(RecoveryNameCharacters) < 0 && expectedJobIds.Contains(name[..32]);
                    var stageName = name is { Length: 78 } && name.StartsWith(".purge-", StringComparison.Ordinal) && name.EndsWith(".media", StringComparison.Ordinal)
                        && name[39] == '-' && name.AsSpan(7, 32).IndexOfAnyExcept(RecoveryNameCharacters) < 0 && name.AsSpan(40, 32).IndexOfAnyExcept(RecoveryNameCharacters) < 0
                        && expectedJobIds.Contains(name);
                    if (name is null || (!heldName && !stageName))
                    {
                        throw new IOException("recoveryRequired");
                    }

                    using var file = OpenNative(directory, name, directory: false);
                    var value = ReadPin(file, directory: false);
                    if (value.Length is <= 0 || value.Length > RemovalPolicy.MaxFileSizeBytes || value.Volume != volume)
                    {
                        throw new IOException("recoveryRequired");
                    }
                }

                return 0;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("recoveryRequired");
        }

        VerifyDirectories(pinned, parent, parent);
    }

    /// <summary>Creates only one private recovery child beneath pinned existing ancestors.</summary>
    /// <param name="root">A coordinator-derived directory outside every scanned root.</param>
    internal static void EnsureRecoveryDirectory(string root)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root))
        {
            throw new IOException("removalPlatformUnsupported");
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(canonical) ?? throw Refused();
        var name = Path.GetFileName(canonical);
        using var pinned = OpenDirectories(parent, parent);
        var parentHandle = pinned.Pins[^1].Handle;
        SyncChecked(parentHandle);
        if (CreateRecoveryDirectory(parentHandle.DangerousGetHandle().ToInt32(), name, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
        {
            throw new IOException("removalRecoveryDirectory");
        }

        using var child = OpenNative(parentHandle, name, directory: true);
        AssertJournalOwner(child, directory: true);
        if (ReadPin(child, directory: true).Volume != pinned.Pins[^1].Volume)
        {
            throw Refused();
        }

        SyncChecked(child);
        SyncChecked(parentHandle);
        VerifyDirectories(pinned, parent, parent);
    }

    /// <summary>Keeps every journal ancestor pinned across one bounded storage operation.</summary>
    /// <typeparam name="T">The operation result.</typeparam>
    /// <param name="root">The existing service-owned0700 directory.</param>
    /// <param name="operation">Trusted bounded relative-descriptor operations only.</param>
    /// <returns>The verified operation result.</returns>
    internal static T InJournalDirectory<T>(string root, Func<SafeFileHandle, T> operation)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new IOException("removalPlatformUnsupported");
        }

        using var directory = OpenDirectories(root, root);
        var handle = directory.Pins[^1].Handle;
        AssertJournalOwner(handle, directory: true);
        var result = operation(handle);
        AssertJournalOwner(handle, directory: true);
        VerifyDirectories(directory, root, root);
        return result;
    }

    /// <summary>Reads one bounded regular0600 owned record without following a link.</summary>
    /// <param name="directory">The pinned journal directory.</param>
    /// <param name="name">A validated basename.</param>
    /// <param name="limit">The byte cap.</param>
    /// <param name="allowEmpty">Only pending records may be empty after an interrupted write.</param>
    /// <returns>The complete stable record bytes.</returns>
    internal static byte[] ReadJournalRecord(SafeFileHandle directory, string name, int limit, bool allowEmpty = false)
    {
        using var handle = OpenNative(directory, name, directory: false);
        AssertJournalOwner(handle, directory: false);
        var before = ReadPin(handle, directory: false);
        if (before.Length < 0 || (!allowEmpty && before.Length == 0) || before.Length > limit)
        {
            throw new IOException("removalJournalCorrupt");
        }

        var bytes = new byte[(int)before.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
            {
                throw new IOException("removalJournalCorrupt");
            }

            offset += read;
        }

        Span<byte> extra = stackalloc byte[1];
        if (RandomAccess.Read(handle, extra, offset) != 0 || ReadPin(handle, directory: false).Identity != before.Identity)
        {
            throw new IOException("removalJournalCorrupt");
        }

        return bytes;
    }

    /// <summary>Appends one generation using exclusive creation, full file flush and no-replace commit.</summary>
    /// <param name="directory">The pinned private directory.</param>
    /// <param name="temporary">A unique temporary basename.</param>
    /// <param name="name">The new generation basename.</param>
    /// <param name="bytes">The complete bounded validated record.</param>
    /// <param name="fault">Optional trusted fault observer.</param>
    internal static void WriteJournalRecord(SafeFileHandle directory, string temporary, string name, ReadOnlySpan<byte> bytes, Action<RemovalJournalPoint>? fault)
    {
        SyncChecked(directory);
        var flags = 1 | 0x40 | 0x80 | 0x80000 | (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000);
        var fd = CreateJournalFile(directory.DangerousGetHandle().ToInt32(), temporary, flags, 0x180);
        if (fd < 0)
        {
            throw new IOException("removalJournalWriteRefused");
        }

        using var file = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        if (SetJournalMode(fd, 0x180) != 0)
        {
            throw new IOException("removalJournalWriteRefused");
        }

        RandomAccess.Write(file, bytes, 0);
        SyncChecked(file);
        fault?.Invoke(RemovalJournalPoint.AfterFileFlush);
        if (RenameNoReplace(directory.DangerousGetHandle().ToInt32(), temporary, directory.DangerousGetHandle().ToInt32(), name, 1) != 0)
        {
            throw new IOException("removalJournalWriteRefused");
        }

        fault?.Invoke(RemovalJournalPoint.AfterRename);
        fault?.Invoke(RemovalJournalPoint.BeforeDirectoryFlush);
        SyncChecked(directory);
    }

    private static void AssertJournalOwner(SafeFileHandle handle, bool directory)
    {
        if (JournalStat(handle.DangerousGetHandle().ToInt32(), string.Empty, 0x1000, 0xf, out var owner) != 0
            || (owner.Mask & 0xf) != 0xf || owner.Owner != EffectiveUser()
            || (owner.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (owner.Mode & 0x1ff) != (directory ? 0x1c0 : 0x180) || (!directory && owner.Links != 1))
        {
            throw new IOException("removalJournalOwnership");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateRecoveryDirectory(int directory, string name, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateJournalFile(int directory, string name, int flags, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int SetJournalMode(int fd, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUser();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int JournalStat(int fd, string path, int flags, uint mask, out JournalOwner value);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct JournalOwner
    {
        [FieldOffset(0)]
        public uint Mask;
        [FieldOffset(16)]
        public uint Links;
        [FieldOffset(20)]
        public uint Owner;
        [FieldOffset(28)]
        public ushort Mode;
    }
}
