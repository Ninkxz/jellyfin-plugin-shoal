using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Internal pinned file evidence; paths and native identities must never enter public replies.</summary>
/// <param name="FullPath">The validated absolute media path.</param>
/// <param name="Root">The authoritative allowed library root.</param>
/// <param name="Length">The length actually hashed.</param>
/// <param name="Sha256">The digest of the complete file.</param>
/// <param name="Identity">Native file metadata and pinned ancestor generation.</param>
internal sealed record RemovalFileSnapshot(string FullPath, string Root, long Length, string Sha256, string Identity);

/// <summary>Read-only native probing shared by local and peer removal verification.</summary>
internal sealed partial class RemovalFiles
{
    private readonly Func<long, TimeSpan> _hashingDeadline = RemovalPolicy.HashingDeadlineFor;

    /// <summary>Fresh-opens and completely hashes one supported, unlinked ordinary file.</summary>
    /// <param name="root">An authoritative configured library root, never a browser-supplied path.</param>
    /// <param name="path">The indexed physical file under that root.</param>
    /// <param name="cancellationToken">Cancellation; hashing also has a size-scaled deadline.</param>
    /// <returns>Current full-file and ancestor evidence.</returns>
    public async Task<RemovalFileSnapshot> ProbeAsync(string root, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)
            || (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal))))
        {
            throw Refused();
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (canonicalRoot[(Path.GetPathRoot(canonicalRoot)?.Length ?? 0)..].Contains(':', StringComparison.Ordinal)
            || canonicalPath[(Path.GetPathRoot(canonicalPath)?.Length ?? 0)..].Contains(':', StringComparison.Ordinal)))
        {
            throw Refused();
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(canonicalRoot) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(prefix, comparison) || string.Equals(canonicalRoot, canonicalPath, comparison))
        {
            throw Refused();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var chain = OpenChain(canonicalRoot, canonicalPath);
        var file = chain.Pins[^1];
        if (file.Length is <= 0 || file.Length > RemovalPolicy.MaxFileSizeBytes)
        {
            throw Refused();
        }

        deadline.CancelAfter(_hashingDeadline(file.Length));
        using var stream = new FileStream(file.Handle, FileAccess.Read, 131072, isAsync: OperatingSystem.IsWindows());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        long read = 0;
        while (read < file.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Length - read)), deadline.Token).ConfigureAwait(false);
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

        deadline.Token.ThrowIfCancellationRequested();
        foreach (var pin in chain.Pins)
        {
            if (ReadPin(pin.Handle, pin.Directory).Identity != pin.Identity)
            {
                throw Refused();
            }
        }

        using var reopened = OpenChain(canonicalRoot, canonicalPath);
        if (!chain.Pins.Select(static p => p.Identity).SequenceEqual(reopened.Pins.Select(static p => p.Identity), StringComparer.Ordinal))
        {
            throw Refused();
        }

        // Snapshots persist in the journal, so they omit device and mount IDs (unstable across restarts);
        // the live pins above compared the full identities, which still catches a swap within this probe.
        var ancestry = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', chain.Pins.Select(static p => RemovalJournal.PersistentIdentity(p.Identity))))));
        return new(canonicalPath, canonicalRoot, read, Convert.ToHexStringLower(hash.GetHashAndReset()), RemovalJournal.PersistentIdentity(file.Identity) + "|" + ancestry);
    }

    private static IOException Refused() => new("mediaFileUnavailable");

    private static ProbeChain OpenChain(string root, string path)
    {
        var chain = new ProbeChain();
        try
        {
            var filesystemRoot = Path.GetPathRoot(path) ?? throw Refused();
            var components = path[filesystemRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = filesystemRoot;
            var handle = OpenNative(null, current, directory: true);
            chain.Add(handle, current, directory: true);
            foreach (var component in components)
            {
                var directory = chain.Pins.Count <= components.Length - 1;
                current = Path.Combine(current, component);
                handle = OpenNative(chain.Pins[^1].Handle, OperatingSystem.IsLinux() ? component : current, directory);
                chain.Add(handle, current, directory);
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var rootIndex = chain.Pins.FindIndex(p => comparer.Equals(p.Path, root));
            if (rootIndex < 0 || chain.Pins.Skip(rootIndex).Any(p => p.Volume != chain.Pins[rootIndex].Volume))
            {
                throw Refused();
            }

            return chain;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            chain.Dispose();
            throw Refused();
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    private sealed class ProbeChain : IDisposable
    {
        public List<ProbePin> Pins { get; } = [];

        public void Add(SafeFileHandle handle, string path, bool directory)
        {
            try
            {
                var value = ReadPin(handle, directory);
                Pins.Add(new(handle, path, directory, value.Identity, value.Volume, value.Length));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var pin in Pins)
            {
                pin.Handle.Dispose();
            }
        }
    }

    private sealed record ProbePin(SafeFileHandle Handle, string Path, bool Directory, string Identity, string Volume, long Length);
}
