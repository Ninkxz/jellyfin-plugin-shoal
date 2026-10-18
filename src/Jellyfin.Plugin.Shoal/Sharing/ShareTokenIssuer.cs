using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// The one sequence that gives a sharing account a fresh token (DESIGN 4.1b step 3 and v2.6.1): sign out every device (that kills the old token),
/// record the device that may sign in, re-assert the account's locked policy, and mint a token for that device through core's
/// <c>AuthenticateDirect</c>. Approving an invite and rotating a token both call it, so they cannot drift apart.
/// </summary>
internal static class ShareTokenIssuer
{
    /// <summary>
    /// Issues a token. The caller holds whatever lock keeps two issues for one share from running together.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts, devices and sessions.</param>
    /// <param name="shareId">The share.</param>
    /// <param name="deviceId">The device id the token is minted for (Home's).</param>
    /// <param name="deviceName">The device name shown in the dashboard.</param>
    /// <param name="replaceExpectedDevices">True at approval (the device list becomes this one device); false at rotation (the list stays).</param>
    /// <param name="homeServerIdPrefix">The first 12 hex characters of Home's server id to remember (display only), or null to keep what is stored.</param>
    /// <param name="mintAttempts">How many times minting is tried (1 at approval, 2 at rotation).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The access token (the caller hands it on once and keeps it nowhere), or null when the share was paused while the policy was applied: nothing is minted then (Warden 10 M2).</returns>
    public static async Task<string?> IssueAsync(IStateStore state, IShareUserSource users, string shareId, string deviceId, string deviceName, bool replaceExpectedDevices, string? homeServerIdPrefix, int mintAttempts, CancellationToken cancellationToken)
    {
        var share = state.Current.PeerRole.Shares.First(x => x.ShareId == shareId);
        foreach (var device in users.Devices(share.ShareUserId))
        {
            await users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, cancellationToken).ConfigureAwait(false);
        }

        await state.UpdateAsync(
            s =>
            {
                var target = s.PeerRole.Shares.First(x => x.ShareId == shareId);
                if (replaceExpectedDevices)
                {
                    target.ExpectedDeviceIds = [deviceId];
                }

                if (homeServerIdPrefix is not null)
                {
                    target.HomeServerIdPrefix = homeServerIdPrefix;
                }
            },
            cancellationToken).ConfigureAwait(false);
        var current = state.Current.PeerRole.Shares.First(x => x.ShareId == shareId);
        await users.ApplyPolicyAsync(current.ShareUserId, ShareService.PolicyOf(current, state.Current), cancellationToken).ConfigureAwait(false);
        if (!state.Current.PeerRole.Shares.First(x => x.ShareId == shareId).Enabled)
        {
            return null; // a pause landed after the share was read: it wins, and no token is minted for a paused share
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await users.MintTokenAsync(current.ShareUserId, deviceId, deviceName, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The one retry is part of the rotation rule (DESIGN v2.6.1 step 3); the second failure is rethrown.
            catch (Exception) when (attempt < mintAttempts && !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                // try again once
            }
        }
    }
}
