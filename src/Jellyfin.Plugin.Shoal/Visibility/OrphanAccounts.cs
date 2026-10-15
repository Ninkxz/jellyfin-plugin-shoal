using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Finds sharing accounts that no share manages (Warden 08 M7): after "start over" or a restore of an older state, the accounts Shoal made
/// for friends are still in Jellyfin but the state no longer lists them. They are only reported; an admin decides to delete them.
/// </summary>
internal static class OrphanAccounts
{
    /// <summary>The start of every name Shoal gives a sharing account.</summary>
    public const string Prefix = "shoal-";

    /// <summary>
    /// Lists the accounts named <c>shoal-*</c> that no share in the state uses. An administrator account is never listed, and nothing is
    /// listed while the state is not trusted (recovery mode would make every account look orphaned).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">Every account.</param>
    /// <returns>The orphans.</returns>
    public static IReadOnlyList<AccountInfo> Find(IStateStore state, IReadOnlyList<AccountInfo> accounts)
    {
        if (state.Status != StateStatus.Ready)
        {
            return [];
        }

        var managed = VisibilityService.ShareUserIds(state.Current);
        return [.. accounts.Where(a => !a.IsAdministrator && a.Name.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase) && !managed.Contains(a.Id))];
    }
}
