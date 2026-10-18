using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// A stub copy another server owns, as the planner needs it to start a group.
/// </summary>
/// <param name="PeerKey">The server whose catalog holds the stub (the owner of the metadata).</param>
/// <param name="Entry">Its catalog entry (active, with a stub).</param>
/// <param name="LibKey">The library the stub is in, or empty when it has no library entry yet.</param>
public sealed record OwnedCopy(string PeerKey, CatalogEntry Entry, string LibKey);

/// <summary>
/// What one server's movie sync tells the groups.
/// </summary>
/// <param name="PeerKey">The server that was synced.</param>
/// <param name="Movies">Its movie entries after the sync.</param>
/// <param name="LibKeyOf">The library a mapping of this server feeds (mapping key to library key), or empty.</param>
/// <param name="SelectedSources">The media source ids of each title (peer item id to source ids, best first) that this run saw in the details, hidden or not.</param>
public sealed record GroupPeerInput(string PeerKey, IReadOnlyList<CatalogEntry> Movies, Func<string, string> LibKeyOf, IReadOnlyDictionary<string, IReadOnlyList<string>> SelectedSources);

/// <summary>
/// Keeps the global title groups in line with the catalogs (PLAN v2.9 K42, movies). Pure: no I/O, no clock.
/// <list type="bullet">
/// <item>A group starts when a server's title is a <c>duplicate-peer</c> of another server's stub: the stub becomes the group's copy and its server the
/// owner of the metadata. The other server becomes a hidden member: it has no stub of its own, only a place in the group (at most
/// <see cref="GroupsDocument.MaxMembers"/> servers).</item>
/// <item>The owner stays the owner until it leaves: its stub is gone (the title is no longer on that server, or the server was removed). Then the copy goes,
/// and the group with it; the remaining servers make a new one at their next sync (the folder of the old owner is gone, so the item id changes).</item>
/// <item>A group with fewer than two servers is dropped: a title on one server behaves exactly as before.</item>
/// </list>
/// </summary>
public static class GroupPlanner
{
    /// <summary>
    /// Applies one server's sync to the groups, in place.
    /// </summary>
    /// <param name="document">The groups.</param>
    /// <param name="input">The server's entries after its sync.</param>
    /// <param name="ownerOf">Finds the server and entry that own a stub key among the other servers, or null.</param>
    /// <param name="knownPeers">The servers that are paired now; members of any other server are dropped.</param>
    /// <param name="newKey">Makes a new random group key.</param>
    /// <returns>True when anything changed.</returns>
    public static bool ApplyPeer(GroupsDocument document, GroupPeerInput input, Func<string, OwnedCopy?> ownerOf, IReadOnlySet<string> knownPeers, Func<string> newKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ownerOf);
        ArgumentNullException.ThrowIfNull(knownPeers);
        ArgumentNullException.ThrowIfNull(newKey);
        var me = input.PeerKey;
        var changed = false;
        var active = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        var duplicates = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in input.Movies.Where(static e => e.Kind == EntryKind.Movie))
        {
            if (entry.State is EntryState.Active or EntryState.HeldRemoval && entry.StubRelPath is not null)
            {
                active.TryAdd(entry.StubKey, entry);
            }
            else if (entry.State == EntryState.DuplicatePeer && entry.DuplicateOf is { Length: > 0 } of)
            {
                duplicates.TryAdd(of, entry);
            }
        }

        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in document.Groups.Where(static g => g.Kind == GroupKind.Movie))
        {
            // Servers that are no longer paired leave every group.
            changed |= group.Members.RemoveAll(m => !knownPeers.Contains(m.PeerKey)) > 0;
            if (!knownPeers.Contains(group.MetaOwner))
            {
                changed |= group.Copies.Count > 0;
                document.Retired.AddRange(group.Copies.Where(static c => c.Extra));
                group.Copies.Clear();
                continue;
            }

            if (string.Equals(group.MetaOwner, me, StringComparison.Ordinal))
            {
                // The owner's stub is gone: it left, and the copy with it.
                changed |= group.Copies.RemoveAll(c => !c.Extra && !active.ContainsKey(c.StubKey)) > 0; // an extra copy is the copy writer's, never the owner's own stub
                var own = group.Copies.Select(c => active.GetValueOrDefault(c.StubKey)).FirstOrDefault(static e => e is not null);
                if (own is null)
                {
                    continue;
                }

                changed |= Upsert(group, me, own, input, prepend: true);
                foreach (var copy in group.Copies)
                {
                    handled.Add(copy.StubKey);
                    if (active.TryGetValue(copy.StubKey, out var copyEntry))
                    {
                        changed |= RefreshCopy(copy, copyEntry, input.LibKeyOf(copyEntry.MappingKey));
                    }
                }

                continue;
            }

            var mine = group.Copies.Select(c => duplicates.GetValueOrDefault(c.StubKey)).FirstOrDefault(static e => e is not null);
            if (mine is null)
            {
                changed |= group.Members.RemoveAll(m => string.Equals(m.PeerKey, me, StringComparison.Ordinal)) > 0;
                continue;
            }

            foreach (var copy in group.Copies)
            {
                handled.Add(copy.StubKey);
            }

            changed |= Upsert(group, me, mine, input, prepend: false);
        }

        // New groups: a duplicate of a stub that no group knows yet.
        var byCopy = document.Groups.SelectMany(static g => g.Copies.Select(c => (c.StubKey, Group: g))).GroupBy(static x => x.StubKey, StringComparer.Ordinal).ToDictionary(static x => x.Key, static x => x.First().Group, StringComparer.Ordinal);
        foreach (var (of, entry) in duplicates)
        {
            if (handled.Contains(of) || byCopy.ContainsKey(of) || ownerOf(of) is not { } owner || string.Equals(owner.PeerKey, me, StringComparison.Ordinal) || !knownPeers.Contains(owner.PeerKey))
            {
                continue;
            }

            var group = new TitleGroup { GroupKey = newKey(), Kind = GroupKind.Movie, MetaOwner = owner.PeerKey };
            group.Members.Add(MemberOf(owner.PeerKey, owner.Entry, owner.LibKey, null));
            group.Copies.Add(new GroupCopy { LibKey = owner.LibKey, StubKey = owner.Entry.StubKey, FolderRel = FolderOf(owner.Entry) });
            Upsert(group, me, entry, input, prepend: false);
            document.Groups.Add(group);
            byCopy[of] = group;
            changed = true;
        }

        // A group ends without the owner's own stub or with fewer than two servers; its extra copies are retired, so their folders are deleted from the record.
        foreach (var ended in document.Groups.Where(static g => !g.Copies.Any(static c => !c.Extra) || g.Members.Count < 2))
        {
            document.Retired.AddRange(ended.Copies.Where(static c => c.Extra));
        }

        changed |= document.Groups.RemoveAll(static g => !g.Copies.Any(static c => !c.Extra) || g.Members.Count < 2) > 0;
        return changed;
    }

    /// <summary>
    /// Applies one server's TV sync to the series groups (PLAN v2.9 K43), in place. The owner is the higher-ranked server whose series folder Home shows; a
    /// member is a server whose series entry links to it (<c>DuplicateOf</c>): fully hidden, or showing only the episodes the owner lacks. Episodes are matched at
    /// play time by season and number, so the group holds the servers only.
    /// </summary>
    /// <param name="document">The groups.</param>
    /// <param name="me">The server that was synced.</param>
    /// <param name="series">Its series entries after the sync.</param>
    /// <param name="libKeyOf">The library a mapping of this server feeds.</param>
    /// <param name="ownerOf">Finds the server and series entry that own a series stub key among the other servers, or null.</param>
    /// <param name="knownPeers">The servers that are paired now.</param>
    /// <param name="newKey">Makes a new random group key.</param>
    /// <returns>True when anything changed.</returns>
    public static bool ApplyTvPeer(GroupsDocument document, string me, IReadOnlyList<CatalogEntry> series, Func<string, string> libKeyOf, Func<string, OwnedCopy?> ownerOf, IReadOnlySet<string> knownPeers, Func<string> newKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(libKeyOf);
        ArgumentNullException.ThrowIfNull(ownerOf);
        ArgumentNullException.ThrowIfNull(knownPeers);
        ArgumentNullException.ThrowIfNull(newKey);
        var changed = false;
        var active = series.Where(static e => e.Kind == EntryKind.Series && e.State is EntryState.Active or EntryState.HeldRemoval && e.FolderRel is not null)
            .GroupBy(static e => e.StubKey, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        var links = series.Where(static e => e.Kind == EntryKind.Series && e.State is EntryState.Active or EntryState.HeldRemoval or EntryState.DuplicatePeer && e.DuplicateOf is { Length: >= 8 } of && of != "local")
            .GroupBy(static e => e.DuplicateOf!, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in document.Groups.Where(static g => g.Kind == GroupKind.Series))
        {
            changed |= group.Members.RemoveAll(m => !knownPeers.Contains(m.PeerKey)) > 0;
            if (!knownPeers.Contains(group.MetaOwner))
            {
                changed |= group.Copies.Count > 0;
                group.Copies.Clear();
                continue;
            }

            foreach (var copy in group.Copies)
            {
                handled.Add(copy.StubKey);
            }

            if (string.Equals(group.MetaOwner, me, StringComparison.Ordinal))
            {
                changed |= group.Copies.RemoveAll(c => !active.ContainsKey(c.StubKey)) > 0; // the owner's series folder is gone: the group ends
                if (group.Copies.FirstOrDefault() is not { } copy || !active.TryGetValue(copy.StubKey, out var own))
                {
                    continue;
                }

                changed |= UpsertSeries(group, me, own, libKeyOf(own.MappingKey), prepend: true);
                var folder = own.FolderRel ?? string.Empty;
                var lib = libKeyOf(own.MappingKey);
                if (!string.Equals(copy.FolderRel, folder, StringComparison.Ordinal) || !string.Equals(copy.LibKey, lib, StringComparison.Ordinal))
                {
                    copy.FolderRel = folder;
                    copy.LibKey = lib;
                    changed = true;
                }

                continue;
            }

            var mine = group.Copies.Select(c => links.GetValueOrDefault(c.StubKey)).FirstOrDefault(static e => e is not null);
            changed |= mine is null
                ? group.Members.RemoveAll(m => string.Equals(m.PeerKey, me, StringComparison.Ordinal)) > 0
                : UpsertSeries(group, me, mine, libKeyOf(mine.MappingKey), prepend: false);
        }

        foreach (var (of, entry) in links)
        {
            if (handled.Contains(of) || ownerOf(of) is not { } owner || string.Equals(owner.PeerKey, me, StringComparison.Ordinal) || !knownPeers.Contains(owner.PeerKey) || owner.Entry.Kind != EntryKind.Series)
            {
                continue;
            }

            var group = new TitleGroup { GroupKey = newKey(), Kind = GroupKind.Series, MetaOwner = owner.PeerKey };
            UpsertSeries(group, owner.PeerKey, owner.Entry, owner.LibKey, prepend: false);
            group.Copies.Add(new GroupCopy { LibKey = owner.LibKey, StubKey = owner.Entry.StubKey, FolderRel = owner.Entry.FolderRel ?? string.Empty });
            UpsertSeries(group, me, entry, libKeyOf(entry.MappingKey), prepend: false);
            document.Groups.Add(group);
            handled.Add(of);
            changed = true;
        }

        changed |= document.Groups.RemoveAll(static g => g.Kind == GroupKind.Series && (g.Copies.Count == 0 || g.Members.Count < 2)) > 0;
        return changed;
    }

    /// <summary>
    /// Whether a server's own extra quality versions are hidden for a title: the group has two or more servers, so the version picker goes and the
    /// play picks the server (and later the source) instead.
    /// </summary>
    /// <param name="group">The group, or null when the title has none.</param>
    /// <returns>True when the extras are hidden sources.</returns>
    public static bool HidesExtras(TitleGroup? group) => group is { Members.Count: >= 2 };

    private static bool Upsert(TitleGroup group, string peerKey, CatalogEntry entry, GroupPeerInput input, bool prepend)
    {
        var existing = group.Members.FirstOrDefault(m => string.Equals(m.PeerKey, peerKey, StringComparison.Ordinal));
        var fresh = MemberOf(peerKey, entry, input.LibKeyOf(entry.MappingKey), input.SelectedSources.GetValueOrDefault(entry.PeerItemId) ?? existing?.Sources);
        if (existing is null)
        {
            if (group.Members.Count >= GroupsDocument.MaxMembers)
            {
                return false; // the cap: a fifth server stays a plain duplicate, never used for a play
            }

            if (prepend)
            {
                group.Members.Insert(0, fresh);
            }
            else
            {
                group.Members.Add(fresh);
            }

            return true;
        }

        if (string.Equals(existing.PeerItemId, fresh.PeerItemId, StringComparison.Ordinal)
            && string.Equals(existing.LibKey, fresh.LibKey, StringComparison.Ordinal)
            && existing.Sources.SequenceEqual(fresh.Sources, StringComparer.Ordinal))
        {
            return false;
        }

        existing.PeerItemId = fresh.PeerItemId;
        existing.LibKey = fresh.LibKey;
        existing.Sources = fresh.Sources;
        return true;
    }

    private static bool UpsertSeries(TitleGroup group, string peerKey, CatalogEntry entry, string libKey, bool prepend)
    {
        var existing = group.Members.FirstOrDefault(m => string.Equals(m.PeerKey, peerKey, StringComparison.Ordinal));
        if (existing is null)
        {
            if (group.Members.Count >= GroupsDocument.MaxMembers)
            {
                return false;
            }

            var member = new GroupMember { PeerKey = peerKey, PeerItemId = entry.PeerItemId, LibKey = libKey };
            if (prepend)
            {
                group.Members.Insert(0, member);
            }
            else
            {
                group.Members.Add(member);
            }

            return true;
        }

        if (string.Equals(existing.PeerItemId, entry.PeerItemId, StringComparison.Ordinal) && string.Equals(existing.LibKey, libKey, StringComparison.Ordinal))
        {
            return false;
        }

        existing.PeerItemId = entry.PeerItemId;
        existing.LibKey = libKey;
        return true;
    }

    private static bool RefreshCopy(GroupCopy copy, CatalogEntry entry, string libKey)
    {
        var folder = FolderOf(entry);
        if (string.Equals(copy.FolderRel, folder, StringComparison.Ordinal) && string.Equals(copy.LibKey, libKey, StringComparison.Ordinal))
        {
            return false;
        }

        copy.FolderRel = folder;
        copy.LibKey = libKey;
        return true;
    }

    private static GroupMember MemberOf(string peerKey, CatalogEntry entry, string libKey, IReadOnlyList<string>? sources)
    {
        var ids = sources is { Count: > 0 }
            ? sources
            : entry.Sources.Select(static s => s.PeerMediaSourceId).Where(static s => s.Length == 32).DefaultIfEmpty(entry.PeerMediaSourceId).ToList();
        return new GroupMember
        {
            PeerKey = peerKey,
            PeerItemId = entry.PeerItemId,
            LibKey = libKey,
            Sources = [.. ids.Where(static s => s is { Length: 32 }).Distinct(StringComparer.Ordinal)],
        };
    }

    private static string FolderOf(CatalogEntry entry) => entry.StubRelPath?.Split('/')[0] ?? string.Empty;
}
