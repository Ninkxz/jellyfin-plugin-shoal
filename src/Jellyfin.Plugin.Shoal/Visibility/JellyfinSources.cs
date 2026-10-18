using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// <see cref="ILibrarySource"/> from Jellyfin's library manager.
/// </summary>
public sealed class JellyfinLibrarySource : ILibrarySource
{
    private readonly ILibraryManager _libraries;
    private readonly ISubtitleManager _subtitles;
    private readonly IMediaSegmentManager _segments;
    private readonly IServerApplicationPaths? _paths;
    private readonly ILibraryMonitor? _monitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibrarySource"/> class.
    /// </summary>
    /// <param name="libraries">The library manager.</param>
    /// <param name="subtitles">The subtitle manager (the names of its providers are listed as disabled).</param>
    /// <param name="segments">The media segment manager (the names and ids of its providers are listed as disabled).</param>
    /// <param name="paths">The server paths (where the virtual folders live, for a rename), or null.</param>
    /// <param name="monitor">The library monitor (paused during a rename, as core does), or null.</param>
    public JellyfinLibrarySource(ILibraryManager libraries, ISubtitleManager subtitles, IMediaSegmentManager segments, IServerApplicationPaths? paths = null, ILibraryMonitor? monitor = null)
    {
        _libraries = libraries;
        _subtitles = subtitles;
        _segments = segments;
        _paths = paths;
        _monitor = monitor;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryInfo> List()
    {
        var result = new List<LibraryInfo>();
        foreach (var folder in _libraries.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                var savesNfo = folder.LibraryOptions?.MetadataSavers?.Any(static s => string.Equals(s, "Nfo", StringComparison.OrdinalIgnoreCase)) == true;
#pragma warning disable CS0618 // Obsolete in 12.1; still set by older servers.
                var fetches = folder.LibraryOptions is { } options && (options.EnableInternetProviders || (options.TypeOptions ?? []).Any(static t => t.MetadataFetchers is { Length: > 0 }));
#pragma warning restore CS0618
                result.Add(new LibraryInfo(id, folder.Name ?? string.Empty, folder.Locations ?? [], folder.CollectionType?.ToString().ToLowerInvariant() ?? string.Empty, savesNfo, fetches));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task CreateAsync(string name, string path, CancellationToken cancellationToken) => CreateAsync(name, path, "movies", cancellationToken);

    /// <inheritdoc />
    public async Task CreateAsync(string name, string path, string collectionType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tv = string.Equals(collectionType, "tvshows", StringComparison.Ordinal);
        var providers = _subtitles.GetSupportedProviders(new Movie()).Select(static p => p.Name);
        var segmentProviders = _segments.GetSupportedProviders(new Movie()).SelectMany(static p => new[] { p.Name, p.Id });
        var options = LibraryOptionsFactory.Create(path, providers, segmentProviders, tv);
        await _libraries.AddVirtualFolder(name, tv ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies, options, refreshLibrary: false).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false); // Pilot 15: its folder id exists only after this
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken) => _libraries.ValidateTopLibraryFolders(cancellationToken, false);

    /// <inheritdoc />
    public async Task PurgeItemsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        // Removing a virtual folder leaves its items in the database until a scan (Pilot round 6, finding 1): delete them here, files untouched.
        // A TV library: the leaves go first, so no series or season is deleted under items that are still to be listed.
        foreach (var types in new[] { new[] { BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.Episode }, [BaseItemKind.Season], [BaseItemKind.Series] })
        {
            foreach (var item in _libraries.GetItemList(new InternalItemsQuery { ParentId = libraryId, Recursive = true, IncludeItemTypes = types }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _libraries.RemoveVirtualFolder(name, refreshLibrary: false);
    }

    /// <inheritdoc />
    public Task AddPathAsync(string libraryName, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _libraries.AddMediaPath(libraryName, new MediaPathInfo(path));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemovePathAsync(string libraryName, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _libraries.RemoveMediaPath(libraryName, path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PurgeItemsBelowAsync(Guid libraryId, string path, CancellationToken cancellationToken)
    {
        var root = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var types in new[] { new[] { BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.Episode }, [BaseItemKind.Season], [BaseItemKind.Series] })
        {
            foreach (var item in _libraries.GetItemList(new InternalItemsQuery { ParentId = libraryId, Recursive = true, IncludeItemTypes = types }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Path is { } itemPath && System.IO.Path.GetFullPath(itemPath).StartsWith(root, comparison))
                {
                    _libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool LockOptions(string name)
    {
        if (_paths is null || !LibraryNames.IsSafe(name))
        {
            return false;
        }

        var path = LibraryNames.ChildOf(_paths.DefaultUserViewsPath, name);
        if (!System.IO.Directory.Exists(path))
        {
            return false;
        }

        var options = CollectionFolder.GetLibraryOptions(path);
        if (!LibraryOptionsFactory.Lock(options))
        {
            return false;
        }

        CollectionFolder.SaveLibraryOptions(path, options);
        return true;
    }

    /// <inheritdoc />
    public async Task RenameAsync(string name, string newName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_paths is null)
        {
            throw new NotSupportedException();
        }

        // What core's own rename does (LibraryStructureController): the virtual folder is a folder below the user views path, named after the library.
        // Warden 10 M3: the new name must be one plain folder name and the target a direct child of the views path; the current folder must be one too.
        var root = _paths.DefaultUserViewsPath;
        var target = LibraryNames.ChildOf(root, newName);
        var current = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name));
        if (!string.Equals(System.IO.Path.GetDirectoryName(current), System.IO.Path.GetDirectoryName(target), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The library folder is not below the user views path.");
        }

        if (!System.IO.Directory.Exists(current))
        {
            throw new InvalidOperationException("The library does not exist.");
        }

        var caseOnly = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && System.IO.Directory.Exists(target))
        {
            throw new InvalidOperationException("A library with that name exists.");
        }

        _monitor?.Stop();
        try
        {
            if (caseOnly)
            {
                var temp = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
                System.IO.Directory.Move(current, temp);
                try
                {
                    System.IO.Directory.Move(temp, target);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // Warden 12 L3: never leave the library under the temporary name.
                    System.IO.Directory.Move(temp, current);
                    throw;
                }
            }
            else
            {
                System.IO.Directory.Move(current, target);
            }
        }
        finally
        {
            CollectionFolder.OnCollectionFolderChange();
            _monitor?.Start();
        }

        // Pilot 15 RENAME: until the top folders are validated, the renamed library has no folder id (Jellyfin lists ItemId undefined) and Shoal cannot find it.
        // A failure here is not thrown: the folder is renamed already, and the caller's check of the new name renames it back.
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }
    }
}

/// <summary>
/// <see cref="IAccountSource"/> from Jellyfin's user manager.
/// </summary>
public sealed class JellyfinAccountSource : IAccountSource
{
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinAccountSource"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    public JellyfinAccountSource(IUserManager users) => _users = users;

    /// <inheritdoc />
    public IReadOnlyList<AccountInfo> List() => _users.GetUsers().Select(Describe).ToList();

    /// <inheritdoc />
    public async Task SetBlockedTagsAsync(Guid userId, IReadOnlyList<string> blockedTags, CancellationToken cancellationToken)
    {
        await PolicyGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The same DTO the dashboard edits, read inside the gate, so every other setting travels through unchanged.
            var policy = Policy(userId);
            policy.BlockedTags = [.. blockedTags];
            await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        }
        finally
        {
            PolicyGate.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ChangeBlockedTagsAsync(Guid userId, IReadOnlyCollection<string> remove, IReadOnlyCollection<string> add, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        await PolicyGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Warden 10 M4: read, change only these tags, write, and read again; a write that did not stick is done once more from a fresh read.
            for (var attempt = 0; ; attempt++)
            {
                var policy = Policy(userId);
                var current = policy.BlockedTags ?? [];
                var next = PolicyGate.Next(current, remove, add);
                if (!next.SequenceEqual(current, StringComparer.Ordinal))
                {
                    policy.BlockedTags = [.. next];
                    await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
                }

                var after = (IReadOnlyList<string>)(Policy(userId).BlockedTags ?? []);
                if (PolicyGate.Holds(after, remove, add) || attempt >= 1)
                {
                    return after;
                }
            }
        }
        finally
        {
            PolicyGate.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetFoldersAsync(Guid userId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders, CancellationToken cancellationToken)
    {
        await PolicyGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The policy comes from the same DTO the dashboard edits, read inside the gate: a tag written a moment ago is never written back to its old value.
            var policy = Policy(userId);
            policy.EnableAllFolders = enableAllFolders;
            policy.EnabledFolders = enableAllFolders ? [] : [.. enabledFolders];
            await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        }
        finally
        {
            PolicyGate.Gate.Release();
        }
    }

    private MediaBrowser.Model.Users.UserPolicy Policy(Guid userId)
    {
        var user = _users.GetUserById(userId) ?? throw new InvalidOperationException("The account no longer exists.");
        return _users.GetUserDto(user).Policy ?? throw new InvalidOperationException("The account has no policy.");
    }

    private static AccountInfo Describe(User user)
    {
        var described = JellyfinCallerUsers.Describe(user);
        return new AccountInfo(
            user.Id,
            user.Username,
            described.IsDisabled,
            user.HasPermission(PermissionKind.IsAdministrator),
            described.IsRestricted,
            user.HasPermission(PermissionKind.EnableAllFolders),
            user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
            user.GetPreferenceValues<string>(PreferenceKind.BlockedTags));
    }
}
