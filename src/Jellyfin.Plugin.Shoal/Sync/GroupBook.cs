using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The title groups in memory (PLAN v2.9 K42): read once from <see cref="IGroupStore"/>, changed copy-on-write under one lock, so a play reading a group
/// never sees one half changed. Lookups are by the stub key of a group copy (what a stub's catalog entry carries) and by member.
/// </summary>
public sealed class GroupBook
{
    private readonly IGroupStore _store;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Snapshot? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupBook"/> class from the state's stub root (what the plugin registers).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    public GroupBook(IStateStore state, ILogger<GroupBook> logger)
        : this(new GroupStore(() => state.Status == StateStatus.Ready ? state.Current.StubRoot : null, logger), logger)
    {
        var root = state.Status == StateStatus.Ready ? state.Current.StubRoot : null;
        state.Changed += (_, e) =>
        {
            if (!string.Equals(root, e.State.StubRoot, StringComparison.Ordinal))
            {
                root = e.State.StubRoot;
                Invalidate(); // only a new stub root means another file
            }
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupBook"/> class (tests: any store and logger).
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="logger">The logger.</param>
    internal GroupBook(IGroupStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// All groups (read-only by convention: change them only through <see cref="Update"/>).
    /// </summary>
    /// <returns>The groups.</returns>
    public IReadOnlyList<TitleGroup> All() => Current().Document.Groups;

    /// <summary>
    /// The park tags of the groups whose every server is parked (K47): their copies are hidden; a group with one server still up stays visible and plays from it.
    /// </summary>
    /// <param name="parked">The parked servers.</param>
    /// <returns>The tags.</returns>
    public IReadOnlyCollection<string> ParkedGroupTags(IReadOnlySet<string> parked)
    {
        ArgumentNullException.ThrowIfNull(parked);
        return parked.Count == 0 ? [] : [.. Current().Document.Groups.Where(g => g.Members.Count > 0 && g.Members.All(m => parked.Contains(m.PeerKey))).Select(static g => Jellyfin.Plugin.Shoal.State.LibraryFolders.ParkGroupTag(g.GroupKey)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The <c>shoal-alt-</c> tags that some copy carries (K45: the tag service blocks each for the users who can open its library).
    /// </summary>
    /// <returns>The tags.</returns>
    public IReadOnlySet<string> AltTagsInUse() => Current().Document.Groups.SelectMany(static g => g.Copies).SelectMany(static c => c.AltTags).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The group whose copy has this stub key.
    /// </summary>
    /// <param name="stubKey">The stub key of a stub's catalog entry.</param>
    /// <returns>The group, or null when the title has none (a title on one server).</returns>
    public TitleGroup? ByCopy(string? stubKey) => stubKey is not null && Current().ByCopy.TryGetValue(stubKey, out var group) ? group : null;

    /// <summary>
    /// The retired extra copies whose folders are still to be deleted (K44/K45).
    /// </summary>
    /// <returns>The copies (read-only by convention).</returns>
    public IReadOnlyList<GroupCopy> RetiredCopies() => Current().Document.Retired ?? [];

    /// <summary>
    /// The extra copy (K44/K45) whose folder this is, with its group.
    /// </summary>
    /// <param name="libKey">The library the folder is in.</param>
    /// <param name="folder">The title folder directly below the library folder.</param>
    /// <returns>The group and the copy, or null when no extra copy records that folder.</returns>
    public (TitleGroup Group, GroupCopy Copy)? ExtraCopyAt(string libKey, string folder) =>
        Current().ByExtraFolder.TryGetValue((libKey, OperatingSystem.IsWindows() ? folder.ToUpperInvariant() : folder), out var found) ? found : null;

    /// <summary>
    /// The <c>shoal-alt-</c> tags of the copy a stub item is: an extra copy by its folder, else the copy whose stub key is the item's catalog entry's.
    /// </summary>
    /// <param name="libKey">The shared library the item is in, or empty for a legacy folder.</param>
    /// <param name="folder">The title folder.</param>
    /// <param name="stubKey">The stub key of the item's catalog entry, or null.</param>
    /// <returns>The tags; empty when the title has no copy in another library.</returns>
    public IReadOnlyList<string> AltTagsOf(string libKey, string folder, string? stubKey)
    {
        if (libKey.Length > 0 && ExtraCopyAt(libKey, folder) is { } extra)
        {
            return extra.Copy.AltTags;
        }

        return ByCopy(stubKey) is { } group ? group.Copies.FirstOrDefault(c => !c.Extra && c.StubKey == stubKey)?.AltTags ?? [] : [];
    }

    /// <summary>
    /// Changes the groups: the change runs on a copy, which is saved and then put in place.
    /// </summary>
    /// <param name="change">Changes the copy; returns true when anything changed (false: nothing is written).</param>
    /// <returns>True when a change was saved.</returns>
    public bool Update(Func<GroupsDocument, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var copy = Clone(Current().Document);
            if (!change(copy))
            {
                return false;
            }

            try
            {
                _store.Save(copy);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The Shoal title groups could not be saved ({Error}); the next sync tries again", ex.GetType().Name);
                return false;
            }

            _current = new Snapshot(copy);
            return true;
        }
    }

    /// <summary>
    /// Forgets the cached groups; the next lookup reads the file again (the stub root may have changed).
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _current = null;
        }
    }

    private static GroupsDocument Clone(GroupsDocument document) =>
        JsonSerializer.Deserialize<GroupsDocument>(JsonSerializer.SerializeToUtf8Bytes(document)) ?? new GroupsDocument();

    private Snapshot Current()
    {
        var current = _current;
        if (current is not null)
        {
            return current;
        }

        lock (_lock)
        {
            return _current ??= new Snapshot(_store.Load());
        }
    }

    private sealed class Snapshot
    {
        public Snapshot(GroupsDocument document)
        {
            Document = document;
            ByCopy = new Dictionary<string, TitleGroup>(StringComparer.Ordinal);
            ByExtraFolder = new Dictionary<(string LibKey, string Folder), (TitleGroup Group, GroupCopy Copy)>();
            foreach (var group in document.Groups)
            {
                foreach (var copy in group.Copies)
                {
                    ByCopy.TryAdd(copy.StubKey, group);
                    if (copy.Extra)
                    {
                        ByExtraFolder.TryAdd((copy.LibKey, OperatingSystem.IsWindows() ? copy.FolderRel.ToUpperInvariant() : copy.FolderRel), (group, copy));
                    }
                }
            }
        }

        public GroupsDocument Document { get; }

        public Dictionary<string, TitleGroup> ByCopy { get; }

        public Dictionary<(string LibKey, string Folder), (TitleGroup Group, GroupCopy Copy)> ByExtraFolder { get; }
    }
}
