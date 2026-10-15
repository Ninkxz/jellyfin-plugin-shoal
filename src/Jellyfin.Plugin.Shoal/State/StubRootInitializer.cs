using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// Gives a fresh install a stub root. Without one nothing can be imported (adding a library answers 409 <c>noStubRoot</c>), and nothing else ever sets it
/// but the admin. When the stored value is empty this creates <see cref="ShoalPaths.DefaultStubRoot"/>, runs the same check the Settings page runs, and
/// stores it. A value the admin set is never touched, and a folder that fails the check is left unset.
/// </summary>
public sealed class StubRootInitializer : IHostedService
{
    private readonly IStateStore _state;
    private readonly IStubRootChecker _checker;
    private readonly ShoalPaths _paths;
    private readonly ILogger<StubRootInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRootInitializer"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="checker">The folder check of the Settings page.</param>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="logger">The logger.</param>
    public StubRootInitializer(IStateStore state, IStubRootChecker checker, ShoalPaths paths, ILogger<StubRootInitializer> logger)
    {
        _state = state;
        _checker = checker;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Sets the default stub root when none is set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a stub root is set afterwards (already there, or just made); false when there is none and the default could not be used.</returns>
    public async Task<bool> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return false; // recovery mode writes nothing
        }

        if (!string.IsNullOrWhiteSpace(_state.Current.StubRoot))
        {
            return true;
        }

        var path = _paths.DefaultStubRoot;
        try
        {
            SecureDirectory.Ensure(path);
            var check = _checker.Check(path);
            if (check != StubRootCheck.Ok)
            {
                _logger.LogWarning("The default stub root cannot be used ({Check}); choose a folder in Settings", check);
                return false;
            }

            await _state.UpdateAsync(
                s =>
                {
                    if (string.IsNullOrWhiteSpace(s.StubRoot))
                    {
                        s.StubRoot = path; // re-read inside the update: an admin who set one meanwhile keeps theirs
                    }
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Shoal set its stub root to the default folder inside its data folder");
            return !string.IsNullOrWhiteSpace(_state.Current.StubRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("The default stub root could not be set up ({Error}); choose a folder in Settings", ex.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning("The default stub root could not be set up at startup ({Error})", ex.GetType().Name); // never blocks the server
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
