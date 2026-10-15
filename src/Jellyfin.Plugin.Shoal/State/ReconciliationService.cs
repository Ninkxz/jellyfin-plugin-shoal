using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Gathers what <see cref="StateReconciler"/> compares (the secret store, the per-peer folders and the Jellyfin libraries) and
/// runs it. Read-only.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly ShoalPaths _paths;
    private readonly ILibraryManager _libraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="libraries">The library manager.</param>
    public ReconciliationService(IStateStore state, ISecretStore secrets, ShoalPaths paths, ILibraryManager libraries)
    {
        _state = state;
        _secrets = secrets;
        _paths = paths;
        _libraries = libraries;
    }

    /// <summary>
    /// Compares <c>state.json</c> with the secrets, the folders and the libraries. In recovery mode there is nothing
    /// meaningful to compare, so the report is empty.
    /// </summary>
    /// <returns>The report.</returns>
    public ReconciliationReport Compute()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return new ReconciliationReport([]);
        }

        string[] folders = [];
        try
        {
            if (Directory.Exists(_paths.PeersDir))
            {
                folders = Directory.EnumerateDirectories(_paths.PeersDir).Select(Path.GetFileName).OfType<string>().ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder shows up as "peer without folder" findings; nothing else to do here.
        }

        var libraries = _libraries.GetVirtualFolders()
            .Select(folder => new LibraryPaths(folder.Name, folder.Locations ?? []))
            .ToList();
        return StateReconciler.Compute(_state.Current, new ReconcileInputs(_secrets.PeerKeys, folders, libraries));
    }
}
