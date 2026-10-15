using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// The link rule of two-way sharing (DESIGN v2.6.2): a friend who is both a server this server watches and a friend who watches this server is found by the first
/// 12 hex characters of the other server's id. The link is for display only; no authorization decision reads it, and a friend who lies about the id in the invite
/// can at worst produce a wrong "also watches you" panel.
/// </summary>
public static partial class FriendLinks
{
    /// <summary>The length of the id prefix that links the two records.</summary>
    public const int PrefixLength = 12;

    /// <summary>
    /// The prefix of the server a peer record points to.
    /// </summary>
    /// <param name="peer">The peer record.</param>
    /// <returns>12 lower-case hex characters, or null when the peer has no pinned id yet.</returns>
    public static string? PeerPrefix(PeerState peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.PinnedServerId is { Length: >= PrefixLength } id && PrefixRegex().IsMatch(id[..PrefixLength].ToLowerInvariant()) ? id[..PrefixLength].ToLowerInvariant() : null;
    }

    /// <summary>
    /// The prefix of the server a share's friend runs: stored at the approval, or for an older share taken from its first expected device id
    /// (<c>shoal-&lt;12 hex&gt;-&lt;8 hex&gt;</c>).
    /// </summary>
    /// <param name="share">The share record.</param>
    /// <returns>12 lower-case hex characters, or null when the share has none.</returns>
    public static string? SharePrefix(ShareState share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (PrefixRegex().IsMatch(share.HomeServerIdPrefix ?? string.Empty))
        {
            return share.HomeServerIdPrefix;
        }

        foreach (var device in share.ExpectedDeviceIds)
        {
            if (DeviceRegex().Match(device ?? string.Empty) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a server id belongs to a friend who already watches this server.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="serverId">A normalized server id (lower-case hex).</param>
    /// <returns>True when a share has that prefix.</returns>
    public static bool AlsoWatchesYou(ShoalState state, string serverId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (serverId is not { Length: >= PrefixLength })
        {
            return false;
        }

        var prefix = serverId[..PrefixLength].ToLowerInvariant();
        return state.PeerRole.Shares.Exists(s => string.Equals(SharePrefix(s), prefix, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\Ashoal-([0-9a-f]{12})-[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceRegex();
}
