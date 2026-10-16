using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// <see cref="ILocalMovies"/> from Jellyfin's library: every movie whose path is not below the stub root.
/// </summary>
public sealed class JellyfinLocalMovies : ILocalMovies
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLocalMovies"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public JellyfinLocalMovies(ILibraryManager library) => _library = library;

    /// <inheritdoc />
    public IReadOnlyList<LocalMovie> List(string stubRoot)
    {
        var root = string.IsNullOrWhiteSpace(stubRoot) ? null : Path.GetFullPath(stubRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var movies = new List<LocalMovie>();
        foreach (var item in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true }))
        {
            if (root is not null && item.Path is { Length: > 0 } path && Path.GetFullPath(path).StartsWith(root, comparison))
            {
                continue; // a federated stub, not Home's own copy
            }

            movies.Add(new LocalMovie(
                MetadataSanitizer.CanonicalProviderIds(item.ProviderIds),
                item.Name ?? string.Empty,
                item.ProductionYear,
                (item as Video)?.Size ?? 0));
        }

        return movies;
    }
}

/// <summary>
/// <see cref="ILocalSeries"/> from Jellyfin's library.
/// </summary>
public sealed class JellyfinLocalSeries : ILocalSeries
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLocalSeries"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public JellyfinLocalSeries(ILibraryManager library) => _library = library;

    /// <inheritdoc />
    public IReadOnlyList<LocalSeries> List(string stubRoot)
    {
        var root = string.IsNullOrWhiteSpace(stubRoot) ? null : Path.GetFullPath(stubRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var series = new List<LocalSeries>();
        foreach (var item in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true }))
        {
            if (root is not null && item.Path is { Length: > 0 } path && Path.GetFullPath(path).StartsWith(root, comparison))
            {
                continue; // a federated series, not Home's own
            }

            series.Add(new LocalSeries(MetadataSanitizer.CanonicalProviderIds(item.ProviderIds), item.Name ?? string.Empty, item.ProductionYear));
        }

        return series;
    }
}

/// <summary>
/// <see cref="ILibraryRefresh"/> from Jellyfin's library monitor: a change report goes through core's debounced refresher, so only the
/// reported folder is refreshed.
/// </summary>
public sealed class JellyfinLibraryRefresh : ILibraryRefresh
{
    private readonly ILibraryMonitor _monitor;
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryRefresh"/> class.
    /// </summary>
    /// <param name="monitor">The library monitor.</param>
    /// <param name="library">The library manager.</param>
    public JellyfinLibraryRefresh(ILibraryMonitor monitor, ILibraryManager library)
    {
        _monitor = monitor;
        _library = library;
    }

    /// <inheritdoc />
    public bool IsScanRunning => _library.IsScanRunning;

    /// <inheritdoc />
    public void ReportChanged(string path) => _monitor.ReportFileSystemChanged(path);

    /// <inheritdoc />
    public void QueueScan() => _library.QueueLibraryScan();
}
