using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Warden 13 L3 / Warden 14: titles in a placement-Main library show only to accounts that can open the admin's main library in core (EnableAllFolders, or the
/// library on EnabledFolders). Preview, the move job and the add-friend wizard use this one rule, so their access reports agree.
/// </summary>
internal static class MainAccess
{
    /// <summary>
    /// The Jellyfin library that holds a placement-Main library's folder, found by folder only (the stored id can be null or stale, Warden 13 L1).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="library">The layout library.</param>
    /// <param name="all">Jellyfin's libraries.</param>
    /// <returns>The main library's id; null for placement Shoal; <see cref="Guid.Empty"/> when no library holds the folder (its titles show nowhere).</returns>
    public static Guid? MainIdOf(ShoalState state, State.ShoalLibrary library, IReadOnlyList<LibraryInfo> all)
    {
        if (library.Placement != LibraryPlacement.Main)
        {
            return null;
        }

        var path = LibraryFolders.PathOf(state.StubRoot, library);
        return (path is null ? null : all.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path))))?.Id ?? Guid.Empty;
    }

    /// <summary>
    /// Whether the account can open the main library a library's titles are in (always true when they are not in one). An account Shoal manages (it had "all
    /// libraries" and its list is still the one Shoal wrote) counts as able: Shoal's list holds every library that is not Shoal's own, the main library too,
    /// and gives "all libraries" back when no Shoal library is left.
    /// </summary>
    /// <param name="state">The state (the accounts Shoal manages).</param>
    /// <param name="account">The account.</param>
    /// <param name="mainId">The main library (see <see cref="MainIdOf"/>), or null.</param>
    /// <returns>True when nothing in core hides the titles from it.</returns>
    public static bool Opens(ShoalState state, AccountInfo account, Guid? mainId)
    {
        if (mainId is not { } id)
        {
            return true;
        }

        if (id == Guid.Empty)
        {
            return false;
        }

        return account.CanOpen(id)
            || (state.Home.Visibility.ManagedUsers.TryGetValue(account.Id, out var managed)
                && string.Equals(VisibilityService.HashFolders(account.EnabledFolders), managed.LastWrittenFoldersHash, StringComparison.Ordinal));
    }
}
