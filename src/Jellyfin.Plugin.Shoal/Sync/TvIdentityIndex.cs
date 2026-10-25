using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>Indexes complete series identities without flattening conflicting provider bundles.</summary>
/// <typeparam name="T">The candidate record.</typeparam>
internal sealed class TvIdentityIndex<T>
{
    private readonly Dictionary<string, List<Candidate>> _keys = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="TvIdentityIndex{T}"/> class.</summary>
    /// <param name="items">Candidates in preference order.</param>
    /// <param name="identity">Each candidate's full identity.</param>
    internal TvIdentityIndex(IEnumerable<T> items, Func<T, LocalSeries> identity)
    {
        var order = 0;
        foreach (var item in items)
        {
            var value = identity(item);
            var candidate = new Candidate(order++, item, value);
            foreach (var key in TvPlanner.SeriesKeys(value.ProviderIds, value.Title, value.Year))
            {
                if (!_keys.TryGetValue(key, out var list))
                {
                    list = [];
                    _keys.Add(key, list);
                }

                list.Add(candidate);
            }
        }
    }

    /// <summary>Finds compatible, unambiguous candidates in preference order.</summary>
    /// <param name="ids">Source provider ids.</param>
    /// <param name="title">Source title.</param>
    /// <param name="year">Source year.</param>
    /// <returns>Matches, or none when weak candidates bridge conflicting identities.</returns>
    internal List<T> Match(CanonicalIds ids, string title, int? year)
    {
        var candidates = TvPlanner.SeriesKeys(ids, title, year)
            .SelectMany(key => _keys.GetValueOrDefault(key) ?? [])
            .DistinctBy(static c => c.Order)
            .Where(c => Matches(ids, title, year, c.Identity))
            .OrderBy(static c => c.Order).ToList();
        var aggregate = ids;
        foreach (var candidate in candidates)
        {
            var other = candidate.Identity.ProviderIds;
            if (Conflicts(aggregate, other) || (aggregate.HasAny && other.HasAny && !SharesStrong(aggregate, other)))
            {
                return [];
            }

            aggregate = new CanonicalIds(aggregate.Tmdb ?? other.Tmdb, aggregate.Tvdb ?? other.Tvdb, aggregate.Imdb ?? other.Imdb);
        }

        return [.. candidates.Select(static c => c.Item)];
    }

    /// <summary>Checks every shared provider namespace for disagreement.</summary>
    /// <param name="left">One identity.</param>
    /// <param name="right">The other identity.</param>
    /// <returns>Whether the identities conflict.</returns>
    internal static bool Conflicts(CanonicalIds left, CanonicalIds right) =>
        (left.Tmdb is not null && right.Tmdb is not null && left.Tmdb != right.Tmdb)
        || (left.Tvdb is not null && right.Tvdb is not null && left.Tvdb != right.Tvdb)
        || (left.Imdb is not null && right.Imdb is not null && !string.Equals(left.Imdb, right.Imdb, StringComparison.Ordinal));

    /// <summary>Checks for an identical strong provider id.</summary>
    /// <param name="left">One identity.</param>
    /// <param name="right">The other identity.</param>
    /// <returns>Whether a provider id agrees.</returns>
    internal static bool SharesStrong(CanonicalIds left, CanonicalIds right) =>
        (left.Tmdb is not null && left.Tmdb == right.Tmdb)
        || (left.Tvdb is not null && left.Tvdb == right.Tvdb)
        || (left.Imdb is not null && string.Equals(left.Imdb, right.Imdb, StringComparison.Ordinal));

    private static bool Matches(CanonicalIds ids, string title, int? year, LocalSeries candidate)
    {
        if (Conflicts(ids, candidate.ProviderIds))
        {
            return false;
        }

        if (SharesStrong(ids, candidate.ProviderIds))
        {
            return true;
        }

        return (!ids.HasAny || !candidate.ProviderIds.HasAny)
            && TitleKey.Weak(title, year) is { } weak
            && string.Equals(weak, TitleKey.Weak(candidate.Title, candidate.Year), StringComparison.Ordinal);
    }

    private sealed record Candidate(int Order, T Item, LocalSeries Identity);
}
