#pragma warning disable SA1600 // internal plumbing; the public surface is documented
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// A global byte budget for what Home reads ahead of a friend's server. Everything that buffers asks first; with the budget spent a stream is simply not buffered
/// (it goes straight through, as before), so the memory Home uses for read-ahead can never grow past the limit.
/// </summary>
public sealed class ReadAheadBudget
{
    private readonly long _limit;
    private long _used;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadAheadBudget"/> class.
    /// </summary>
    /// <param name="limitBytes">The most bytes that may be reserved at once.</param>
    public ReadAheadBudget(long limitBytes) => _limit = limitBytes;

    /// <summary>Gets the bytes reserved now.</summary>
    public long Used => Interlocked.Read(ref _used);

    /// <summary>
    /// Reserves bytes.
    /// </summary>
    /// <param name="bytes">How many.</param>
    /// <returns>True when they were reserved; false when the budget would be exceeded (nothing is reserved).</returns>
    public bool TryReserve(long bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _used);
            if (current + bytes > _limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _used, current + bytes, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Gives bytes back.
    /// </summary>
    /// <param name="bytes">How many.</param>
    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}

/// <summary>
/// Reads the next segments of an HLS stream from a friend's server into memory, so the client reads them from Home's fast side (owner request, 2026-10-16). One fetch
/// at a time per stream, never past the window of <c>HlsBufferAhead</c> segments after the one the client last asked for, and bounded in bytes per stream and for the
/// whole of Home: the bytes of a segment are reserved <b>before</b> they are read (held and in-flight bytes both count), so Home never holds more than the limits say.
/// Filling starts with the second sequential request and pauses after a seek. A segment is dropped once the client is past it, and a stream's segments are dropped when it
/// stops, when its slot is gone, when its server is stopped or removed, or after it was idle.
/// </summary>
public sealed class HlsReadAhead : IDisposable
{
    /// <summary>The most bytes held (and being read) for one stream.</summary>
    public const long PerStreamBytes = 64L * 1024 * 1024;

    /// <summary>The most bytes held (and being read) for all streams.</summary>
    public const long GlobalBytes = 256L * 1024 * 1024;

    /// <summary>The largest segment that is buffered; a bigger one is left to the normal streaming path.</summary>
    public const long MaxSegmentBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan IdleLife = TimeSpan.FromMinutes(2);

    private readonly Func<int> _ahead;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ReadAheadBudget _budget = new(GlobalBytes);
    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Timer _sweep;
    private readonly Func<string, bool> _alive;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsReadAhead"/> class.
    /// </summary>
    /// <param name="ahead">The window in segments (<c>HlsBufferAhead</c>).</param>
    /// <param name="alive">Says whether a grant still holds its stream slot and its server may be used (a stream for which it says no is dropped, and none is started).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public HlsReadAhead(Func<int> ahead, Func<string, bool> alive, TimeProvider time, ILogger<HlsReadAhead> logger)
    {
        _ahead = ahead;
        _alive = alive;
        _time = time;
        _logger = logger;
        _sweep = new Timer(_ => ReleaseWhereGone(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Gets the bytes held and being read for all streams now.</summary>
    public long HeldBytes => _budget.Used;

    /// <summary>Gets the number of streams that hold segments.</summary>
    public int Streams => _streams.Count;

    /// <summary>Gets the window in force, clamped to 2..20.</summary>
    public int Window => Math.Clamp(_ahead(), 2, 20);

    /// <summary>Gets or sets a value indicating whether segments are read ahead at all (always on in the plugin; tests switch it off to count Peer calls exactly).</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>
    /// Looks for a segment that is held or being read.
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="segment">The segment number.</param>
    /// <returns>The task that ends with the bytes (or null when the read failed), or null when nothing is held or read for it.</returns>
    public Task<byte[]?>? Find(string grantId, int segment)
    {
        if (!Enabled || !_streams.TryGetValue(grantId, out var state))
        {
            return null;
        }

        lock (_lock)
        {
            state.LastUsed = _time.GetUtcNow();
            return state.Held.TryGetValue(segment, out var held) ? held.Task : null;
        }
    }

    /// <summary>
    /// The client asked for (or was served) a segment: drop what it is past and, once it reads in order, keep reading the window after it, one segment at a time.
    /// A first request, or a jump (a seek), only moves the position: nothing is read for it until the next request in order.
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="peerKey">The server the stream comes from.</param>
    /// <param name="segment">The segment the client is at.</param>
    /// <param name="count">How many segments the playlist lists.</param>
    /// <param name="fetch">Reads one segment (returns null for "not now" or a failure). It must reserve the bytes it is going to hold on the reservation before it reads them, and honor the token.</param>
    public void Advance(string grantId, string peerKey, int segment, int count, Func<int, ReadAheadReservation, CancellationToken, Task<byte[]?>> fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (!Enabled || !_alive(grantId))
        {
            return; // a stream that was released, or whose server was stopped, gets no new state from a late request
        }

        var state = _streams.GetOrAdd(grantId, static (_, key) => new StreamState(key), peerKey);
        lock (_lock)
        {
            if (state.Stopped)
            {
                return;
            }

            state.LastUsed = _time.GetUtcNow();
            var sequential = state.Last is { } last && segment == last + 1;
            if (state.Last is not { } previous || segment != previous)
            {
                state.Fill = sequential;
            }

            state.Last = segment;
            state.At = segment;
            state.Failed.Clear();
            foreach (var behind in state.Held.Keys.Where(k => k < segment).ToList())
            {
                Drop(state, behind);
            }

            if (!state.Fill || state.Running)
            {
                return;
            }

            state.Running = true;
            _ = Task.Run(() => WorkAsync(state, count, fetch));
        }
    }

    /// <summary>
    /// Drops everything held for a stream and stops its reading (the stream stopped, its slot was freed, or the next episode took its place).
    /// </summary>
    /// <param name="grantId">The grant.</param>
    public void Release(string grantId)
    {
        if (_streams.TryRemove(grantId, out var state))
        {
            Clear(state);
        }
    }

    /// <summary>
    /// Drops every stream that comes from a server (it was removed, paused, stopped by a failed check, or its key is being replaced).
    /// </summary>
    /// <param name="peerKey">The server.</param>
    public void ReleasePeer(string peerKey)
    {
        foreach (var (id, state) in _streams.ToList())
        {
            if (string.Equals(state.PeerKey, peerKey, StringComparison.Ordinal))
            {
                Release(id);
            }
        }
    }

    /// <summary>
    /// Drops every stream that was idle for two minutes or whose grant no longer holds its stream slot or may no longer use its server.
    /// </summary>
    public void ReleaseWhereGone()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, state) in _streams)
        {
            bool idle;
            lock (_lock)
            {
                idle = now - state.LastUsed > IdleLife;
            }

            if (idle || !_alive(id))
            {
                Release(id);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var id in _streams.Keys.ToList())
        {
            Release(id);
        }
    }

    internal bool Reserve(StreamState state, Held held, long bytes)
    {
        lock (_lock)
        {
            if (bytes <= 0 || held.Bytes != 0 || held.Dropped || state.Stopped)
            {
                return false;
            }

            if (state.Bytes + bytes > PerStreamBytes || !_budget.TryReserve(bytes))
            {
                held.Refused = true; // no room: the round ends, the next request in order tries again
                return false;
            }

            state.Bytes += bytes;
            held.Bytes = bytes;
            return true;
        }
    }

    private static void Complete(Held held, byte[]? bytes) => held.Source.TrySetResult(bytes);

    private async Task WorkAsync(StreamState state, int count, Func<int, ReadAheadReservation, CancellationToken, Task<byte[]?>> fetch)
    {
        var finished = false; // set under the lock together with Running = false, so a new round can never be skipped or cut short
        try
        {
            while (true)
            {
                Held held;
                int next;
                lock (_lock)
                {
                    if (state.Stopped || !state.Fill)
                    {
                        finished = true;
                        state.Running = false;
                        return;
                    }

                    var last = Math.Min(count - 1, state.At + Window);
                    next = Enumerable.Range(state.At + 1, Math.Max(0, last - state.At)).FirstOrDefault(i => !state.Held.ContainsKey(i) && !state.Failed.Contains(i), -1);
                    if (next < 0 || state.Bytes >= PerStreamBytes)
                    {
                        finished = true;
                        state.Running = false;
                        return; // the window is full (or the stream is at its byte limit)
                    }

                    held = new Held();
                    state.Held[next] = held;
                }

                byte[]? bytes = null;
                try
                {
                    bytes = await fetch(next, new ReadAheadReservation(this, state, held), state.Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // released
                }
#pragma warning disable CA1031 // A read-ahead never breaks the stream; the client asks for the segment itself.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogInformation("A segment read-ahead failed ({Type})", ex.GetType().Name);
                }

                lock (_lock)
                {
                    var current = state.Held.TryGetValue(next, out var there) && ReferenceEquals(there, held);
                    if (bytes is null || bytes.Length > held.Bytes || !current || state.Stopped)
                    {
                        // A failed or unwanted read gives its reservation back (a dropped segment already did) and is not tried again until the client moves on.
                        Drop(state, next);
                        state.Failed.Add(next);
                        if (state.Stopped || held.Refused)
                        {
                            finished = true;
                            state.Running = false;
                            return;
                        }

                        continue;
                    }

                    var spare = held.Bytes - bytes.Length; // reserved for the most the segment could be, held for what it is
                    if (spare > 0)
                    {
                        held.Bytes = bytes.Length;
                        state.Bytes -= spare;
                        _budget.Release(spare);
                    }
                }

                Complete(held, bytes);
            }
        }
        finally
        {
            if (!finished)
            {
                lock (_lock)
                {
                    state.Running = false;
                }
            }
        }
    }

    private void Drop(StreamState state, int segment)
    {
        if (state.Held.Remove(segment, out var held))
        {
            state.Bytes -= held.Bytes;
            _budget.Release(held.Bytes);
            held.Bytes = 0;
            held.Dropped = true;
            Complete(held, null);
        }
    }

    private void Clear(StreamState state)
    {
        lock (_lock)
        {
            state.Stopped = true;
            foreach (var segment in state.Held.Keys.ToList())
            {
                Drop(state, segment);
            }

            try
            {
                state.Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already stopped
            }
        }
    }

    internal sealed class Held
    {
        public TaskCompletionSource<byte[]?> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]?> Task => Source.Task;

        public long Bytes { get; set; }

        public bool Dropped { get; set; }

        public bool Refused { get; set; }
    }

    internal sealed class StreamState(string peerKey)
    {
        public string PeerKey { get; } = peerKey;

        public Dictionary<int, Held> Held { get; } = [];

        public HashSet<int> Failed { get; } = [];

        public CancellationTokenSource Stop { get; } = new();

        public long Bytes { get; set; }

        public int At { get; set; }

        public int? Last { get; set; }

        public bool Fill { get; set; }

        public bool Stopped { get; set; }

        public bool Running { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }
}

/// <summary>
/// The bytes a read-ahead of one segment has reserved. The read asks for them before it reads, from the stream's limit and from the whole of Home's, and goes ahead
/// only when it got them; whatever it did not need is given back when it ends.
/// </summary>
public sealed class ReadAheadReservation
{
    private readonly HlsReadAhead _owner;
    private readonly HlsReadAhead.StreamState _state;
    private readonly HlsReadAhead.Held _held;

    internal ReadAheadReservation(HlsReadAhead owner, HlsReadAhead.StreamState state, HlsReadAhead.Held held)
    {
        _owner = owner;
        _state = state;
        _held = held;
    }

    /// <summary>
    /// Reserves the bytes of the segment (once).
    /// </summary>
    /// <param name="bytes">The most the segment can be.</param>
    /// <returns>True when the stream's limit and Home's limit both have room; false means: do not read.</returns>
    public bool TryReserve(long bytes) => _owner.Reserve(_state, _held, bytes);
}
