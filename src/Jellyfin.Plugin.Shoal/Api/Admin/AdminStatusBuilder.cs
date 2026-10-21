using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Builds the Overview data from the state, the secret store and whatever the live source knows (docs/ui-spec.md 13). Pure apart
/// from reading its inputs; it returns handles, names and numbers and nothing that could be a credential.
/// </summary>
internal static class AdminStatusBuilder
{
    /// <summary>The order in which two halves of a friend roll up into one status: the first word that either half has wins (DESIGN v2.6.2).</summary>
    private static readonly string[] RollUpOrder = ["AuthFailed", "IdentityMismatch", "TlsKeyChanged", "PairingAborted", "Offline", "Degraded", "Paused", "Online"];

    /// <summary>
    /// Builds the Status response.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="live">What is known about the peers and shares right now.</param>
    /// <param name="homeName">This server's name.</param>
    /// <param name="syncAllUntilUtc">When "Sync now" works again, or null.</param>
    /// <param name="guard">The guard counters.</param>
    /// <returns>The response.</returns>
    public static StatusDto Build(IStateStore state, ISecretStore secrets, IPeerLiveStatus live, string? homeName, DateTime? syncAllUntilUtc, IGuardCounters guard)
    {
        var current = state.Current;
        var recovery = state.Status == StateStatus.Recovery;

        // In recovery mode the state is empty and every peer counts as disabled: there is nothing to list.
        var servers = recovery ? [] : current.Home.Peers.Select(p => Server(p, live.ForPeer(p.PeerKey))).ToList();
        var friends = recovery ? [] : current.PeerRole.Shares.Select(s => Friend(s, live.ForShare(s.ShareId))).ToList();
        return new StatusDto
        {
            Servers = servers,
            Friends = friends,
            FriendLinks = recovery ? [] : Links(current.Home.Peers, servers, current.PeerRole.Shares, friends),
            Cooldowns = new CooldownsDto(syncAllUntilUtc),
            State = Banner(state),
            SecretsUnreadable = secrets.Status == SecretStoreStatus.Unreadable,
            KeySource = KeySourceName(secrets.Info.KeySource),
            SecretsReason = secrets.Status == SecretStoreStatus.Unreadable ? SecretsReasonName(secrets.Info.Reason) : null,
            HomeName = homeName,
            InviteFailures = new InviteFailuresDto(guard.InviteFailuresLastHour, guard.InviteFailuresLastHour >= 5)
        };
    }

    /// <summary>
    /// Builds one server card.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="info">What is known about it.</param>
    /// <returns>The card.</returns>
    public static ServerDto Server(PeerState peer, PeerLiveInfo info)
    {
        // A server the admin paused is Disabled whatever the monitor says. A server nobody has measured yet is shown as offline
        // without a time ("not measured"), never as online.
        var status = info.Status is "Pairing" or "PairingAborted" ? info.Status : !peer.Enabled ? "Disabled" : info.Status ?? "Offline";
        return new ServerDto
        {
            Handle = peer.PeerKey,
            DisplayName = peer.DisplayName,
            Url = peer.BaseUrl,
            Network = peer.Network,
            Proxy = !string.IsNullOrEmpty(peer.ProxyUrl),
            Status = status,
            PairingMethod = peer.Pairing is null ? null : peer.Pairing.Method == PairingMethod.Invite ? "invite" : "qc",
            NeedsRepair = peer.Pairing?.Method == PairingMethod.Qc,
            RepairBlockedByHttp = peer.Pairing?.Method == PairingMethod.Qc && peer.Network != PeerNetwork.Tailnet && peer.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            PairingError = info.PairingError,
            PairingPhase = info.PairingPhase,
            PairingStoppedUtc = info.PairingStoppedUtc,
            Path = info.Path ?? (string.IsNullOrEmpty(peer.ProxyUrl) ? "unknown" : "proxy"),
            DerpRegion = info.DerpRegion,
            RttMs = info.RttMs,
            Mbps = info.Mbps,
            JitterMs = info.JitterMs,
            LossPct = info.LossPct,
            OfflineSinceUtc = info.OfflineSinceUtc,
            NextCheckUtc = info.NextCheckUtc,
            Syncing = info.Syncing,
            LastSync = SyncInfoDto.From(info.LastSync),
            TitleCount = info.TitleCount,
            HiddenByDedup = info.HiddenByDedup,
            DedupWinner = info.DedupWinner,
            AlertCount = info.AlertCount,
            HeldRemovals = info.HeldRemovals,
            HeldAdditions = info.HeldAdditions,
            ActiveStreams = new StreamsDto(info.ActiveStreams, peer.MaxConcurrentStreams),
            VerdictSummary = [.. info.VerdictSummary.Select(static v => new VerdictDto(v.Cls, v.Verdict))]
        };
    }

    /// <summary>
    /// Links the servers this server watches to the friends who watch it, by the first 12 characters of the server id (DESIGN v2.6.2). The link is for display only.
    /// A prefix that more than one record has links nothing: a friend who lied about the id must not pull two records together.
    /// </summary>
    /// <param name="peers">The peer records.</param>
    /// <param name="servers">The cards of the peers, in the same order.</param>
    /// <param name="shares">The share records.</param>
    /// <param name="friends">The friend rows of the shares, in the same order.</param>
    /// <returns>One entry per friend: linked ones, then servers alone, then friends alone.</returns>
    public static IReadOnlyList<FriendLinkDto> Links(IReadOnlyList<PeerState> peers, IReadOnlyList<ServerDto> servers, IReadOnlyList<ShareState> shares, IReadOnlyList<FriendDto> friends)
    {
        var peerPrefixes = peers.Select(FriendLinks.PeerPrefix).ToList();
        var sharePrefixes = shares.Select(FriendLinks.SharePrefix).ToList();
        var used = new HashSet<int>();
        var result = new List<FriendLinkDto>();
        for (var p = 0; p < peers.Count; p++)
        {
            var prefix = peerPrefixes[p];
            var s = prefix is not null && peerPrefixes.Count(x => x == prefix) == 1 && sharePrefixes.Count(x => x == prefix) == 1 ? sharePrefixes.IndexOf(prefix) : -1;
            if (s >= 0)
            {
                used.Add(s);
            }

            result.Add(Entry(peers[p], servers[p], s >= 0 ? shares[s].ShareId : null, s >= 0 ? friends[s] : null));
        }

        for (var s = 0; s < shares.Count; s++)
        {
            if (!used.Contains(s))
            {
                result.Add(Entry(null, null, shares[s].ShareId, friends[s]));
            }
        }

        return result;
    }

    /// <summary>
    /// The worse of two status words.
    /// </summary>
    /// <param name="a">One half's word, or null.</param>
    /// <param name="b">The other half's word, or null.</param>
    /// <returns>The word that comes first in the roll-up order.</returns>
    public static string RollUp(string? a, string? b)
    {
        var ia = a is null ? int.MaxValue : Array.IndexOf(RollUpOrder, a);
        var ib = b is null ? int.MaxValue : Array.IndexOf(RollUpOrder, b);
        if (ia < 0)
        {
            ia = Array.IndexOf(RollUpOrder, "Degraded"); // a word the order does not know is never better than Degraded
        }

        if (ib < 0)
        {
            ib = Array.IndexOf(RollUpOrder, "Degraded");
        }

        var worst = Math.Min(ia, ib);
        return worst == int.MaxValue ? "Online" : RollUpOrder[worst];
    }

    /// <summary>
    /// Builds one friend row.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <param name="info">What is known about it.</param>
    /// <returns>The row.</returns>
    public static FriendDto Friend(ShareState share, ShareLiveInfo info)
    {
        var status = info.AccessRemoved ? "AccessRemoved" : share.Enabled ? "Active" : "Paused";
        return new FriendDto(
            share.ShareId,
            share.FriendName,
            status,
            share.LibraryIds.Count,
            info.DeviceCount > 0 ? info.DeviceCount : share.ExpectedDeviceIds.Count,
            info.UnexpectedDeviceCount,
            info.LastSeenUtc,
            info.PlayingNow,
            info.TranscodingNow);
    }

    private static FriendLinkDto Entry(PeerState? peer, ServerDto? server, string? shareId, FriendDto? friend)
    {
        var watch = server is null ? null : new WatchSideDto(server.Handle, server.Status, server.TitleCount, server.ActiveStreams.N);
        var share = friend is null ? null : new ShareSideDto(friend.Handle, friend.Status, friend.LibraryCount, friend.DeviceCount, friend.UnexpectedDeviceCount, friend.PlayingNow, friend.TranscodingNow, friend.LastSeenUtc);
        var handle = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((peer?.PeerKey ?? string.Empty) + "|" + (shareId ?? string.Empty))))[..16];
        var status = RollUp(watch is null ? null : WatchWord(watch.Status), share is null ? null : ShareWord(share.Status));

        // 2.9.3 K6: the name the admin reads is the alias (else the friend's name) without links; an empty result falls back to the friend's own name.
        var name = peer is null ? string.Empty : Sync.MetadataSanitizer.FriendName(peer.ShownName());
        return new FriendLinkDto(handle, name.Length > 0 ? name : friend?.Name ?? string.Empty, status, watch is not null && share is not null, watch, share);
    }

    // A server card's word as the roll-up knows it: Disabled is Paused, a pairing that has not finished is Degraded.
    private static string WatchWord(string status) => status switch { "Disabled" => "Paused", "Pairing" => "Degraded", _ => status };

    // A share's word: a friend whose access was removed can no longer connect, which is the same as AuthFailed.
    private static string ShareWord(string status) => status switch { "Active" => "Online", "AccessRemoved" => "AuthFailed", _ => "Paused" };

    private static StateBannerDto Banner(IStateStore state)
    {
        var load = state.LoadInfo;
        if (state.Status == StateStatus.Recovery)
        {
            return new StateBannerDto("Recovery", load.Recovery == RecoveryReason.NewerSchema ? "newerSchema" : "unreadable", null);
        }

        RestoredFromDto? restored = null;
        if (load.RestoredFrom is not null)
        {
            var written = load.Candidates.FirstOrDefault(c => string.Equals(c.FileName, load.RestoredFrom, StringComparison.Ordinal))?.WrittenUtc ?? state.Current.WrittenUtc;
            restored = new RestoredFromDto(load.RestoredFrom, written, load.AccessReviewRequired);
        }

        return new StateBannerDto("Normal", null, restored);
    }

    private static string? KeySourceName(KeySource source) => source switch
    {
        KeySource.Environment => "env",
        KeySource.Dpapi => "dpapi",
        KeySource.EnvironmentFile => "envFile",
        KeySource.KeyFile => "file",
        _ => null
    };

    private static string SecretsReasonName(SecretUnreadableReason reason) => reason switch
    {
        SecretUnreadableReason.BadKey => "badKey",
        SecretUnreadableReason.AmbiguousKey => "bothKeys",
        _ => "decryptFailed"
    };
}
