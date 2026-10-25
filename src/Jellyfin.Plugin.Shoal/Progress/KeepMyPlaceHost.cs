using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>Bounded coalesced event queue and incremental startup/restart catch-up.</summary>
internal sealed class KeepMyPlaceHost : IHostedService, IAsyncDisposable
{
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _data;
    private readonly JellyfinKeepMyPlaceLibrary _adapter;
    private readonly KeepMyPlaceService _service;
    private readonly ISettingsAccessor _settings;
    private readonly ILogger<KeepMyPlaceHost> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly object _queueGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private int _overflow;
    private int _offset;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="KeepMyPlaceHost"/> class.</summary>
    /// <param name="library">Core item events.</param>
    /// <param name="data">Core userdata events.</param>
    /// <param name="adapter">Fresh local adapter.</param>
    /// <param name="service">Serial retention/reconciliation.</param>
    /// <param name="settings">Current enable setting.</param>
    /// <param name="logger">Fixed diagnostics only, never userdata.</param>
    public KeepMyPlaceHost(ILibraryManager library, IUserDataManager data, JellyfinKeepMyPlaceLibrary adapter, KeepMyPlaceService service, ISettingsAccessor settings, ILogger<KeepMyPlaceHost> logger)
    {
        _library = library;
        _data = data;
        _adapter = adapter;
        _service = service;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        _data.UserDataSaved += OnSaved;
        _library.ItemAdded += OnItem;
        _library.ItemUpdated += OnItem;
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            _data.UserDataSaved -= OnSaved;
            _library.ItemAdded -= OnItem;
            _library.ItemUpdated -= OnItem;
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    private void OnSaved(object? sender, UserDataSaveEventArgs e)
    {
        _adapter.ObserveSave();
        if (e.Item is { } item)
        {
            Enqueue(item.Id);
        }
    }

    private void OnItem(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { } item)
        {
            Enqueue(item.Id);
        }
    }

    private void Enqueue(Guid id)
    {
        try
        {
            if (!_settings.Current.KeepMyPlaceEnabled || id == Guid.Empty)
            {
                return;
            }

            lock (_queueGate)
            {
                if (_pending.Count >= 1024)
                {
                    Interlocked.Exchange(ref _overflow, 1);
                    return;
                }

                _pending.TryAdd(id, 0);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _overflow, 1);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    if (!_settings.Current.KeepMyPlaceEnabled)
                    {
                        await _service.PruneAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (Interlocked.Exchange(ref _overflow, 0) != 0)
                    {
                        _offset = 0;
                    }

                    var changed = _pending.Keys.Take(256).ToArray();
                    foreach (var id in changed)
                    {
                        _pending.TryRemove(id, out _);
                    }

                    var page = _adapter.SourceItems(_offset, 256);
                    _offset = page.Count < 256 ? 0 : _offset + 256;
                    await _service.ReconcileAsync(changed.Concat(page).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning("Keep My Place could not reconcile local history; it will retry without changing the queued source files");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
