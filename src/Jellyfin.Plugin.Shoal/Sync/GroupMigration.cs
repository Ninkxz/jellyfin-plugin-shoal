using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>What the migration would do, or did.</summary>
/// <param name="DuplicateTitles">Movie entries that are a <c>duplicate-peer</c> of another server's stub and are in no group yet.</param>
/// <param name="NewGroups">Groups it makes (one per owner stub).</param>
/// <param name="ExtraCopies">Extra copies the copy writer would add (one per other v2.9 library a member feeds), after the users' alternate-copy tags.</param>
/// <param name="Ran">Whether it ran (false for a preview).</param>
/// <param name="Refused"><c>busy</c> (a layout change runs) or <c>syncing</c> (a sync runs) when it did not run, or null.</param>
public sealed record GroupMigrationReport(int DuplicateTitles, int NewGroups, int ExtraCopies, bool Ran, string? Refused = null);

/// <summary>
/// PLAN v2.9 K55: turns the existing <c>duplicate-peer</c> movie entries into title groups now, without waiting for each server's next sync. Order, so no user ever
/// sees a title twice: the groups are recorded (no file changes: the owner's stub is the copy), then the copy writer gives every user the alternate-copy tags
/// first, then writes the extra copies, then asks for a targeted scan. TV series hidden by an older build are turned into groups by their next TV sync.
/// </summary>
public sealed class GroupMigration
{
    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly GroupBook _groups;
    private readonly GroupCopyWriter? _copies;
    private readonly Jellyfin.Plugin.Shoal.Visibility.LayoutLock? _lock;
    private readonly ISyncHold? _syncs;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupMigration"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="groups">The title groups.</param>
    /// <param name="copies">The copy writer (tags first, then copies, then scan), or null.</param>
    /// <param name="layoutLock">The layout lock (no migration during a layout change, Warden 11 L5), or null.</param>
    /// <param name="syncs">The sync lock (no migration during a sync), or null.</param>
    public GroupMigration(IStateStore state, CatalogStore catalogs, GroupBook groups, GroupCopyWriter? copies = null, Jellyfin.Plugin.Shoal.Visibility.LayoutLock? layoutLock = null, ISyncHold? syncs = null)
    {
        _lock = layoutLock;
        _syncs = syncs;
        _state = state;
        _catalogs = catalogs;
        _groups = groups;
        _copies = copies;
    }

    /// <summary>
    /// What a run would do; nothing is written.
    /// </summary>
    /// <returns>The report.</returns>
    public GroupMigrationReport Preview()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new GroupMigrationReport(0, 0, 0, false);
        }

        var state = _state.Current;
        var document = Clone(_groups.All());
        var before = document.Groups.Count;
        var pending = Apply(state, document);
        return new GroupMigrationReport(pending, document.Groups.Count - before, ExtraCopies(state, document), false);
    }

    /// <summary>
    /// Runs the migration: groups first, then the copy writer (tags, copies, scan).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    public async Task<GroupMigrationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new GroupMigrationReport(0, 0, 0, false);
        }

        // Warden 11 L5: never while a layout change or a sync runs (both write groups and copies).
        using var layoutHeld = _lock is null ? null : _lock.TryEnter();
        if (_lock is not null && layoutHeld is null)
        {
            return new GroupMigrationReport(0, 0, 0, false, "busy");
        }

        using var syncHeld = _syncs is null ? null : _syncs.TryHold();
        if (_syncs is not null && syncHeld is null)
        {
            return new GroupMigrationReport(0, 0, 0, false, "syncing");
        }

        var state = _state.Current;
        var pending = 0;
        var made = 0;
        _groups.Update(document =>
        {
            var before = document.Groups.Count;
            pending = Apply(state, document);
            made = document.Groups.Count - before;
            return pending > 0 || made != 0;
        });
        var extra = ExtraCopies(state, CloneDocument());
        if (_copies is not null)
        {
            await _copies.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return new GroupMigrationReport(pending, made, extra, true);
    }

    private static string NewKey() => RandomNumberGenerator.GetString(KeyAlphabet, 16);

    private static GroupsDocument Clone(IReadOnlyList<TitleGroup> groups) => System.Text.Json.JsonSerializer.Deserialize<GroupsDocument>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new GroupsDocument { Groups = [.. groups] }))!;

    private static int ExtraCopies(ShoalState state, GroupsDocument document) =>
        document.Groups.Where(static g => g.Kind == GroupKind.Movie).Sum(g =>
        {
            var primary = g.Copies.FirstOrDefault(static c => !c.Extra)?.LibKey ?? string.Empty;
            return GroupCopyWriter.WantedLibraries(state, g, primary).Count(lib => !g.Copies.Any(c => c.LibKey == lib));
        });

    private static string LibKeyOf(ShoalState state, PeerState peer, string mappingKey) =>
        peer.Libraries.FirstOrDefault(m => m.MappingKey == mappingKey) is { } mapping
            ? LibraryFolders.LibraryOf(state, peer.PeerKey, mapping)?.LibKey ?? StateUpgrade.LegacyLibKey(peer.PeerKey, mappingKey)
            : string.Empty;

    private GroupsDocument CloneDocument() => Clone(_groups.All());

    // Every server's movie entries go through the same planner a sync uses; it counts the duplicates that no group held before.
    private int Apply(ShoalState state, GroupsDocument document)
    {
        var catalogs = state.Home.Peers.Select(p => (Peer: p, Load: _catalogs.Load(p.PeerKey))).Where(static x => !x.Load.Unreadable).ToList();
        var owners = new Dictionary<string, OwnedCopy>(StringComparer.Ordinal);
        foreach (var (peer, load) in catalogs)
        {
            foreach (var entry in load.Document.Entries.Where(static e => e.Kind == EntryKind.Movie && e.State is EntryState.Active or EntryState.HeldRemoval && e.StubRelPath is not null))
            {
                owners.TryAdd(entry.StubKey, new OwnedCopy(peer.PeerKey, entry, LibKeyOf(state, peer, entry.MappingKey)));
            }
        }

        var known = state.Home.Peers.Select(static p => p.PeerKey).ToHashSet(StringComparer.Ordinal);
        var grouped = document.Groups.SelectMany(static g => g.Copies.Select(static c => c.StubKey)).ToHashSet(StringComparer.Ordinal);
        var pending = catalogs.Sum(x => x.Load.Document.Entries.Count(e => e.Kind == EntryKind.Movie && e.State == EntryState.DuplicatePeer && e.DuplicateOf is { } of && owners.ContainsKey(of) && !grouped.Contains(of)));
        foreach (var (peer, load) in catalogs)
        {
            var input = new GroupPeerInput(peer.PeerKey, [.. load.Document.Entries.Where(static e => e.Kind == EntryKind.Movie)], m => LibKeyOf(state, peer, m), new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
            GroupPlanner.ApplyPeer(document, input, owners.GetValueOrDefault, known, NewKey);
        }

        return pending;
    }
}
