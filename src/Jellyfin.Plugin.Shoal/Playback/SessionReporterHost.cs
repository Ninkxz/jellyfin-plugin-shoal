#pragma warning disable SA1600 // internal plumbing

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Connects core's playback events to <see cref="PeerSessionReporter"/> and runs the 30 second keepalive (DESIGN 6.8). It only reads the event and
/// queues work: nothing here waits for a Peer.
/// </summary>
public sealed class SessionReporterHost : IHostedService, IAsyncDisposable
{
    private readonly ISessionManager _sessions;
    private readonly PeerSessionReporter _reporter;
    private readonly CancellationTokenSource _stop = new();
    private Task? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionReporterHost"/> class.
    /// </summary>
    /// <param name="sessions">Core's session manager.</param>
    /// <param name="reporter">The reporter.</param>
    public SessionReporterHost(ISessionManager sessions, PeerSessionReporter reporter)
    {
        _sessions = sessions;
        _reporter = reporter;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reporter.Start();
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _timer = Task.Run(() => KeepAliveAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_timer is not null)
        {
            await _timer.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _reporter.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    // 2.9.2 K9: the item and device go along, because jellyfin-web reports a direct play with PlaySessionId "" (PeerSessionReporter.FindMine).
    internal static PlaybackNote? Note(PlaybackProgressEventArgs e) =>
        e.Users is { Count: > 0 } users
            ? new PlaybackNote(users[0].Id, e.PlaySessionId, e.PlaybackPositionTicks, e.IsPaused, e.Item?.Id ?? e.MediaInfo?.Id ?? Guid.Empty, string.IsNullOrEmpty(e.DeviceId) ? null : DeviceKey.Cap(e.DeviceId))
            : null;

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (Note(e) is { } note)
        {
            _reporter.OnStart(note);
        }
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (Note(e) is { } note)
        {
            _reporter.OnProgress(note);
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Note(e) is { } note)
        {
            _reporter.OnStop(note);
        }
    }

    private async Task KeepAliveAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                _reporter.KeepAlive();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
