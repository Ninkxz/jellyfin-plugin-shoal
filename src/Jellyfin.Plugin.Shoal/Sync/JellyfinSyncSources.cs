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
    private readonly MediaBrowser.Model.IO.IFileSystem? _fileSystem;
    private readonly Microsoft.Extensions.Logging.ILogger<JellyfinLibraryRefresh>? _logger;
    private readonly MediaBrowser.Controller.Providers.IProviderManager? _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryRefresh"/> class.
    /// </summary>
    /// <param name="monitor">The library monitor.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="fileSystem">The file system (a library scan reads it), or null.</param>
    /// <param name="logger">The logger, or null.</param>
    /// <param name="providers">The provider manager (queues the same refresh as the dashboard's "Scan library"), or null.</param>
    public JellyfinLibraryRefresh(ILibraryMonitor monitor, ILibraryManager library, MediaBrowser.Model.IO.IFileSystem? fileSystem = null, Microsoft.Extensions.Logging.ILogger<JellyfinLibraryRefresh>? logger = null, MediaBrowser.Controller.Providers.IProviderManager? providers = null)
    {
        _monitor = monitor;
        _library = library;
        _fileSystem = fileSystem;
        _logger = logger;
        _providers = providers;
    }

    /// <inheritdoc />
    public bool IsScanRunning => _library.IsScanRunning;

    /// <inheritdoc />
    public void ReportChanged(string path) => _monitor.ReportFileSystemChanged(path);

    /// <inheritdoc />
    public void QueueScan() => _library.QueueLibraryScan();

    /// <inheritdoc />
    public void ScanLibrariesAt(IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (_fileSystem is null || folders.Count == 0)
        {
            return;
        }

        var wanted = folders.ToList();
        var logger = _logger ?? (Microsoft.Extensions.Logging.ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // In the background: a library of a few thousand stubs takes a while; the caller (a move job, a library create) does not wait for it.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // Pilot 15: a library just created or renamed has ItemId undefined until its top folder is validated; then it is found by its folder.
                await _library.ValidateTopLibraryFolders(System.Threading.CancellationToken.None, false).ConfigureAwait(false);
                var (libraries, alone) = Plan(_library.GetVirtualFolders(), wanted);
                if (_providers is null)
                {
                    alone = wanted; // no provider manager: every folder by path
                    libraries.Clear();
                }

                foreach (var id in libraries)
                {
                    _providers!.QueueRefresh(id, NewOptions(), MediaBrowser.Controller.Providers.RefreshPriority.High);
                    Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Shoal queued a scan of a library it changed");
                }

                var scanned = 0;
                foreach (var path in alone)
                {
                    if (_library.FindByPath(path, true) is not Folder physical)
                    {
                        continue;
                    }

                    await physical.RefreshMetadata(NewOptions(), System.Threading.CancellationToken.None).ConfigureAwait(false);
                    await physical.ValidateChildren(new Progress<double>(), NewOptions(), recursive: true, allowRemoveRoot: false, System.Threading.CancellationToken.None).ConfigureAwait(false);
                    var titles = _library.GetCount(new InternalItemsQuery { ParentId = physical.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode] });
                    Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Shoal scanned a folder it changed inside a library ({Titles} titles in it now)", titles);
                    scanned++;
                }

                if (libraries.Count == 0 && scanned == 0)
                {
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "Shoal did not find the library of a folder it changed; a library scan is queued");
                    _library.QueueLibraryScan();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "A Shoal library could not be scanned ({Error}); a library scan will pick it up", ex.GetType().Name);
            }
        });
    }

    /// <summary>
    /// Splits the changed folders (Pilot 16). A library of Shoal's own (one folder) gets the dashboard's "Scan library" refresh by its id, which also links a
    /// renamed library to its folder again. A folder inside a library with other folders (an admin's main library) is scanned alone, by path, never the whole
    /// library. Pilot 16 found the earlier scan did nothing: core's <c>CollectionFolder.ValidateChildren</c> is empty; only its physical folders scan.
    /// </summary>
    /// <param name="virtualFolders">Jellyfin's libraries.</param>
    /// <param name="folders">The changed folders.</param>
    /// <returns>The library ids to refresh, and the folders to scan alone.</returns>
    internal static (List<Guid> Libraries, List<string> Folders) Plan(IEnumerable<MediaBrowser.Model.Entities.VirtualFolderInfo> virtualFolders, IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(virtualFolders);
        ArgumentNullException.ThrowIfNull(folders);
        var all = virtualFolders.ToList();
        var libraries = new List<Guid>();
        var alone = new List<string>();
        foreach (var folder in folders)
        {
            var holder = all.FirstOrDefault(f => (f.Locations ?? []).Any(location => Jellyfin.Plugin.Shoal.State.LibraryFolders.SamePath(location, folder)));
            if (holder is { Locations.Length: 1 } && Guid.TryParse(holder.ItemId, out var id) && id != Guid.Empty)
            {
                if (!libraries.Contains(id))
                {
                    libraries.Add(id);
                }
            }
            else if (holder is not null)
            {
                alone.Add(folder);
            }
        }

        return (libraries, alone);
    }

    private MediaBrowser.Controller.Providers.MetadataRefreshOptions NewOptions() => new(new MediaBrowser.Controller.Providers.DirectoryService(_fileSystem!));
}
