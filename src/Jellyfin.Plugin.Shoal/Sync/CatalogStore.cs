using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The result of reading a catalog.
/// </summary>
/// <param name="Document">The catalog; an empty one when there is no file yet. Never null.</param>
/// <param name="Unreadable">True when a file exists but cannot be used (damaged or from a newer Shoal). A sync does not start over from an
/// empty catalog then, because every stub on disk would look new.</param>
/// <param name="Dropped">How many entries were dropped because they failed validation.</param>
public sealed record CatalogLoad(CatalogDocument Document, bool Unreadable, int Dropped);

/// <summary>
/// Loads and saves <c>peers/&lt;peerKey&gt;/catalog.json</c> (DESIGN 3.3): atomically, with a <c>.bak</c>, mode 0600. Whatever is read is validated again,
/// because the stub paths in it are used for deletions.
/// </summary>
public sealed class CatalogStore
{
    /// <summary>The largest catalog file that is read.</summary>
    public const long MaxBytes = 128L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly ShoalPaths _paths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogStore"/> class.
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public CatalogStore(ShoalPaths paths, ILogger<CatalogStore> logger)
        : this(paths, (ILogger)logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogStore"/> class (tests: any logger).
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    internal CatalogStore(ShoalPaths paths, ILogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Whether a stub path from a catalog is safe to use: <c>folder/file.strm</c>, two plain segments.
    /// </summary>
    /// <param name="relativePath">The path as stored (always with '/').</param>
    /// <returns>True when it is exactly two plain path segments.</returns>
    public static bool IsSafeStubPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > 600)
        {
            return false;
        }

        var parts = relativePath.Split('/');
        return parts.Length == 2 && parts.All(IsPlainSegment) && parts[1].EndsWith(".strm", StringComparison.Ordinal);
    }

    /// <summary>
    /// Gives a cheap fingerprint of the catalog file (last write and length) so readers can cache a parsed catalog until it changes.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>The fingerprint, or null when there is no file.</returns>
    public (long Ticks, long Length)? Stamp(string peerKey)
    {
        var info = new FileInfo(FilePath(peerKey));
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
    }

    /// <summary>
    /// Reads the catalog of a peer.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>The catalog.</returns>
    public CatalogLoad Load(string peerKey)
    {
        var path = FilePath(peerKey);
        if (!File.Exists(path))
        {
            return new CatalogLoad(Empty(peerKey), false, 0);
        }

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            var loaded = TryRead(candidate, peerKey);
            if (loaded is not null)
            {
                return loaded;
            }
        }

        _logger.LogWarning("The catalog of peer {Peer} could not be read; the sync waits for the admin", Redact.Id(peerKey));
        return new CatalogLoad(Empty(peerKey), true, 0);
    }

    /// <summary>
    /// Writes the catalog atomically.
    /// </summary>
    /// <param name="document">The catalog; its <see cref="CatalogDocument.PeerKey"/> names the file.</param>
    public void Save(CatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = FilePath(document.PeerKey);
        SecureDirectory.Ensure(Path.GetDirectoryName(path)!);
        document.Schema = CatalogDocument.CurrentSchema;
        document.Usage.People = document.Names.People.Count;
        document.Usage.Genres = document.Names.Genres.Count;
        document.Usage.Studios = document.Names.Studios.Count;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        var outcome = AtomicFile.Write(path, bytes, path + ".bak");
        if (outcome != AtomicWriteOutcome.Normal)
        {
            _logger.LogInformation("The catalog of peer {Peer} was saved without a normal replace ({Outcome})", Redact.Id(document.PeerKey), outcome);
        }
    }

    private static bool IsPlainSegment(string segment) =>
        segment.Length is > 0 and <= 255
        && segment is not ("." or "..")
        && segment.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0
        && !segment.Any(char.IsControl)
        && segment == segment.Trim(' ', '.');

    private static bool IsHex32(string? value) =>
        value is { Length: 32 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsKey(string? value) =>
        value is { Length: >= 8 and <= 32 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z'));

    private static CatalogDocument Empty(string peerKey) => new() { PeerKey = peerKey };

    private string FilePath(string peerKey) => Path.Combine(_paths.PeerDir(peerKey), "catalog.json");

    private static CatalogLoad? TryRead(string path, string peerKey)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxBytes)
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllBytes(path), Options);
            if (document is null || document.Schema is < CatalogDocument.OldestReadableSchema or > CatalogDocument.CurrentSchema || !string.Equals(document.PeerKey, peerKey, StringComparison.Ordinal))
            {
                return null;
            }

            var dropped = Sanitize(document);
            return new CatalogLoad(document, false, dropped);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Schema 3 (DESIGN v2.6.4): keeps at most four valid sources per entry and gives an entry that has none (every schema-2 entry) the one it always
    /// had, built from its own fields, so nothing on disk changes name. A source with a bad key, id or path is dropped; a trickplay level out of range is dropped.
    /// </summary>
    private static void SanitizeSources(CatalogEntry entry)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        entry.Sources = [.. entry.Sources.Where(s => IsKey(s.StubKey)
            && (s.PeerMediaSourceId.Length == 0 || IsHex32(s.PeerMediaSourceId))
            && (s.StubRelPath is null || IsSafeStubPath(s.StubRelPath))
            && seen.Add(s.StubKey)).Take(CatalogSource.MaxPerTitle)];
        foreach (var source in entry.Sources)
        {
            source.Trickplay = [.. source.Trickplay.Where(static l => l.IsValid()).DistinctBy(static l => l.Width).Take(TrickplayLevel.MaxPerSource)];
        }

        if (entry.Sources.Count == 0)
        {
            entry.Sources.Add(new CatalogSource
            {
                StubKey = entry.StubKey,
                PeerMediaSourceId = entry.PeerMediaSourceId,
                Container = entry.Container,
                VideoCodec = entry.VideoCodec,
                Width = entry.Width,
                Height = entry.Height,
                Bitrate = entry.Bitrate,
                SizeBytes = entry.SizeBytes,
                RuntimeTicks = entry.RuntimeTicks,
                StubRelPath = entry.StubRelPath,
            });
        }
    }

    /// <summary>Drops what cannot be trusted and returns how many entries were dropped.</summary>
    private static int Sanitize(CatalogDocument document)
    {
        var before = document.Entries.Count;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        document.Entries = document.Entries.Where(e => IsKey(e.StubKey)
            && IsHex32(e.PeerItemId)
            && (e.PeerMediaSourceId.Length == 0 || IsHex32(e.PeerMediaSourceId))
            && e.State is not EntryState.Removed
            && (e.StubRelPath is null || IsSafeStubPath(e.StubRelPath))
            && (e.State is not (EntryState.Active or EntryState.HeldRemoval) || e.StubRelPath is not null)
            && seenKeys.Add(e.StubKey)
            && seenItems.Add(e.MappingKey + "/" + e.PeerItemId)).Take(CatalogDocument.MaxEntries).ToList();
        foreach (var entry in document.Entries)
        {
            SanitizeSources(entry);
        }

        document.Schema = CatalogDocument.CurrentSchema; // a schema-2 file is migrated by the step above and written as 3
        document.HeldAdditions = [.. document.HeldAdditions.Where(IsHex32).Distinct(StringComparer.Ordinal)];
        document.HeldRemovals = [.. document.HeldRemovals.Where(k => document.Entries.Any(e => e.StubKey == k && e.State == EntryState.HeldRemoval))];
        document.Libraries = [.. document.Libraries.Where(l => l.PeerLibraryId.Length == 0 || IsHex32(l.PeerLibraryId))];
        document.LastSync?.Alerts.RemoveAll(static a => a.Length > 40);
        document.Names.People = [.. document.Names.People.Take(20_000)];
        document.Names.Genres = [.. document.Names.Genres.Take(20_000)];
        document.Names.Studios = [.. document.Names.Studios.Take(20_000)];
        return before - document.Entries.Count;
    }
}
