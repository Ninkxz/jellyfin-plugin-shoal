using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Shoal.Storage;

/// <summary>
/// The layout of Shoal's own data folder, <c>&lt;DataPath&gt;/shoal/</c> (DESIGN 3.1). Directories are 0700 and files 0600 on Linux.
/// </summary>
public sealed partial class ShoalPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalPaths"/> class.
    /// </summary>
    /// <param name="dataPath">Jellyfin's <c>IApplicationPaths.DataPath</c> (a local volume).</param>
    public ShoalPaths(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        Root = Path.Combine(dataPath, "shoal");
    }

    /// <summary>Gets the Shoal data folder.</summary>
    public string Root { get; }

    /// <summary>Gets the path of <c>state.json</c>.</summary>
    public string StateFile => Path.Combine(Root, "state.json");

    /// <summary>Gets the path of <c>state.json.bak</c> (the previous good state).</summary>
    public string StateBackup => StateFile + ".bak";

    /// <summary>Gets the folder for the daily copies of the state.</summary>
    public string BackupsDir => Path.Combine(Root, "backups");

    /// <summary>Gets the path of <c>master.key</c>.</summary>
    public string MasterKeyFile => Path.Combine(Root, "master.key");

    /// <summary>Gets the path of <c>secrets.enc</c>.</summary>
    public string SecretsFile => Path.Combine(Root, "secrets.enc");

    /// <summary>Gets the folder that holds one sub-folder per peer.</summary>
    public string PeersDir => Path.Combine(Root, "peers");

    /// <summary>Gets the folder for Peer-role data.</summary>
    public string PeerRoleDir => Path.Combine(Root, "peer-role");

    /// <summary>Gets the default stub root.</summary>
    public string DefaultStubRoot => Path.Combine(Root, "stubs");

    /// <summary>
    /// Checks the shape of a peer key. The key names folders and secret entries, so nothing else is ever used in a path.
    /// </summary>
    /// <param name="peerKey">The candidate.</param>
    /// <returns>True when it is 16 to 64 lower-case hex characters.</returns>
    public static bool IsValidPeerKey(string? peerKey) => peerKey is not null && PeerKeyRegex().IsMatch(peerKey);

    /// <summary>
    /// Gets the folder of one peer.
    /// </summary>
    /// <param name="peerKey">A valid peer key.</param>
    /// <returns>The folder path.</returns>
    /// <exception cref="ArgumentException">The key is not valid.</exception>
    public string PeerDir(string peerKey) =>
        IsValidPeerKey(peerKey) ? Path.Combine(PeersDir, peerKey) : throw new ArgumentException("Invalid peer key.", nameof(peerKey));

    [GeneratedRegex(@"\A[0-9a-f]{16,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PeerKeyRegex();
}
