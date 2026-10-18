using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Where a connection's titles live and which library rules apply to them (PLAN v2.9 K44/K45). Pure: reads the state, never the disk.
/// <para>
/// A mapping of a connection lands in the library of v2.9 or later that the connection feeds for the mapping's type (folder <c>&lt;StubRoot&gt;/libs/&lt;libKey&gt;</c>,
/// shared with the other connections that feed it); otherwise in its legacy folder <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>, exactly as before v2.9,
/// so an upgraded install changes no file.
/// </para>
/// </summary>
public static class LibraryFolders
{
    /// <summary>The prefix of the NFO tag that names a library (<c>shoal-lib-&lt;libKey8&gt;</c>); blocked for users the library does not admit when it is added into a main library.</summary>
    public const string LibTagPrefix = "shoal-lib-";

    /// <summary>The prefix of the NFO tag a lower-ordered copy of a title carries for each higher-ordered library that also holds it (<c>shoal-alt-&lt;libKey8&gt;</c>).</summary>
    public const string AltTagPrefix = "shoal-alt-";

    /// <summary>The tag every Shoal stub carries; blocked for a user who hides friends' titles and for every sharing account.</summary>
    public const string HideAllTag = "Shoal";

    /// <summary>The prefix of the item tags that park a server's stubs (PLAN v2.9 K47): <c>shoal-park-p-&lt;peer8&gt;</c> on a server's own stubs, <c>shoal-park-g-&lt;group8&gt;</c> on a title group's copy.</summary>
    public const string ParkTagPrefix = "shoal-park-";

    /// <summary>The most Shoal libraries of v2.9 or later (legacy per-friend libraries do not count).</summary>
    public const int MaxLibraries = 50;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The library tag of a library.
    /// </summary>
    /// <param name="libKey">The library key (16 lower-case hex characters).</param>
    /// <returns><c>shoal-lib-</c> and the first 8 characters.</returns>
    public static string LibTag(string libKey) => LibTagPrefix + Short(libKey);

    /// <summary>
    /// The alternate-copy tag of a library.
    /// </summary>
    /// <param name="libKey">The library key.</param>
    /// <returns><c>shoal-alt-</c> and the first 8 characters.</returns>
    public static string AltTag(string libKey) => AltTagPrefix + Short(libKey);

    /// <summary>
    /// Whether a tag is one Shoal manages in <c>BlockedTags</c> (a library tag, an alternate-copy tag, or the hide-all tag).
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>True for a Shoal tag.</returns>
    public static bool IsShoalTag(string tag) =>
        string.Equals(tag, HideAllTag, StringComparison.Ordinal) || tag.StartsWith(LibTagPrefix, StringComparison.Ordinal) || tag.StartsWith(AltTagPrefix, StringComparison.Ordinal) || tag.StartsWith(ParkTagPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The park tag of a server's own stubs (K47): blocked for every account while the server is parked.
    /// </summary>
    /// <param name="peerKey">The server.</param>
    /// <returns>The tag.</returns>
    public static string ParkPeerTag(string peerKey) => ParkTagPrefix + "p-" + Short(peerKey);

    /// <summary>
    /// The park tag of a title group's copy (K47): blocked only while every server of the group is parked.
    /// </summary>
    /// <param name="groupKey">The group.</param>
    /// <returns>The tag.</returns>
    public static string ParkGroupTag(string groupKey) => ParkTagPrefix + "g-" + Short(groupKey);

    /// <summary>
    /// The library of v2.9 or later that a connection feeds for a type, or null (the validator allows at most one).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="type"><c>movies</c> or <c>tvshows</c>.</param>
    /// <returns>The library, or null.</returns>
    public static ShoalLibrary? FedBy(ShoalState state, string peerKey, string type)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.Type, type, StringComparison.Ordinal) && l.Feeds.Contains(peerKey, StringComparer.Ordinal));
    }

    /// <summary>
    /// The legacy library of a mapping (its folder is <c>&lt;peerKey&gt;/&lt;Subfolder&gt;</c>), or null.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The library, or null.</returns>
    public static ShoalLibrary? LegacyOf(ShoalState state, string peerKey, LibraryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mapping);
        var pathRel = peerKey + "/" + mapping.Subfolder;
        return state.Home.Libraries.FirstOrDefault(l => l.IsLegacy && string.Equals(l.PathRel, pathRel, StringComparison.Ordinal));
    }

    /// <summary>
    /// The library a mapping's titles are in: the one the connection feeds for the type, else the mapping's legacy library, else null.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The library, or null.</returns>
    public static ShoalLibrary? LibraryOf(ShoalState state, string peerKey, LibraryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return FedBy(state, peerKey, mapping.CollectionType) ?? LegacyOf(state, peerKey, mapping);
    }

    /// <summary>
    /// The folder a mapping's stubs are written to: <c>&lt;StubRoot&gt;/libs/&lt;libKey&gt;</c> when the connection feeds a library of the type, else the legacy
    /// <c>&lt;StubRoot&gt;/&lt;peerKey&gt;/&lt;Subfolder&gt;</c>. Null when the parts do not make a path below the stub root.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The full path, or null.</returns>
    public static string? FolderOf(ShoalState state, string peerKey, LibraryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mapping);
        return FedBy(state, peerKey, mapping.CollectionType) is { } library
            ? PathOf(state.StubRoot, library)
            : Visibility.VisibilityService.MappingPath(state.StubRoot, peerKey, mapping.Subfolder);
    }

    /// <summary>
    /// The folder of a library, from its recorded <see cref="ShoalLibrary.PathRel"/>, confined below the stub root. Null when it is not a valid path.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <param name="library">The library.</param>
    /// <returns>The full path, or null.</returns>
    public static string? PathOf(string stubRoot, ShoalLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(stubRoot) || !Path.IsPathRooted(stubRoot))
        {
            return null;
        }

        var parts = library.PathRel.Split('/');
        if (parts.Length != 2)
        {
            return null;
        }

        if (!library.IsLegacy && !string.Equals(parts[1], library.LibKey, StringComparison.Ordinal))
        {
            return null; // a libs folder is always the library's own key
        }

        return Visibility.VisibilityService.MappingPath(stubRoot, parts[0], parts[1]);
    }

    /// <summary>
    /// The extra NFO tags of a mapping's stubs: the library tag when the titles are in a library of v2.9 or later, nothing for a legacy folder (so an upgrade
    /// rewrites no NFO).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<string> NfoTags(ShoalState state, string peerKey, LibraryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return FedBy(state, peerKey, mapping.CollectionType) is { } library ? [LibTag(library.LibKey)] : [];
    }

    /// <summary>
    /// The access rule of a federated item, from the library its file is in (CallerContext step 6, K45): a library of v2.9 or later answers with its own access,
    /// and only while the item's connection still feeds it; a legacy folder answers with its mapping's access (the source of truth for legacy libraries).
    /// Null means "no rule applies": the caller refuses.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The item's connection.</param>
    /// <param name="mappingKey">The item's mapping.</param>
    /// <param name="libKey">The library key the item's path names, or empty for a legacy folder.</param>
    /// <param name="isCopy">Whether the item is an extra copy of a grouped title (K44/K45): then the library it is in answers with its own access, whoever owns the
    /// title's metadata (the copy exists because a member feeds that library, and playback only uses members that user may open).</param>
    /// <returns>The access, or null.</returns>
    public static LibraryAccess? AccessOfItem(ShoalState state, string peerKey, string mappingKey, string libKey, bool isCopy = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (isCopy)
        {
            return string.IsNullOrEmpty(peerKey) || string.IsNullOrEmpty(libKey)
                ? null
                : state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.LibKey, libKey, StringComparison.Ordinal) && l.Feeds.Count > 0)?.Access;
        }

        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(peerKey))
        {
            return null; // owner unknown: fail closed
        }

        var mapping = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal))?.Libraries.FirstOrDefault(m => string.Equals(m.MappingKey, mappingKey, StringComparison.Ordinal));
        if (mapping is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(libKey))
        {
            return mapping.Access;
        }

        var library = state.Home.Libraries.FirstOrDefault(l => !l.IsLegacy && string.Equals(l.LibKey, libKey, StringComparison.Ordinal));
        return library is not null && library.Feeds.Contains(peerKey, StringComparer.Ordinal) && string.Equals(library.Type, mapping.CollectionType, StringComparison.Ordinal) ? library.Access : null;
    }

    /// <summary>
    /// Whether NFOs of a mapping's titles leave out genres and studios (Warden 11 L3): yes unless the library they land in is visible to everyone, restricted profiles
    /// included. A user who cannot open a library may still see genre and studio names in core's lists; a library everyone opens has nothing to hide.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>True when genres and studios are left out.</returns>
    public static bool HidesFacets(ShoalState state, string peerKey, LibraryMapping mapping) => HidesFacets(AccessOfMapping(state, peerKey, mapping));

    /// <summary>
    /// Whether a library with this access gets NFOs without genres and studios.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <returns>True unless everyone, restricted profiles included, may open it.</returns>
    public static bool HidesFacets(LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return !(access.Mode == AccessMode.Everyone && access.IncludeRestricted);
    }

    /// <summary>
    /// The access of the library a mapping's titles land in: the shared library's own when the connection feeds one for the type, else the mapping's (legacy).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="peerKey">The connection.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The access.</returns>
    public static LibraryAccess AccessOfMapping(ShoalState state, string peerKey, LibraryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return FedBy(state, peerKey, mapping.CollectionType)?.Access ?? mapping.Access;
    }

    /// <summary>
    /// The access of a library: its own for a library of v2.9 or later; for a legacy library the access of the mapping it came from (the source of truth), or its
    /// stored copy when the mapping is gone.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="library">The library.</param>
    /// <returns>The access.</returns>
    public static LibraryAccess AccessOf(ShoalState state, ShoalLibrary library)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(library);
        if (!library.IsLegacy)
        {
            return library.Access;
        }

        return MappingOfLegacy(state, library)?.Mapping.Access ?? library.Access;
    }

    /// <summary>
    /// The connection and mapping a legacy library came from, or null.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="library">A legacy library.</param>
    /// <returns>The peer and mapping, or null.</returns>
    public static (PeerState Peer, LibraryMapping Mapping)? MappingOfLegacy(ShoalState state, ShoalLibrary library)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(library);
        var parts = library.PathRel.Split('/');
        if (!library.IsLegacy || parts.Length != 2)
        {
            return null;
        }

        var peer = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, parts[0], StringComparison.Ordinal));
        var mapping = peer?.Libraries.FirstOrDefault(m => string.Equals(m.Subfolder, parts[1], StringComparison.Ordinal));
        return peer is null || mapping is null ? null : (peer, mapping);
    }

    /// <summary>
    /// The connections whose titles are in a library: its feeds, or for a legacy library its one connection.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <returns>The peer keys.</returns>
    public static IReadOnlyList<string> ConnectionsOf(ShoalLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.IsLegacy ? [library.PathRel.Split('/')[0]] : library.Feeds;
    }

    /// <summary>
    /// Whether two full paths name the same folder (trailing separators ignored; case-insensitive on Windows).
    /// </summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they are the same.</returns>
    public static bool SamePath(string a, string b)
    {
        try
        {
            var x = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var y = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(x, y, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Short(string libKey)
    {
        ArgumentNullException.ThrowIfNull(libKey);
        return libKey.Length >= 8 ? libKey[..8] : libKey;
    }
}
