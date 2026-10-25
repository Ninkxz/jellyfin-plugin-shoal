using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Storage;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>Private capture with opaque local ownership, retained at most 90 days.</summary>
internal sealed record KeepMyPlaceSnapshot(KeepMyPlaceSource Source, FederatedItem Owner, DateTime CapturedUtc, IReadOnlyList<Guid>? SourceFolderIds = null);

/// <summary>Bounded private atomic history store. Corruption and write failure block deletion.</summary>
internal sealed class KeepMyPlaceStore
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 12 };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private List<KeepMyPlaceSnapshot> _entries = [];
    private List<TransferReceipt> _receipts = [];
    private bool _healthy = true;

    /// <summary>Initializes a new instance of the <see cref="KeepMyPlaceStore"/> class.</summary>
    /// <param name="path">Inside Shoal's protected data directory.</param>
    /// <param name="time">The local clock.</param>
    public KeepMyPlaceStore(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxBytes)
                {
                    _healthy = false;
                    return;
                }

                var document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllBytes(path), JsonOptions);
                _healthy = document is { Schema: 1, Entries.Count: <= MaxEntries, Receipts.Count: <= MaxEntries } && document.Entries.All(Valid)
                    && document.Entries.Select(s => (s.Source.History.UserId, s.Source.Media.ItemId)).Distinct().Count() == document.Entries.Count
                    && document.Receipts.All(r => r is not null && r.User != Guid.Empty && r.Destination != Guid.Empty && r.WrittenUtc.Kind == DateTimeKind.Utc);
                if (_healthy)
                {
                    _entries = document!.Entries;
                    _receipts = document.Receipts;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _healthy = false;
        }
    }

    /// <summary>Gets a value indicating whether loading succeeded. No history is exposed in admin status.</summary>
    public bool Healthy => _healthy;

    /// <summary>Prunes private history even when disabled, without creating a file on a fresh install.</summary>
    /// <param name="userExists">Current local user existence only.</param>
    /// <returns>Whether maintenance succeeded.</returns>
    public bool Prune(Func<Guid, bool> userExists)
    {
        lock (_gate)
        {
            return !File.Exists(_path) || Save([], userExists);
        }
    }

    /// <summary>Returns detached recent captures under a lock.</summary>
    /// <returns>The private captures.</returns>
    public IReadOnlyList<KeepMyPlaceSnapshot> Read()
    {
        lock (_gate)
        {
            return _healthy ? _entries.Where(Recent).ToArray() : [];
        }
    }

    /// <summary>Atomically replaces each captured source/user entry, including resets.</summary>
    /// <param name="snapshots">Locally verified captures.</param>
    /// <param name="userExists">Current local user membership, used to purge deleted users.</param>
    /// <returns>Whether persistence succeeded. A false result must hold removal.</returns>
    public bool Save(IReadOnlyList<KeepMyPlaceSnapshot> snapshots, Func<Guid, bool> userExists)
    {
        lock (_gate)
        {
            if (!_healthy || snapshots.Any(s => !Valid(s)))
            {
                return false;
            }

            var replacements = snapshots.Select(s => (s.Source.History.UserId, s.Source.Media.ItemId)).ToHashSet();
            var existing = _entries.ToDictionary(s => (s.Source.History.UserId, s.Source.Media.ItemId));
            var next = _entries.Where(s => Recent(s) && userExists(s.Source.History.UserId) && !replacements.Contains((s.Source.History.UserId, s.Source.Media.ItemId)))
                .Concat(snapshots.Where(s => userExists(s.Source.History.UserId)).Select(s => existing.TryGetValue((s.Source.History.UserId, s.Source.Media.ItemId), out var prior) && Recent(prior) && SameCapture(prior, s) ? prior : s)).ToList();
            if (next.Count > MaxEntries)
            {
                return false; // never evict unconsumed history merely to make a new deletion succeed
            }

            try
            {
                var receipts = _receipts.Where(r => userExists(r.User) && r.WrittenUtc >= _time.GetUtcNow().UtcDateTime.AddDays(-90)).ToList();
                if (next.Count == _entries.Count && receipts.Count == _receipts.Count && next.All(s => existing.TryGetValue((s.Source.History.UserId, s.Source.Media.ItemId), out var prior) && ReferenceEquals(prior, s))
                    && File.Exists(_path))
                {
                    return true;
                }

                var content = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, next, receipts), JsonOptions);
                if (content.Length > MaxBytes)
                {
                    return false;
                }

                AtomicFile.Write(_path, content);
                _entries = next;
                _receipts = receipts;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Checks a durable terminal receipt to prevent replay after a local reset.</summary>
    /// <param name="user">The local user.</param>
    /// <param name="destination">The local item.</param>
    /// <returns>Whether a transfer was already attempted successfully.</returns>
    public bool Transferred(Guid user, Guid destination)
    {
        lock (_gate)
        {
            return _receipts.Any(r => r.User == user && r.Destination == destination);
        }
    }

    /// <summary>Persists a terminal receipt after a successful core save.</summary>
    /// <param name="user">The local user.</param>
    /// <param name="destination">The local item.</param>
    /// <returns>Whether the receipt is durable.</returns>
    public bool MarkTransferred(Guid user, Guid destination)
    {
        lock (_gate)
        {
            if (!_healthy || _receipts.Count >= MaxEntries)
            {
                return false;
            }

            try
            {
                var receipts = _receipts.Append(new TransferReceipt(user, destination, _time.GetUtcNow().UtcDateTime)).ToList();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, _entries, receipts), JsonOptions);
                if (bytes.Length > MaxBytes)
                {
                    return false;
                }

                AtomicFile.Write(_path, bytes);
                _receipts = receipts;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private bool Valid(KeepMyPlaceSnapshot? s) => s is { Source.Media.Identity: { }, Source.History: { }, Owner: { } }
        && s.Source.Media.ItemId != Guid.Empty && s.Source.History.UserId != Guid.Empty
        && s.CapturedUtc.Kind == DateTimeKind.Utc && s.CapturedUtc > DateTime.UnixEpoch && s.CapturedUtc <= _time.GetUtcNow().UtcDateTime
        && s.Source.History.PositionTicks is >= 0 and <= TimeSpan.TicksPerDay * 30 && s.Source.History.PlayCount >= 0
        && (s.Source.History.LastPlayedUtc is null || (s.Source.History.LastPlayedUtc.Value.Kind == DateTimeKind.Utc && s.Source.History.LastPlayedUtc <= _time.GetUtcNow().UtcDateTime))
        && (s.Source.Media.RuntimeTicks is null || s.Source.Media.RuntimeTicks is > 0 and <= TimeSpan.TicksPerDay * 30)
        && Enum.IsDefined(s.Source.Media.Identity.Kind)
        && (s.Source.Media.Identity.Kind == KeepMyPlaceKind.Movie || (s.Source.Media.Identity.Season is >= 0 && s.Source.Media.Identity.Episode is > 0 && (s.Source.Media.Identity.End ?? s.Source.Media.Identity.Episode) >= s.Source.Media.Identity.Episode))
        && ShoalPaths.IsValidPeerKey(s.Owner.PeerKey) && s.Owner.MappingKey is { Length: > 0 and <= 64 } && s.Owner.LibKey is { Length: <= 64 }
        && s.Source.Media.Edition?.Length is not > 256
        && (s.SourceFolderIds is null || (s.SourceFolderIds.Count <= 16 && s.SourceFolderIds.All(id => id != Guid.Empty)))
        && ValidIds(s.Source.Media.Identity.Ids) && (s.Source.Media.Identity.EpisodeIds is null || ValidIds(s.Source.Media.Identity.EpisodeIds));

    private static bool ValidIds(IReadOnlyDictionary<string, string>? ids) => ids is { Count: <= 3 }
        && ids.All(p => p.Key is "Imdb" or "Tmdb" or "Tvdb" && p.Value is { Length: > 0 and <= 64 });

    private static bool SameCapture(KeepMyPlaceSnapshot a, KeepMyPlaceSnapshot b) => a.Source.History == b.Source.History && a.Owner == b.Owner
        && a.Source.Media.RuntimeTicks == b.Source.Media.RuntimeTicks && a.Source.Media.Edition == b.Source.Media.Edition
        && a.Source.Media.Identity.Ids.Count == b.Source.Media.Identity.Ids.Count
        && (a.Source.Media.Identity.EpisodeIds?.Count ?? 0) == (b.Source.Media.Identity.EpisodeIds?.Count ?? 0)
        && KeepMyPlacePolicy.Matches(a.Source.Media.Identity, b.Source.Media.Identity)
        && (a.SourceFolderIds ?? []).SequenceEqual(b.SourceFolderIds ?? []);

    private bool Recent(KeepMyPlaceSnapshot s) => s.CapturedUtc <= _time.GetUtcNow().UtcDateTime && s.CapturedUtc >= _time.GetUtcNow().UtcDateTime.AddDays(-90);

    private sealed record StoreDocument(int Schema, List<KeepMyPlaceSnapshot> Entries, List<TransferReceipt> Receipts);

    private sealed record TransferReceipt(Guid User, Guid Destination, DateTime WrittenUtc);
}
