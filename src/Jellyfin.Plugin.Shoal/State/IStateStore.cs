using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Shoal's own atomic store for topology and security settings (DESIGN 3.2a, 3.7). One writer at a time; every write is a
/// temp file, fsync and replace, with a <c>.bak</c> and a daily copy. Core's plugin XML is not used for anything critical
/// because core silently replaces it with defaults when it cannot read it.
/// </summary>
public interface IStateStore
{
    /// <summary>Raised after a successful write. Handlers run on the writer's thread; they must not block.</summary>
    event EventHandler<StateChangedEventArgs>? Changed;

    /// <summary>Gets the status. In <see cref="StateStatus.Recovery"/> <see cref="Current"/> is empty (every peer counts as disabled).</summary>
    StateStatus Status { get; }

    /// <summary>Gets the current state. Never null, never changed in place: treat it as read-only.</summary>
    ShoalState Current { get; }

    /// <summary>Gets what happened when the state was loaded.</summary>
    StateLoadInfo LoadInfo { get; }

    /// <summary>
    /// "Confirm" on the restore banner: clears <see cref="StateLoadInfo.AccessReviewRequired"/> after the admin has reviewed who can see each
    /// library and which friends can connect (DESIGN 3.7). It changes nothing else.
    /// </summary>
    /// <returns>True when a review was pending and is now confirmed; false when nothing was waiting (the caller answers 409).</returns>
    bool ConfirmRestore();

    /// <summary>
    /// Applies a change: the current state is cloned, <paramref name="mutate"/> edits the clone, the clone is validated and
    /// written atomically, and only then does it become <see cref="Current"/>. When anything fails nothing changes.
    /// </summary>
    /// <param name="mutate">The change.</param>
    /// <param name="cancellationToken">Cancellation (before the write starts).</param>
    /// <returns>The new state.</returns>
    /// <exception cref="StateRecoveryException">The store is in recovery mode.</exception>
    /// <exception cref="InvalidOperationException">The changed state failed validation.</exception>
    Task<ShoalState> UpdateAsync(Action<ShoalState> mutate, CancellationToken cancellationToken = default);

    /// <summary>Lists the files that could hold the state, newest preference first, with their health (read-only).</summary>
    /// <returns>The candidates.</returns>
    IReadOnlyList<StateCandidate> GetCandidates();

    /// <summary>
    /// "Restore from file": makes a usable candidate the state. A primary that exists is kept as
    /// <c>state.json.corrupt-&lt;timestamp&gt;</c>, never overwritten in place.
    /// </summary>
    /// <param name="candidateFileName">A <see cref="StateCandidate.FileName"/> from <see cref="GetCandidates"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the state is restored.</returns>
    /// <exception cref="InvalidOperationException">The candidate does not exist or is not usable.</exception>
    Task RestoreFromAsync(string candidateFileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Start over": moves <c>state.json</c> and <c>state.json.bak</c> aside as <c>*.corrupt-&lt;timestamp&gt;</c> and starts with an empty state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the new state is written.</returns>
    Task StartOverAsync(CancellationToken cancellationToken = default);
}
