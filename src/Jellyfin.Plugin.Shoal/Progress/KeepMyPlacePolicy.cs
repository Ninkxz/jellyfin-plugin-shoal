using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>Conservative local progress transfer rules, independent of Jellyfin's write API.</summary>
internal static class KeepMyPlacePolicy
{
    private static readonly string[] Providers = ["Imdb", "Tmdb", "Tvdb"];

    /// <summary>Decides a minimal progress mutation using current local facts.</summary>
    /// <param name="source">The locally captured source.</param>
    /// <param name="destination">The real local media.</param>
    /// <param name="history">Its current user history.</param>
    /// <param name="context">Fresh authorization and concurrency facts.</param>
    /// <returns>A minimal mutation or a stable skip reason.</returns>
    public static KeepMyPlaceDecision Decide(KeepMyPlaceSource source, KeepMyPlaceMedia destination, KeepMyPlaceHistory history, KeepMyPlaceContext context)
    {
        if (!context.Enabled)
        {
            return new("disabled");
        }

        if (context.UserId == Guid.Empty || source.History.UserId != context.UserId || history.UserId != context.UserId || !context.UserEligible)
        {
            return new("user");
        }

        if (!context.SourceAdmitted || !context.DestinationAdmitted || !context.DestinationLocal)
        {
            return new("access");
        }

        if (source.Media.ItemId == Guid.Empty || destination.ItemId == Guid.Empty || source.Media.ItemId == destination.ItemId || context.SameHistoryRecord)
        {
            return new("sameHistory");
        }

        if (context.ActivePlayback)
        {
            return new("activePlayback");
        }

        if (context.DestinationHistoryObserved || history.Played || history.PositionTicks != 0 || history.PlayCount != 0 || history.LastPlayedUtc.HasValue || history.HasPersonalHistory)
        {
            return new("destinationHistory");
        }

        if (!Matches(source.Media.Identity, destination.Identity))
        {
            return new("identity");
        }

        var progress = source.History;
        if (progress.PositionTicks < 0 || progress.PlayCount < 0)
        {
            return new("invalidProgress");
        }

        if (progress.Played)
        {
            return new("watched", true, 0, Math.Max(1, progress.PlayCount), progress.LastPlayedUtc);
        }

        if (progress.PositionTicks == 0)
        {
            return new("noProgress");
        }

        if (source.Media.RuntimeTicks is not > 0 || destination.RuntimeTicks is not > 0)
        {
            return new("unknownRuntime");
        }

        var from = source.Media.RuntimeTicks.Value;
        var to = destination.RuntimeTicks.Value;
        var tolerance = Math.Min(TimeSpan.TicksPerMinute, Math.Max(from, to) / 50);
        if (Math.Max(from, to) - Math.Min(from, to) > tolerance || progress.PositionTicks >= from || progress.PositionTicks >= to)
        {
            return new("runtime");
        }

        if (!string.IsNullOrWhiteSpace(source.Media.Edition) && !string.IsNullOrWhiteSpace(destination.Edition)
            && !string.Equals(source.Media.Edition.Trim(), destination.Edition.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new("edition");
        }

        return new("resume", false, progress.PositionTicks, progress.PlayCount, progress.LastPlayedUtc);
    }

    /// <summary>Requires a strong identity with no shared-provider conflict and exact episode range.</summary>
    /// <param name="source">The captured identity.</param>
    /// <param name="destination">The current identity.</param>
    /// <returns>Whether both identities safely describe the same media.</returns>
    public static bool Matches(KeepMyPlaceIdentity source, KeepMyPlaceIdentity destination)
    {
        if (source.Kind != destination.Kind || !StrongMatch(source.Ids, destination.Ids))
        {
            return false;
        }

        return source.Kind == KeepMyPlaceKind.Movie
            || (source.Season is >= 0 && source.Episode is > 0 && destination.Season == source.Season && destination.Episode == source.Episode
                && (source.End ?? source.Episode) >= source.Episode && (source.End ?? source.Episode) == (destination.End ?? destination.Episode)
                && !Conflicts(source.EpisodeIds, destination.EpisodeIds));
    }

    // Canonical provider IDs only; unknown namespaces cannot establish identity.
    private static bool StrongMatch(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> destination) =>
        !Conflicts(source, destination) && Providers.Any(p => Canonical(source, p) is { } id && id == Canonical(destination, p));

    private static bool Conflicts(IReadOnlyDictionary<string, string>? source, IReadOnlyDictionary<string, string>? destination) =>
        Providers.Any(p => Value(source, p) is { } a && Value(destination, p) is { } b
            && (Canonical(source, p) is not { } canonical || canonical != Canonical(destination, p) || string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)));

    private static string? Value(IReadOnlyDictionary<string, string>? ids, string provider) =>
        ids?.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value)).Value;

    private static string? Canonical(IReadOnlyDictionary<string, string>? ids, string provider)
    {
        var value = Value(ids, provider)?.Trim().ToLowerInvariant();
        if (value is null)
        {
            return null;
        }

        if (provider == "Imdb")
        {
            return value.Length is >= 3 and <= 12 && value.StartsWith("tt", StringComparison.Ordinal) && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0 ? value : null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
}
