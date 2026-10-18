using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// Locks the options of Shoal's libraries again soon after anyone saves library options (Pilot 15 R6: an NFO saver turned on by hand stayed on until the next
/// scan). Saves close together are folded into one pass after a short delay; saves made while a guard pass runs are the pass's own and are ignored (Warden 12 L1;
/// a save by someone else in that moment is picked up by the next scan).
/// </summary>
public sealed class LibraryOptionsWatch : IHostedService, IDisposable
{
    private readonly Func<int> _enforce;
    private readonly Func<bool> _isEnforcing;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _delay;
    private readonly CancellationTokenSource _stop = new();
    private int _pending;
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOptionsWatch"/> class.
    /// </summary>
    /// <param name="guard">The options guard.</param>
    /// <param name="logger">The logger.</param>
    public LibraryOptionsWatch(LibraryOptionsGuard guard, ILogger<LibraryOptionsWatch> logger)
        : this((guard ?? throw new ArgumentNullException(nameof(guard))).Enforce, logger, TimeProvider.System, TimeSpan.FromSeconds(2), () => guard.Enforcing)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOptionsWatch"/> class (tests: any pass, clock and delay).
    /// </summary>
    /// <param name="enforce">The pass.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    /// <param name="delay">How long saves are folded together.</param>
    /// <param name="isEnforcing">Whether a guard pass runs now (its own saves are ignored, Warden 12 L1), or null.</param>
    internal LibraryOptionsWatch(Func<int> enforce, ILogger logger, TimeProvider time, TimeSpan delay, Func<bool>? isEnforcing = null)
    {
        _enforce = enforce;
        _logger = logger;
        _time = time;
        _delay = delay;
        _isEnforcing = isEnforcing ?? (static () => false);
    }

    /// <summary>Gets the pass that runs now, or a completed task (tests wait on it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Subscribe();
            _subscribed = true;
        }
        catch (Exception ex) when (ex is MissingMemberException or TypeLoadException)
        {
            _logger.LogInformation("This server has no library options event; Shoal locks its libraries' options after each scan only");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            Unsubscribe();
            _subscribed = false;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    /// <summary>
    /// Some library's options were saved: one pass runs after the delay (a pass already waiting takes this save too).
    /// </summary>
    internal void OnChanged()
    {
        if (_isEnforcing() || Interlocked.Exchange(ref _pending, 1) == 1 || _stop.IsCancellationRequested)
        {
            return;
        }

        Running = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, _time, _stop.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _pending, 0);
                _enforce();
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Exchange(ref _pending, 0);
                _logger.LogWarning("Shoal could not check its libraries' options ({Error})", ex.GetType().Name);
            }
        });
    }

    // Separate methods: on a server without the event only these fail, and StartAsync says so.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Subscribe() => CollectionFolder.LibraryOptionsUpdated += OnOptionsUpdated;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unsubscribe() => CollectionFolder.LibraryOptionsUpdated -= OnOptionsUpdated;

    private void OnOptionsUpdated(object? sender, EventArgs e) => OnChanged();
}
