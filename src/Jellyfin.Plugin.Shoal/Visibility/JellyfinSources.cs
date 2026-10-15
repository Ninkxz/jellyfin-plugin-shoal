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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Subtitles;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibrarySource"/> class.
    /// </summary>
    /// <param name="libraries">The library manager.</param>
    /// <param name="subtitles">The subtitle manager (the names of its providers are listed as disabled).</param>
    /// <param name="segments">The media segment manager (the names and ids of its providers are listed as disabled).</param>
    public JellyfinLibrarySource(ILibraryManager libraries, ISubtitleManager subtitles, IMediaSegmentManager segments)
    {
        _libraries = libraries;
        _subtitles = subtitles;
        _segments = segments;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryInfo> List()
    {
        var result = new List<LibraryInfo>();
        foreach (var folder in _libraries.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                result.Add(new LibraryInfo(id, folder.Name ?? string.Empty, folder.Locations ?? []));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task CreateAsync(string name, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providers = _subtitles.GetSupportedProviders(new Movie()).Select(static p => p.Name);
        var segmentProviders = _segments.GetSupportedProviders(new Movie()).SelectMany(static p => new[] { p.Name, p.Id });
        var options = LibraryOptionsFactory.Create(path, providers, segmentProviders);
        return _libraries.AddVirtualFolder(name, CollectionTypeOptions.movies, options, refreshLibrary: false);
    }

    /// <inheritdoc />
    public async Task PurgeItemsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        // Removing a virtual folder leaves its items in the database until a scan (Pilot round 6, finding 1): delete them here, files untouched.
        var items = _libraries.GetItemList(new InternalItemsQuery { ParentId = libraryId, Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Video] });
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _libraries.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _libraries.RemoveVirtualFolder(name, refreshLibrary: false);
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
    public async Task SetFoldersAsync(Guid userId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _users.GetUserById(userId) ?? throw new InvalidOperationException("The account no longer exists.");

        // The policy comes from the same DTO the dashboard edits, so every other setting travels through unchanged.
        var policy = _users.GetUserDto(user).Policy ?? throw new InvalidOperationException("The account has no policy.");
        policy.EnableAllFolders = enableAllFolders;
        policy.EnabledFolders = enableAllFolders ? [] : [.. enabledFolders];
        await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
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
            user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }
}
