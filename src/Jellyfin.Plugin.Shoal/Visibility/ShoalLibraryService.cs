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
public sealed record LibraryCreateResult(LibraryCreateError Error, Guid? LibraryId)
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalLibraryService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ShoalLibraryService(IStateStore state, ILibrarySource libraries, VisibilityService visibility, TimeProvider time, ILogger<ShoalLibraryService> logger)
    {
        _state = state;
        _libraries = libraries;
        _visibility = visibility;
        _time = time;
        _logger = logger;
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

        var existing = _libraries.List();
        var shoal = VisibilityService.ShoalLibraries(state, existing).FirstOrDefault(l => l.PeerKey == peerKey && l.MappingKey == mappingKey);
        if (shoal is not null)
        {
            return new LibraryCreateResult(LibraryCreateError.AlreadyExists, shoal.Id);
        }

        var name = MetadataSanitizer.DisplayName(string.IsNullOrWhiteSpace(mapping.LocalLibraryName) ? peer.DisplayName + " Movies" : mapping.LocalLibraryName);
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
            await _libraries.CreateAsync(name, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Jellyfin did not create the library of a mapping ({Error})", ex.GetType().Name);
            return new LibraryCreateResult(LibraryCreateError.Failed, null);
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var created = _libraries.List().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
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

        // The items first: a removed library otherwise keeps its titles listed until a scan, and an account restored to "all libraries" would see them.
        await _libraries.PurgeItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        await _libraries.RemoveAsync(library.Name, cancellationToken).ConfigureAwait(false);
        await _visibility.ForgetLibraryAsync(shoal.Id, cancellationToken).ConfigureAwait(false);
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
        foreach (var shoal in VisibilityService.ShoalLibraries(_state.Current, _libraries.List()).ToList())
        {
            if (await RemoveAsync(shoal.PeerKey, shoal.MappingKey, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        var restored = await _visibility.RestoreManagedAccountsAsync(cancellationToken).ConfigureAwait(false);
        return (removed, restored);
    }
}
