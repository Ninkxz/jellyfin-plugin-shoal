using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Picks the server a play of a grouped title goes to (PLAN v2.9 K42): resolved at PlaybackInfo time, per play; the grant holds the choice, so a running
/// stream never switches.
/// </summary>
public interface IGroupPlayback
{
    /// <summary>
    /// The servers to try for a play, best first: up now, trusted, and only those whose library the caller may open. Null when the title has no group
    /// of two or more servers (a title on one server plays exactly as before).
    /// </summary>
    /// <param name="stubPeerKey">The server the stub belongs to (from its path).</param>
    /// <param name="stubEntry">The stub's catalog entry.</param>
    /// <param name="user">The caller.</param>
    /// <returns>The candidates (possibly empty: then the play goes to the stub's own server as before), or null.</returns>
    IReadOnlyList<MemberChoice>? Candidates(string stubPeerKey, CatalogEntry stubEntry, CallerUser user);

    /// <summary>
    /// The servers to try for a play of an extra copy of a grouped title (K44/K45): only members whose library the caller may open, the owner included; the
    /// member whose library holds the copy was checked with the item itself. An empty list refuses the play (never the owner by default).
    /// </summary>
    /// <param name="stubPeerKey">The owner of the title's metadata.</param>
    /// <param name="stubEntry">The owner's catalog entry.</param>
    /// <param name="user">The caller.</param>
    /// <param name="copyLibKey">The library the extra copy is in.</param>
    /// <returns>The candidates, best first; empty when none may play.</returns>
    IReadOnlyList<MemberChoice> CandidatesForCopy(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string copyLibKey) => [];

    /// <summary>
    /// The choice a grant made, for the byte routes that follow it (a static stream must stay on the server PlaybackInfo picked).
    /// </summary>
    /// <param name="stubPeerKey">The server the stub belongs to.</param>
    /// <param name="stubEntry">The stub's catalog entry.</param>
    /// <param name="grantPeerKey">The server of the grant.</param>
    /// <param name="grantPeerItemId">The item id of the grant.</param>
    /// <returns>The member, or null when the grant's server is not a member of the title's group.</returns>
    MemberChoice? Member(string stubPeerKey, CatalogEntry stubEntry, string grantPeerKey, Guid grantPeerItemId);

    /// <summary>
    /// Whether the caller may still play from this member (Warden 10 L2): the same library rule as <see cref="Candidates"/>, without the up-or-down part. A grant's
    /// member is checked again with it on the byte routes, so a user who lost access to that member's library stops at once.
    /// </summary>
    /// <param name="stubPeerKey">The server the stub belongs to.</param>
    /// <param name="stubEntry">The stub's catalog entry.</param>
    /// <param name="member">The member.</param>
    /// <param name="user">The caller.</param>
    /// <param name="copyLibKey">The library of the extra copy played, or null for the owner's stub.</param>
    /// <returns>True when the caller may play from it.</returns>
    bool Admits(string stubPeerKey, CatalogEntry stubEntry, MemberChoice member, CallerUser user, string? copyLibKey) => true;

    /// <summary>
    /// Every peer id of every member of a grouped title, for the canary check.
    /// </summary>
    /// <param name="stubEntry">The stub's catalog entry.</param>
    /// <returns>The ids; empty when the title has no group.</returns>
    IReadOnlyList<Guid> PeerIds(CatalogEntry stubEntry);
}

/// <summary>
/// One server a play of a grouped title may go to.
/// </summary>
/// <param name="PeerKey">The server.</param>
/// <param name="Entry">Its catalog entry for the title (the stub's own entry for the owner, the hidden member's entry otherwise).</param>
/// <param name="PeerItemId">The item id on that server.</param>
/// <param name="PeerMediaSourceId">The media source played on that server (its primary version).</param>
/// <param name="IsOwner">Whether this is the server whose stub the title is.</param>
public sealed record MemberChoice(string PeerKey, CatalogEntry Entry, Guid PeerItemId, Guid PeerMediaSourceId, bool IsOwner);

/// <summary>
/// The <see cref="IGroupPlayback"/> of the plugin.
/// </summary>
public sealed class GroupPlayback : IGroupPlayback
{
    private readonly GroupBook _groups;
    private readonly StubCatalog _catalog;
    private readonly IStateStore _state;
    private readonly PeerRegistry _registry;
    private readonly IConnectionRanking _ranking;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupPlayback"/> class.
    /// </summary>
    /// <param name="groups">The title groups.</param>
    /// <param name="catalog">The catalogs.</param>
    /// <param name="state">The state store.</param>
    /// <param name="registry">The peer statuses.</param>
    /// <param name="ranking">The connection order.</param>
    public GroupPlayback(GroupBook groups, StubCatalog catalog, IStateStore state, PeerRegistry registry, IConnectionRanking ranking)
    {
        _groups = groups;
        _catalog = catalog;
        _state = state;
        _registry = registry;
        _ranking = ranking;
    }

    /// <inheritdoc />
    public IReadOnlyList<MemberChoice>? Candidates(string stubPeerKey, CatalogEntry stubEntry, CallerUser user) => Pick(stubPeerKey, stubEntry, user, null);

    /// <inheritdoc />
    public IReadOnlyList<MemberChoice> CandidatesForCopy(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string copyLibKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(copyLibKey);
        return Pick(stubPeerKey, stubEntry, user, copyLibKey) ?? [];
    }

    /// <inheritdoc />
    public MemberChoice? Member(string stubPeerKey, CatalogEntry stubEntry, string grantPeerKey, Guid grantPeerItemId)
    {
        ArgumentNullException.ThrowIfNull(stubEntry);
        if (Group(stubPeerKey, stubEntry) is not { } group)
        {
            return null;
        }

        if (stubEntry.Kind == EntryKind.Episode)
        {
            // K43: a series member is the server; the grant names its episode.
            return group.Members.Where(m => m.PeerKey == grantPeerKey).Select(m => Choice(stubPeerKey, stubEntry, m)).FirstOrDefault(c => c is not null && c.PeerItemId == grantPeerItemId);
        }

        var member = group.Members.FirstOrDefault(m => m.PeerKey == grantPeerKey && m.PeerItemId == grantPeerItemId.ToString("N"));
        return member is null ? null : Choice(stubPeerKey, stubEntry, member);
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> PeerIds(CatalogEntry stubEntry)
    {
        ArgumentNullException.ThrowIfNull(stubEntry);
        if (stubEntry.Kind == EntryKind.Episode)
        {
            // K43: the ids of each member's hidden source of this episode (the series ids are not in a PlaybackInfo answer).
            if (_groups.ByCopy(stubEntry.ParentKey) is not { Kind: GroupKind.Series } series)
            {
                return [];
            }

            return [.. series.Members.Where(m => m.PeerKey != series.MetaOwner).Select(m => SameEpisode(stubEntry, m)).OfType<CatalogEntry>()
                .SelectMany(static e => new[] { e.PeerItemId, e.PeerMediaSourceId }).Select(static s => Guid.TryParseExact(s, "N", out var id) ? id : Guid.Empty).Where(static g => g != Guid.Empty).Distinct()];
        }

        if (_groups.ByCopy(stubEntry.StubKey) is not { } group)
        {
            return [];
        }

        return [.. group.Members.SelectMany(static m => m.Sources.Append(m.PeerItemId)).Select(static s => Guid.TryParseExact(s, "N", out var id) ? id : Guid.Empty).Where(static g => g != Guid.Empty).Distinct()];
    }

    /// <inheritdoc />
    public bool Admits(string stubPeerKey, CatalogEntry stubEntry, MemberChoice member, CallerUser user, string? copyLibKey)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(user);
        if (_state.Status != StateStatus.Ready || Group(stubPeerKey, stubEntry) is null)
        {
            return false;
        }

        var state = _state.Current;
        return state.Home.Peers.FirstOrDefault(p => p.PeerKey == member.PeerKey) is { Enabled: true } peer && AdmitsMember(state, VisibilityService.ShareUserIds(state), peer, member, user, copyLibKey);
    }

    private IReadOnlyList<MemberChoice>? Pick(string stubPeerKey, CatalogEntry stubEntry, CallerUser user, string? copyLibKey)
    {
        ArgumentNullException.ThrowIfNull(stubEntry);
        ArgumentNullException.ThrowIfNull(user);
        if (Group(stubPeerKey, stubEntry) is not { } group || _state.Status != StateStatus.Ready)
        {
            return null;
        }

        var state = _state.Current;
        var shareUsers = VisibilityService.ShareUserIds(state);
        var choices = new List<(MemberChoice Choice, PeerState Peer)>();
        foreach (var member in group.Members)
        {
            if (state.Home.Peers.FirstOrDefault(p => p.PeerKey == member.PeerKey) is not { Enabled: true } peer
                || !CatalogSyncService.IsReachable(_registry.StatusOf(member.PeerKey))
                || Choice(stubPeerKey, stubEntry, member) is not { } choice)
            {
                continue; // a play skips a server that is down at once
            }

            if (!AdmitsMember(state, shareUsers, peer, choice, user, copyLibKey))
            {
                continue;
            }

            choices.Add((choice, peer));
        }

        var rank = _ranking.Order();
        var known = choices.ToDictionary(static c => c.Choice.PeerKey, StringComparer.Ordinal);
        var order = rank.Where(known.ContainsKey).Concat(known.Keys.Where(k => !rank.Contains(k, StringComparer.Ordinal))).ToList();
        var ordered = VerdictEngine.OrderForTitle(
            order,
            key => known[key].Peer.PriorityTier,
            key => VerdictEngine.FitRank(known[key].Peer.QualityLimit, known[key].Choice.Entry.Width, known[key].Choice.Entry.Height, known[key].Choice.Entry.Bitrate, _ranking.Throughput(key)));
        return [.. ordered.Select(k => known[k].Choice)];
    }

    // Failover never crosses a library the caller cannot open. The library checked with the item itself is exempt: the owner's own stub, or for an extra copy the
    // copy's library (then the owner is checked like any member).
    private static bool AdmitsMember(ShoalState state, IReadOnlySet<Guid> shareUsers, PeerState peer, MemberChoice choice, CallerUser user, string? copyLibKey)
    {
        var mapping = peer.Libraries.FirstOrDefault(l => l.MappingKey == choice.Entry.MappingKey);
        var exempt = copyLibKey is null ? choice.IsOwner : mapping is not null && LibraryFolders.FedBy(state, peer.PeerKey, mapping.CollectionType)?.LibKey == copyLibKey;
        return exempt || (mapping is not null && AccessPolicy.Admitted(user, LibraryFolders.AccessOfMapping(state, peer.PeerKey, mapping), shareUsers));
    }

    // A movie by its own stub; an episode (K43) by its series stub, the copy of a series group. Only the owner's stubs are grouped (a member's own extra episodes are single).
    private TitleGroup? Group(string stubPeerKey, CatalogEntry stubEntry) => stubEntry.Kind switch
    {
        EntryKind.Movie => _groups.ByCopy(stubEntry.StubKey) is { Kind: GroupKind.Movie } group && group.MetaOwner == stubPeerKey && GroupPlanner.HidesExtras(group) ? group : null,
        EntryKind.Episode => _groups.ByCopy(stubEntry.ParentKey) is { Kind: GroupKind.Series } series && series.MetaOwner == stubPeerKey && series.Members.Count >= 2 ? series : null,
        _ => null,
    };

    private MemberChoice? Choice(string stubPeerKey, CatalogEntry stubEntry, GroupMember member)
    {
        var owner = member.PeerKey == stubPeerKey;
        var entry = owner ? stubEntry : stubEntry.Kind == EntryKind.Episode ? SameEpisode(stubEntry, member) : _catalog.FindMovie(member.PeerKey, member.PeerItemId);
        if (entry is null || (!owner && entry.State != EntryState.DuplicatePeer) || !Guid.TryParseExact(entry.PeerItemId, "N", out var item))
        {
            return null; // the member's catalog no longer has it as a hidden copy: the next sync puts the group right
        }

        var source = Guid.TryParseExact(entry.PeerMediaSourceId, "N", out var parsed) ? parsed : item;
        return new MemberChoice(member.PeerKey, entry, item, source, owner);
    }

    // K43: the member's hidden source of the same episode: same season, same first and last number (a two-part file never stands in for one part).
    private CatalogEntry? SameEpisode(CatalogEntry stubEntry, GroupMember member)
    {
        if (stubEntry.Season is not { } season || stubEntry.Episode is not { } number)
        {
            return null;
        }

        var hidden = _catalog.FindHiddenEpisode(member.PeerKey, member.PeerItemId, season, number, stubEntry.EpisodeEnd);
        return hidden is not null && (hidden.EpisodeEnd ?? number) == (stubEntry.EpisodeEnd ?? number) ? hidden : null;
    }
}
