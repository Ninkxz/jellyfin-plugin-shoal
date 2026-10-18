using System;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// One layout change at a time (PLAN v2.9 K44): creating, renaming or deleting a library, changing its access or the order, and the move job all take this lock.
/// A move job holds it for its whole run, so a second change while a job runs is refused rather than queued.
/// </summary>
public sealed class LayoutLock : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _jobRunning;

    /// <summary>Gets a value indicating whether a move job holds the lock.</summary>
    public bool JobRunning => _jobRunning;

    /// <summary>
    /// Takes the lock without waiting.
    /// </summary>
    /// <param name="job">Whether the caller is a move job (shown as "a move is running").</param>
    /// <returns>A handle that releases the lock, or null when it is taken.</returns>
    public IDisposable? TryEnter(bool job = false)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }

        _jobRunning = job;
        return new Release(this);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed class Release(LayoutLock owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner._jobRunning = false;
                owner._gate.Release();
            }
        }
    }
}
