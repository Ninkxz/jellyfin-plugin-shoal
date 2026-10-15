using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// The encrypted store for peer tokens (<c>secrets.enc</c>, DESIGN 8.2). Keyed by <see cref="PeerState.PeerKey"/>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Gets the status.</summary>
    SecretStoreStatus Status { get; }

    /// <summary>Gets information for the admin UI (no secrets).</summary>
    SecretStoreInfo Info { get; }

    /// <summary>Gets the peer keys that have a credential, or null when the store is unreadable.</summary>
    IReadOnlyCollection<string>? PeerKeys { get; }

    /// <summary>
    /// Gets a credential.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <returns>The credential, or null when there is none or the store is unreadable.</returns>
    PeerCredential? TryGet(string peerKey);

    /// <summary>
    /// Stores (or replaces) a credential and writes the file atomically.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <param name="credential">The credential.</param>
    /// <param name="cancellationToken">Cancellation (before the write starts).</param>
    /// <returns>A task that completes when the credential is on disk.</returns>
    /// <exception cref="InvalidOperationException">The store is unreadable; nothing is overwritten.</exception>
    Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a credential.
    /// </summary>
    /// <param name="peerKey">The peer key.</param>
    /// <param name="cancellationToken">Cancellation (before the write starts).</param>
    /// <returns>True when a credential was removed.</returns>
    /// <exception cref="InvalidOperationException">The store is unreadable; nothing is overwritten.</exception>
    Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Start over": moves <c>secrets.enc</c> (and <c>master.key</c> when the key is kept there) aside as <c>*.corrupt-&lt;timestamp&gt;</c> and
    /// starts with an empty store. Every peer then needs a new pairing. This is the only way out of an unreadable store besides
    /// restoring the key; Shoal never does it by itself.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the store has been reloaded.</returns>
    /// <exception cref="InvalidOperationException">The key comes from the environment and is not usable; fix the variable instead.</exception>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
