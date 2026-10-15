using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Authorization;

/// <summary>
/// Where the index learns the local items and their paths. The production class talks to <c>ILibraryManager</c>; tests use a fake.
/// </summary>
public interface IItemPaths
{
    /// <summary>Raised when a library item is added.</summary>
    event EventHandler<ItemPathEventArgs>? Added;

    /// <summary>Raised when a library item is removed.</summary>
    event EventHandler<ItemPathEventArgs>? Removed;

    /// <summary>
    /// Looks up one item's path. This is classification, never authorization.
    /// </summary>
    /// <param name="itemId">The local item id.</param>
    /// <returns>The path, or null when core has no such item (or it has no path).</returns>
    string? PathOf(Guid itemId);

    /// <summary>
    /// Lists the movies in the libraries that live below the stub root.
    /// </summary>
    /// <param name="stubRoot">The stub root.</param>
    /// <returns>The items.</returns>
    IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot);
}

/// <summary>
/// An item added to or removed from the library, reduced to what the index needs.
/// </summary>
public sealed class ItemPathEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ItemPathEventArgs"/> class.
    /// </summary>
    /// <param name="id">The local item id.</param>
    /// <param name="path">The item's path, or null.</param>
    public ItemPathEventArgs(Guid id, string? path)
    {
        Id = id;
        Path = path;
    }

    /// <summary>Gets the local item id.</summary>
    public Guid Id { get; }

    /// <summary>Gets the item's path, or null.</summary>
    public string? Path { get; }
}

/// <summary>
/// A federated item as the index knows it.
/// </summary>
/// <param name="PeerKey">The peer.</param>
/// <param name="MappingKey">The library mapping.</param>
/// <param name="FolderName">The stub's folder name below the library folder.</param>
/// <param name="FileName">The file name of the item's <c>.strm</c> (a title with several versions has one per version), or empty when the path names only the folder.</param>
public sealed record IndexedStub(string PeerKey, string MappingKey, string FolderName, string FileName = "");

/// <summary>
/// The set of federated local items (DESIGN 3.5): <c>Contains</c> is an O(1) hit. The authoritative rule is the item's path: canonicalized, it starts
/// with the validated stub root, a peer folder and a mapped library folder. The index is rebuilt in the background at startup and after scans, and
/// follows core's item events, so a stub scanned after the last rebuild is found too. An empty or invalid stub root means nothing is federated
/// (Warden B-5). This is classification, never authorization.
/// </summary>
public sealed class ShoalIndex : IFederatedItems, IDisposable
{
    private readonly IItemPaths _items;
    private readonly IStateStore _state;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _rebuild = new(1, 1);
    private ConcurrentDictionary<Guid, IndexedStub> _index = new();
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalIndex"/> class.
    /// </summary>
    /// <param name="items">The library items.</param>
    /// <param name="state">The state store (stub root and mappings).</param>
    /// <param name="logger">The logger.</param>
    public ShoalIndex(IItemPaths items, IStateStore state, ILogger<ShoalIndex> logger)
        : this(items, state, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalIndex"/> class (tests: any logger).
    /// </summary>
    /// <param name="items">The library items.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    internal ShoalIndex(IItemPaths items, IStateStore state, ILogger logger)
    {
        _items = items;
        _state = state;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the first rebuild has finished. Before then, misses fall back to the path rule.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Gets the number of federated items in the index.</summary>
    public int Count => _index.Count;

    /// <summary>
    /// Applies the path rule.
    /// </summary>
    /// <param name="state">The state (stub root and mappings).</param>
    /// <param name="itemPath">The item's path.</param>
    /// <returns>The library of the item, or null when the path is not below a mapped library's stub folder.</returns>
    public static FederatedItem? FromPath(ShoalState state, string itemPath)
    {
        var stub = Classify(state, itemPath);
        return stub is null ? null : new FederatedItem(stub.PeerKey, stub.MappingKey);
    }

    /// <summary>
    /// Applies the path rule and also names the stub's folder.
    /// </summary>
    /// <param name="state">The state (stub root and mappings).</param>
    /// <param name="itemPath">The item's path.</param>
    /// <returns>The stub, or null when the path is not below a mapped library's stub folder.</returns>
    public static IndexedStub? Classify(ShoalState state, string itemPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.StubRoot) || string.IsNullOrWhiteSpace(itemPath))
        {
            return null;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(Path.GetFullPath(state.StubRoot), Path.GetFullPath(itemPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 3)
        {
            return null; // <peerKey>/<Subfolder>/<something>
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var peer = state.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, parts[0], StringComparison.Ordinal));
        var mapping = peer?.Libraries.FirstOrDefault(l => string.Equals(l.Subfolder, parts[1], comparison));
        return peer is null || mapping is null ? null : new IndexedStub(peer.PeerKey, mapping.MappingKey, parts[2], parts.Length > 3 ? parts[3] : string.Empty);
    }

    /// <summary>
    /// Whether an item is federated: an O(1) hit in the index.
    /// </summary>
    /// <param name="localItemId">The local item id.</param>
    /// <returns>True when the index holds the item.</returns>
    public bool Contains(Guid localItemId) => _index.ContainsKey(localItemId);

    /// <summary>
    /// Whether an item is federated, for the routes whose core handler would read the stub: an index miss still applies the path rule (one lookup),
    /// so a stub scanned after the last snapshot never falls through to core.
    /// </summary>
    /// <param name="localItemId">The local item id.</param>
    /// <returns>True when the index holds the item or its path says so.</returns>
    public bool ContainsOrClassify(Guid localItemId) => Get(localItemId) is not null;

    /// <summary>
    /// Lists the ids of the federated items in the index.
    /// </summary>
    /// <returns>A snapshot of the ids.</returns>
    public IReadOnlyList<Guid> Ids() => [.. _index.Keys];

    /// <summary>
    /// Gets a federated item.
    /// </summary>
    /// <param name="localItemId">The local item id.</param>
    /// <returns>The stub, or null when the item is not federated.</returns>
    public IndexedStub? Get(Guid localItemId)
    {
        if (_index.TryGetValue(localItemId, out var known))
        {
            return known;
        }

        var path = _items.PathOf(localItemId);
        var stub = path is null ? null : Classify(_state.Current, path);
        if (stub is not null)
        {
            _index[localItemId] = stub; // found by the path rule: remember it
        }

        return stub;
    }

    /// <inheritdoc />
    public FederatedItem? Find(Guid localItemId) => Get(localItemId) is { } stub ? new FederatedItem(stub.PeerKey, stub.MappingKey) : null;

    /// <summary>
    /// Starts following core's item events and the stub root.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _items.Added += OnAdded;
        _items.Removed += OnRemoved;
        _state.Changed += OnStateChanged;
    }

    /// <summary>
    /// Rebuilds the index from the library. Items that are not below a mapped library's folder are left out, so an empty or invalid stub root
    /// gives an empty index.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the new index is in place.</returns>
    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _rebuild.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = new ConcurrentDictionary<Guid, IndexedStub>();
            if (_state.Status == StateStatus.Ready && !string.IsNullOrWhiteSpace(_state.Current.StubRoot))
            {
                var state = _state.Current;
                foreach (var item in await Task.Run(() => _items.ItemsBelow(state.StubRoot), cancellationToken).ConfigureAwait(false))
                {
                    if (item.Path is not null && Classify(state, item.Path) is { } stub)
                    {
                        fresh[item.Id] = stub;
                    }
                }
            }

            _index = fresh;
            IsReady = true;
            _logger.LogDebug("The Shoal index holds {Count} federated items", fresh.Count);
        }
        finally
        {
            _rebuild.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_started)
        {
            _items.Added -= OnAdded;
            _items.Removed -= OnRemoved;
            _state.Changed -= OnStateChanged;
            _started = false;
        }

        _rebuild.Dispose();
    }

    private void OnAdded(object? sender, ItemPathEventArgs change)
    {
        if (change.Path is not null && _state.Status == StateStatus.Ready && Classify(_state.Current, change.Path) is { } stub)
        {
            _index[change.Id] = stub;
        }
    }

    private void OnRemoved(object? sender, ItemPathEventArgs change) => _index.TryRemove(change.Id, out _);

    private void OnStateChanged(object? sender, StateChangedEventArgs e) => _ = RebuildQuietlyAsync();

    private async Task RebuildQuietlyAsync()
    {
        try
        {
            await RebuildAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("The Shoal index could not be rebuilt ({Error})", ex.GetType().Name);
        }
    }
}

/// <summary>
/// <see cref="IItemPaths"/> from the library manager.
/// </summary>
public sealed class JellyfinItemPaths : IItemPaths
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinItemPaths"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public JellyfinItemPaths(ILibraryManager library)
    {
        _library = library;
        _library.ItemAdded += (_, e) => Added?.Invoke(this, new ItemPathEventArgs(e.Item.Id, e.Item.Path));
        _library.ItemRemoved += (_, e) => Removed?.Invoke(this, new ItemPathEventArgs(e.Item.Id, e.Item.Path));
    }

    /// <inheritdoc />
    public event EventHandler<ItemPathEventArgs>? Added;

    /// <inheritdoc />
    public event EventHandler<ItemPathEventArgs>? Removed;

    /// <inheritdoc />
    public string? PathOf(Guid itemId)
    {
#pragma warning disable RS0030 // Allowed here only: classification of an item by its path, never authorization (DESIGN 8.8).
        return _library.GetItemById(itemId)?.Path;
#pragma warning restore RS0030
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemPathEventArgs> ItemsBelow(string stubRoot)
    {
        var root = Path.GetFullPath(stubRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var result = new List<ItemPathEventArgs>();
        foreach (var folder in _library.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var folderId) || !(folder.Locations ?? []).Any(l => Path.GetFullPath(l).StartsWith(root, comparison)))
            {
                continue;
            }

            foreach (var item in _library.GetItemList(new InternalItemsQuery { ParentId = folderId, Recursive = true, IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Video] }))
            {
                result.Add(new ItemPathEventArgs(item.Id, item.Path));
            }
        }

        return result;
    }
}

/// <summary>
/// Builds the index in the background at startup and keeps it current.
/// </summary>
public sealed class ShoalIndexHost : IHostedService
{
    private readonly ShoalIndex _index;
    private readonly ILogger<ShoalIndexHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalIndexHost"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="logger">The logger.</param>
    public ShoalIndexHost(ShoalIndex index, ILogger<ShoalIndexHost> logger)
    {
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _index.Start();
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _index.RebuildAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning("The Shoal index could not be built at startup ({Error})", ex.GetType().Name);
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
