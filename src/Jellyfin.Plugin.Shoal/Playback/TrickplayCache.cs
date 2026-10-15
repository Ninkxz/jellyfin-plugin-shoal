using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The disk cache of validated trickplay tiles (DESIGN v2.6.5): <c>&lt;root&gt;/&lt;peerKey&gt;/&lt;stubKey&gt;/&lt;width&gt;_&lt;index&gt;.jpg</c>. Names hold only Home's random keys and two
/// bounded integers, so no peer id or token reaches a file name and a path cannot escape. The folder is capped (least recently used files go first, down to 90
/// percent of the cap); a hit refreshes the access time in memory at once and on disk at most once a minute. Only validated JPEGs are ever written.
/// </summary>
public sealed partial class TrickplayCache
{
    /// <summary>The most files the start-up walk counts before it stops.</summary>
    public const int MaxWalkFiles = 200_000;

    private const string SignatureFile = "signature";
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private readonly string _root;
    private readonly Func<long> _capBytes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private long _total;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayCache"/> class.
    /// </summary>
    /// <param name="root">The cache folder (<c>&lt;DataPath&gt;/shoal/trickplay</c>).</param>
    /// <param name="capBytes">Reads the cap in bytes (<c>TrickplayCacheGb</c>).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayCache(string root, Func<long> capBytes, TimeProvider time, ILogger<TrickplayCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _capBytes = capBytes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the bytes the cache holds now.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    /// <summary>Gets the number of tiles the cache holds now.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _tiles.Count;
            }
        }
    }

    /// <summary>
    /// Whether the keys are the shapes Home itself makes (so a name can never contain a separator or a dot segment).
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <returns>True when both are safe to use in a path.</returns>
    public static bool IsSafe(string? peerKey, string? stubKey) => ShoalPaths.IsValidPeerKey(peerKey) && stubKey is not null && StubKeyRegex().IsMatch(stubKey);

    /// <summary>
    /// Walks the folder once at start-up: counts the tiles and their bytes, removes leftover temp files, and stops at <see cref="MaxWalkFiles"/>.
    /// </summary>
    public void Scan()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var walked = 0;
        var loaded = new Dictionary<string, Tile>(StringComparer.Ordinal);
        try
        {
            foreach (var peerDir in Directory.EnumerateDirectories(_root))
            {
                var peerKey = Path.GetFileName(peerDir);
                foreach (var stubDir in Directory.EnumerateDirectories(peerDir))
                {
                    var stubKey = Path.GetFileName(stubDir);
                    if (!IsSafe(peerKey, stubKey))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(stubDir))
                    {
                        if (++walked > MaxWalkFiles)
                        {
                            _logger.LogWarning("The trickplay cache holds more than {Max} files; the walk stopped", MaxWalkFiles);
                            Adopt(loaded);
                            return;
                        }

                        var name = Path.GetFileName(file);
                        if (name.Contains(".tmp-", StringComparison.Ordinal))
                        {
                            TryDelete(file);
                            continue;
                        }

                        if (!TileNameRegex().IsMatch(name))
                        {
                            continue;
                        }

                        var info = new FileInfo(file);
                        loaded[KeyOf(peerKey, stubKey, name)] = new Tile(info.Length, info.LastWriteTimeUtc.Ticks, 0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The trickplay cache could not be read completely ({Error})", ex.GetType().Name);
        }

        Adopt(loaded);
    }

    /// <summary>
    /// Makes sure the stub's cached tiles belong to the metadata the catalog holds now. When the signature differs (the peer regenerated its tiles with other
    /// dimensions), the stub's folder is purged.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="signature">A text that changes whenever the stub's levels change.</param>
    public void EnsureSignature(string peerKey, string stubKey, string signature)
    {
        if (!IsSafe(peerKey, stubKey))
        {
            return;
        }

        var folder = Path.Combine(_root, peerKey, stubKey);
        var key = peerKey + "/" + stubKey;
        lock (_lock)
        {
            if (!_signatures.TryGetValue(key, out var known))
            {
                known = ReadSignature(folder);
            }

            if (known == signature)
            {
                _signatures[key] = signature;
                return;
            }
        }

        RemoveStub(peerKey, stubKey);
        try
        {
            SecureDirectory.Ensure(folder);
            File.WriteAllText(Path.Combine(folder, SignatureFile), signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("The trickplay signature could not be stored ({Error})", ex.GetType().Name);
        }

        lock (_lock)
        {
            _signatures[key] = signature;
        }
    }

    /// <summary>
    /// Reads a cached tile.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="width">The thumbnail width.</param>
    /// <param name="index">The tile index.</param>
    /// <returns>The tile, or null on a miss.</returns>
    public byte[]? TryGet(string peerKey, string stubKey, int width, int index)
    {
        if (!IsSafe(peerKey, stubKey))
        {
            return null;
        }

        var key = KeyOf(peerKey, stubKey, NameOf(width, index));
        var now = _time.GetUtcNow().UtcTicks;
        var touchFile = false;
        lock (_lock)
        {
            if (!_tiles.TryGetValue(key, out var tile))
            {
                return null;
            }

            touchFile = now - tile.TouchedTicks >= TouchEvery.Ticks;
            _tiles[key] = tile with { AccessTicks = now, TouchedTicks = touchFile ? now : tile.TouchedTicks };
        }

        var path = PathOf(peerKey, stubKey, width, index);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (touchFile)
            {
                File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            }

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Forget(key);
            return null;
        }
    }

    /// <summary>
    /// Stores a validated tile, then trims the cache to its cap.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="stubKey">The stub key.</param>
    /// <param name="width">The thumbnail width.</param>
    /// <param name="index">The tile index.</param>
    /// <param name="bytes">The validated JPEG.</param>
    /// <returns>False when the tile could not be stored (the caller still answers with the bytes).</returns>
    public bool Store(string peerKey, string stubKey, int width, int index, ReadOnlySpan<byte> bytes)
    {
        if (!IsSafe(peerKey, stubKey) || bytes.Length == 0 || bytes.Length > _capBytes())
        {
            return false;
        }

        var path = PathOf(peerKey, stubKey, width, index);
        var temp = path + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        try
        {
            SecureDirectory.Ensure(Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = AtomicFile.PrivateFileMode;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            _logger.LogDebug("A trickplay tile could not be cached ({Error})", ex.GetType().Name);
            return false;
        }

        var key = KeyOf(peerKey, stubKey, NameOf(width, index));
        var now = _time.GetUtcNow().UtcTicks;
        lock (_lock)
        {
            if (_tiles.TryGetValue(key, out var old))
            {
                _total -= old.Size;
            }

            _tiles[key] = new Tile(bytes.Length, now, now);
            _total += bytes.Length;
        }

        Trim();
        return true;
    }

    /// <summary>
    /// Deletes the tiles of one stub.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="stubKey">The stub key.</param>
    public void RemoveStub(string peerKey, string stubKey)
    {
        if (!IsSafe(peerKey, stubKey))
        {
            return;
        }

        var prefix = peerKey + "/" + stubKey + "/";
        DeleteWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        TryDeleteDirectory(Path.Combine(_root, peerKey, stubKey));
        lock (_lock)
        {
            _signatures.Remove(peerKey + "/" + stubKey);
        }
    }

    /// <summary>
    /// Deletes the tiles of every stub that is not in <paramref name="live"/> (a removed stub, library or peer). Run after a scan.
    /// </summary>
    /// <param name="live">The (peer key, stub key) pairs that still exist.</param>
    /// <returns>How many stub folders were removed.</returns>
    public int Reconcile(IReadOnlySet<(string PeerKey, string StubKey)> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var peerDir in Directory.EnumerateDirectories(_root).ToList())
            {
                var peerKey = Path.GetFileName(peerDir);
                foreach (var stubDir in Directory.EnumerateDirectories(peerDir).ToList())
                {
                    var stubKey = Path.GetFileName(stubDir);
                    if (IsSafe(peerKey, stubKey) && live.Contains((peerKey, stubKey)))
                    {
                        continue;
                    }

                    if (IsSafe(peerKey, stubKey))
                    {
                        RemoveStub(peerKey, stubKey);
                    }
                    else
                    {
                        TryDeleteDirectory(stubDir);
                    }

                    removed++;
                }

                if (!Directory.EnumerateFileSystemEntries(peerDir).Any())
                {
                    TryDeleteDirectory(peerDir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("The trickplay cache could not be reconciled ({Error})", ex.GetType().Name);
        }

        return removed;
    }

    /// <summary>
    /// Deletes everything (the admin's "Clear").
    /// </summary>
    /// <returns>The bytes freed.</returns>
    public long Clear()
    {
        long freed;
        lock (_lock)
        {
            freed = _total;
            _tiles.Clear();
            _signatures.Clear();
            _total = 0;
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("The trickplay cache could not be cleared completely ({Error})", ex.GetType().Name);
        }

        return freed;
    }

    [GeneratedRegex(@"\A[a-z0-9]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StubKeyRegex();

    [GeneratedRegex(@"\A[0-9]{1,4}_[0-9]{1,6}\.jpg\z", RegexOptions.CultureInvariant)]
    private static partial Regex TileNameRegex();

    private static string NameOf(int width, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{width}_{index}.jpg");

    private static string KeyOf(string peerKey, string stubKey, string name) => peerKey + "/" + stubKey + "/" + name;

    private static string ReadSignature(string folder)
    {
        try
        {
            return File.ReadAllText(Path.Combine(folder, SignatureFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next walk.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next reconcile.
        }
    }

    private string PathOf(string peerKey, string stubKey, int width, int index) => Path.Combine(_root, peerKey, stubKey, NameOf(width, index));

    private void Adopt(Dictionary<string, Tile> loaded)
    {
        lock (_lock)
        {
            foreach (var (key, tile) in loaded)
            {
                if (!_tiles.ContainsKey(key))
                {
                    _tiles[key] = tile;
                    _total += tile.Size;
                }
            }
        }

        Trim();
    }

    private void Forget(string key)
    {
        lock (_lock)
        {
            if (_tiles.Remove(key, out var tile))
            {
                _total -= tile.Size;
            }
        }
    }

    private void DeleteWhere(Func<string, bool> match)
    {
        List<string> doomed;
        lock (_lock)
        {
            doomed = [.. _tiles.Keys.Where(match)];
            foreach (var key in doomed)
            {
                _total -= _tiles[key].Size;
                _tiles.Remove(key);
            }
        }

        foreach (var key in doomed)
        {
            var parts = key.Split('/');
            TryDelete(Path.Combine(_root, parts[0], parts[1], parts[2]));
        }
    }

    // After a write: above the cap, delete the least recently used tiles until the folder is at 90 percent of it.
    private void Trim()
    {
        var cap = _capBytes();
        List<string> doomed;
        lock (_lock)
        {
            if (_total <= cap)
            {
                return;
            }

            var target = cap / 10 * 9;
            doomed = [];
            foreach (var (key, tile) in _tiles.OrderBy(static t => t.Value.AccessTicks))
            {
                if (_total <= target)
                {
                    break;
                }

                doomed.Add(key);
                _total -= tile.Size;
            }

            foreach (var key in doomed)
            {
                _tiles.Remove(key);
            }
        }

        foreach (var key in doomed)
        {
            var parts = key.Split('/');
            TryDelete(Path.Combine(_root, parts[0], parts[1], parts[2]));
        }
    }

    private readonly record struct Tile(long Size, long AccessTicks, long TouchedTicks);
}
