using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging;
using LayoutLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Why a layout change was refused.
/// </summary>
public enum LayoutError
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>The state is in recovery mode.</summary>
    RecoveryMode,

    /// <summary>No such library (or account, or connection).</summary>
    NotFound,

    /// <summary>The request is not valid (a type, a placement, a name, an order).</summary>
    Invalid,

    /// <summary>A library with that name exists.</summary>
    NameInUse,

    /// <summary>There are already <see cref="LibraryFolders.MaxLibraries"/> libraries.</summary>
    TooMany,

    /// <summary>The library still has connections that feed it.</summary>
    NotEmpty,

    /// <summary>A legacy per-friend library goes with its connection or a move, never by itself.</summary>
    Legacy,

    /// <summary>Another layout change or a move job is running, or a sync is.</summary>
    Busy,

    /// <summary>There is no valid stub root.</summary>
    NoStubRoot,

    /// <summary>The main library to add into is not usable (missing, a Shoal library, another type, or below the stub root).</summary>
    MainLibraryInvalid,

    /// <summary>A sharing account cannot be chosen.</summary>
    ShareUser,

    /// <summary>Jellyfin refused.</summary>
    Failed,
}

/// <summary>
/// The outcome of a layout change.
/// </summary>
/// <param name="Error">What went wrong, or <see cref="LayoutError.None"/>.</param>
/// <param name="LibKey">The library the change concerns, when there is one.</param>
public sealed record LayoutResult(LayoutError Error, string? LibKey = null)
{
    /// <summary>Gets a value indicating whether the change was made.</summary>
    public bool Ok => Error == LayoutError.None;

    /// <summary>Gets a refusal.</summary>
    /// <param name="error">The reason.</param>
    /// <returns>The result.</returns>
    public static LayoutResult Refused(LayoutError error) => new(error);
}

/// <summary>
/// A library to create (PLAN v2.9 K44).
/// </summary>
/// <param name="Name">The name users see.</param>
/// <param name="Type"><c>movies</c> or <c>tvshows</c>.</param>
/// <param name="Placement">Its own library, or added into a main library.</param>
/// <param name="MainLibraryId">The main library for placement Main.</param>
public sealed record LibraryCreateRequest(string Name, string Type, LibraryPlacement Placement, Guid? MainLibraryId = null);

/// <summary>
/// The Shoal libraries of v2.9 (PLAN K44): create, rename, delete, order, and who hides friends' titles. A new library starts in "Only selected users" with the admin
/// who made it (owner D3). Placement Shoal makes a Jellyfin library of its own with exactly the library folder; placement Main adds the folder into an existing main
/// library, and the titles are hidden by the <c>shoal-lib-</c> tag from every account the library does not admit; the tags go on before the folder does.
/// </summary>
public sealed class LibraryLayoutService
{
    private const string KeyAlphabet = "0123456789abcdef";

    private readonly IStateStore _state;
    private readonly ILibrarySource _libraries;
    private readonly IAccountSource _accounts;
    private readonly VisibilityService _visibility;
    private readonly LayoutLock _lock;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TagService? _tags;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryLayoutService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="visibility">The visibility service (library lists, then tags).</param>
    /// <param name="layoutLock">The layout lock.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tags">The tag service (uninstall takes its tags away), or null.</param>
    public LibraryLayoutService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, VisibilityService visibility, LayoutLock layoutLock, TimeProvider time, ILogger<LibraryLayoutService> logger, TagService? tags = null)
        : this(state, libraries, accounts, visibility, layoutLock, time, (ILogger)logger, tags)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryLayoutService"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="layoutLock">The layout lock.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="tags">The tag service (uninstall takes its tags away), or null.</param>
    internal LibraryLayoutService(IStateStore state, ILibrarySource libraries, IAccountSource accounts, VisibilityService visibility, LayoutLock layoutLock, TimeProvider time, ILogger logger, TagService? tags = null)
    {
        _state = state;
        _libraries = libraries;
        _accounts = accounts;
        _visibility = visibility;
        _lock = layoutLock;
        _time = time;
        _logger = logger;
        _tags = tags;
    }

    /// <summary>Gets the layout lock (shared with the move job).</summary>
    public LayoutLock Lock => _lock;

    /// <summary>Gets or sets the copy writer (one stub per library per group): a new order changes the alt tags; a library about to go loses its copies first.</summary>
    internal GroupCopyWriter? Copies { get; set; }

    /// <summary>Gets or sets what locks the options of Shoal's libraries again right after a library is made (Pilot 15 R6), or null.</summary>
    internal LibraryOptionsGuard? Options { get; set; }

    /// <summary>Gets or sets what scans a library Shoal renamed (Pilot 16), or null.</summary>
    internal Jellyfin.Plugin.Shoal.Sync.ILibraryRefresh? Scans { get; set; }

    /// <summary>
    /// Makes a new random library key.
    /// </summary>
    /// <returns>16 lower-case hex characters.</returns>
    public static string NewLibKey() => RandomNumberGenerator.GetString(KeyAlphabet, 16);

    /// <summary>
    /// Creates a library.
    /// </summary>
    /// <param name="request">What to create.</param>
    /// <param name="adminUserId">The admin: the only user selected at the start.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome, with the new key.</returns>
    public async Task<LayoutResult> CreateAsync(LibraryCreateRequest request, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var held = _lock.TryEnter();
        if (held is null)
        {
            return LayoutResult.Refused(LayoutError.Busy);
        }

        return await CreateLockedAsync(request, adminUserId, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a rename without changing anything (Warden 12 L4: a combined edit checks the name before it commits an access change). The rename itself checks again.
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="name">The new name.</param>
    /// <returns>None when the rename may go ahead, else why not.</returns>
    public LayoutError CheckRename(string libKey, string name)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutError.RecoveryMode;
        }

        var state = _state.Current;
        var library = state.Home.Libraries.FirstOrDefault(l => l.LibKey == libKey);
        if (library is null)
        {
            return LayoutError.NotFound;
        }

        var clean = MetadataSanitizer.DisplayName(name);
        if (clean.Length == 0 || !string.Equals(clean, name, StringComparison.Ordinal) || !LibraryNames.IsSafe(clean))
        {
            return LayoutError.Invalid;
        }

        if (string.Equals(clean, library.Name, StringComparison.Ordinal))
        {
            return LayoutError.None;
        }

        var all = _libraries.List();
        var own = OwnJellyfinLibrary(state, library, all);
        return state.Home.Libraries.Any(l => l.LibKey != libKey && string.Equals(l.Name, clean, StringComparison.OrdinalIgnoreCase))
            || (library.Placement == LibraryPlacement.Shoal && all.Any(l => l.Id != own?.Id && string.Equals(l.Name, clean, StringComparison.OrdinalIgnoreCase)))
            ? LayoutError.NameInUse
            : LayoutError.None;
    }

    /// <summary>
    /// Renames a library. A library of its own is renamed in Jellyfin too; a library added into a main library changes only the name Shoal shows.
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LayoutResult> RenameAsync(string libKey, string name, CancellationToken cancellationToken = default)
    {
        using var held = _lock.TryEnter();
        if (held is null)
        {
            return LayoutResult.Refused(LayoutError.Busy);
        }

        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        var state = _state.Current;
        var library = state.Home.Libraries.FirstOrDefault(l => l.LibKey == libKey);
        if (library is null)
        {
            return LayoutResult.Refused(LayoutError.NotFound);
        }

        // Pilot 14 RENAME: a name that cleaning would change (leading or trailing space, odd characters) is refused, so Shoal and Jellyfin hold the same name.
        var clean = MetadataSanitizer.DisplayName(name);
        if (clean.Length == 0 || !string.Equals(clean, name, StringComparison.Ordinal) || !LibraryNames.IsSafe(clean))
        {
            return LayoutResult.Refused(LayoutError.Invalid);
        }

        if (string.Equals(clean, library.Name, StringComparison.Ordinal))
        {
            return new LayoutResult(LayoutError.None, libKey);
        }

        var all = _libraries.List();
        var own = OwnJellyfinLibrary(state, library, all);
        if (library.Placement == LibraryPlacement.Shoal && own is null)
        {
            // Pilot 15 RENAME: a library without a folder id yet is not listed; renaming only Shoal's record would split the two names.
            await _libraries.RefreshAsync(cancellationToken).ConfigureAwait(false);
            all = _libraries.List();
            own = OwnJellyfinLibrary(state, library, all);
            if (own is null)
            {
                _logger.LogWarning("Shoal did not find its library in Jellyfin; it was not renamed");
                return LayoutResult.Refused(LayoutError.Failed);
            }
        }

        if (state.Home.Libraries.Any(l => l.LibKey != libKey && string.Equals(l.Name, clean, StringComparison.OrdinalIgnoreCase))
            || (library.Placement == LibraryPlacement.Shoal && all.Any(l => l.Id != own?.Id && string.Equals(l.Name, clean, StringComparison.OrdinalIgnoreCase))))
        {
            return LayoutResult.Refused(LayoutError.NameInUse);
        }

        if (library.Placement == LibraryPlacement.Shoal && own is not null && !string.Equals(own.Name, clean, StringComparison.Ordinal))
        {
            try
            {
                await _libraries.RenameAsync(own.Name, clean, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Jellyfin did not rename a Shoal library ({Error})", ex.GetType().Name);
                return LayoutResult.Refused(LayoutError.Failed);
            }
        }

        // A renamed library can get a new folder id; it is found again by its folder.
        var renamed = OwnJellyfinLibrary(state, library, _libraries.List());
        if (library.Placement == LibraryPlacement.Shoal && own is not null && (renamed is null || !string.Equals(renamed.Name, clean, StringComparison.Ordinal)))
        {
            // Pilot 14/15 RENAME: Jellyfin does not show the new name at Shoal's folder: the folder is renamed back, so both keep the old name.
            _logger.LogWarning("Jellyfin did not show a renamed Shoal library under its new name; the old name is kept");
            await RollBackRenameAsync(own.Name, clean).ConfigureAwait(false);
            return LayoutResult.Refused(LayoutError.Failed);
        }

        await _state.UpdateAsync(
            s =>
            {
                var target = s.Home.Libraries.First(l => l.LibKey == libKey);
                target.Name = clean;
                if (target.Placement == LibraryPlacement.Shoal && renamed is not null)
                {
                    target.JellyfinLibraryId = renamed.Id;
                }

                if (LibraryFolders.MappingOfLegacy(s, target) is { } legacy)
                {
                    legacy.Mapping.LocalLibraryName = clean;
                }
            },
            cancellationToken).ConfigureAwait(false);
        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (own is not null && LibraryFolders.PathOf(state.StubRoot, library) is { } renamedPath)
        {
            // Pilot 16 RENAME: core gives the renamed library a new item and drops the old one; its titles show again only after a scan of that library.
            Scans?.ScanLibrariesAt([renamedPath]);
        }

        return new LayoutResult(LayoutError.None, libKey);
    }

    /// <summary>
    /// Deletes a library that no connection feeds. Only a library of v2.9 or later: a legacy library goes with its connection or a move.
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LayoutResult> DeleteAsync(string libKey, CancellationToken cancellationToken = default)
    {
        using var held = _lock.TryEnter();
        if (held is null)
        {
            return LayoutResult.Refused(LayoutError.Busy);
        }

        return await DeleteLockedAsync(libKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets where new connections go (the default layout picker), without moving anything: the preset new connections follow and, per type, the library they
    /// join. A target must be a non-legacy library of that type; null clears it (the next connection then gets a new "Friends' ..." library).
    /// </summary>
    /// <param name="preset">The preset for new connections.</param>
    /// <param name="targets">Per type (<c>movies</c>, <c>tvshows</c>), the library key or null; a type left out is kept.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LayoutResult> SetDefaultAsync(LayoutPreset preset, IReadOnlyDictionary<string, string?>? targets, CancellationToken cancellationToken = default)
    {
        using var held = _lock.TryEnter();
        if (held is null)
        {
            return LayoutResult.Refused(LayoutError.Busy);
        }

        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        if (!Enum.IsDefined(preset) || (targets?.Count ?? 0) > 2)
        {
            return LayoutResult.Refused(LayoutError.Invalid);
        }

        var state = _state.Current;
        foreach (var (type, key) in targets ?? new Dictionary<string, string?>())
        {
            if (type is not ("movies" or "tvshows"))
            {
                return LayoutResult.Refused(LayoutError.Invalid);
            }

            if (key is not null && !state.Home.Libraries.Any(l => l.LibKey == key && !l.IsLegacy && l.Type == type))
            {
                return LayoutResult.Refused(LayoutError.NotFound);
            }
        }

        await _state.UpdateAsync(
            s =>
            {
                s.Home.Layout.Default = preset;
                foreach (var (type, key) in targets ?? new Dictionary<string, string?>())
                {
                    if (key is null)
                    {
                        s.Home.Layout.DefaultTargets.Remove(type);
                    }
                    else
                    {
                        s.Home.Layout.DefaultTargets[type] = key;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        return new LayoutResult(LayoutError.None, null);
    }

    /// <summary>
    /// Sets the library order (a title shows in the first library a user can open). It must name every library once.
    /// </summary>
    /// <param name="order">The library keys, first first.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LayoutResult> SetOrderAsync(IReadOnlyList<string> order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        using var held = _lock.TryEnter();
        if (held is null)
        {
            return LayoutResult.Refused(LayoutError.Busy);
        }

        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        var keys = _state.Current.Home.Libraries.Select(static l => l.LibKey).ToHashSet(StringComparer.Ordinal);
        if (order.Count != keys.Count || order.Distinct(StringComparer.Ordinal).Count() != order.Count || !order.All(keys.Contains))
        {
            return LayoutResult.Refused(LayoutError.Invalid);
        }

        await _state.UpdateAsync(s => s.Home.LibraryOrder = [.. order], cancellationToken).ConfigureAwait(false);
        if (Copies is not null)
        {
            await Copies.RunAsync(cancellationToken).ConfigureAwait(false); // new alt tags: to the users first, then into the copies
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return new LayoutResult(LayoutError.None);
    }

    /// <summary>
    /// "Hide friends' titles" for one account: the hide-all tag goes into its <c>BlockedTags</c> (and playback refuses friends' titles to it); turning it off takes only
    /// Shoal's own tag away again.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="hidden">Whether friends' titles are hidden from it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<LayoutResult> SetFriendTitlesHiddenAsync(Guid userId, bool hidden, CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        if (VisibilityService.ShareUserIds(_state.Current).Contains(userId))
        {
            return LayoutResult.Refused(LayoutError.ShareUser);
        }

        if (!_accounts.List().Any(a => a.Id == userId))
        {
            return LayoutResult.Refused(LayoutError.NotFound);
        }

        await _state.UpdateAsync(
            s =>
            {
                s.Home.Visibility.HiddenFriendTitles.RemoveAll(id => id == userId);
                if (hidden)
                {
                    s.Home.Visibility.HiddenFriendTitles.Add(userId);
                }
            },
            cancellationToken).ConfigureAwait(false);
        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return new LayoutResult(LayoutError.None);
    }

    /// <summary>
    /// "Remove everything" (DESIGN 5.6, W6 L-5) for the shared libraries: each is taken out of Jellyfin (its own library, or its folder in a main library) and every
    /// tag Shoal added is taken away. The state and the files stay.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many libraries were taken out of Jellyfin.</returns>
    public async Task<int> RemoveAllFromJellyfinAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return 0;
        }

        var state = _state.Current;
        var removed = 0;
        foreach (var library in state.Home.Libraries.Where(static l => !l.IsLegacy).ToList())
        {
            var all = _libraries.List();
            var path = LibraryFolders.PathOf(state.StubRoot, library);
            try
            {
                if (library.Placement == LibraryPlacement.Main)
                {
                    if (path is not null && all.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path))) is { } main)
                    {
                        await _libraries.PurgeItemsBelowAsync(main.Id, path, cancellationToken).ConfigureAwait(false);
                        await _libraries.RemovePathAsync(main.Name, path, cancellationToken).ConfigureAwait(false);
                        removed++;
                    }
                }
                else if (OwnJellyfinLibrary(state, library, all) is { } own)
                {
                    // Warden 13 M1: a library the admin added folders to keeps them.
                    if (await ShoalLibraryRemoval.RemoveAsync(_libraries, own, path, cancellationToken).ConfigureAwait(false))
                    {
                        await _visibility.ForgetLibraryAsync(own.Id, cancellationToken).ConfigureAwait(false);
                    }

                    removed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Jellyfin did not remove a Shoal library ({Error})", ex.GetType().Name);
            }
        }

        if (_tags is not null)
        {
            await _tags.RemoveAllAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// Creates a library while the caller holds the layout lock (the move job, a preset). The access starts as "Only selected users" with the admin, or the given
    /// access (the intersection of the sources, for a combined library).
    /// </summary>
    /// <param name="request">What to create.</param>
    /// <param name="adminUserId">The admin.</param>
    /// <param name="feeds">The connections that feed it from the start (none have stubs in it yet).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="access">The access to start with, or null for the admin alone.</param>
    /// <returns>The outcome, with the new key.</returns>
    internal async Task<LayoutResult> CreateLockedAsync(LibraryCreateRequest request, Guid adminUserId, IReadOnlyList<string> feeds, CancellationToken cancellationToken, LibraryAccess? access = null)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        var state = _state.Current;
        if (!Path.IsPathRooted(state.StubRoot))
        {
            return LayoutResult.Refused(LayoutError.NoStubRoot);
        }

        var name = MetadataSanitizer.DisplayName(request.Name);
        if (name.Length == 0 || !string.Equals(name, request.Name, StringComparison.Ordinal) || !LibraryNames.IsSafe(name) || request.Type is not ("movies" or "tvshows") || !Enum.IsDefined(request.Placement))
        {
            return LayoutResult.Refused(LayoutError.Invalid);
        }

        if (state.Home.Libraries.Count(static l => !l.IsLegacy) >= LibraryFolders.MaxLibraries)
        {
            return LayoutResult.Refused(LayoutError.TooMany);
        }

        var all = _libraries.List();
        if (state.Home.Libraries.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            || (request.Placement == LibraryPlacement.Shoal && all.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            return LayoutResult.Refused(LayoutError.NameInUse);
        }

        LibraryInfo? main = null;
        if (request.Placement == LibraryPlacement.Main)
        {
            main = all.FirstOrDefault(l => l.Id == request.MainLibraryId);
            if (main is null || !IsMainCandidate(state, all, main, request.Type))
            {
                return LayoutResult.Refused(LayoutError.MainLibraryInvalid);
            }
        }

        var libKey = NewLibKey();
        while (state.Home.Libraries.Any(l => l.LibKey == libKey))
        {
            libKey = NewLibKey();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var library = new LayoutLibrary
        {
            LibKey = libKey,
            Name = name,
            Type = request.Type,
            Placement = request.Placement,
            JellyfinLibraryId = main?.Id,
            Feeds = [.. feeds],
            Access = access ?? new LibraryAccess { Mode = AccessMode.Selected, SelectedUserIds = [adminUserId], ChangedUtc = now, ChangedByUserId = adminUserId },
            PathRel = LayoutLibrary.LibsFolder + "/" + libKey,
        };

        // The record and its access go first: there is no moment in which the folder is in a library and anyone else could see it.
        await _state.UpdateAsync(
            s =>
            {
                s.Home.Libraries.Add(library);
                s.Home.LibraryOrder.Add(libKey);
            },
            cancellationToken).ConfigureAwait(false);
        var path = LibraryFolders.PathOf(state.StubRoot, library)!;
        try
        {
            if (main is not null)
            {
                await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false); // the shoal-lib tag is on every account the library does not admit first
                SecureDirectory.Ensure(path);
                await _libraries.AddPathAsync(main.Name, path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SecureDirectory.Ensure(path);
                await _libraries.CreateAsync(name, path, request.Type, cancellationToken).ConfigureAwait(false);
                var created = _libraries.List().FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)));
                await _state.UpdateAsync(s => s.Home.Libraries.First(l => l.LibKey == libKey).JellyfinLibraryId = created?.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Jellyfin did not take a new Shoal library ({Error})", ex.GetType().Name);
            if (main is not null)
            {
                await RemoveDanglingPathAsync(main.Name, path).ConfigureAwait(false); // Warden 13 L4
            }

            await _state.UpdateAsync(s => s.Home.Libraries.RemoveAll(l => l.LibKey == libKey), CancellationToken.None).ConfigureAwait(false);
            LibraryTree.DeleteIfEmpty(state.StubRoot, library, _logger);
            return LayoutResult.Refused(LayoutError.Failed);
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        Options?.Enforce();
        _logger.LogInformation("Shoal created a library ({Placement})", request.Placement);
        return new LayoutResult(LayoutError.None, libKey);
    }

    /// <summary>
    /// Deletes a library while the caller holds the layout lock (the move job removes a library a move left empty).
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="allowLegacy">Whether a legacy library may go (the move job, once its connection moved out).</param>
    /// <returns>The outcome.</returns>
    internal async Task<LayoutResult> DeleteLockedAsync(string libKey, CancellationToken cancellationToken, bool allowLegacy = false)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return LayoutResult.Refused(LayoutError.RecoveryMode);
        }

        var state = _state.Current;
        var library = state.Home.Libraries.FirstOrDefault(l => l.LibKey == libKey);
        if (library is null)
        {
            return LayoutResult.Refused(LayoutError.NotFound);
        }

        if (library.IsLegacy && !allowLegacy)
        {
            return LayoutResult.Refused(LayoutError.Legacy);
        }

        if (!library.IsLegacy && library.Feeds.Count > 0)
        {
            return LayoutResult.Refused(LayoutError.NotEmpty);
        }

        if (Copies is not null && !library.IsLegacy)
        {
            await Copies.RunAsync(cancellationToken).ConfigureAwait(false); // no member feeds it: its title copies are retired and deleted while it is still known
        }

        var all = _libraries.List();
        var path = LibraryFolders.PathOf(state.StubRoot, library);
        try
        {
            if (library.Placement == LibraryPlacement.Main && !library.IsLegacy)
            {
                // Only the library that holds Shoal's own folder, and only that folder (a stored id can be stale): nothing of the admin's is removed.
                var main = path is null ? null : all.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)));
                if (main is not null && path is not null)
                {
                    await _libraries.PurgeItemsBelowAsync(main.Id, path, cancellationToken).ConfigureAwait(false);
                    await _libraries.RemovePathAsync(main.Name, path, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (OwnJellyfinLibrary(state, library, all) is { } own
                && await ShoalLibraryRemoval.RemoveAsync(_libraries, own, path, cancellationToken).ConfigureAwait(false))
            {
                // Warden 12 L2 / 13 M1: a library the admin added folders to keeps them (only Shoal's folder went); a library of Shoal's alone went whole.
                await _visibility.ForgetLibraryAsync(own.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Jellyfin did not remove a Shoal library ({Error})", ex.GetType().Name);
            return LayoutResult.Refused(LayoutError.Failed);
        }

        await _state.UpdateAsync(
            s =>
            {
                s.Home.Libraries.RemoveAll(l => l.LibKey == libKey);
                s.Home.LibraryOrder.RemoveAll(k => k == libKey);
                foreach (var type in s.Home.Layout.DefaultTargets.Where(t => t.Value == libKey).Select(static t => t.Key).ToList())
                {
                    s.Home.Layout.DefaultTargets.Remove(type);
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (path is not null && !library.IsLegacy)
        {
            LibraryTree.DeleteIfEmpty(state.StubRoot, library, _logger);
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Shoal removed a library");
        return new LayoutResult(LayoutError.None, libKey);
    }

    /// <summary>
    /// Re-applies the library lists and the tags (after a move job).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when both are in line.</returns>
    internal Task ReconcileAsync(CancellationToken cancellationToken) => _visibility.ReconcileAsync(cancellationToken);

    /// <summary>
    /// The Jellyfin library of a layout library of its own (placement Shoal, or legacy): the one whose folder is the library's folder.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="library">The layout library.</param>
    /// <param name="all">Jellyfin's libraries.</param>
    /// <returns>The library, or null.</returns>
    internal static LibraryInfo? OwnJellyfinLibrary(ShoalState state, LayoutLibrary library, IReadOnlyList<LibraryInfo> all)
    {
        if (library.Placement == LibraryPlacement.Main && !library.IsLegacy)
        {
            return null;
        }

        var path = LibraryFolders.PathOf(state.StubRoot, library);
        return path is null ? null : all.FirstOrDefault(l => l.Locations.Any(location => LibraryFolders.SamePath(location, path)));
    }

    /// <summary>
    /// Whether a Jellyfin library may take Shoal folders as a main library (placement Main): the admin's own library of the type, not a Shoal library, and not
    /// one whose only folders are Shoal's.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="all">Jellyfin's libraries.</param>
    /// <param name="library">The library.</param>
    /// <param name="type"><c>movies</c> or <c>tvshows</c>.</param>
    /// <returns>True when it may.</returns>
    internal static bool IsMainCandidate(ShoalState state, IReadOnlyList<LibraryInfo> all, LibraryInfo library, string type) =>
        string.Equals(library.CollectionType, type, StringComparison.Ordinal)
        && !VisibilityService.ShoalLibraries(state, all).Any(l => l.Id == library.Id)
        && !HasShoalPath(state, library);

    // A main library must be the admin's own: none of its folders is below the stub root (Shoal's folders, or a folder that holds the stub root), except the
    // recorded folders of other Shoal libraries already added into it.
    private static bool HasShoalPath(ShoalState state, LibraryInfo library)
    {
        var root = Path.GetFullPath(state.StubRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var mainFolders = state.Home.Libraries.Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Main).Select(l => LibraryFolders.PathOf(state.StubRoot, l)).OfType<string>().ToList();
        if (library.Locations.Count == 0 || library.Locations.All(location => mainFolders.Any(m => LibraryFolders.SamePath(m, location))))
        {
            return true; // nothing of the admin's own in it
        }

        return library.Locations.Where(location => !mainFolders.Any(m => LibraryFolders.SamePath(m, location))).Any(location =>
        {
            try
            {
                var full = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(full, root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison) || root.StartsWith(full + Path.DirectorySeparatorChar, comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return true;
            }
        });
    }

    // Warden 13 L4: a half-done add can leave Shoal's folder in the admin's library; take it out again (only that folder), best effort.
    private async Task RemoveDanglingPathAsync(string mainName, string path)
    {
        try
        {
            if (_libraries.List().FirstOrDefault(l => string.Equals(l.Name, mainName, StringComparison.Ordinal)) is { } main && main.Locations.Any(location => LibraryFolders.SamePath(location, path)))
            {
                await _libraries.RemovePathAsync(mainName, path, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Shoal could not take its folder out of a main library after a failed add ({Error})", ex.GetType().Name);
        }
    }

    // Pilot 15 RENAME: the folder was renamed but Jellyfin does not show it at the new name: rename it back so Jellyfin and Shoal keep the same (old) name.
    private async Task RollBackRenameAsync(string oldName, string newName)
    {
        try
        {
            await _libraries.RenameAsync(newName, oldName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Shoal could not rename a library back after a failed rename ({Error})", ex.GetType().Name);
        }
    }
}
