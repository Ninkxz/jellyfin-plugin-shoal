using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>
/// One lock per share, shared by everything that changes the sign-in state of a share: token rotation, approval, pause, resume and revoke. Without it
/// a pause that lands in the middle of a rotation could be undone by the rotation's stale policy (Warden 10 M2).
/// </summary>
public sealed class ShareLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the lock of a share without waiting.
    /// </summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The held lock, or null when another operation holds it.</returns>
    public IDisposable? TryHold(string shareId)
    {
        var gate = For(shareId);
        return gate.Wait(0, CancellationToken.None) ? new Releaser(gate) : null;
    }

    /// <summary>
    /// Waits for the lock of a share.
    /// </summary>
    /// <param name="shareId">The share.</param>
    /// <param name="cancellationToken">Cancellation of the wait.</param>
    /// <returns>The held lock.</returns>
    public async Task<IDisposable> HoldAsync(string shareId, CancellationToken cancellationToken)
    {
        var gate = For(shareId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private SemaphoreSlim For(string shareId) => _locks.GetOrAdd(shareId, static _ => new SemaphoreSlim(1, 1));

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
