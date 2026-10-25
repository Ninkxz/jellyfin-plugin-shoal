using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Progress;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>Captures local progress before a confined stub mutation; a failed capture holds the mutation.</summary>
internal static class RetentionGuard
{
    /// <summary>Captures a recorded stub before deletion.</summary>
    /// <param name="retention">Optional capture service.</param>
    /// <param name="stubRoot">Confinement root.</param>
    /// <param name="libraryFolder">Library folder.</param>
    /// <param name="relativePath">Recorded stub path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether deletion may proceed.</returns>
    internal static async Task<bool> BeforeFileAsync(IKeepMyPlaceRetention? retention, string stubRoot, string libraryFolder, string? relativePath, CancellationToken cancellationToken)
    {
        if (retention is null || relativePath is null)
        {
            return true;
        }

        if (!CatalogStore.IsSafeStubPath(relativePath) && !CatalogStore.IsSafeEpisodePath(relativePath))
        {
            return false;
        }

        var path = Path.Combine(libraryFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return !PathSanitizer.HasLinkBelow(stubRoot, path)
            && await retention.RetainBeforeRemovalAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures every stub in a bounded, link-free folder.</summary>
    /// <param name="retention">Optional capture service.</param>
    /// <param name="stubRoot">Confinement root.</param>
    /// <param name="folder">Folder to capture.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether mutation may proceed.</returns>
    internal static async Task<bool> BeforeFolderAsync(IKeepMyPlaceRetention? retention, string stubRoot, string folder, CancellationToken cancellationToken)
    {
        if (retention is null)
        {
            return true;
        }

        try
        {
            if (PathSanitizer.HasLinkBelow(stubRoot, folder))
            {
                return false;
            }

            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((folder, 0));
            var count = 0;
            while (pending.TryPop(out var next))
            {
                foreach (var item in new DirectoryInfo(next.Path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++count > 100_000 || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }

                    if (item is DirectoryInfo)
                    {
                        if (next.Depth >= 4)
                        {
                            return false;
                        }

                        pending.Push((item.FullName, next.Depth + 1));
                    }
                    else if (item.Extension.Equals(".strm", StringComparison.OrdinalIgnoreCase)
                        && !await retention.RetainBeforeRemovalAsync(item.FullName, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
