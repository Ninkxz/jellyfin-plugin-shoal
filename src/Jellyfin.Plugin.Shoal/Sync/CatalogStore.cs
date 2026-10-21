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

/// <summary>A manual sync request that did not run (Pilot 24 SKIPPED-INVISIBLE). It never replaces the catalog's last real sync.</summary>
/// <param name="AtUtc">When the request was turned away (the sync service's clock).</param>
/// <param name="Reason">The sync service's skip word (tooSoon, scanRunning, busy, layoutJob, ...).</param>
public sealed record SkippedSync(DateTime AtUtc, string Reason);

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SkippedSync> _skipped = new(StringComparer.Ordinal);

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
    /// Remembers, in memory only, how the last manual sync request of a peer went: skipped (it did not run) or null (it ran). The catalog file
    /// is not touched, so a skip can never overwrite the last real result; the Overview and the library rows read it next to that result.
    /// </summary>
    /// <param name="peerKey">The peer.</param>
    /// <param name="skipped">The skip, or null when the request ran.</param>
    public void NoteManualRequest(string peerKey, SkippedSync? skipped)
    {
        if (skipped is null)
        {
            _skipped.TryRemove(peerKey, out _);
        }
        else
        {
            _skipped[peerKey] = skipped;
        }
    }

    /// <summary>Gets the last manual sync request of a peer that was skipped, or null.</summary>
    /// <param name="peerKey">The peer.</param>
    /// <returns>The skip, or null.</returns>
    public SkippedSync? LastSkipped(string peerKey) => _skipped.TryGetValue(peerKey, out var skipped) ? skipped : null;

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
    /// Whether an episode's stub path from a catalog is safe to use: <c>series/Season NN/SnnEnn.strm</c>, three plain segments (DESIGN v2.7.1).
    /// </summary>
    /// <param name="relativePath">The path as stored (always with '/').</param>
    /// <returns>True when it is exactly three plain path segments ending in <c>.strm</c>.</returns>
    public static bool IsSafeEpisodePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > 700)
        {
            return false;
        }

        var parts = relativePath.Split('/');
        return parts.Length == 3 && parts.All(IsPlainSegment) && parts[2].EndsWith(".strm", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a series or season folder from a catalog is safe to use: one plain segment for a series, two for a season.
    /// </summary>
    /// <param name="folderRel">The folder relative to the library folder (always with '/').</param>
    /// <param name="segments">1 for a series, 2 for a season.</param>
    /// <returns>True when the folder has exactly that many plain segments.</returns>
    public static bool IsSafeFolder(string? folderRel, int segments)
    {
        if (string.IsNullOrEmpty(folderRel) || folderRel.Length > 600)
        {
            return false;
        }

        var parts = folderRel.Split('/');
        return parts.Length == segments && parts.All(IsPlainSegment);
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
                loaded.Document.LoadedWithLoss = loaded.Dropped > 0 || !string.Equals(candidate, path, StringComparison.Ordinal);
                if (loaded.Dropped > 0)
                {
                    _logger.LogWarning("The catalog of peer {Peer} dropped {Count} entries that failed validation or passed a cap; their stubs stay on disk until the next sync removes them", Redact.Id(peerKey), loaded.Dropped);
                }

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

    // The fields each kind may carry. A movie has no TV fields; a series and a season are folders with no stub; an episode has three-segment paths and numbers in range.
    private static bool ValidShape(CatalogEntry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Movie:
                return (e.StubRelPath is null || IsSafeStubPath(e.StubRelPath))
                    && (e.State is not (EntryState.Active or EntryState.HeldRemoval) || e.StubRelPath is not null);
            case EntryKind.Series:
                // A series Home hides because it has the series itself has no folder.
                return e.StubRelPath is null && (e.FolderRel is null ? e.State is EntryState.DuplicateLocal or EntryState.DuplicatePeer : IsSafeFolder(e.FolderRel, 1));
            case EntryKind.Season:
                return e.StubRelPath is null && IsSafeFolder(e.FolderRel, 2) && IsKey(e.ParentKey) && e.Season is >= 0 and <= TvLimits.MaxSeasonNumber;
            default:
                return IsKey(e.ParentKey)
                    && e.Season is >= 0 and <= TvLimits.MaxSeasonNumber
                    && e.Episode is >= 0 and <= TvLimits.MaxEpisodeNumber
                    && (e.EpisodeEnd is null || (e.EpisodeEnd >= e.Episode && e.EpisodeEnd <= e.Episode + TvLimits.MaxEpisodeSpan))
                    && IsHex32(e.PeerSeriesId)
                    && (e.StubRelPath is null || IsSafeEpisodePath(e.StubRelPath))
                    && (e.State is not (EntryState.Active or EntryState.HeldRemoval) || e.StubRelPath is not null);
        }
    }

    private static bool IsHex32(string? value) =>
        value is { Length: 32 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsKey(string? value) =>
        value is { Length: >= 8 and <= 32 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z'));

    // A mapping key: letters, digits, '-' and '_' (the Add route's ValidKey), at most 64.
    private static bool IsMappingKey(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-' or '_');

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
        Func<string?, bool> safePath = entry.Kind == EntryKind.Episode ? IsSafeEpisodePath : IsSafeStubPath;
        entry.Sources = [.. entry.Sources.Where(s => IsKey(s.StubKey)
            && (s.PeerMediaSourceId.Length == 0 || IsHex32(s.PeerMediaSourceId))
            && (s.StubRelPath is null || safePath(s.StubRelPath))
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
        var playable = 0;
        var containers = 0;
        var kept = new List<CatalogEntry>();
        static bool Plausible(CatalogEntry e) =>
            Enum.IsDefined(e.Kind) && IsKey(e.StubKey) && IsHex32(e.PeerItemId)
            && (e.PeerMediaSourceId.Length == 0 || IsHex32(e.PeerMediaSourceId))
            && e.State is not EntryState.Removed
            && ValidShape(e);

        // 2.9.3: two series entries for one peer series (2.9.0 to 2.9.2 wrote one when a hidden record was promoted): the one with a folder is kept, so its
        // episodes stay; the first one won before, which dropped the stubs just written and made the next sync write another copy of the series.
        var seriesKept = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in document.Entries.Where(static e => e.Kind == EntryKind.Series && Plausible(e)))
        {
            var item = e.MappingKey + "/" + e.PeerItemId;
            if (!seriesKept.TryGetValue(item, out var have) || (have.FolderRel is null && e.FolderRel is not null && e.State is EntryState.Active or EntryState.HeldRemoval))
            {
                seriesKept[item] = e;
            }
        }

        foreach (var e in document.Entries)
        {
            var isContainer = e.Kind is EntryKind.Series or EntryKind.Season;
            if (!Plausible(e)
                || (e.Kind == EntryKind.Series && !ReferenceEquals(seriesKept.GetValueOrDefault(e.MappingKey + "/" + e.PeerItemId), e))
                || !seenKeys.Add(e.StubKey)
                || !seenItems.Add(e.MappingKey + "/" + e.PeerItemId))
            {
                continue;
            }

            // The cap of playable entries is the old one; series and seasons are counted apart and never push a playable entry out (DESIGN v2.7.2).
            if (isContainer ? ++containers > TvLimits.MaxContainerEntries : ++playable > CatalogDocument.MaxEntries)
            {
                continue;
            }

            kept.Add(e);
        }

        // Relationship fields are re-derived from what is valid: an episode or season whose series entry is not there is dropped.
        var seriesKeys = kept.Where(static e => e.Kind == EntryKind.Series).Select(static e => e.StubKey).ToHashSet(StringComparer.Ordinal);
        document.Entries = [.. kept.Where(e => e.Kind is EntryKind.Movie or EntryKind.Series || (e.ParentKey is not null && seriesKeys.Contains(e.ParentKey)))];
        foreach (var entry in document.Entries)
        {
            SanitizeSources(entry);
            entry.PeerEpisodeCount = entry.Kind == EntryKind.Series && entry.PeerEpisodeCount is { } listed ? Math.Clamp(listed, 0, TvLimits.MaxEpisodesPerSeries) : null;
        }

        document.Schema = CatalogDocument.CurrentSchema; // an older file is migrated by the steps above and written as the current one; nothing on disk is renamed
        document.PendingDetail = [.. document.PendingDetail.Where(IsHex32).Distinct(StringComparer.Ordinal).Take(TvLimits.MaxEpisodesPerSeries * 5)];
        document.ImagesPending = [.. document.ImagesPending.Where(IsKey).Distinct(StringComparer.Ordinal).Take(CatalogDocument.MaxEntries)];
        document.ImageFollowUps = Math.Clamp(document.ImageFollowUps, 0, TvLimits.MaxImageFollowUps);
        document.HeldAdditions = [.. document.HeldAdditions.Where(IsHex32).Distinct(StringComparer.Ordinal)];
        var stillHeld = document.HeldAdditions.ToHashSet(StringComparer.Ordinal);
        document.HeldAdditionInfo = [.. (document.HeldAdditionInfo ?? []).Where(h => h is not null && IsHex32(h.PeerItemId) && stillHeld.Contains(h.PeerItemId) && IsMappingKey(h.MappingKey) && Enum.IsDefined(h.Kind))
            .DistinctBy(static h => h.MappingKey + "/" + h.PeerItemId, StringComparer.Ordinal)
            .Take(CatalogDocument.MaxEntries + TvLimits.MaxSeriesPerPeer)];
        foreach (var info in document.HeldAdditionInfo)
        {
            info.Title = PeerText.Clean(info.Title, HeldAddition.TitleMax);
            info.Year = info.Year is >= 1870 and <= 2100 ? info.Year : null;
        }

        // K7: a stamp that is not a plausible shown name (40 characters) is treated as a different name, so the labels are rewritten once.
        document.LabelName = document.LabelName is { Length: > 80 } ? string.Empty : document.LabelName;
        document.TvHeldAdditions = [.. document.TvHeldAdditions.Where(IsHex32).Distinct(StringComparer.Ordinal).Take(CatalogDocument.MaxEntries + TvLimits.MaxSeriesPerPeer)];
        document.HeldSeries = [.. document.HeldSeries.Where(static h => IsKey(h.Key) && IsHex32(h.PeerSeriesId) && h.MappingKey.Length is > 0 and <= 64 && h.Title.Length <= 300).DistinctBy(static h => h.Key, StringComparer.Ordinal).Take(TvLimits.MaxSeriesPerPeer)];
        foreach (var held in document.HeldSeries)
        {
            held.Episodes = Math.Clamp(held.Episodes, 0, TvLimits.MaxEpisodesPerSeries);
            held.Year = held.Year is >= 1870 and <= 2100 ? held.Year : null;
        }

        document.HeldRemovals = [.. document.HeldRemovals.Where(k => document.Entries.Any(e => e.StubKey == k && e.State == EntryState.HeldRemoval))];
        document.Libraries = [.. document.Libraries.Where(l => l.PeerLibraryId.Length == 0 || IsHex32(l.PeerLibraryId))];
        document.LastSync?.Alerts.RemoveAll(static a => a.Length > 40);
        document.Names.People = [.. document.Names.People.Take(20_000)];
        document.Names.Genres = [.. document.Names.Genres.Take(20_000)];
        document.Names.Studios = [.. document.Names.Studios.Take(20_000)];
        return before - document.Entries.Count;
    }
}
