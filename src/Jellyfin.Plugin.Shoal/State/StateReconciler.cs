using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// What kind of mismatch the startup reconciliation found (DESIGN 3.7). Nothing is ever repaired or deleted automatically;
/// the admin chooses "Relink" or "Delete".
/// </summary>
public enum ReconcileKind
{
    /// <summary>A peer in <c>state.json</c> has no entry in the secret store (it cannot authenticate).</summary>
    PeerWithoutSecret,

    /// <summary>A peer in <c>state.json</c> has no <c>peers/&lt;peerKey&gt;/</c> folder.</summary>
    PeerWithoutFolder,

    /// <summary>A mapped library has no Jellyfin library whose only path is its stub folder.</summary>
    LibraryMissing,

    /// <summary>The secret store has an entry for a peer that is not in <c>state.json</c>.</summary>
    SecretWithoutPeer,

    /// <summary>A <c>peers/&lt;key&gt;/</c> folder belongs to no peer in <c>state.json</c>.</summary>
    FolderWithoutPeer,

    /// <summary>
    /// A Jellyfin library lies under the stub root but belongs to no peer: "Restore detected: re-pair or remove this library".
    /// </summary>
    LibraryWithoutPeer
}

/// <summary>
/// One mismatch.
/// </summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="PeerKey">The peer concerned (a key or folder name), when there is one.</param>
/// <param name="Detail">A library name or folder name for display (text only), or null.</param>
public sealed record ReconcileFinding(ReconcileKind Kind, string? PeerKey, string? Detail);

/// <summary>
/// A Jellyfin library with its paths.
/// </summary>
/// <param name="Name">The library name.</param>
/// <param name="Paths">The library's folders.</param>
public sealed record LibraryPaths(string Name, IReadOnlyList<string> Paths);

/// <summary>
/// What the reconciliation compares the state with.
/// </summary>
/// <param name="SecretPeerKeys">The peer keys that have a secret, or null when the secret store cannot be read (the secret checks are skipped).</param>
/// <param name="PeerFolderNames">The folder names found below <c>peers/</c>.</param>
/// <param name="Libraries">The Jellyfin libraries.</param>
public sealed record ReconcileInputs(
    IReadOnlyCollection<string>? SecretPeerKeys,
    IReadOnlyCollection<string> PeerFolderNames,
    IReadOnlyCollection<LibraryPaths> Libraries);

/// <summary>
/// The result of a reconciliation.
/// </summary>
/// <param name="Findings">The mismatches; empty when everything lines up.</param>
public sealed record ReconciliationReport(IReadOnlyList<ReconcileFinding> Findings)
{
    /// <summary>Gets a value indicating whether nothing needs attention.</summary>
    public bool IsClean => Findings.Count == 0;
}

/// <summary>
/// The read-only startup reconciliation of <c>state.json</c>, the secret store, the per-peer folders and the Jellyfin libraries
/// (DESIGN 3.7). A pure function: it reads nothing and changes nothing.
/// </summary>
public static class StateReconciler
{
    /// <summary>
    /// Compares the state with what exists.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="inputs">The things to compare with.</param>
    /// <returns>The report.</returns>
    public static ReconciliationReport Compute(ShoalState state, ReconcileInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);

        var findings = new List<ReconcileFinding>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var stubRoot = Normalize(state.StubRoot);
        var peerKeys = state.Home.Peers.Select(p => p.PeerKey).ToHashSet(StringComparer.Ordinal);
        var secretKeys = inputs.SecretPeerKeys?.ToHashSet(StringComparer.Ordinal);
        var folders = inputs.PeerFolderNames.ToHashSet(StringComparer.Ordinal);

        foreach (var peer in state.Home.Peers)
        {
            // A peer that is still pairing, or whose pairing was stopped, is stored disabled with no secret and no libraries (DESIGN 3.7): only a
            // peer that finished a pairing is expected to have a secret.
            if (peer.Pairing is not null && secretKeys is not null && !secretKeys.Contains(peer.PeerKey))
            {
                findings.Add(new ReconcileFinding(ReconcileKind.PeerWithoutSecret, peer.PeerKey, peer.DisplayName));
            }

            if (!folders.Contains(peer.PeerKey))
            {
                findings.Add(new ReconcileFinding(ReconcileKind.PeerWithoutFolder, peer.PeerKey, peer.DisplayName));
            }

            foreach (var mapping in peer.Libraries)
            {
                var expected = stubRoot.Length == 0 ? null : Normalize(Path.Combine(stubRoot, peer.PeerKey, mapping.Subfolder));
                var found = expected is not null
                    && inputs.Libraries.Any(l => l.Paths.Count == 1 && string.Equals(Normalize(l.Paths[0]), expected, comparison));
                if (!found)
                {
                    findings.Add(new ReconcileFinding(ReconcileKind.LibraryMissing, peer.PeerKey, mapping.LocalLibraryName));
                }
            }
        }

        if (secretKeys is not null)
        {
            findings.AddRange(secretKeys
                .Where(key => !peerKeys.Contains(key))
                .Order(StringComparer.Ordinal)
                .Select(key => new ReconcileFinding(ReconcileKind.SecretWithoutPeer, key, null)));
        }

        findings.AddRange(folders
            .Where(folder => !peerKeys.Contains(folder))
            .Order(StringComparer.Ordinal)
            .Select(folder => new ReconcileFinding(ReconcileKind.FolderWithoutPeer, folder, folder)));

        if (stubRoot.Length > 0)
        {
            foreach (var library in inputs.Libraries)
            {
                foreach (var path in library.Paths)
                {
                    var normalized = Normalize(path);
                    var prefix = stubRoot + Path.DirectorySeparatorChar;
                    if (!normalized.StartsWith(prefix, comparison))
                    {
                        continue;
                    }

                    var firstSegment = normalized[prefix.Length..].Split(Path.DirectorySeparatorChar)[0];
                    if (!peerKeys.Contains(firstSegment))
                    {
                        findings.Add(new ReconcileFinding(ReconcileKind.LibraryWithoutPeer, null, library.Name));
                        break;
                    }
                }
            }
        }

        return new ReconciliationReport(findings);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
