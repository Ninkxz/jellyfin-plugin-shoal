using System;
using System.IO;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Shoal.Authorization;

/// <summary>
/// <see cref="ICallerUsers"/> from Jellyfin's user manager.
/// </summary>
public sealed class JellyfinCallerUsers : ICallerUsers
{
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinCallerUsers"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    public JellyfinCallerUsers(IUserManager users) => _users = users;

    /// <inheritdoc />
    public CallerUser? Find(Guid userId)
    {
        var user = _users.GetUserById(userId);
        return user is null ? null : Describe(user);
    }

    /// <summary>
    /// Describes a Jellyfin user for the access rules.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The description.</returns>
    internal static CallerUser Describe(User user)
    {
        var restricted = user.MaxParentalRatingScore is not null
            || user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Length > 0
            || user.GetPreferenceValues<string>(PreferenceKind.AllowedTags).Length > 0
            || user.AccessSchedules.Count > 0;
        return new CallerUser(
            user.Id,
            user.HasPermission(PermissionKind.IsDisabled),
            restricted,
            user.HasPermission(PermissionKind.EnableMediaPlayback),
            user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
            user.HasPermission(PermissionKind.EnablePlaybackRemuxing),
            user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding),
            user.RemoteClientBitrateLimit);
    }
}

/// <summary>
/// <see cref="IVisibleItems"/> from the library manager, always through the user's own view.
/// </summary>
public sealed class LibraryVisibleItems : IVisibleItems
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryVisibleItems"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    public LibraryVisibleItems(ILibraryManager library, IUserManager users)
    {
        _library = library;
        _users = users;
    }

    /// <inheritdoc />
    public bool IsVisible(Guid userId, Guid itemId)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

#pragma warning disable RS0030 // Allowed here only: the user is non-null, so core returns the item only when it is visible to this user (DESIGN 8.5 step 5, 8.8).
        return _library.GetItemById<BaseItem>(itemId, user) is not null;
#pragma warning restore RS0030
    }
}
