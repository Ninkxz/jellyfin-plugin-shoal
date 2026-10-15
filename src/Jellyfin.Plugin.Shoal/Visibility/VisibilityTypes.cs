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
}

/// <summary>
/// A Jellyfin library (virtual folder), as the visibility rules see it.
/// </summary>
/// <param name="Id">The library's folder item id, the id that appears in <c>EnabledFolders</c>.</param>
/// <param name="Name">The library name.</param>
/// <param name="Locations">The library's paths.</param>
public sealed record LibraryInfo(Guid Id, string Name, IReadOnlyList<string> Locations);

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
public sealed record AccountInfo(
    Guid Id,
    string Name,
    bool IsDisabled,
    bool IsAdministrator,
    bool IsRestricted,
    bool EnableAllFolders,
    IReadOnlyList<Guid> EnabledFolders)
{
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
