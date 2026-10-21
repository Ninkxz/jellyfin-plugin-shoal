using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Why a library could not be created.
/// </summary>
public enum LibraryCreateError
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>The state is in recovery mode.</summary>
    RecoveryMode,

    /// <summary>No such peer or mapping.</summary>
    UnknownLibrary,

    /// <summary>The stub root is empty or relative, or the mapping's subfolder is not one plain segment below the peer's folder.</summary>
    InvalidPath,

    /// <summary>A library with the same name already exists.</summary>
    NameInUse,

    /// <summary>A library at that path already exists.</summary>
    AlreadyExists,

    /// <summary>Jellyfin refused to create the library.</summary>
    Failed,
}

/// <summary>
/// The outcome of <see cref="ShoalLibraryService.CreateAsync"/>.
/// </summary>
/// <param name="Error">What went wrong, or <see cref="LibraryCreateError.None"/>.</param>
/// <param name="LibraryId">The library's folder id (also for <see cref="LibraryCreateError.AlreadyExists"/>).</param>
/// <param name="SharedLibKey">The shared layout library the mapping's titles go into (K44), or null for a per-friend library of its own. Its access is the shared
/// library's: the add-friend wizard must not change it.</param>
/// <param name="Gained">Who can open the connection's titles because they joined that shared library (Warden 10 M1: reported, never blocked), or null.</param>
public sealed record LibraryCreateResult(LibraryCreateError Error, Guid? LibraryId, string? SharedLibKey = null, AccessGainReport? Gained = null)
{
    /// <summary>Gets a value indicating whether the library was created.</summary>
    public bool Ok => Error == LibraryCreateError.None;
}

/// <summary>
/// Creates and removes the local library of a mapping (DESIGN 5.6). A new library starts in "Only selected users" with the admin who created it
/// as the only selected user; every other account, other admins included, does not see it.
/// </summary>
public sealed class ShoalLibraryService
{
    private readonly IStateStore _state;
    private readonly ILibrarySource _libraries;
    private readonly VisibilityService _visibility;
    private readonly TimeProvider _time;
    private readonly ILogger<ShoalLibraryService> _logger;
    private readonly LibraryLayoutService? _layout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalLibraryService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="layout">The library layout (K44): a new mapping joins its connection's shared library or the layout's default, or null for v1 behavior (a
    /// per-friend library for every mapping).</param>
    public ShoalLibraryService(IStateStore state, ILibrarySource libraries, VisibilityService visibility, TimeProvider time, ILogger<ShoalLibraryService> logger, LibraryLayoutService? layout = null)
    {
        _state = state;
        _libraries = libraries;
        _visibility = visibility;
        _time = time;
        _logger = logger;
        _layout = layout;
    }

    /// <summary>
    /// Creates the Jellyfin library of a mapping. The mapping must already exist in the state.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="mappingKey">The mapping.</param>
    /// <param name="adminUserId">The admin who runs the wizard: the only user selected at the start (owner D3).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LibraryCreateResult> CreateAsync(string peerKey, string mappingKey, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new LibraryCreateResult(LibraryCreateError.RecoveryMode, null);
        }

        var state = _state.Current;
        var peer = state.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey);
        var mapping = peer?.Libraries.FirstOrDefault(l => l.MappingKey == mappingKey);
        if (peer is null || mapping is null)
        {
            return new LibraryCreateResult(LibraryCreateError.UnknownLibrary, null);
        }

        if (!Path.IsPathRooted(state.StubRoot) || VisibilityService.MappingPath(state.StubRoot, peerKey, mapping.Subfolder) is not { } path)
        {
            return new LibraryCreateResult(LibraryCreateError.InvalidPath, null);
        }

        if (_layout is not null && await PlaceInLayoutAsync(peer, mapping, adminUserId, cancellationToken).ConfigureAwait(false) is { } placed)
        {
            return placed;
        }

        var existing = _libraries.List();
        var shoal = VisibilityService.ShoalLibraries(state, existing).FirstOrDefault(l => l.PeerKey == peerKey && l.MappingKey == mappingKey);
        if (shoal is not null)
        {
            return new LibraryCreateResult(LibraryCreateError.AlreadyExists, shoal.Id);
        }

        var name = MetadataSanitizer.DisplayName(string.IsNullOrWhiteSpace(mapping.LocalLibraryName) ? LibraryNames.FromFriend(MetadataSanitizer.FriendName(peer.DisplayName), "Friend") + (mapping.CollectionType == "tvshows" ? " TV shows" : " Movies") : mapping.LocalLibraryName);
        if (name.Length == 0 || existing.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new LibraryCreateResult(LibraryCreateError.NameInUse, null);
        }

        // The access is set before the library exists, so there is no moment in which it could be seen by anyone else.
        var now = _time.GetUtcNow().UtcDateTime;
        await _state.UpdateAsync(
            s =>
            {
                var access = s.Home.Peers.First(p => p.PeerKey == peerKey).Libraries.First(l => l.MappingKey == mappingKey).Access;
                access.Mode = AccessMode.Selected;
                access.SelectedUserIds = [adminUserId];
                access.IncludeRestricted = false;
                access.ChangedUtc = now;
                access.ChangedByUserId = adminUserId;
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            SecureDirectory.Ensure(path);
            await _libraries.CreateAsync(name, path, mapping.CollectionType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Jellyfin did not create the library of a mapping ({Error})", ex.GetType().Name);
            return new LibraryCreateResult(LibraryCreateError.Failed, null);
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var created = _libraries.List().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
        if (_layout is not null)
        {
            // K41/K44: the per-friend library gets its layout entry (a legacy library: its folder is the mapping's).
            await _state.UpdateAsync(
                s =>
                {
                    var added = s.Home.Peers.First(p => p.PeerKey == peerKey).Libraries.First(l => l.MappingKey == mappingKey);
                    if (LibraryFolders.LegacyOf(s, peerKey, added) is null)
                    {
                        var libKey = StateUpgrade.LegacyLibKey(peerKey, mappingKey);
                        s.Home.Libraries.Add(new Jellyfin.Plugin.Shoal.State.ShoalLibrary
                        {
                            LibKey = libKey,
                            Name = name,
                            Type = added.CollectionType,
                            Placement = LibraryPlacement.Shoal,
                            JellyfinLibraryId = created?.Id,
                            Feeds = [peerKey],
                            Access = new LibraryAccess { Mode = added.Access.Mode, SelectedUserIds = [.. added.Access.SelectedUserIds], IncludeRestricted = added.Access.IncludeRestricted, ChangedUtc = added.Access.ChangedUtc, ChangedByUserId = added.Access.ChangedByUserId },
                            PathRel = peerKey + "/" + added.Subfolder,
                        });
                        s.Home.LibraryOrder.Add(libKey);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new LibraryCreateResult(LibraryCreateError.None, created?.Id);
    }

    /// <summary>
    /// Removes the Jellyfin library of a mapping and undoes Shoal's own access changes for it. The stubs on disk and the mapping are not touched.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="mappingKey">The mapping.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a library was removed; false when there was none.</returns>
    public async Task<bool> RemoveAsync(string peerKey, string mappingKey, CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return false;
        }

        var libraries = _libraries.List();
        var shoal = VisibilityService.ShoalLibraries(_state.Current, libraries).FirstOrDefault(l => l.PeerKey == peerKey && l.MappingKey == mappingKey);
        var library = shoal is null ? null : libraries.FirstOrDefault(l => l.Id == shoal.Id);
        if (shoal is null || library is null)
        {
            return false;
        }

        // Warden 13 M1: the shared removal rule. A per-friend library the admin added folders to keeps them and their items; only the mapping's folder goes.
        var state = _state.Current;
        var mapping = state.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey)?.Libraries.FirstOrDefault(m => m.MappingKey == mappingKey);
        var path = mapping is null || !Path.IsPathRooted(state.StubRoot) ? null : VisibilityService.MappingPath(state.StubRoot, peerKey, mapping.Subfolder);
        if (await ShoalLibraryRemoval.RemoveAsync(_libraries, library, path, cancellationToken).ConfigureAwait(false))
        {
            await _visibility.ForgetLibraryAsync(shoal.Id, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// "Remove everything" (DESIGN 5.6, W6 L-5): deletes every Shoal library first, then gives "all libraries" back to the accounts Shoal managed
    /// whose list still is the one it wrote. With no Shoal library left, nothing can be exposed. Stubs and mappings are not touched.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many libraries were removed and how many accounts were restored.</returns>
    public async Task<(int LibrariesRemoved, int AccountsRestored)> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return (0, 0);
        }

        var removed = 0;
        foreach (var shoal in VisibilityService.ShoalLibraries(_state.Current, _libraries.List()).Where(static l => l.LibKey.Length == 0).ToList())
        {
            if (await RemoveAsync(shoal.PeerKey, shoal.MappingKey, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        if (_layout is not null)
        {
            // K44: every shared library goes from Jellyfin too (its own library, or its folder in a main library); the state keeps the layout for a later reinstall.
            removed += await _layout.RemoveAllFromJellyfinAsync(cancellationToken).ConfigureAwait(false);
        }

        var restored = await _visibility.RestoreManagedAccountsAsync(cancellationToken).ConfigureAwait(false);
        return (removed, restored);
    }

    // K44: where a new mapping's titles go. The connection already feeds a shared library of the type: there. Otherwise, with the All mixed or Custom layout, the
    // layout's default library of the type (a new "Friends' movies" or "Friends' TV shows" when there is none yet). With One per friend, null: a per-friend library.
    private async Task<LibraryCreateResult?> PlaceInLayoutAsync(PeerState peer, LibraryMapping mapping, Guid adminUserId, CancellationToken cancellationToken)
    {
        var state = _state.Current;
        if (LibraryFolders.FedBy(state, peer.PeerKey, mapping.CollectionType) is { } fed)
        {
            return new LibraryCreateResult(LibraryCreateError.None, fed.JellyfinLibraryId, fed.LibKey);
        }

        if (state.Home.Layout.Default == LayoutPreset.PerFriend)
        {
            return null;
        }

        using var held = _layout!.Lock.TryEnter();
        if (held is null)
        {
            return new LibraryCreateResult(LibraryCreateError.Failed, null); // a layout change or a move is running
        }

        if (state.Home.Layout.DefaultTargets.TryGetValue(mapping.CollectionType, out var targetKey)
            && state.Home.Libraries.FirstOrDefault(l => l.LibKey == targetKey && !l.IsLegacy && l.Type == mapping.CollectionType) is { } target)
        {
            await _state.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == targetKey).Feeds.Add(peer.PeerKey), cancellationToken).ConfigureAwait(false);
            await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);

            // Warden 10 M1 (owner decision): a library set to Everyone keeps taking new friends; the wizard only says who now sees this friend's titles.
            // Warden 14: in a placement-Main library only accounts that can open the main library see the titles (MainAccess, by folder).
            var targetMain = MainAccess.MainIdOf(state, target, _libraries.List());
            var gained = AccessGainReport.Of(AccessGain.Gaining(_visibility.Accounts().Where(a => MainAccess.Opens(_state.Current, a, targetMain)), target.Access, [], VisibilityService.ShareUserIds(state)));
            return new LibraryCreateResult(LibraryCreateError.None, target.JellyfinLibraryId, target.LibKey, gained);
        }

        var name = mapping.CollectionType == "tvshows" ? "Friends' TV shows" : "Friends' movies";
        var created = await _layout.CreateLockedAsync(new LibraryCreateRequest(name, mapping.CollectionType, LibraryPlacement.Shoal), adminUserId, [peer.PeerKey], cancellationToken).ConfigureAwait(false);
        if (!created.Ok)
        {
            return new LibraryCreateResult(created.Error == LayoutError.NameInUse ? LibraryCreateError.NameInUse : LibraryCreateError.Failed, null);
        }

        await _state.UpdateAsync(s => s.Home.Layout.DefaultTargets[mapping.CollectionType] = created.LibKey!, cancellationToken).ConfigureAwait(false);
        var library = _state.Current.Home.Libraries.First(l => l.LibKey == created.LibKey);
        return new LibraryCreateResult(LibraryCreateError.None, library.JellyfinLibraryId, library.LibKey);
    }
}
