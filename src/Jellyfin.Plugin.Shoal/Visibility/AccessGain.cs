using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Who gains access when a connection's titles go into a library (Warden 10 M1, owner decision 2026-10-18: NOT blocked, only reported). A library set to
/// Everyone keeps including newly added friends and newly created users; the Preview, the move job and the add-friend wizard name the accounts that can open the
/// titles afterwards but could not before, and which of them are restricted profiles.
/// </summary>
public static class AccessGain
{
    /// <summary>
    /// The accounts the target admits that no source admits today. Sharing accounts are never counted (they are never admitted).
    /// </summary>
    /// <param name="accounts">The accounts.</param>
    /// <param name="target">The target library's access.</param>
    /// <param name="sources">The access of the libraries the titles are in now (none for a new connection).</param>
    /// <param name="shareUsers">The sharing accounts.</param>
    /// <returns>The gaining accounts.</returns>
    public static IReadOnlyList<AccountInfo> Gaining(IEnumerable<AccountInfo> accounts, LibraryAccess target, IReadOnlyList<LibraryAccess> sources, IReadOnlySet<Guid> shareUsers)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sources);
        return [.. accounts.Where(a =>
        {
            var user = a.ToCallerUser();
            return AccessPolicy.Admitted(user, target, shareUsers) && !sources.Any(s => AccessPolicy.Admitted(user, s, shareUsers));
        })];
    }
}

/// <summary>
/// The gain report of a change: who can open the titles afterwards and could not before, and which of them are restricted profiles (a parental limit, an
/// unrated block, an allowed-tags list or a schedule). Informational only.
/// </summary>
/// <param name="Users">The gaining accounts.</param>
/// <param name="RestrictedUsers">Those of them that are restricted profiles.</param>
public sealed record AccessGainReport(IReadOnlyList<Guid> Users, IReadOnlyList<Guid> RestrictedUsers)
{
    /// <summary>Gets an empty report.</summary>
    public static AccessGainReport None { get; } = new([], []);

    /// <summary>
    /// The report of a list of gaining accounts.
    /// </summary>
    /// <param name="gaining">The accounts.</param>
    /// <returns>The report.</returns>
    public static AccessGainReport Of(IEnumerable<AccountInfo> gaining)
    {
        ArgumentNullException.ThrowIfNull(gaining);
        var list = gaining.DistinctBy(static a => a.Id).ToList();
        return new AccessGainReport([.. list.Select(static a => a.Id)], [.. list.Where(static a => a.IsRestricted).Select(static a => a.Id)]);
    }
}
