using System;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Shoal.Storage;

/// <summary>
/// Flushes a directory entry to disk on Linux, so a rename survives a power cut (DESIGN 3.7 step 3). Best effort: when the call
/// is not available it does nothing, and the write is still atomic.
/// </summary>
internal static partial class NativeFsync
{
    private const int ReadOnly = 0;

    /// <summary>
    /// Flushes <paramref name="directory"/> on Linux; does nothing elsewhere.
    /// </summary>
    /// <param name="directory">The folder whose entries changed.</param>
    /// <returns>True when the folder was flushed.</returns>
    public static bool SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            var fd = Open(directory, ReadOnly);
            if (fd < 0)
            {
                return false;
            }

            try
            {
                return Fsync(fd) == 0;
            }
            finally
            {
                _ = Close(fd);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
