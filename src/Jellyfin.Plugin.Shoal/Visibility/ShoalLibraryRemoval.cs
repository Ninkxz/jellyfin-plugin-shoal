using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Takes a library of Shoal's out of Jellyfin (Warden 13 M1, one rule for delete, Remove everything, uninstall and the legacy removal): a library that holds only
/// Shoal's folder goes whole; a library the admin added folders to keeps them and its items, and only the items below Shoal's folder and that folder go.
/// </summary>
internal static class ShoalLibraryRemoval
{
    /// <summary>
    /// Removes Shoal's part of a library.
    /// </summary>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="library">The library.</param>
    /// <param name="shoalPath">Shoal's folder in it, or null when unknown.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the whole library went (the caller forgets its id); false when only Shoal's folder went, or nothing (unknown folder beside others).</returns>
    public static async Task<bool> RemoveAsync(ILibrarySource libraries, LibraryInfo library, string? shoalPath, CancellationToken cancellationToken)
    {
        var others = shoalPath is null ? library.Locations.Count > 1 : library.Locations.Any(location => !LibraryFolders.SamePath(location, shoalPath));
        if (others)
        {
            if (shoalPath is not null && library.Locations.Any(location => LibraryFolders.SamePath(location, shoalPath)))
            {
                await libraries.PurgeItemsBelowAsync(library.Id, shoalPath, cancellationToken).ConfigureAwait(false);
                await libraries.RemovePathAsync(library.Name, shoalPath, cancellationToken).ConfigureAwait(false);
            }

            return false; // the admin's folders and their items stay
        }

        // The items first: a removed library otherwise keeps its titles listed until a scan.
        await libraries.PurgeItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        await libraries.RemoveAsync(library.Name, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
