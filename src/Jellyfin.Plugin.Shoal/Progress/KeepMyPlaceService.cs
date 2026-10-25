using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>Await before removing a Shoal playable stub; false holds removal for safe retry.</summary>
public interface IKeepMyPlaceRetention
{
    /// <summary>Persists local history before sync removes a playable stub.</summary>
    /// <param name="absoluteStubPath">The locally owned stub file.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether deletion may proceed.</returns>
    Task<bool> RetainBeforeRemovalAsync(string absoluteStubPath, CancellationToken cancellationToken);
}

/// <summary>Feature-specific local ABI seam; no peer transport is available here.</summary>
internal interface IKeepMyPlaceLibrary
{
    /// <summary>Checks whether a local user still exists.</summary>
    /// <param name="userId">Local user.</param>
    /// <returns>Whether it exists.</returns>
    bool UserExists(Guid userId);

    /// <summary>Captures an indexed Shoal source, or null when ownership is unknown.</summary>
    /// <param name="itemId">Local item.</param>
    /// <returns>Current local captures.</returns>
    IReadOnlyList<KeepMyPlaceSnapshot>? Capture(Guid itemId);

    /// <summary>Captures before file deletion; empty means no indexed history, null means unsafe.</summary>
    /// <param name="path">Owned local file path.</param>
    /// <returns>Current captures or refusal.</returns>
    IReadOnlyList<KeepMyPlaceSnapshot>? CapturePath(string path);

    /// <summary>Returns a bounded page of indexed sources for catch-up.</summary>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page limit.</param>
    /// <returns>Local source item IDs.</returns>
    IReadOnlyList<Guid> SourceItems(int offset, int limit);

    /// <summary>Finds bounded visible real local candidates.</summary>
    /// <param name="snapshot">Local source.</param>
    /// <returns>Candidate media.</returns>
    IReadOnlyList<KeepMyPlaceMedia> Destinations(KeepMyPlaceSnapshot snapshot);

    /// <summary>Revalidates source, target and access immediately before the local save.</summary>
    /// <param name="snapshot">Captured source.</param>
    /// <param name="destinationItemId">Local target.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether progress was applied.</returns>
    bool ApplyFresh(KeepMyPlaceSnapshot snapshot, Guid destinationItemId, CancellationToken cancellationToken);
}

/// <summary>Serial reconciliation and awaited durable retention, independent of core events.</summary>
internal sealed class KeepMyPlaceService : IKeepMyPlaceRetention, IDisposable
{
    private readonly IKeepMyPlaceLibrary _library;
    private readonly KeepMyPlaceStore _store;
    private readonly Func<bool> _enabled;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _reconcileOffset;

    /// <summary>Initializes a new instance of the <see cref="KeepMyPlaceService"/> class.</summary>
    /// <param name="library">Local core adapter.</param>
    /// <param name="store">The private durable store.</param>
    /// <param name="enabled">Current setting.</param>
    public KeepMyPlaceService(IKeepMyPlaceLibrary library, KeepMyPlaceStore store, Func<bool> enabled)
    {
        _library = library;
        _store = store;
        _enabled = enabled;
    }

    /// <inheritdoc />
    public async Task<bool> RetainBeforeRemovalAsync(string absoluteStubPath, CancellationToken cancellationToken)
    {
        if (!_enabled())
        {
            return true;
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = _library.CapturePath(absoluteStubPath);
            return snapshots is not null && _store.Save(snapshots, _library.UserExists);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Captures current history and reconciles with bounded indexed local destinations.</summary>
    /// <param name="changedItems">Coalesced source item events.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Completion.</returns>
    public async Task ReconcileAsync(IReadOnlyList<Guid> changedItems, CancellationToken cancellationToken)
    {
        if (!_enabled())
        {
            return;
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allCaptures = new List<KeepMyPlaceSnapshot>();
            foreach (var id in changedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_library.Capture(id) is { } captures)
                {
                    allCaptures.AddRange(captures);
                }
            }

            if (!_store.Healthy || !_store.Save(allCaptures, _library.UserExists))
            {
                return;
            }

            // A target may have several sources; resolve newest meaningful history first, never peer order.
            var all = _store.Read();
            var batch = all.Skip(_reconcileOffset).Take(128).ToArray();
            _reconcileOffset = _reconcileOffset + batch.Length >= all.Count ? 0 : _reconcileOffset + batch.Length;
            var targets = new Dictionary<(Guid User, Guid Item), List<KeepMyPlaceSnapshot>>();
            var targetMedia = new Dictionary<(Guid User, Guid Item), KeepMyPlaceMedia>();
            foreach (var snapshot in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_library.UserExists(snapshot.Source.History.UserId))
                {
                    continue;
                }

                if (!snapshot.Source.History.Played && snapshot.Source.History.PositionTicks <= 0)
                {
                    continue;
                }

                var matches = _library.Destinations(snapshot).Where(d => KeepMyPlacePolicy.Matches(snapshot.Source.Media.Identity, d.Identity)).Take(2).ToArray();
                if (matches.Length != 1)
                {
                    continue; // two real local versions are ambiguous, even if one is pristine
                }

                var key = (snapshot.Source.History.UserId, matches[0].ItemId);
                targetMedia[key] = matches[0];
                if (!targets.TryGetValue(key, out var sources))
                {
                    targets[key] = sources = [];
                }

                sources.Add(snapshot);
            }

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_store.Transferred(target.Key.User, target.Key.Item))
                {
                    continue;
                }

                var ordered = all.Where(s => s.Source.History.UserId == target.Key.User
                        && (s.Source.History.Played || s.Source.History.PositionTicks > 0)
                        && KeepMyPlacePolicy.Matches(s.Source.Media.Identity, targetMedia[target.Key].Identity))
                    .OrderByDescending(s => s.Source.History.LastPlayedUtc ?? s.CapturedUtc).ToArray();
                if (ordered.Length is 0 or > 128 || ordered.Any(a => ordered.Any(b => !KeepMyPlacePolicy.Matches(a.Source.Media.Identity, b.Source.Media.Identity))))
                {
                    continue;
                }

                var newest = ordered[0];
                var date = newest.Source.History.LastPlayedUtc ?? newest.CapturedUtc;
                if (ordered.Any(s => (s.Source.History.LastPlayedUtc ?? s.CapturedUtc) == date && s.Source.History != newest.Source.History))
                {
                    continue;
                }

                if (_library.ApplyFresh(newest, target.Key.Item, cancellationToken))
                {
                    _store.MarkTransferred(target.Key.User, target.Key.Item);
                }
            }

            _store.Save([], _library.UserExists); // prune deleted users and expired captures
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Runs retention maintenance while opted out without scanning media or changing core history.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Completion.</returns>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Prune(_library.UserExists);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _serial.Dispose();
}
