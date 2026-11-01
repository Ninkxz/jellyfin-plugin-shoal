using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>
/// 2.10.4 K1: a full-file SHA-256 that works on any filesystem Jellyfin can read (CIFS/SMB, NFS, vfat, exfat, NTFS), with plain .NET file
/// calls and no native pinning. It replaces <see cref="RemovalFiles.ProbeAsync"/> for "delete like Jellyfin" and the friend-side proof: the
/// native probe refuses every filesystem outside its allow-list, which is every network share. What it gives up against the native probe is
/// handle-level identity (inode/ancestor pins); it compensates with a before/after length and write-time compare, a reparse-point check on every
/// component below the root (before and after) and the end-of-file check. Paths never leave the snapshot.
/// </summary>
internal sealed class PortableFileProbe
{
    private const int BufferBytes = 1024 * 1024; // large reads: a network share answers each request with a round trip
    private readonly Func<long, TimeSpan> _deadline;
    private readonly Func<CancellationToken, Task>? _afterRead;

    /// <summary>Initializes a new instance of the <see cref="PortableFileProbe"/> class with the size-scaled hashing deadline.</summary>
    public PortableFileProbe()
        : this(RemovalPolicy.DeleteHashingDeadlineFor, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PortableFileProbe"/> class for controlled tests.</summary>
    /// <param name="deadline">The hashing deadline for a file length.</param>
    /// <param name="afterRead">Runs after the last byte is hashed and before the second look (tests change the file here).</param>
    internal PortableFileProbe(Func<long, TimeSpan> deadline, Func<CancellationToken, Task>? afterRead)
    {
        _deadline = deadline;
        _afterRead = afterRead;
    }

    /// <summary>Opens and completely hashes one ordinary file strictly inside a library root.</summary>
    /// <param name="root">An authoritative configured library root, never a browser-supplied path.</param>
    /// <param name="path">The indexed physical file under that root.</param>
    /// <param name="cancellationToken">Cancellation; hashing also has the size-scaled deadline.</param>
    /// <returns>The snapshot; <see cref="RemovalFileSnapshot.Identity"/> is <c>p:{length}:{lastWriteUtcTicks}</c>.</returns>
    /// <exception cref="IOException"><c>mediaFileUnavailable</c> for any refusal (same word as the native probe).</exception>
    public async Task<RemovalFileSnapshot> ProbeAsync(string root, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (canonicalRoot, canonicalPath) = Contain(root, path);
        var before = Look(canonicalRoot, canonicalPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline(before.Length));

        string digest;
        long read = 0;
        var options = FileOptions.SequentialScan | (OperatingSystem.IsWindows() ? FileOptions.Asynchronous : FileOptions.None);
        using (var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, options))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (stream.Length != before.Length)
            {
                throw Refused();
            }

            var buffer = new byte[BufferBytes];
            while (read < before.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, before.Length - read)), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw Refused();
                }

                hash.AppendData(buffer, 0, count);
                read += count;
            }

            if (await stream.ReadAsync(buffer.AsMemory(0, 1), deadline.Token).ConfigureAwait(false) != 0)
            {
                throw Refused();
            }

            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (_afterRead is not null)
        {
            await _afterRead(deadline.Token).ConfigureAwait(false);
        }

        deadline.Token.ThrowIfCancellationRequested();
        var after = Look(canonicalRoot, canonicalPath);
        if (after != before || read != before.Length)
        {
            throw Refused();
        }

        return new(canonicalPath, canonicalRoot, read, digest, Identity(before.Length, before.LastWriteTicks));
    }

    /// <summary>The identity string of a file look, also used by the delete-time re-check.</summary>
    /// <param name="length">The file length.</param>
    /// <param name="lastWriteUtcTicks">The last write time in UTC ticks.</param>
    /// <returns><c>p:{length}:{ticks}</c>.</returns>
    internal static string Identity(long length, long lastWriteUtcTicks) => "p:" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + lastWriteUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Canonicalizes both paths and requires the file strictly inside the root (same rules as the native probe, plus UNC shares).</summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The file.</param>
    /// <returns>The canonical root without a trailing separator and the canonical file path.</returns>
    internal static (string Root, string Path) Contain(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(root) || !System.IO.Path.IsPathFullyQualified(path)
            || root.Contains('\0', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw Refused();
        }

        // A plain UNC share (\\server\share\...) is allowed: that is how Jellyfin on Windows reaches a NAS. Device paths (\\?\, \\.\) are not.
        if (OperatingSystem.IsWindows() && (IsDevicePath(root) || IsDevicePath(path)))
        {
            throw Refused();
        }

        var canonicalRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var canonicalPath = System.IO.Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (canonicalRoot[(System.IO.Path.GetPathRoot(canonicalRoot)?.Length ?? 0)..].Contains(':', StringComparison.Ordinal)
            || canonicalPath[(System.IO.Path.GetPathRoot(canonicalPath)?.Length ?? 0)..].Contains(':', StringComparison.Ordinal)))
        {
            throw Refused(); // alternate data streams
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = System.IO.Path.EndsInDirectorySeparator(canonicalRoot) ? canonicalRoot : canonicalRoot + System.IO.Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(prefix, comparison) || string.Equals(canonicalRoot, canonicalPath, comparison) || System.IO.Path.EndsInDirectorySeparator(canonicalPath))
        {
            throw Refused();
        }

        return (canonicalRoot, canonicalPath);
    }

    private static bool IsDevicePath(string value) => value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal)
        || value.StartsWith("//?/", StringComparison.Ordinal) || value.StartsWith("//./", StringComparison.Ordinal);

    // One look at the file and every directory strictly below the root: all must exist, none may be a link or reparse point.
    private static FileLook Look(string root, string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || IsLink(file) || (file.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw Refused();
        }

        var length = file.Length;
        if (!RemovalPolicy.DeleteSizeAllowed(length))
        {
            throw Refused();
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        for (var directory = file.Directory; directory is not null && !comparer.Equals(System.IO.Path.TrimEndingDirectorySeparator(directory.FullName), root); directory = directory.Parent)
        {
            if (!directory.Exists || IsLink(directory))
            {
                throw Refused();
            }
        }

        return new(length, file.LastWriteTimeUtc.Ticks);
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;

    private static IOException Refused() => new("mediaFileUnavailable");

    private readonly record struct FileLook(long Length, long LastWriteTicks);
}
