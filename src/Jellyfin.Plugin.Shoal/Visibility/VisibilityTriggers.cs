using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Visibility;

/// <summary>
/// The timing of the "an account was created" triggers (DESIGN 5.6): reconcile 30 s after creation, and again after the first policy update
/// within 60 s, because the dashboard creates an account and then posts its library access.
/// </summary>
public sealed class UserCreationWatch : IDisposable
{
    private readonly VisibilityService _visibility;
    private readonly TimeProvider _time;
    private readonly ILogger<UserCreationWatch> _logger;
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<Guid, Entry> _created = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreationWatch"/> class.
    /// </summary>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public UserCreationWatch(VisibilityService visibility, TimeProvider time, ILogger<UserCreationWatch> logger)
        : this(visibility, time, logger, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreationWatch"/> class (tests: short delays).
    /// </summary>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="firstDelay">The delay before the reconcile after creation.</param>
    /// <param name="window">How long after creation the first update counts.</param>
    internal UserCreationWatch(VisibilityService visibility, TimeProvider time, ILogger<UserCreationWatch> logger, TimeSpan firstDelay, TimeSpan window)
    {
        _visibility = visibility;
        _time = time;
        _logger = logger;
        _firstDelay = firstDelay;
        _window = window;
    }

    /// <summary>
    /// An account was created: reconcile after the first delay.
    /// </summary>
    /// <param name="userId">The account.</param>
    public void UserCreated(Guid userId)
    {
        Prune();
        _created[userId] = new Entry(_time.GetUtcNow());
        _ = Task.Run(() => ReconcileLaterAsync(TimeSpan.Zero)); // Warden 10 M4: at once, so a new account does not list Main-placement titles for the first delay
        _ = ReconcileLaterAsync(_firstDelay); // and again once the dashboard has written the new account's policy
    }

    /// <summary>
    /// An account's policy was updated. The first update of an account created within the window triggers a reconcile at once; any other
    /// update only refreshes the drift list (which is computed on demand), as core's own writes are not ours to follow.
    /// </summary>
    /// <param name="userId">The account.</param>
    public void UserUpdated(Guid userId)
    {
        if (_created.TryGetValue(userId, out var entry)
            && _time.GetUtcNow() - entry.Created <= _window
            && entry.TryHandle())
        {
            _ = ReconcileLaterAsync(TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _created)
        {
            if (now - pair.Value.Created > _window)
            {
                _created.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task ReconcileLaterAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            }

            await _visibility.ReconcileAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("A reconcile after an account was created failed ({Error})", ex.GetType().Name);
        }
    }

    private sealed class Entry(DateTimeOffset created)
    {
        private int _handled;

        public DateTimeOffset Created { get; } = created;

        /// <summary>Marks the entry handled; true only for the first caller.</summary>
        public bool TryHandle() => Interlocked.Exchange(ref _handled, 1) == 0;
    }
}

/// <summary>
/// Feeds "an account was created" events to <see cref="UserCreationWatch"/>.
/// </summary>
public sealed class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly UserCreationWatch _watch;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    /// <param name="watch">The watch.</param>
    public UserCreatedConsumer(UserCreationWatch watch) => _watch = watch;

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        _watch.UserCreated(eventArgs.Argument.Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Reconciles after every library scan (the Shoal libraries are scanned like any other, and new local libraries must reach managed accounts).
/// </summary>
public sealed class ReconcileAfterScan : ILibraryPostScanTask
{
    private readonly VisibilityService _visibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconcileAfterScan"/> class.
    /// </summary>
    /// <param name="visibility">The visibility service.</param>
    public ReconcileAfterScan(VisibilityService visibility) => _visibility = visibility;

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }
}

/// <summary>
/// Starts the account watch, the hourly reconcile and the uninstall hook.
/// </summary>
public sealed class VisibilityHost : IHostedService, IDisposable
{
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly VisibilityService _visibility;
    private readonly UserCreationWatch _watch;
    private readonly IUserManager _users;
    private readonly ILogger<VisibilityHost> _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityHost"/> class.
    /// </summary>
    /// <param name="visibility">The visibility service.</param>
    /// <param name="watch">The account watch.</param>
    /// <param name="users">The user manager (for the update event).</param>
    /// <param name="logger">The logger.</param>
    public VisibilityHost(VisibilityService visibility, UserCreationWatch watch, IUserManager users, ILogger<VisibilityHost> logger)
    {
        _visibility = visibility;
        _watch = watch;
        _users = users;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _users.OnUserUpdated += OnUserUpdated;
        Plugin.UninstallHook = RestoreOnUninstall;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _users.OnUserUpdated -= OnUserUpdated;
        Plugin.UninstallHook = null;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e) => _watch.UserUpdated(e.Argument.Id);

    private void RestoreOnUninstall()
    {
        // Synchronous and bounded: best effort, then Jellyfin carries on with the uninstall.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var restored = _visibility.RestoreManagedAccountsAsync(timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            _logger.LogInformation("Shoal gave \"all libraries\" back to {Count} accounts before uninstalling", restored);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Shoal could not restore every account before uninstalling ({Error})", ex.GetType().Name);
        }
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(StartupDelay, stopping).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Hourly);
            do
            {
                try
                {
                    await _visibility.ReconcileAsync(stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The scheduled reconcile failed ({Error})", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
