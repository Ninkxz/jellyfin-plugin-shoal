using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// Tells whether the volume that holds <c>&lt;DataPath&gt;/shoal/</c> can protect secret files (DESIGN 3.1, 8.2, Warden A-16). On
/// network or FUSE volumes Unix modes are usually ignored, so <c>secrets.enc</c> and <c>master.key</c> are then effectively plaintext.
/// </summary>
internal static class VolumeCheck
{
    /// <summary>The text the Settings screen shows when the volume cannot protect secret files.</summary>
    public const string Warning = "This volume cannot protect secret files: treat secrets.enc and master.key as plaintext (supply the key with JELLYFIN_SHOAL_KEY_FILE and keep the data folder on a local disk).";

    /// <summary>
    /// Looks at the file system type of the folder (Linux: <c>/proc/self/mountinfo</c>; Windows: UNC paths).
    /// </summary>
    /// <param name="folder">The Shoal data folder.</param>
    /// <param name="mountInfo">Test seam: the content of <c>/proc/self/mountinfo</c>; null reads the real file on Linux.</param>
    /// <returns>The warning, or null when nothing suspicious was found.</returns>
    public static string? CheckFileSystem(string folder, string? mountInfo = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return folder.StartsWith(@"\\", StringComparison.Ordinal) && !folder.StartsWith(@"\\?\", StringComparison.Ordinal) ? Warning : null;
        }

        if (mountInfo is null)
        {
            try
            {
                mountInfo = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/mountinfo", Encoding.UTF8) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return mountInfo is null ? null : IsNetworkOrFuse(FileSystemTypeOf(Path.GetFullPath(folder), mountInfo)) ? Warning : null;
    }

    /// <summary>
    /// After a secret file was created with mode 0600: whether the volume kept that mode.
    /// </summary>
    /// <param name="file">A file Shoal created with <see cref="Storage.AtomicFile.PrivateFileMode"/>.</param>
    /// <returns>The warning when the mode is wider than owner read/write, otherwise null.</returns>
    public static string? CheckFileMode(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.GetUnixFileMode(file) == Storage.AtomicFile.PrivateFileMode ? null : Warning;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the file system type of the mount that holds <paramref name="fullPath"/> (the longest mount point that is a prefix).
    /// </summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <param name="mountInfo">The content of <c>/proc/self/mountinfo</c>.</param>
    /// <returns>The type (for example <c>ext4</c>, <c>cifs</c>, <c>fuse.sshfs</c>), or null when no mount matches.</returns>
    internal static string? FileSystemTypeOf(string fullPath, string mountInfo)
    {
        string? bestType = null;
        var bestLength = -1;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var left = line[..separator].Split(' ');
            var right = line[(separator + 3)..].Split(' ');
            if (left.Length < 5 || right.Length < 1)
            {
                continue;
            }

            var mountPoint = Unescape(left[4]);
            if (IsPrefix(mountPoint, fullPath) && mountPoint.Length > bestLength)
            {
                bestLength = mountPoint.Length;
                bestType = right[0];
            }
        }

        return bestType;
    }

    private static bool IsNetworkOrFuse(string? type) =>
        type is not null
        && (type is "cifs" or "smb3" or "smbfs" or "9p"
            || type.StartsWith("nfs", StringComparison.Ordinal)
            || type.StartsWith("fuse.", StringComparison.Ordinal));

    private static bool IsPrefix(string mountPoint, string path)
    {
        if (mountPoint == "/")
        {
            return true;
        }

        return path.StartsWith(mountPoint, StringComparison.Ordinal) && (path.Length == mountPoint.Length || path[mountPoint.Length] == '/');
    }

    // mountinfo writes space, tab, newline and backslash as \040, \011, \012 and \134.
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length && IsOctal(value.AsSpan(i + 1, 3)))
            {
                result.Append((char)(((value[i + 1] - '0') * 64) + ((value[i + 2] - '0') * 8) + (value[i + 3] - '0')));
                i += 3;
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    private static bool IsOctal(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits)
        {
            if (c is < '0' or > '7')
            {
                return false;
            }
        }

        return true;
    }
}
