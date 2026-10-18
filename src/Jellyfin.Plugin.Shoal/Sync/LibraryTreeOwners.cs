using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The owner lookup of the plugin (K44/K45): a title folder in a shared library folder belongs to the connection whose catalog records it, or, for an extra copy of
/// a grouped movie, to the group's owner, while a member of the group still feeds that library. Anything else has no owner (refused).
/// </summary>
public sealed class LibraryTreeOwners : ILibraryTreeOwners
{
    private readonly StubCatalog _catalog;
    private readonly GroupBook? _groups;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryTreeOwners"/> class.
    /// </summary>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="groups">The title groups, or null.</param>
    public LibraryTreeOwners(StubCatalog catalog, GroupBook? groups = null)
    {
        _catalog = catalog;
        _groups = groups;
    }

    /// <inheritdoc />
    public TreeOwner? OwnerOf(ShoalState state, ShoalLibrary library, string topFolder)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(library);
        return _catalog.OwnerOf(state, library, topFolder) ?? CopyOwner(state, library, topFolder);
    }

    private TreeOwner? CopyOwner(ShoalState state, ShoalLibrary library, string topFolder)
    {
        if (library.IsLegacy || _groups?.ExtraCopyAt(library.LibKey, topFolder) is not { } found || !found.Copy.Written)
        {
            return null;
        }

        var (group, _) = found;
        var feeds = group.Members.Any(m => string.Equals(m.LibKey, library.LibKey, StringComparison.Ordinal) && LibraryFolders.FedBy(state, m.PeerKey, "movies")?.LibKey == library.LibKey);
        var primary = group.Copies.FirstOrDefault(static c => !c.Extra);
        if (!feeds || primary is null)
        {
            return null; // no member feeds the library any more: the copy is about to be retired
        }

        var entry = _catalog.Find(group.MetaOwner, primary.FolderRel);
        if (entry is null || entry.Kind != EntryKind.Movie || !string.Equals(entry.StubKey, primary.StubKey, StringComparison.Ordinal) || entry.State is not (EntryState.Active or EntryState.HeldRemoval))
        {
            return null;
        }

        return new TreeOwner(group.MetaOwner, entry.MappingKey, primary.FolderRel, IsCopy: true);
    }
}
