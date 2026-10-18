using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

// Wire shapes of the quality limit (PLAN v2.9 K50). The routes that use them (PUT Shoal/Admin/Peers/{peerKey}/QualityLimit with expectedCurrent,
// POST .../Recommendations/Dismiss) come with K51/K53 and require elevation. Class names on the wire are Off, 480, 720, 1080 and 2160.

/// <summary>The quality limit of one connection as the server detail page shows it.</summary>
/// <param name="MaxClass">Off, 480, 720, 1080 or 2160.</param>
/// <param name="MaxMbps">The bitrate limit in Mbps, or null.</param>
/// <param name="SetUtc">When it was last set, or null.</param>
/// <param name="Origin">Manual or Recommendation.</param>
public sealed record QualityLimitDto(QualityClass MaxClass, int? MaxMbps, DateTime? SetUtc, QualityLimitOrigin Origin)
{
    /// <summary>
    /// The DTO of a stored limit. The admin who set it is not sent (a user id stays on the server).
    /// </summary>
    /// <param name="limit">The stored limit.</param>
    /// <returns>The DTO.</returns>
    public static QualityLimitDto From(QualityLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new QualityLimitDto(limit.MaxClass, limit.MaxMbps, limit.SetUtc, limit.Origin);
    }
}

/// <summary>The limit the admin last saw, sent back with a change so a concurrent edit is refused instead of overwritten.</summary>
public sealed class QualityLimitExpected
{
    /// <summary>Gets the class the admin saw.</summary>
    public QualityClass? MaxClass { get; init; }

    /// <summary>Gets the Mbps the admin saw (null for none).</summary>
    public int? MaxMbps { get; init; }

    /// <summary>
    /// Whether the stored limit is still the one the admin saw.
    /// </summary>
    /// <param name="current">The stored limit.</param>
    /// <returns>True when class and Mbps match.</returns>
    public bool Matches(QualityLimit current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return MaxClass is { } cls && cls == current.MaxClass && MaxMbps == current.MaxMbps;
    }
}

/// <summary>The body of <c>PUT Shoal/Admin/Peers/{peerKey}/QualityLimit</c>.</summary>
public sealed class QualityLimitRequest
{
    /// <summary>Gets the new class.</summary>
    public QualityClass? MaxClass { get; init; }

    /// <summary>Gets the new bitrate limit in Mbps, or null for none.</summary>
    public int? MaxMbps { get; init; }

    /// <summary>Gets a value indicating whether this applies the speed-test recommendation (origin Recommendation) rather than a manual choice.</summary>
    public bool FromRecommendation { get; init; }

    /// <summary>Gets the limit the admin last saw; required.</summary>
    public QualityLimitExpected? ExpectedCurrent { get; init; }

    /// <summary>
    /// Checks the body and builds the limit to store. The Mbps is clamped to 1..1000, never refused, so a typed 5000 becomes 1000.
    /// </summary>
    /// <param name="byUserId">The admin making the change.</param>
    /// <param name="now">The time of the change (UTC).</param>
    /// <param name="limit">The limit to store.</param>
    /// <param name="problem">A fixed error text when the body is not usable.</param>
    /// <returns>True when the body is usable.</returns>
    public bool TryBuild(Guid byUserId, DateTime now, out QualityLimit limit, out string? problem)
    {
        limit = new QualityLimit();
        if (MaxClass is not { } cls || !Enum.IsDefined(cls))
        {
            problem = "Choose a quality limit.";
            return false;
        }

        if (ExpectedCurrent?.MaxClass is null)
        {
            problem = "The current limit is missing. Reload the page and try again.";
            return false;
        }

        limit = new QualityLimit
        {
            MaxClass = cls,
            MaxMbps = MaxMbps is { } mbps ? Math.Clamp(mbps, QualityLimit.MinMbps, QualityLimit.MaxMbpsCap) : null,
            SetUtc = now,
            SetByUserId = byUserId,
            Origin = FromRecommendation ? QualityLimitOrigin.Recommendation : QualityLimitOrigin.Manual,
        };
        problem = null;
        return true;
    }
}

/// <summary>The body of <c>POST Shoal/Admin/Peers/{peerKey}/Recommendations/Dismiss</c>.</summary>
public sealed class DismissRecommendationRequest
{
    /// <summary>Gets the class the card suggested.</summary>
    public QualityClass? SuggestedClass { get; init; }
}

/// <summary>A dismissed recommendation as the page shows it.</summary>
/// <param name="SuggestedClass">The class that was suggested.</param>
/// <param name="DismissedUtc">When it was dismissed.</param>
public sealed record DismissedRecommendationDto(QualityClass SuggestedClass, DateTime DismissedUtc)
{
    /// <summary>
    /// The DTOs of a connection's dismissals, newest first. Throughput and user ids stay on the server.
    /// </summary>
    /// <param name="dismissed">The stored dismissals.</param>
    /// <returns>The DTOs.</returns>
    public static IReadOnlyList<DismissedRecommendationDto> From(IEnumerable<DismissedRecommendation> dismissed)
    {
        ArgumentNullException.ThrowIfNull(dismissed);
        return [.. dismissed.OrderByDescending(static d => d.DismissedUtc).Select(static d => new DismissedRecommendationDto(d.SuggestedClass, d.DismissedUtc))];
    }
}

/// <summary>The quality panel of one connection (server detail page, PLAN v2.9 U-Q1/U-Q2).</summary>
/// <param name="Limit">The stored limit.</param>
/// <param name="Effective">What playback enforces.</param>
/// <param name="CostText">"Titles above this quality will be converted by {Server}. That uses their computer's power.", or null with no limit. Set as text only.</param>
/// <param name="Recommendation">The speed-test card, or null when none shows.</param>
/// <param name="Dismissed">The dismissals, newest first.</param>
/// <param name="Rank">Why the connection holds its place in the play order, or null before the first evaluation.</param>
public sealed record PeerQualityDto(QualityLimitDto Limit, EffectiveLimitDto Effective, string? CostText, RecommendationDto? Recommendation, IReadOnlyList<DismissedRecommendationDto> Dismissed, RankDto? Rank);

/// <summary>The limit as playback enforces it.</summary>
/// <param name="MaxClass">Off, 480, 720, 1080 or 2160.</param>
/// <param name="MaxMbps">The bitrate limit (clamped 1..1000), or null.</param>
/// <param name="MaxWidth">The box width, or null for no box.</param>
/// <param name="MaxHeight">The box height, or null for no box.</param>
/// <param name="Limited">Whether anything is limited.</param>
/// <param name="Text">"No limit", "Up to 1080p", "Up to 8 Mbps" or "Up to 720p, 5 Mbps".</param>
public sealed record EffectiveLimitDto(QualityClass MaxClass, int? MaxMbps, int? MaxWidth, int? MaxHeight, bool Limited, string Text);

/// <summary>The speed-test recommendation card.</summary>
/// <param name="SuggestedClass">The limit to apply (PUT with fromRecommendation true).</param>
/// <param name="SlowForClass">The class the link is slow for.</param>
/// <param name="ThroughputMbps">T now (rounded to 0.1).</param>
/// <param name="TitlesAbove">The friend's titles above the suggested class.</param>
/// <param name="TitlesKnown">The friend's titles with a known size.</param>
/// <param name="Text">"This link is slow for 4K. Limit {Server} to 1080p?" (set as text only).</param>
public sealed record RecommendationDto(QualityClass SuggestedClass, QualityClass SlowForClass, double ThroughputMbps, int TitlesAbove, int TitlesKnown, string Text)
{
    /// <summary>The DTO of a card.</summary>
    /// <param name="card">The card.</param>
    /// <param name="serverName">The server's shown name.</param>
    /// <returns>The DTO.</returns>
    public static RecommendationDto From(Jellyfin.Plugin.Shoal.Diagnostics.QualityRecommendation card, string serverName)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new RecommendationDto(card.SuggestedClass, card.SlowForClass, card.ThroughputMbps, card.TitlesAbove, card.TitlesKnown, card.Text(serverName));
    }
}

/// <summary>Why a connection holds its place.</summary>
/// <param name="Position">Its place, 1 first.</param>
/// <param name="Count">How many connections.</param>
/// <param name="Tier">Prefer, Normal or Backup.</param>
/// <param name="Reason">Plain words.</param>
/// <param name="Label">"Automatic: 2nd of 3 (slower link)".</param>
public sealed record RankDto(int Position, int Count, PriorityTier Tier, string Reason, string Label)
{
    /// <summary>The DTO of an explanation.</summary>
    /// <param name="why">The explanation.</param>
    /// <returns>The DTO.</returns>
    public static RankDto From(Jellyfin.Plugin.Shoal.Diagnostics.RankExplanation why)
    {
        ArgumentNullException.ThrowIfNull(why);
        return new RankDto(why.Position, why.Count, why.Tier, why.Reason, why.Label);
    }
}

/// <summary>One row of "Which server plays first".</summary>
/// <param name="PeerKey">The connection.</param>
/// <param name="Name">Its shown name (set as text only).</param>
/// <param name="Tier">Prefer, Normal or Backup.</param>
/// <param name="Position">Its place, 1 first.</param>
/// <param name="Count">How many connections.</param>
/// <param name="Reason">Plain words, or null before the first evaluation.</param>
/// <param name="Label">"Automatic: 2nd of 3 (slower link)", or null before the first evaluation.</param>
/// <param name="Effective">Its effective quality limit.</param>
public sealed record PlayOrderRowDto(string PeerKey, string Name, PriorityTier Tier, int Position, int Count, string? Reason, string? Label, EffectiveLimitDto Effective);

/// <summary>The backup counts of a connection for the overlap panel.</summary>
/// <param name="SharedWithOthers">Its titles another friend's server also has (one group each).</param>
/// <param name="PlaysFirst">Of those, the ones it plays first (by tier and play order; a play still skips a server that is down).</param>
/// <param name="BackupFor">Of those, the ones it is a hidden backup for.</param>
public sealed record BackupCountsDto(int SharedWithOthers, int PlaysFirst, int BackupFor);

/// <summary>The streams playing now from one friend's server.</summary>
/// <param name="PeerKey">The connection.</param>
/// <param name="Name">Its shown name (set as text only).</param>
/// <param name="Streams">The streams, oldest first.</param>
public sealed record PlayingFriendDto(string PeerKey, string Name, IReadOnlyList<PlayingStreamDto> Streams);

/// <summary>One stream playing now.</summary>
/// <param name="ItemId">The local item id (N form).</param>
/// <param name="Title">The title (sanitized catalog text; set as text only).</param>
/// <param name="UserId">The local user (N form).</param>
/// <param name="Method">direct or converted.</param>
/// <param name="SourceQuality">480p, 720p, 1080p or 4K of the friend's file, or null when not known.</param>
/// <param name="SourceMbps">The friend's file bitrate in Mbps, or null.</param>
/// <param name="HeldByLimit">Whether it is converted because of the quality limit.</param>
/// <param name="LimitText">The limit the stream is held to ("Up to 1080p"), or null.</param>
/// <param name="StartedUtc">When its PlaybackInfo ran.</param>
public sealed record PlayingStreamDto(string ItemId, string Title, string UserId, string Method, string? SourceQuality, double? SourceMbps, bool HeldByLimit, string? LimitText, DateTimeOffset StartedUtc);

/// <summary>The body of <c>PUT Shoal/Admin/Peers/{peerKey}/Priority</c> (also <c>.../PriorityTier</c>).</summary>
public sealed class PriorityTierRequest
{
    /// <summary>Gets the new tier: Prefer, Normal or Backup.</summary>
    public PriorityTier? Tier { get; init; }

    /// <summary>Gets the tier the admin saw; required.</summary>
    public PriorityTier? ExpectedCurrent { get; init; }
}
