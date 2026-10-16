using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The one definition of the title counts the admin page shows, so the picker, the libraries list and the server card say the same numbers.
/// A title is a movie or a series; seasons and episodes belong to their series and are never counted on their own.
/// <list type="bullet">
/// <item><description><c>remoteCount</c>: the titles the friend's server lists in the library (what the last sync saw: with a stub, held back, or left without one because another copy wins).</description></item>
/// <item><description><c>localCount</c>: the titles of the library that have a stub on this server (what a viewer here can play).</description></item>
/// <item><description><c>hiddenByDedup</c>: the titles with no stub because this server, or a better-ranked friend, carries them already. <c>remoteCount</c> is always <c>localCount</c> + <c>hiddenByDedup</c> + the additions a guard holds back.</description></item>
/// </list>
/// </summary>
/// <param name="Remote">Titles the friend lists.</param>
/// <param name="Local">Titles with a stub here.</param>
/// <param name="Hidden">Titles left to another copy.</param>
internal readonly record struct TitleCounts(int Remote, int Local, int Hidden)
{
    /// <summary>Counts the titles of the entries (optionally of one library).</summary>
    /// <param name="entries">The catalog entries.</param>
    /// <param name="mappingKey">The library, or null for all of them.</param>
    /// <returns>The three counts.</returns>
    public static TitleCounts Of(IEnumerable<CatalogEntry> entries, string? mappingKey = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int remote = 0, local = 0, hidden = 0;
        foreach (var e in entries)
        {
            if (e.Kind is not (EntryKind.Movie or EntryKind.Series) || (mappingKey is not null && !string.Equals(e.MappingKey, mappingKey, StringComparison.Ordinal)))
            {
                continue;
            }

            switch (e.State)
            {
                case EntryState.Active or EntryState.HeldRemoval:
                    local++;
                    remote++;
                    break;
                case EntryState.DuplicatePeer or EntryState.DuplicateLocal:
                    hidden++;
                    remote++;
                    break;
                case EntryState.HeldAddition:
                    remote++;
                    break;
            }
        }

        return new TitleCounts(remote, local, hidden);
    }

    /// <summary>Names the server that carries most of the titles a library lost to dedup.</summary>
    /// <param name="state">The state.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="peerKey">The server the titles belong to.</param>
    /// <param name="entries">The catalog entries (the hidden ones are picked here).</param>
    /// <param name="mappingKey">The library, or null for all of them.</param>
    /// <returns>The winner name, or null (this server itself, or not known).</returns>
    public static string? Winner(IStateStore state, CatalogStore catalogs, string peerKey, IEnumerable<CatalogEntry> entries, string? mappingKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(entries);
        var wanted = entries
            .Where(e => e.State == EntryState.DuplicatePeer && e.Kind is EntryKind.Movie or EntryKind.Series && (mappingKey is null || string.Equals(e.MappingKey, mappingKey, StringComparison.Ordinal)))
            .Select(static e => e.DuplicateOf)
            .Where(static k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 || state.Status != StateStatus.Ready)
        {
            return null;
        }

        string? best = null;
        var bestCount = 0;
        foreach (var other in state.Current.Home.Peers.Where(p => p.PeerKey != peerKey))
        {
            var load = catalogs.Load(other.PeerKey);
            if (load.Unreadable)
            {
                continue;
            }

            var count = load.Document.Entries.Count(e => wanted.Contains(e.StubKey));
            if (count > bestCount)
            {
                best = other.DisplayName;
                bestCount = count;
            }
        }

        return best;
    }
}
