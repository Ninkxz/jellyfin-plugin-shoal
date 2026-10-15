using System;
using System.IO;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Shoal.Storage;

/// <summary>
/// Points in <see cref="AtomicFile.Write"/> where a test can simulate a crash.
/// </summary>
internal enum AtomicStage
{
    /// <summary>The temp file is complete and flushed; the target has not been touched.</summary>
    TempWritten,

    /// <summary>Just before the replace.</summary>
    BeforeReplace
}

/// <summary>
/// How a write ended.
/// </summary>
internal enum AtomicWriteOutcome
{
    /// <summary>Written and replaced atomically, with the backup (if one was asked for).</summary>
    Normal,

    /// <summary>
    /// The file system refused the backup link that <see cref="File.Replace(string,string,string?,bool)"/> needs (some network
    /// mounts have no hard links). The backup was made by copying and the file was moved over atomically.
    /// </summary>
    BackupByCopy,

    /// <summary>The backup could not be made at all; the new content was still written atomically.</summary>
    BackupSkipped
}

/// <summary>
/// Thrown by a test hook to simulate a process crash: <see cref="AtomicFile.Write"/> then leaves its temp file behind,
/// exactly as a real crash would, instead of cleaning it up.
/// </summary>
public sealed class SimulatedCrashException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedCrashException"/> class.
    /// </summary>
    public SimulatedCrashException()
        : base("Simulated crash.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedCrashException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedCrashException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The one write protocol for <c>state.json</c>, <c>secrets.enc</c> and the catalogs (DESIGN 3.7): write a temp file in the same
/// folder (0600), flush it to disk, atomically replace the target, keep the previous content as an optional backup, and on
/// Linux flush the folder too. A crash at any point leaves either the old or the new content, never a mix.
/// </summary>
internal static class AtomicFile
{
    /// <summary>The Unix mode of every file Shoal creates: owner read and write only.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically.
    /// </summary>
    /// <param name="path">The target file.</param>
    /// <param name="content">The new content.</param>
    /// <param name="backupPath">Where the previous content of the target goes, or null for no backup.</param>
    /// <param name="hook">Test seam: called at <see cref="AtomicStage"/> points; throw <see cref="SimulatedCrashException"/> to simulate a crash.</param>
    /// <param name="replace">Test seam: replaces <see cref="File.Replace(string,string,string?,bool)"/> (to simulate a file system without hard links).</param>
    /// <returns>How the write ended; anything but <see cref="AtomicWriteOutcome.Normal"/> deserves one log line.</returns>
    public static AtomicWriteOutcome Write(string path, ReadOnlySpan<byte> content, string? backupPath = null, Action<AtomicStage>? hook = null, Action<string, string, string?>? replace = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Path has no directory.", nameof(path));
        var temp = path + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            hook?.Invoke(AtomicStage.TempWritten);
            hook?.Invoke(AtomicStage.BeforeReplace);

            var outcome = AtomicWriteOutcome.Normal;
            if (File.Exists(path))
            {
                outcome = ReplaceExisting(temp, path, backupPath, replace);
            }
            else
            {
                File.Move(temp, path);
            }

            NativeFsync.SyncDirectory(directory);
            return outcome;
        }
        catch (SimulatedCrashException)
        {
            throw; // a real crash would not clean up either
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Deletes leftover <c>*.tmp-*</c> files of this target, which only a crash can leave behind.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="namePrefix">The target's file name; temp files are <c>name.tmp-*</c>.</param>
    /// <returns>How many files were removed.</returns>
    public static int DeleteStaleTempFiles(string directory, string namePrefix)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, namePrefix + ".tmp-*"))
            {
                if (TryDelete(file))
                {
                    removed++;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }

    // File.Replace with a backup makes the backup as a hard link on Unix, which some network mounts refuse (Warden N-9). Nothing has
    // been changed when that fails, so the fallback is safe: copy the old file to the backup, then move the temp file over it.
    private static AtomicWriteOutcome ReplaceExisting(string temp, string path, string? backupPath, Action<string, string, string?>? replace)
    {
        try
        {
            if (replace is not null)
            {
                replace(temp, path, backupPath);
            }
            else
            {
                File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
            }

            return AtomicWriteOutcome.Normal;
        }
        catch (Exception ex) when (backupPath is not null && ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!File.Exists(temp))
            {
                throw; // the replace got further than the backup; nothing to fall back on
            }

            var outcome = AtomicWriteOutcome.BackupByCopy;
            try
            {
                File.Copy(path, backupPath, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                outcome = AtomicWriteOutcome.BackupSkipped;
            }

            File.Move(temp, path, overwrite: true);
            return outcome;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
