using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal;

/// <summary>
/// Writes one summary to the log at startup: how the state and the secrets loaded, what was adjusted, and what the read-only
/// reconciliation found. It changes nothing and logs no secret, token, address or peer id.
/// </summary>
public sealed class StartupReport : IHostedService
{
    private readonly IStateStore _state;
    private readonly ISecretStore _secrets;
    private readonly ReconciliationService _reconciliation;
    private readonly ShoalPaths _paths;
    private readonly ILogger<StartupReport> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupReport"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="reconciliation">The reconciliation service.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public StartupReport(IStateStore state, ISecretStore secrets, ReconciliationService reconciliation, ShoalPaths paths, ILogger<StartupReport> logger)
    {
        _state = state;
        _secrets = secrets;
        _reconciliation = reconciliation;
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var info = _state.LoadInfo;
        _logger.LogInformation(
            "Shoal state: {Status}, loaded from {Source}, {Peers} peer(s), {Shares} share(s); hardening {Mode}",
            _state.Status,
            info.Source,
            _state.Current.Home.Peers.Count,
            _state.Current.PeerRole.Shares.Count,
            _state.Current.PeerRole.HardeningMode);
        if (_state.Status == StateStatus.Recovery)
        {
            _logger.LogError("Shoal is in recovery mode ({Reason}): every peer is disabled and nothing is written until the admin restores a state file or starts over", info.Recovery);
        }
        else if (info.RestoredFrom is not null)
        {
            _logger.LogWarning("Shoal restored its settings from {File}; the unreadable state.json was kept as {Kept}", info.RestoredFrom, info.QuarantinedAs ?? "(nothing to keep)");
        }

        var secrets = _secrets.Info;
        _logger.LogInformation("Shoal secrets: {Status}, key from {KeySource}, {Entries} credential(s)", secrets.Status, secrets.KeySource, secrets.Entries);
        if (secrets.Status != SecretStoreStatus.Ready)
        {
            _logger.LogError("Shoal cannot read its secrets ({Reason}); every peer is refused until the key is restored or the peers are paired again", secrets.Reason);
            if (secrets.Hint is not null)
            {
                _logger.LogError("{Hint}", secrets.Hint);
            }
        }

        if (secrets.VolumeWarning is not null)
        {
            _logger.LogWarning("{Warning}", secrets.VolumeWarning);
        }

        if (Plugin.Instance is { } plugin)
        {
            foreach (var note in plugin.SettingsNotes)
            {
                _logger.LogWarning("Shoal settings adjusted: {Note}", note);
            }
        }

        try
        {
            var report = _reconciliation.Compute();
            foreach (var group in report.Findings.GroupBy(f => f.Kind))
            {
                _logger.LogWarning("Shoal reconciliation: {Kind} x{Count} (see the admin page; nothing was changed)", group.Key, group.Count());
            }
        }
#pragma warning disable CA1031 // Startup reporting must never stop the server from starting; only the exception type is logged.
        catch (System.Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("Shoal reconciliation could not run: {Error}", ex.GetType().Name);
        }

        _logger.LogInformation("Shoal data folder flush to disk: {Supported}", NativeFsync.SyncDirectory(_paths.Root) ? "directory fsync works" : "file fsync only");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
