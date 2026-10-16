using System.Collections.Generic;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Lists Home's own series (every <c>Series</c> outside the stub root).
/// </summary>
public interface ILocalSeries
{
    /// <summary>Lists the series.</summary>
    /// <param name="stubRoot">The stub root; items below it are federated and are left out.</param>
    /// <returns>The series.</returns>
    IReadOnlyList<LocalSeries> List(string stubRoot);
}

/// <summary>
/// Home's own series, for the series-level dedup rule "local always wins" (DESIGN v2.7.0 #3). K38 provides the real list; the default has none.
/// </summary>
/// <param name="ProviderIds">The canonical provider ids.</param>
/// <param name="Title">The title.</param>
/// <param name="Year">The production year, or null.</param>
public sealed record LocalSeries(CanonicalIds ProviderIds, string Title, int? Year);

/// <summary>
/// The default <see cref="ILocalSeries"/>: Home has no series, so nothing is deduplicated until K38 plugs in the real list.
/// </summary>
public sealed class NoLocalSeries : ILocalSeries
{
    /// <summary>Gets the only instance.</summary>
    public static NoLocalSeries Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<LocalSeries> List(string stubRoot) => [];
}
