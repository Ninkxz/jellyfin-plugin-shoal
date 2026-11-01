using System;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Single-flight between the delete batch and the legacy Restore (plan: "the two never overlap"). Register as a singleton.</summary>
internal sealed class RemovalGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Takes the gate without waiting.</summary>
    /// <returns>The release handle, or null when the other side holds it.</returns>
    public IDisposable? TryEnter() => _gate.Wait(0) ? new Release(_gate) : null;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
