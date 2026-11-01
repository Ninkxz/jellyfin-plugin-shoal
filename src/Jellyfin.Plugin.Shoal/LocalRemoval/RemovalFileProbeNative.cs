using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Native no-follow identities for supported local filesystems.</summary>
internal sealed partial class RemovalFiles
{
    /// <summary>The refusal raised when a file or folder is on a filesystem the removal proofs do not support (a network share); an <see cref="System.IO.IOException"/> like every other native refusal.</summary>
    internal const string UnsupportedFilesystem = "unsupportedFilesystem";

    private static SafeFileHandle OpenNative(SafeFileHandle? parent, string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var volumeRoot = System.IO.Path.GetPathRoot(path);
            if (volumeRoot is null || GetDriveType(volumeRoot) != 3)
            {
                throw Refused();
            }

            var flags = 0x02200000U | (directory ? 0U : 0x40000000U);
            var handle = CreateFile(path, directory ? 0x80U : 0x80000000U, 1, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw Refused();
            }

            return handle;
        }

        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw Refused();
        }

        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var directoryFlag = arm ? 0x4000 : 0x10000;
        var options = 0x80000 | 0x800 | (arm ? 0x8000 : 0x20000) | (directory ? directoryFlag : 0);
        var fd = OpenAt(parent?.DangerousGetHandle().ToInt32() ?? -100, path, options);
        if (fd < 0)
        {
            throw Refused();
        }

        return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
    }

    private static (string Identity, string Volume, long Length) ReadPin(SafeFileHandle handle, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformation(handle, out var value) || !GetFileId(handle, 18, out var fileId, 24) || GetFileType(handle) != 1
                || (value.Attributes & 0x441400) != 0 || ((value.Attributes & 0x10) != 0) != directory
                || (!directory && value.LinkCount != 1) || !SupportedWindowsVolume(handle))
            {
                throw Refused();
            }

            var volume = fileId.Volume.ToString("x", CultureInfo.InvariantCulture);
            var identity = FormattableString.Invariant($"w:{volume}:{fileId.High:x}:{fileId.Low:x}:{value.Attributes:x}");
            if (!directory)
            {
                identity += FormattableString.Invariant($":{value.LinkCount}:{value.SizeHigh}:{value.SizeLow}:{value.WriteHigh}:{value.WriteLow}:{value.CreatedHigh}:{value.CreatedLow}");
            }

            return (identity, volume, directory ? 0 : checked(((long)value.SizeHigh << 32) | value.SizeLow));
        }

        var fd = handle.DangerousGetHandle().ToInt32();
        const uint required = 0x13c7; // native statx type/mode/nlink/mtime/ctime/inode/size and mount ID.
        if (Statx(fd, string.Empty, 0x1000, 0x17ff, out var stat) != 0 || (stat.Mask & required) != required
            || (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (!directory && stat.LinkCount != 1)
            || StatFs(fd, out var fs) != 0)
        {
            throw Refused();
        }

        // 2.10.3: only local filesystems whose rename and link semantics the proofs rely on (ext4, xfs, btrfs, tmpfs, overlayfs, zfs). Anything else,
        // a network share (SMB/CIFS, NFS) above all, is refused exactly as before, but with its own reason so the admin is not told recovery files need attention.
        if (fs.Type is not (0xef53 or 0x58465342 or 0x9123683eL or 0x01021994 or 0x794c7630 or 0x2fc12fc1))
        {
            throw new System.IO.IOException(UnsupportedFilesystem);
        }

        var nativeVolume = FormattableString.Invariant($"{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.MountId}");
        var nativeIdentity = FormattableString.Invariant($"l:{nativeVolume}:{stat.Inode}:{stat.Mode:x}");
        if (!directory)
        {
            nativeIdentity += FormattableString.Invariant($":{stat.LinkCount}:{stat.Size}:{stat.ModifiedSeconds}:{stat.ModifiedNanos}:{stat.ChangedSeconds}:{stat.ChangedNanos}");
        }

        return (nativeIdentity, nativeVolume, directory ? 0 : checked((long)stat.Size));
    }

    private static unsafe bool SupportedWindowsVolume(SafeFileHandle handle)
    {
        var name = stackalloc char[32];
        if (!GetVolumeInformation(handle, IntPtr.Zero, 0, out _, out _, out _, name, 32))
        {
            return false;
        }

        var value = new string(name);
        return value is "NTFS" or "ReFS";
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformation(SafeFileHandle handle, out WindowsInfo value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileId(SafeFileHandle handle, int informationClass, out WindowsFileId value, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static partial uint GetFileType(SafeFileHandle handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string root);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(SafeFileHandle handle, IntPtr name, uint nameLength, out uint serial, out uint componentLength, out uint flags, char* filesystem, uint filesystemLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int parent, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int fd, string path, int flags, uint mask, out LinuxStat stat);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int StatFs(int fd, out LinuxFs filesystem);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsInfo
    {
        public uint Attributes;
        public uint CreatedLow;
        public uint CreatedHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint LinkCount;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileId
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(0)]
        public uint Mask;
        [FieldOffset(16)]
        public uint LinkCount;
        [FieldOffset(28)]
        public ushort Mode;
        [FieldOffset(32)]
        public ulong Inode;
        [FieldOffset(40)]
        public ulong Size;
        [FieldOffset(96)]
        public long ChangedSeconds;
        [FieldOffset(104)]
        public uint ChangedNanos;
        [FieldOffset(112)]
        public long ModifiedSeconds;
        [FieldOffset(120)]
        public uint ModifiedNanos;
        [FieldOffset(136)]
        public uint DeviceMajor;
        [FieldOffset(140)]
        public uint DeviceMinor;
        [FieldOffset(144)]
        public ulong MountId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFs
    {
        [FieldOffset(0)]
        public long Type;
    }
}
