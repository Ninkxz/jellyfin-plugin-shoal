using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Upgrades a schema 2 <c>state.json</c> to schema 3 in memory (PLAN v2.9 K41). Every existing per-friend library becomes a <see cref="ShoalLibrary"/>
/// with one feed, its own folder (legacy <see cref="ShoalLibrary.PathRel"/> <c>&lt;peerKey&gt;/&lt;subfolder&gt;</c>) and a copy of its access; the
/// layout is <see cref="LayoutPreset.PerFriend"/>. Nothing on disk changes: the store does not write on load, and the next change writes schema 3.
/// The library keys are derived from the peer and mapping keys, so loading the same file twice gives the same keys.
/// </summary>
internal static class StateUpgrade
{
    /// <summary>The previous schema, which this build still reads.</summary>
    public const int LegacySchema = 2;

    /// <summary>
    /// Whether a file with this schema is read by this build.
    /// </summary>
    /// <param name="schema">The schema on disk.</param>
    /// <returns>True for the current and the previous schema.</returns>
    public static bool IsReadable(int schema) => schema is LegacySchema or ShoalState.CurrentSchema;

    /// <summary>
    /// Upgrades a state read from a schema 2 file, in place. A state at the current schema is left alone.
    /// </summary>
    /// <param name="state">The state as read (not yet validated).</param>
    /// <returns>Notes for the admin and the log (no secrets, no raw values).</returns>
    public static IReadOnlyList<string> Upgrade(ShoalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Schema != LegacySchema)
        {
            return [];
        }

        var notes = new List<string>();
        var libraries = new List<ShoalLibrary>();
        var skipped = 0;
        foreach (var peer in state.Home.Peers)
        {
            foreach (var mapping in peer.Libraries)
            {
                if (mapping.CollectionType is not ("movies" or "tvshows"))
                {
                    skipped++; // kept as a mapping; it simply has no layout entry
                    continue;
                }

                libraries.Add(new ShoalLibrary
                {
                    LibKey = LegacyLibKey(peer.PeerKey, mapping.MappingKey),
                    Name = mapping.LocalLibraryName.Length > 0 ? mapping.LocalLibraryName : (peer.DisplayName + " " + mapping.RemoteName).Trim(),
                    Type = mapping.CollectionType,
                    Placement = LibraryPlacement.Shoal,
                    JellyfinLibraryId = null,
                    Feeds = [peer.PeerKey],
                    Access = CopyOf(mapping.Access),
                    PathRel = peer.PeerKey + "/" + mapping.Subfolder,
                });
            }
        }

        state.Home.Libraries = libraries;
        state.Home.LibraryOrder = [.. libraries.Select(static l => l.LibKey)];
        state.Home.Layout = new LibraryLayout { Default = LayoutPreset.PerFriend };
        state.Schema = ShoalState.CurrentSchema;
        notes.Add($"Shoal settings were upgraded to schema {ShoalState.CurrentSchema}: {libraries.Count} friend librar{(libraries.Count == 1 ? "y" : "ies")} kept as they are (one per friend).");
        if (skipped > 0)
        {
            notes.Add($"{skipped} mapped librar{(skipped == 1 ? "y has" : "ies have")} a type other than movies or TV shows and got no layout entry.");
        }

        return notes;
    }

    /// <summary>
    /// The library key of a legacy library: the first 16 hex characters of a SHA-256 over the peer and mapping keys.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="mappingKey">The mapping key.</param>
    /// <returns>16 lower-case hex characters.</returns>
    public static string LegacyLibKey(string peerKey, string mappingKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("shoal-legacy-library\n" + peerKey + "\n" + mappingKey));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static LibraryAccess CopyOf(LibraryAccess access) => new()
    {
        Mode = access.Mode,
        SelectedUserIds = [.. access.SelectedUserIds],
        IncludeRestricted = access.IncludeRestricted,
        ChangedUtc = access.ChangedUtc,
        ChangedByUserId = access.ChangedByUserId,
    };
}
