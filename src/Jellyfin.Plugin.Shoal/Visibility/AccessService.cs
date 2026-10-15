using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Why a change of a library's access was refused.
/// </summary>
public enum AccessError
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>The state is in recovery mode: nothing can be changed.</summary>
    RecoveryMode,

    /// <summary>No such peer or library mapping.</summary>
    UnknownLibrary,

    /// <summary>The selection contains a Shoal share user (DESIGN 5.6, W2 N-2): answers 400.</summary>
    ShareUser,

    /// <summary>The selection contains an account that does not exist.</summary>
    UnknownUser,

    /// <summary>
    /// A restricted profile would be admitted (selected, or "include restricted" turned on) without the acknowledgement
    /// "Ratings come from the friend's server and are not verified" (DESIGN 5.6, F-MD-06).
    /// </summary>
    AcknowledgementRequired,
}

/// <summary>
/// A requested change of one library's access. A null member leaves that part as it is.
/// </summary>
/// <param name="Mode">The new mode, or null.</param>
/// <param name="SelectedUserIds">The new selection, or null to keep the stored one (switching to Everyone and back keeps it, owner D4).</param>
/// <param name="IncludeRestricted">Whether restricted profiles are admitted in Everyone mode, or null.</param>
/// <param name="RatingsAcknowledged">Whether the admin confirmed that ratings come from the friend's server and are not verified.</param>
public sealed record AccessChange(AccessMode? Mode, IReadOnlyList<Guid>? SelectedUserIds, bool? IncludeRestricted, bool RatingsAcknowledged);

/// <summary>
/// Who gains and who loses access to a library through a change: the list the confirm dialog shows.
/// </summary>
/// <param name="Gaining">Accounts that are not admitted today and will be.</param>
/// <param name="Losing">Accounts that are admitted today and will not be.</param>
public sealed record AccessImpact(IReadOnlyList<Guid> Gaining, IReadOnlyList<Guid> Losing);

/// <summary>
/// The result of <see cref="AccessService.SetAsync"/> and <see cref="AccessService.Preview"/>.
/// </summary>
/// <param name="Error">Why the change was refused, or <see cref="AccessError.None"/>.</param>
/// <param name="Impact">Who gains and loses access (set when the change is valid).</param>
public sealed record AccessChangeResult(AccessError Error, AccessImpact? Impact)
{
    /// <summary>Gets a value indicating whether the change is valid (and, for <see cref="AccessService.SetAsync"/>, was applied).</summary>
    public bool Ok => Error == AccessError.None;
}

/// <summary>
/// Changes who may use a Shoal library (the <c>PUT Shoal/Admin/Libraries/{m}/Access</c> rules of DESIGN 5.6) and reconciles afterwards.
/// </summary>
public sealed class AccessService
{
    private readonly IStateStore _state;
    private readonly IAccountSource _accounts;
    private readonly VisibilityService _visibility;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="visibility">The visibility service (reconciles after a change).</param>
    /// <param name="time">The clock.</param>
    public AccessService(IStateStore state, IAccountSource accounts, VisibilityService visibility, TimeProvider time)
    {
        _state = state;
        _accounts = accounts;
        _visibility = visibility;
        _time = time;
    }

    /// <summary>
    /// Checks a change and says who would gain and lose access, without changing anything.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="mappingKey">The library mapping.</param>
    /// <param name="change">The change.</param>
    /// <returns>The verdict and the impact.</returns>
    public AccessChangeResult Preview(string peerKey, string mappingKey, AccessChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Evaluate(peerKey, mappingKey, change).Result;
    }

    /// <summary>
    /// Applies a change: validates it, stores it and reconciles.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="mappingKey">The library mapping.</param>
    /// <param name="change">The change.</param>
    /// <param name="changedByUserId">The admin who changed it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict; nothing is written unless it is OK.</returns>
    public async Task<AccessChangeResult> SetAsync(string peerKey, string mappingKey, AccessChange change, Guid changedByUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (result, next) = Evaluate(peerKey, mappingKey, change);
        if (!result.Ok || next is null)
        {
            return result;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _state.UpdateAsync(
            s =>
            {
                var access = s.Home.Peers.First(p => p.PeerKey == peerKey).Libraries.First(l => l.MappingKey == mappingKey).Access;
                access.Mode = next.Mode;
                access.SelectedUserIds = [.. next.SelectedUserIds];
                access.IncludeRestricted = next.IncludeRestricted;
                access.ChangedUtc = now;
                access.ChangedByUserId = changedByUserId;
            },
            cancellationToken).ConfigureAwait(false);
        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private (AccessChangeResult Result, LibraryAccess? Next) Evaluate(string peerKey, string mappingKey, AccessChange change)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return (new AccessChangeResult(AccessError.RecoveryMode, null), null);
        }

        var state = _state.Current;
        var mapping = state.Home.Peers.FirstOrDefault(p => p.PeerKey == peerKey)?.Libraries.FirstOrDefault(l => l.MappingKey == mappingKey);
        if (mapping is null)
        {
            return (new AccessChangeResult(AccessError.UnknownLibrary, null), null);
        }

        var before = mapping.Access;
        var accounts = _accounts.List().ToDictionary(static a => a.Id);
        var shareUsers = VisibilityService.ShareUserIds(state);
        var next = new LibraryAccess
        {
            Mode = change.Mode ?? before.Mode,
            SelectedUserIds = [.. (change.SelectedUserIds ?? before.SelectedUserIds).Distinct()],
            IncludeRestricted = change.IncludeRestricted ?? before.IncludeRestricted,
            ChangedUtc = before.ChangedUtc,
            ChangedByUserId = before.ChangedByUserId,
        };

        // Only a new selection is checked; the stored one stays as it is when the caller leaves it out.
        if (change.SelectedUserIds is not null)
        {
            foreach (var id in next.SelectedUserIds)
            {
                if (shareUsers.Contains(id))
                {
                    return (new AccessChangeResult(AccessError.ShareUser, null), null);
                }

                if (!accounts.ContainsKey(id))
                {
                    return (new AccessChangeResult(AccessError.UnknownUser, null), null);
                }
            }
        }

        var newlyRestricted = next.SelectedUserIds.Any(id => !before.SelectedUserIds.Contains(id) && accounts.TryGetValue(id, out var a) && a.IsRestricted);
        var restrictedOn = next.IncludeRestricted && !before.IncludeRestricted;
        if ((newlyRestricted || restrictedOn) && !change.RatingsAcknowledged)
        {
            return (new AccessChangeResult(AccessError.AcknowledgementRequired, null), null);
        }

        var gaining = new List<Guid>();
        var losing = new List<Guid>();
        foreach (var account in accounts.Values)
        {
            var user = account.ToCallerUser();
            var was = AccessPolicy.Admitted(user, before, shareUsers);
            var will = AccessPolicy.Admitted(user, next, shareUsers);
            if (!was && will)
            {
                gaining.Add(account.Id);
            }
            else if (was && !will)
            {
                losing.Add(account.Id);
            }
        }

        return (new AccessChangeResult(AccessError.None, new AccessImpact(gaining, losing)), next);
    }
}
