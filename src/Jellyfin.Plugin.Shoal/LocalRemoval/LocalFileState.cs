using System;
using System.IO;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>W23-5: what a stat says about a path. Only the filesystem's own "not found" is <see cref="Absent"/>; any other error is <see cref="Unknown"/>.</summary>
internal enum LocalFileState
{
    /// <summary>The stat answered: the path is there.</summary>
    Present,

    /// <summary>The stat answered "not found" (FileNotFoundException or DirectoryNotFoundException).</summary>
    Absent,

    /// <summary>The stat failed for another reason (access denied, share offline, transient IO): nothing is known.</summary>
    Unknown,
}

/// <summary>W23-5: reads a <see cref="LocalFileState"/> without the File.Exists trap (File.Exists is false on every error).</summary>
internal static class LocalFileStates
{
    /// <summary>Reads the state of one path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="stat">The stat call (<see cref="File.GetAttributes(string)"/> in real use).</param>
    /// <returns>The state.</returns>
    internal static LocalFileState Read(string path, Func<string, FileAttributes> stat)
    {
        try
        {
            _ = stat(path);
            return LocalFileState.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LocalFileState.Absent;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return LocalFileState.Unknown;
        }
    }

    /// <summary>Reads the state of a path below a library root: "not found" counts only while the root itself answers as a directory (a vanished drive or share makes everything below it "not found").</summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The library root it sits under.</param>
    /// <param name="stat">The stat call.</param>
    /// <returns>The state.</returns>
    internal static LocalFileState ReadUnder(string path, string root, Func<string, FileAttributes> stat)
    {
        var state = Read(path, stat);
        if (state != LocalFileState.Absent)
        {
            return state;
        }

        try
        {
            return (stat(root) & FileAttributes.Directory) != 0 ? LocalFileState.Absent : LocalFileState.Unknown;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return LocalFileState.Unknown;
        }
    }
}
