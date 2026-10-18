using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Diagnostics;

/// <summary>
/// What the automatic ranking knows about one connection at one evaluation (PLAN v2.9 K54).
/// </summary>
/// <param name="PeerKey">The connection.</param>
/// <param name="Tier">The tier the admin chose.</param>
/// <param name="Online">Whether the server answers now.</param>
/// <param name="Trusted">Whether its identity, key and token are accepted (not stopped by a failed auth, a changed id or a changed key, not paused).</param>
/// <param name="Unstable">Whether the last test found jitter over 30 ms or loss over 2 percent.</param>
/// <param name="Throughput">T, the median of the last three speed tests, in Mbps, or null when never measured.</param>
/// <param name="StatusSinceUtc">Since when the current online or offline status holds, or null when not known yet (nothing is demoted or promoted then).</param>
/// <param name="PairingOrder">The hidden tiebreak: the old priority number, then the pairing time, then the key, as a position (0 first).</param>
/// <param name="State">The ranking state stored with the connection (it is not changed; the result carries new copies).</param>
public sealed record RankInput(string PeerKey, PriorityTier Tier, bool Online, bool Trusted, bool Unstable, double? Throughput, DateTimeOffset? StatusSinceUtc, int PairingOrder, AutoRank State);

/// <summary>
/// The result of one evaluation.
/// </summary>
/// <param name="Order">The connections, best first.</param>
/// <param name="States">The new ranking state of each connection.</param>
/// <param name="Changed">Whether the order or any state differs from what was stored.</param>
public sealed record RankOutcome(IReadOnlyList<string> Order, IReadOnlyDictionary<string, AutoRank> States, bool Changed);

/// <summary>
/// Why a connection holds its place in the play order.
/// </summary>
/// <param name="Position">Its place, 1 first.</param>
/// <param name="Count">How many connections there are.</param>
/// <param name="Tier">The tier the admin chose.</param>
/// <param name="Reason">The reason in plain words (fastest link, slower link, offline, unstable link, not measured yet, ...).</param>
/// <param name="Label">The whole line: "Automatic: 2nd of 3 (slower link)".</param>
public sealed record RankExplanation(int Position, int Count, PriorityTier Tier, string Reason, string Label);

/// <summary>
/// The automatic ranking of connections with hysteresis (PLAN v2.9 K54). Pure: the clock is a parameter.
/// </summary>
public static partial class VerdictEngine
{
    /// <summary>How much faster a challenger must be: 25 percent.</summary>
    public const double ChallengeMargin = 1.25;

    /// <summary>How far apart the two winning evaluations of a challenger must be.</summary>
    public static readonly TimeSpan ChallengeSpacing = TimeSpan.FromHours(12);

    /// <summary>How long a connection may be offline before it is demoted at once.</summary>
    public static readonly TimeSpan DemoteAfterOffline = TimeSpan.FromMinutes(30);

    /// <summary>How long a demoted connection must be back and online before it is promoted again.</summary>
    public static readonly TimeSpan PromoteAfterOnline = TimeSpan.FromHours(2);

    /// <summary>
    /// Ranks the connections. The order within a tier is: up (online and trusted, not demoted), then not unstable, then T, then the pairing order. A tier
    /// is never crossed: Prefer before Normal before Backup (a Backup server plays only when nothing above it can, because a play skips servers that are
    /// down). Changes by speed alone have hysteresis: a challenger must beat the one above it by <see cref="ChallengeMargin"/> on two evaluations at least
    /// <see cref="ChallengeSpacing"/> apart, and any evaluation it does not win starts it over. Offline for more than <see cref="DemoteAfterOffline"/>
    /// demotes at once; back, it is promoted after <see cref="PromoteAfterOnline"/> online without a break. The first evaluation (nothing ranked yet)
    /// has no hysteresis; a connection added later starts at the end of its group.
    /// </summary>
    /// <param name="inputs">The connections.</param>
    /// <param name="now">The time of the evaluation (UTC).</param>
    /// <returns>The order and the new states.</returns>
    public static RankOutcome RankConnections(IReadOnlyList<RankInput> inputs, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var states = inputs.ToDictionary(static i => i.PeerKey, static i => Copy(i.State), StringComparer.Ordinal);
        var byKey = inputs.ToDictionary(static i => i.PeerKey, StringComparer.Ordinal);

        // Demotion and promotion by how long the status has held.
        foreach (var input in inputs)
        {
            var state = states[input.PeerKey];
            var up = input.Online && input.Trusted;
            var held = input.StatusSinceUtc is { } since ? now - since.UtcDateTime : (TimeSpan?)null;
            if (!up && state.DemotedUtc is null && held >= DemoteAfterOffline)
            {
                state.DemotedUtc = now;
            }
            else if (up && state.DemotedUtc is not null && held >= PromoteAfterOnline)
            {
                state.DemotedUtc = null;
            }
        }

        var firstEvaluation = inputs.All(static i => i.State.Since is null);
        List<RankInput> order;
        if (firstEvaluation)
        {
            order = [.. inputs.OrderBy(i => Bucket(i, states)).ThenByDescending(static i => i.Throughput ?? 0).ThenBy(static i => i.PairingOrder)];
        }
        else
        {
            // Start from the stored order (new connections last, by pairing order), then move whole groups by their structural key at once (stable sort).
            var previous = inputs.OrderBy(static i => i.State.Since is null ? 1 : 0).ThenBy(static i => i.State.Since is null ? 0 : i.State.Position).ThenBy(static i => i.PairingOrder).ToList();
            order = [.. previous.OrderBy(i => Bucket(i, states))];
            Challenge(order, states, now);
        }

        var changed = false;
        for (var position = 0; position < order.Count; position++)
        {
            var input = order[position];
            var state = states[input.PeerKey];
            if (state.Since is null || state.Position != position)
            {
                state.Since = now;
            }

            state.Position = position;
            state.Score = input.Throughput is { } t && double.IsFinite(t) ? Math.Round(Math.Max(0, t), 3) : 0;
            changed |= !Same(state, byKey[input.PeerKey].State);
        }

        return new RankOutcome([.. order.Select(static i => i.PeerKey)], states, changed);
    }

    /// <summary>
    /// The member order for one title (PLAN v2.9 K54, K52): within each tier, a copy that fits the connection's quality limit (and has T of at least 1.5 times
    /// its bitrate) comes first; the rest keep their place. A fit never moves a copy into another tier.
    /// </summary>
    /// <param name="order">The connections, best first (from <see cref="RankConnections"/>).</param>
    /// <param name="tierOf">The tier of a connection.</param>
    /// <param name="fits">Whether the copy on a connection fits.</param>
    /// <returns>The order for the title.</returns>
    public static IReadOnlyList<string> OrderForTitle(IReadOnlyList<string> order, Func<string, PriorityTier> tierOf, Func<string, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(fits);
        return OrderForTitle(order, tierOf, key => fits(key) ? 0 : 2);
    }

    /// <summary>
    /// The member order for one title by a fit rank (PLAN v2.9 K52): within each tier, lower ranks first (see <see cref="FitRank"/>); equal ranks keep the
    /// connection order. A tier is never crossed.
    /// </summary>
    /// <param name="order">The connections, best first.</param>
    /// <param name="tierOf">The tier of a connection.</param>
    /// <param name="fitRank">The fit rank of the copy on a connection (0 best).</param>
    /// <returns>The order for the title.</returns>
    public static IReadOnlyList<string> OrderForTitle(IReadOnlyList<string> order, Func<string, PriorityTier> tierOf, Func<string, int> fitRank)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(tierOf);
        ArgumentNullException.ThrowIfNull(fitRank);
        return [.. order.Select(static (key, index) => (key, index)).OrderBy(x => TierRank(tierOf(x.key))).ThenBy(x => fitRank(x.key)).ThenBy(static x => x.index).Select(static x => x.key)];
    }

    /// <summary>
    /// How well a copy fits its connection (PLAN v2.9 K52): 0 when it fits the quality limit and T is at least <see cref="Headroom"/> times its bitrate
    /// (<see cref="FitsTitle"/>); 1 when it is inside the limit's box and bitrate but the link is not known to carry it (it still plays without a
    /// conversion forced by the limit); 2 when it is over the limit and would be converted.
    /// </summary>
    /// <param name="limit">The connection's quality limit.</param>
    /// <param name="width">The copy's width, or 0.</param>
    /// <param name="height">The copy's height, or 0.</param>
    /// <param name="bitrate">The copy's bitrate in bits per second, or 0.</param>
    /// <param name="throughputMbps">T of the connection, or null.</param>
    /// <returns>0, 1 or 2.</returns>
    public static int FitRank(QualityLimit limit, int width, int height, int bitrate, double? throughputMbps)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (FitsTitle(limit, width, height, bitrate, throughputMbps))
        {
            return 0;
        }

        var insideBox = QualityLimit.Fits(limit.MaxClass, width, height);
        var insideRate = limit.MaxMbps is not { } cap || (bitrate > 0 && bitrate / 1_000_000.0 <= cap);
        return insideBox && insideRate ? 1 : 2;
    }

    /// <summary>
    /// Whether a copy fits for the title order (<see cref="FitRank"/> 0): inside the quality limit and with T of at least <see cref="Headroom"/> times its bitrate.
    /// </summary>
    /// <param name="limit">The connection's quality limit.</param>
    /// <param name="width">The copy's width, or 0 when not known.</param>
    /// <param name="height">The copy's height, or 0 when not known.</param>
    /// <param name="bitrate">The copy's bitrate in bits per second, or 0 when not known.</param>
    /// <param name="throughputMbps">T of the connection, or null when never measured.</param>
    /// <returns>True when the copy fits; an unknown T or bitrate never fits.</returns>
    public static bool FitsTitle(QualityLimit limit, int width, int height, int bitrate, double? throughputMbps)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (throughputMbps is not { } t || bitrate <= 0 || !QualityLimit.Fits(limit.MaxClass, width, height))
        {
            return false;
        }

        var mbps = bitrate / 1_000_000.0;
        return (limit.MaxMbps is not { } cap || mbps <= cap) && t >= Headroom * mbps;
    }

    /// <summary>
    /// Why each connection holds its place (PLAN v2.9 K53, for the "Which server plays first" view): "Automatic: 2nd of 3 (slower link)". The prefix is
    /// the tier (Always prefer, Automatic, Backup only), the position counts every connection, and the reason is plain words.
    /// </summary>
    /// <param name="outcome">The evaluation.</param>
    /// <param name="inputs">What it was computed from.</param>
    /// <returns>The explanation per connection.</returns>
    public static IReadOnlyDictionary<string, RankExplanation> ExplainRanks(RankOutcome outcome, IReadOnlyList<RankInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(inputs);
        var byKey = inputs.ToDictionary(static i => i.PeerKey, StringComparer.Ordinal);
        var states = outcome.States.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);
        var result = new Dictionary<string, RankExplanation>(StringComparer.Ordinal);
        var count = outcome.Order.Count;
        for (var i = 0; i < count; i++)
        {
            var input = byKey[outcome.Order[i]];
            var state = states.GetValueOrDefault(input.PeerKey) ?? new AutoRank();
            string reason;
            if (!input.Trusted)
            {
                reason = "needs attention";
            }
            else if (!input.Online)
            {
                reason = "offline";
            }
            else if (state.DemotedUtc is not null)
            {
                reason = "back online, waiting until it stays up";
            }
            else if (input.Unstable)
            {
                reason = "unstable link";
            }
            else if (input.Throughput is not { } t)
            {
                reason = "not measured yet";
            }
            else
            {
                var bucket = Bucket(input, states);
                var above = outcome.Order.Take(i).Select(k => byKey[k]).Where(o => o.Online && o.Trusted && Bucket(o, states) == bucket).ToList();
                reason = state.PendingChallenger is not null ? "a faster server is being checked"
                    : above.Count == 0 ? "fastest link"
                    : above.Any(o => (o.Throughput ?? 0) < t) ? "faster, waiting to confirm"
                    : "slower link";
            }

            var prefix = input.Tier switch { PriorityTier.Prefer => "Always prefer", PriorityTier.Backup => "Backup only", _ => "Automatic" };
            result[input.PeerKey] = new RankExplanation(i + 1, count, input.Tier, reason, $"{prefix}: {Ordinal(i + 1)} of {count} ({reason})");
        }

        return result;
    }

    private static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }

    private static int TierRank(PriorityTier tier) => tier switch
    {
        PriorityTier.Prefer => 0,
        PriorityTier.Normal => 1,
        _ => 2,
    };

    // The structural key: tier, then up, then stable. These move at once; only speed has hysteresis.
    private static (int Tier, int Down, int Unstable) Bucket(RankInput input, Dictionary<string, AutoRank> states) =>
        (TierRank(input.Tier), states[input.PeerKey].DemotedUtc is not null ? 1 : 0, input.Unstable ? 1 : 0);

    private static void Challenge(List<RankInput> order, Dictionary<string, AutoRank> states, DateTime now)
    {
        var swapped = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            var incumbent = order[i];
            var state = states[incumbent.PeerKey];
            var below = i + 1 < order.Count && Bucket(order[i + 1], states) == Bucket(incumbent, states) ? order[i + 1] : null;
            if (below is null || swapped.Contains(incumbent.PeerKey))
            {
                ClearChallenge(state);
                continue;
            }

            var mine = incumbent.Throughput ?? 0;
            var theirs = below.Throughput ?? 0;
            var beats = theirs > 0 && theirs >= ChallengeMargin * mine;
            if (!beats)
            {
                ClearChallenge(state);
                continue;
            }

            if (!string.Equals(state.PendingChallenger, below.PeerKey, StringComparison.Ordinal) || state.ChallengeUtc is null)
            {
                state.PendingChallenger = below.PeerKey;
                state.Streak = 1;
                state.ChallengeUtc = now;
                continue;
            }

            if (now - state.ChallengeUtc.Value < ChallengeSpacing)
            {
                continue; // a win, but too soon after the first to count as the second
            }

            // The second win, at least 12 hours after the first: the challenger takes the place.
            ClearChallenge(state);
            ClearChallenge(states[below.PeerKey]);
            order[i] = below;
            order[i + 1] = incumbent;
            swapped.Add(incumbent.PeerKey);
            swapped.Add(below.PeerKey);
        }
    }

    private static void ClearChallenge(AutoRank state)
    {
        state.PendingChallenger = null;
        state.Streak = 0;
        state.ChallengeUtc = null;
    }

    private static AutoRank Copy(AutoRank? state) => state is null ? new AutoRank() : new AutoRank
    {
        Score = state.Score,
        Since = state.Since,
        PendingChallenger = state.PendingChallenger,
        Streak = state.Streak,
        Position = state.Position,
        ChallengeUtc = state.ChallengeUtc,
        DemotedUtc = state.DemotedUtc,
    };

    private static bool Same(AutoRank a, AutoRank? b) => b is not null
        && a.Score.Equals(b.Score) && a.Since == b.Since && string.Equals(a.PendingChallenger, b.PendingChallenger, StringComparison.Ordinal)
        && a.Streak == b.Streak && a.Position == b.Position && a.ChallengeUtc == b.ChallengeUtc && a.DemotedUtc == b.DemotedUtc;
}
