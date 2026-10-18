using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// What a reconcile did to one account.
/// </summary>
public enum VisibilityChangeKind
{
    /// <summary>An explicit list was updated (Shoal libraries added or removed).</summary>
    ListUpdated,

    /// <summary>An "all libraries" account was converted to an explicit list so one Shoal library could be hidden; it is now managed.</summary>
    Converted,

    /// <summary>A managed account's list was recomputed (a library was added or removed).</summary>
    ManagedRefreshed,

    /// <summary>A managed account got "all libraries" back because nothing is hidden from it any more.</summary>
    Restored,

    /// <summary>The admin edited a managed account; Shoal stopped managing it.</summary>
    StoppedManaging,

    /// <summary>A Shoal library was removed from a sharing account's list (DESIGN 5.6, W2 N-2).</summary>
    ShareUserCleaned,
}

/// <summary>
/// Jellyfin's libraries: list, create and remove. The production class talks to <c>ILibraryManager</c>; tests use a fake.
/// </summary>
public interface ILibrarySource
{
    /// <summary>
    /// Lists every library.
    /// </summary>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryInfo> List();

    /// <summary>
    /// Creates a movie library with the locked options of DESIGN 5.6 and one path.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="path">The only path of the library.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the library exists.</returns>
    Task CreateAsync(string name, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a library of the given collection type (<c>movies</c> or <c>tvshows</c>) with the locked options. The default creates a movie library.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="path">The only path of the library.</param>
    /// <param name="collectionType"><c>movies</c> or <c>tvshows</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the library exists.</returns>
    Task CreateAsync(string name, string path, string collectionType, CancellationToken cancellationToken) => CreateAsync(name, path, cancellationToken);

    /// <summary>
    /// Deletes the items of a library from Jellyfin's database, so no title of it stays listed (as an orphan) until the next scan. Files on disk are not touched.
    /// Called before <see cref="RemoveAsync"/>.
    /// </summary>
    /// <param name="libraryId">The library's folder item id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the items are gone.</returns>
    Task PurgeItemsAsync(Guid libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a library (its stubs on disk are not touched).
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the library is gone.</returns>
    Task RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a folder to an existing library (placement Main, K44). Nothing else about the library changes.
    /// </summary>
    /// <param name="libraryName">The library's name.</param>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the path is added.</returns>
    Task AddPathAsync(string libraryName, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// Removes a folder from a library (placement Main, K44). The library and its other folders stay.
    /// </summary>
    /// <param name="libraryName">The library's name.</param>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the path is removed.</returns>
    Task RemovePathAsync(string libraryName, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// Deletes from Jellyfin's database the items of a library whose path is below <paramref name="path"/> (files untouched), so a folder taken out of a main library
    /// leaves no orphans. Items elsewhere in the library are not touched.
    /// </summary>
    /// <param name="libraryId">The library's folder item id.</param>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the items are gone.</returns>
    Task PurgeItemsBelowAsync(Guid libraryId, string path, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Renames a library. Its folder item id may change; callers find a Shoal library again by its path.
    /// </summary>
    /// <param name="name">The current name.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the library has the new name.</returns>
    Task RenameAsync(string name, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// Puts the locked options of a library Shoal owns back (Pilot 14 LBL3): no metadata saver (NFO), no local metadata saving, no internet metadata or image
    /// fetchers, no realtime monitor. Everything else the admin set stays.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <returns>True when something had to be put back.</returns>
    bool LockOptions(string name) => false;

    /// <summary>
    /// Makes Jellyfin define the folder id of every library (Pilot 15: a library just created or renamed has no id until the top folders are validated, so it is
    /// not listed and cannot be scanned). Only the top folders: no library is scanned.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Jellyfin's accounts: list, and change which libraries an account can use. Nothing else about a policy is ever written.
/// </summary>
public interface IAccountSource
{
    /// <summary>
    /// Lists every account.
    /// </summary>
    /// <returns>The accounts.</returns>
    IReadOnlyList<AccountInfo> List();

    /// <summary>
    /// Sets the library access of one account. It writes <c>EnableAllFolders</c> and <c>EnabledFolders</c> and nothing else; in particular
    /// never <c>BlockedMediaFolders</c> (DESIGN 5.6).
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="enableAllFolders">Whether the account gets "all libraries".</param>
    /// <param name="enabledFolders">The explicit list (empty with "all libraries").</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the policy is saved.</returns>
    Task SetFoldersAsync(Guid userId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the <c>BlockedTags</c> of one account (K45). It writes that list and nothing else.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="blockedTags">The whole list.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the policy is saved.</returns>
    Task SetBlockedTagsAsync(Guid userId, IReadOnlyList<string> blockedTags, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// Takes some tags off one account's <c>BlockedTags</c> and adds others, from the list as it is at the moment of the write (Warden 10 M4: a tag another
    /// writer set in between is kept), then reads the list back.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="remove">The tags to take away.</param>
    /// <param name="add">The tags to add.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The list as read back after the write (the caller checks it held).</returns>
    async Task<IReadOnlyList<string>> ChangeBlockedTagsAsync(Guid userId, IReadOnlyCollection<string> remove, IReadOnlyCollection<string> add, CancellationToken cancellationToken)
    {
        var current = List().FirstOrDefault(a => a.Id == userId)?.BlockedTags ?? throw new InvalidOperationException("The account no longer exists.");
        var next = PolicyGate.Next(current, remove, add);
        if (!next.SequenceEqual(current, StringComparer.Ordinal))
        {
            await SetBlockedTagsAsync(userId, next, cancellationToken).ConfigureAwait(false);
        }

        return List().FirstOrDefault(a => a.Id == userId)?.BlockedTags ?? [];
    }
}

/// <summary>
/// A Jellyfin library (virtual folder), as the visibility rules see it.
/// </summary>
/// <param name="Id">The library's folder item id, the id that appears in <c>EnabledFolders</c>.</param>
/// <param name="Name">The library name.</param>
/// <param name="Locations">The library's paths.</param>
/// <param name="CollectionType">The library's collection type in lower case (<c>movies</c>, <c>tvshows</c>, ...), or empty when core does not say.</param>
/// <param name="SavesNfo">Whether the library's NFO metadata saver is on (Pilot 14: a main library holding Shoal stubs must have it off).</param>
/// <param name="FetchesMetadata">Whether the library fetches internet metadata (Warden 13 L6: in a main library it can rewrite friends' titles).</param>
public sealed record LibraryInfo(Guid Id, string Name, IReadOnlyList<string> Locations, string CollectionType = "", bool SavesNfo = false, bool FetchesMetadata = false);

/// <summary>
/// A Jellyfin account, as the visibility rules see it.
/// </summary>
/// <param name="Id">The user id.</param>
/// <param name="Name">The user name (shown in the picker; never logged with a library id).</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
/// <param name="IsAdministrator">Whether the account is an administrator.</param>
/// <param name="IsRestricted">Whether the account has a parental limit, blocks unrated items, allows only some tags or has an access schedule.</param>
/// <param name="EnableAllFolders">Whether the account has "all libraries".</param>
/// <param name="EnabledFolders">The explicit library list (meaningful when <paramref name="EnableAllFolders"/> is false).</param>
/// <param name="BlockedTags">The account's <c>BlockedTags</c>, or null when the source does not say (K45).</param>
public sealed record AccountInfo(
    Guid Id,
    string Name,
    bool IsDisabled,
    bool IsAdministrator,
    bool IsRestricted,
    bool EnableAllFolders,
    IReadOnlyList<Guid> EnabledFolders,
    IReadOnlyList<string>? BlockedTags = null)
{
    /// <summary>
    /// Whether the account can open a Jellyfin library in core: "all libraries", or the library is on its list.
    /// </summary>
    /// <param name="libraryId">The library's folder id.</param>
    /// <returns>True when it can.</returns>
    public bool CanOpen(Guid libraryId) => EnableAllFolders || EnabledFolders.Contains(libraryId);

    /// <summary>
    /// Describes the account for the access rule <see cref="AccessPolicy.Admitted"/>.
    /// </summary>
    /// <returns>The description.</returns>
    internal CallerUser ToCallerUser() => new(Id, IsDisabled, IsRestricted, true, true, true, false, null);
}

/// <summary>
/// One change a reconcile made (or, for <see cref="VisibilityChangeKind.StoppedManaging"/>, decided).
/// </summary>
/// <param name="UserId">The account.</param>
/// <param name="Kind">What happened.</param>
public sealed record VisibilityChange(Guid UserId, VisibilityChangeKind Kind);

/// <summary>
/// The result of one reconcile.
/// </summary>
/// <param name="Changes">The changes, in account order. Empty when nothing needed doing (a second reconcile always returns this).</param>
/// <param name="Skipped">True when the state is in recovery mode and nothing was evaluated.</param>
public sealed record ReconcileResult(IReadOnlyList<VisibilityChange> Changes, bool Skipped = false)
{
    /// <summary>Gets the number of policy writes the reconcile made.</summary>
    public int Writes => Changes.Count(static c => c.Kind != VisibilityChangeKind.StoppedManaging);
}

/// <summary>
/// An account whose core access to a Shoal library differs from what Shoal's access mode says (DESIGN 5.6 drift list).
/// </summary>
/// <param name="UserId">The account.</param>
/// <param name="LibraryId">The Shoal library.</param>
/// <param name="HasAccess">Whether the account can open the library in core today.</param>
/// <param name="ShouldHaveAccess">Whether Shoal's access mode admits the account.</param>
public sealed record AccessDrift(Guid UserId, Guid LibraryId, bool HasAccess, bool ShouldHaveAccess);

/// <summary>
/// The drift list of the Libraries and access screen.
/// </summary>
/// <param name="Differences">Accounts whose access differs from the rule. Reconcile re-applies the rule at its next run.</param>
/// <param name="Managed">Accounts whose library list Shoal maintains ("Shoal keeps this account's library list so it can hide a library").</param>
/// <param name="StoppedManaging">Accounts Shoal stopped managing because an admin edited them (since this server started).</param>
public sealed record DriftReport(IReadOnlyList<AccessDrift> Differences, IReadOnlyList<Guid> Managed, IReadOnlyList<Guid> StoppedManaging);
