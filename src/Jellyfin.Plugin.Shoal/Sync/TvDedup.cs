using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The keys that decide whether a peer series is a series Home already has (DESIGN v2.7.0 decision 3): the match is by series, never by episode, and a match hides
/// the whole peer series (<c>duplicate-local</c>). A strong key is a canonical provider id (<c>tvdb:81189</c>, <c>tmdb:1396</c>, <c>imdb:tt0903747</c>); the weak key
/// <c>title|year</c> (normalized and case-folded) is used only for a series that has no provider id at all.
/// </summary>
public static class TvDedup
{
    /// <summary>
    /// The keys of a peer series.
    /// </summary>
    /// <param name="series">The sanitized series.</param>
    /// <returns>The strong keys, or the one weak key when the series has no provider id.</returns>
    public static IReadOnlyList<string> Keys(SanitizedSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Keys(series.ProviderIds, series.Title, series.Year);
    }

    /// <summary>
    /// The keys of a series entry in the catalog or of one of Home's own series.
    /// </summary>
    /// <param name="ids">The canonical provider ids.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, or null.</param>
    /// <returns>The strong keys, or the one weak key when there is no provider id.</returns>
    public static IReadOnlyList<string> Keys(CanonicalIds ids, string title, int? year) =>
        ids.HasAny ? ids.DedupKeys() : [Weak(title, year)];

    /// <summary>
    /// The keys a Home series offers for matching: all its strong keys and its weak key (a peer series without ids can still match it by title and year).
    /// </summary>
    /// <param name="ids">The canonical provider ids.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, or null.</param>
    /// <returns>Every key.</returns>
    public static IReadOnlyList<string> LocalKeys(CanonicalIds ids, string title, int? year) => [.. ids.DedupKeys(), Weak(title, year)];

    /// <summary>
    /// Whether a peer series matches one of Home's own.
    /// </summary>
    /// <param name="series">The peer series.</param>
    /// <param name="localKeys">The keys of Home's series (<see cref="LocalKeys"/>).</param>
    /// <returns>True when the peer series is a duplicate of a local one.</returns>
    public static bool IsDuplicateLocal(SanitizedSeries series, IReadOnlySet<string> localKeys)
    {
        ArgumentNullException.ThrowIfNull(localKeys);
        return Keys(series).Any(localKeys.Contains);
    }

    private static string Weak(string title, int? year) => PathSanitizer.CollisionKey(title) + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}
