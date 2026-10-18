using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Diagnostics;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>What a limit change did.</summary>
public enum QualityChangeOutcome
{
    /// <summary>Saved.</summary>
    Saved,

    /// <summary>The connection is unknown.</summary>
    NotFound,

    /// <summary>The body is not usable.</summary>
    Invalid,

    /// <summary>The stored limit is not the one the admin saw.</summary>
    Changed,

    /// <summary>The card the admin dismissed is not the one shown now.</summary>
    Stale,
}

/// <summary>
/// What the admin page reads and changes about quality limits and the play order (PLAN v2.9 K51-K53): the limit of each connection, the speed-test
/// recommendation and its dismissal, "Which server plays first", the backup counts for the overlap panel and the streams playing now. No peer ids, no
/// tokens, no throughput of a dismissal and no admin ids leave it.
/// </summary>
public sealed class QualityAdmin
{
    /// <summary>The cost line under a limit; <c>{Server}</c> is the server's shown name.</summary>
    public const string CostTemplate = "Titles above this quality will be converted by {Server}. That uses their computer's power.";

    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly Func<string, IReadOnlyList<SpeedSample>> _history;
    private readonly IConnectionRanking _ranking;
    private readonly TimeProvider _time;
    private readonly GroupBook? _groups;
    private readonly GrantStore? _grants;
    private readonly ISlotLedger? _slots;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityAdmin"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs (the sizes the recommendation reads).</param>
    /// <param name="history">Reads the speed samples of a connection, oldest first.</param>
    /// <param name="ranking">The play order.</param>
    /// <param name="time">The clock.</param>
    /// <param name="groups">The title groups (backup counts), or null.</param>
    /// <param name="grants">The grants (playing now), or null.</param>
    /// <param name="slots">Which grants hold a stream (playing now), or null.</param>
    public QualityAdmin(IStateStore state, CatalogStore catalogs, Func<string, IReadOnlyList<SpeedSample>> history, IConnectionRanking ranking, TimeProvider time, GroupBook? groups = null, GrantStore? grants = null, ISlotLedger? slots = null)
    {
        _state = state;
        _catalogs = catalogs;
        _history = history;
        _ranking = ranking;
        _time = time;
        _groups = groups;
        _grants = grants;
        _slots = slots;
    }

    /// <summary>
    /// The quality panel of one connection.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>The panel, or null when the connection is unknown.</returns>
    public PeerQualityDto? Panel(string peerKey)
    {
        var peer = Find(peerKey);
        if (peer is null)
        {
            return null;
        }

        var card = Recommend(peer);
        return new PeerQualityDto(
            QualityLimitDto.From(peer.QualityLimit),
            Effective(peer.QualityLimit),
            CostText(peer),
            card is null ? null : RecommendationDto.From(card, peer.ShownName()),
            DismissedRecommendationDto.From(peer.Dismissed),
            _ranking.Explain(peer.PeerKey) is { } why ? RankDto.From(why) : null);
    }

    /// <summary>
    /// The recommendation of a connection now.
    /// </summary>
    /// <param name="peer">The connection.</param>
    /// <returns>The card, or null.</returns>
    public QualityRecommendation? Recommend(PeerState peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var samples = _history(peer.PeerKey);
        var t = VerdictEngine.Throughput(samples);
        var latest = samples.LastOrDefault(static s => s.Kind != SpeedKinds.Playback && s.Error is null && (s.JitterMs is not null || s.LossPct is not null));
        var unstable = VerdictEngine.Unstable(latest?.JitterMs, latest?.LossPct);
        return VerdictEngine.Recommend(peer.QualityLimit, t, unstable, Titles(peer.PeerKey), peer.Dismissed, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Changes the limit of a connection, only when the stored one is still what the admin saw.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="request">The body.</param>
    /// <param name="adminId">The admin.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<QualityChange> SetLimitAsync(string peerKey, QualityLimitRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Find(peerKey) is null)
        {
            return new QualityChange(QualityChangeOutcome.NotFound, null, null);
        }

        if (!request.TryBuild(adminId, _time.GetUtcNow().UtcDateTime, out var limit, out var problem))
        {
            return new QualityChange(QualityChangeOutcome.Invalid, null, problem);
        }

        var outcome = QualityChangeOutcome.NotFound;
        QualityLimit? now = null;
        await _state.UpdateAsync(
            s =>
            {
                var target = s.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal));
                if (target is null)
                {
                    return;
                }

                // Checked inside the write, so two admins cannot both pass the check.
                if (!request.ExpectedCurrent!.Matches(target.QualityLimit))
                {
                    outcome = QualityChangeOutcome.Changed;
                    now = target.QualityLimit;
                    return;
                }

                target.QualityLimit = limit;
                outcome = QualityChangeOutcome.Saved;
                now = limit;
            },
            cancellationToken).ConfigureAwait(false);
        return new QualityChange(outcome, now is null ? null : QualityLimitDto.From(now), null);
    }

    /// <summary>
    /// Sets the priority tier of a connection (Always prefer / Normal / Backup only), only when the stored one is still what the admin saw, then ranks again.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="request">The body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome, the tier stored after it (the new one, or the one there now when it changed) and why the connection holds its place.</returns>
    public async Task<(QualityChangeOutcome Outcome, PriorityTier? Tier, RankDto? Rank)> SetTierAsync(string peerKey, PriorityTierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Find(peerKey) is null)
        {
            return (QualityChangeOutcome.NotFound, null, null);
        }

        if (request.Tier is not { } tier || !Enum.IsDefined(tier) || request.ExpectedCurrent is not { } expected || !Enum.IsDefined(expected))
        {
            return (QualityChangeOutcome.Invalid, null, null);
        }

        var outcome = QualityChangeOutcome.NotFound;
        PriorityTier? now = null;
        await _state.UpdateAsync(
            s =>
            {
                if (s.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)) is not { } target)
                {
                    return;
                }

                now = target.PriorityTier;
                if (target.PriorityTier != expected)
                {
                    outcome = QualityChangeOutcome.Changed;
                    return;
                }

                target.PriorityTier = tier;
                now = tier;
                outcome = QualityChangeOutcome.Saved;
            },
            cancellationToken).ConfigureAwait(false);
        if (outcome == QualityChangeOutcome.Saved)
        {
            await _ranking.RecomputeAsync(cancellationToken).ConfigureAwait(false); // GET PlayOrder shows the new order at once
        }

        return (outcome, now, _ranking.Explain(peerKey) is { } why ? RankDto.From(why) : null);
    }

    /// <summary>
    /// Dismisses the recommendation the page shows. The class the card suggested must be the one suggested now; the throughput now is stored with it (for the
    /// "falls more than 30 percent" rule) and never sent back.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="suggested">The class on the card.</param>
    /// <param name="adminId">The admin.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>NotFound, Invalid (no class), Stale (no card now, or another class) or Saved.</returns>
    public async Task<QualityChangeOutcome> DismissAsync(string peerKey, QualityClass? suggested, Guid adminId, CancellationToken cancellationToken)
    {
        var peer = Find(peerKey);
        if (peer is null)
        {
            return QualityChangeOutcome.NotFound;
        }

        if (suggested is not { } cls || !Enum.IsDefined(cls) || cls == QualityClass.Off)
        {
            return QualityChangeOutcome.Invalid;
        }

        if (Recommend(peer) is not { } card || card.SuggestedClass != cls)
        {
            return QualityChangeOutcome.Stale;
        }

        var dismissal = new DismissedRecommendation { SuggestedClass = cls, ThroughputMbps = card.ThroughputMbps, DismissedUtc = _time.GetUtcNow().UtcDateTime, DismissedByUserId = adminId };
        var saved = false;
        await _state.UpdateAsync(
            s =>
            {
                if (s.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)) is { } target)
                {
                    target.Dismissed.Add(dismissal);
                    if (target.Dismissed.Count > 20)
                    {
                        target.Dismissed = [.. target.Dismissed.OrderByDescending(static d => d.DismissedUtc).Take(20)];
                    }

                    saved = true;
                }
            },
            cancellationToken).ConfigureAwait(false);
        return saved ? QualityChangeOutcome.Saved : QualityChangeOutcome.NotFound;
    }

    /// <summary>
    /// "Which server plays first": every connection in the play order with its tier, place, reason and effective limit.
    /// </summary>
    /// <returns>The rows, first first.</returns>
    public IReadOnlyList<PlayOrderRowDto> PlayOrder()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return [];
        }

        var peers = _state.Current.Home.Peers.ToDictionary(static p => p.PeerKey, StringComparer.Ordinal);
        var order = _ranking.Order().Where(peers.ContainsKey).ToList();
        order.AddRange(peers.Keys.Where(k => !order.Contains(k, StringComparer.Ordinal)));
        return [.. order.Select((key, index) =>
        {
            var peer = peers[key];
            var why = _ranking.Explain(key);
            return new PlayOrderRowDto(key, peer.ShownName(), peer.PriorityTier, index + 1, order.Count, why?.Reason, why?.Label, Effective(peer.QualityLimit));
        })];
    }

    /// <summary>
    /// The backup counts of one connection for the overlap panel: how many of its titles other friends also have, how many of those it plays first (by
    /// tier and play order), and for how many it is a hidden backup.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <returns>The counts, or null when there are no groups.</returns>
    public BackupCountsDto? Backups(string peerKey)
    {
        if (_groups is null || _state.Status != StateStatus.Ready)
        {
            return null;
        }

        var tiers = _state.Current.Home.Peers.ToDictionary(static p => p.PeerKey, static p => p.PriorityTier, StringComparer.Ordinal);
        var rank = _ranking.Order();
        int shared = 0, first = 0;
        foreach (var group in _groups.All())
        {
            if (!group.Members.Any(m => string.Equals(m.PeerKey, peerKey, StringComparison.Ordinal)))
            {
                continue;
            }

            shared++;
            var members = group.Members.Select(static m => m.PeerKey).Where(tiers.ContainsKey).ToList();
            var ordered = rank.Where(members.Contains).Concat(members.Where(m => !rank.Contains(m, StringComparer.Ordinal))).ToList();
            if (VerdictEngine.OrderForTitle(ordered, k => tiers[k], static _ => 0) is [var top, ..] && string.Equals(top, peerKey, StringComparison.Ordinal))
            {
                first++;
            }
        }

        return new BackupCountsDto(shared, first, shared - first);
    }

    /// <summary>
    /// The streams playing now from each friend's server (the Playing now block): one row per running stream.
    /// </summary>
    /// <returns>The friends with at least one stream, in play order.</returns>
    public IReadOnlyList<PlayingFriendDto> PlayingNow()
    {
        if (_grants is null || _slots is null || _state.Status != StateStatus.Ready)
        {
            return [];
        }

        var peers = _state.Current.Home.Peers.ToDictionary(static p => p.PeerKey, StringComparer.Ordinal);
        var playing = _grants.All().Where(g => _slots.HoldsSlot(g) && peers.ContainsKey(g.PeerKey)).ToList();
        var rank = _ranking.Order();
        return [.. playing.GroupBy(static g => g.PeerKey, StringComparer.Ordinal)
            .OrderBy(g => rank.Contains(g.Key, StringComparer.Ordinal) ? rank.ToList().IndexOf(g.Key) : int.MaxValue)
            .Select(g => new PlayingFriendDto(g.Key, peers[g.Key].ShownName(), [.. g.OrderBy(static s => s.CreatedUtc).Select(StreamOf)]))];
    }

    /// <summary>The effective limit of a stored one.</summary>
    /// <param name="limit">The limit.</param>
    /// <returns>The DTO.</returns>
    public static EffectiveLimitDto Effective(QualityLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        var cap = QualityCap.From(limit);
        return new EffectiveLimitDto(limit.MaxClass, limit.MaxMbps is { } m ? Math.Clamp(m, QualityLimit.MinMbps, QualityLimit.MaxMbpsCap) : null, cap is { MaxWidth: > 0 } ? cap.MaxWidth : null, cap is { MaxHeight: > 0 } ? cap.MaxHeight : null, cap is not null, QualityCap.Describe(limit));
    }

    private static PlayingStreamDto StreamOf(Grant grant)
    {
        var cls = VerdictEngine.ClassOf(grant.SourceWidth, grant.SourceHeight);
        return new PlayingStreamDto(
            grant.LocalItemId.ToString("N"),
            grant.Title,
            grant.LocalUserId.ToString("N"),
            grant.Method == PlayMethod.Transcode ? "converted" : "direct",
            cls == QualityClass.Off ? null : QualityCap.ClassLabel(cls),
            grant.SourceBitrate > 0 ? Math.Round(grant.SourceBitrate / 1_000_000.0, 1) : null,
            grant.HeldByLimit,
            grant.Cap is { } cap ? CapText(cap) : null,
            grant.CreatedUtc);
    }

    private static string CapText(QualityCap cap)
    {
        var cls = cap.MaxWidth > 0 ? VerdictEngine.ClassOf(cap.MaxWidth, cap.MaxHeight) : QualityClass.Off;
        return QualityCap.Describe(new QualityLimit { MaxClass = cls, MaxMbps = cap.MaxBitrate > 0 ? cap.MaxBitrate / 1_000_000 : null });
    }

    private static string? CostText(PeerState peer) =>
        QualityCap.From(peer.QualityLimit) is null ? null : CostTemplate.Replace("{Server}", peer.ShownName(), StringComparison.Ordinal);

    private PeerState? Find(string peerKey) =>
        _state.Status == StateStatus.Ready ? _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal)) : null;

    // The friend's titles that can play from it: listed (or held back from removal) and the hidden copies of groups. Home's own copies never play from a friend.
    private List<TitleSize> Titles(string peerKey)
    {
        var load = _catalogs.Load(peerKey);
        if (load.Unreadable)
        {
            return [];
        }

        return [.. load.Document.Entries
            .Where(static e => e.Kind is EntryKind.Movie or EntryKind.Episode && e.State is EntryState.Active or EntryState.HeldRemoval or EntryState.DuplicatePeer)
            .Select(static e => new TitleSize(e.Width, e.Height, e.Bitrate))];
    }
}

/// <summary>The result of a limit change.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Limit">The stored limit after it (the new one, or the one that is there now when it changed).</param>
/// <param name="Problem">The fixed error text of an unusable body.</param>
public sealed record QualityChange(QualityChangeOutcome Outcome, QualityLimitDto? Limit, string? Problem);
