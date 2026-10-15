using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A clock that only moves when a test says so (or when a simulated link "transfers" something). Its timestamps tick in nanoseconds, so
/// <c>GetElapsedTime</c> is exact and independent of how fast the machine running the tests is.
/// </summary>
internal sealed class VirtualClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private long _nanoseconds;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => Interlocked.Read(ref _nanoseconds);

    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp() / 100);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _nanoseconds, by.Ticks * 100);

    /// <summary>Moves the clock forward to a point in time (never back).</summary>
    /// <param name="nanoseconds">The timestamp.</param>
    public void AdvanceTo(long nanoseconds)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref _nanoseconds);
            if (nanoseconds <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _nanoseconds, nanoseconds, seen) != seen);
    }
}

/// <summary>
/// A Jellyfin peer in memory, on a link with a rate and a round trip, driven by a <see cref="VirtualClock"/>: it answers <c>/System/Ping</c> and
/// <c>/Playback/BitrateTest</c> over a hand-made HTTP/1.1 connection, and every byte it "sends" moves the virtual clock by the time the link would
/// have needed. The numbers a speed test measures are therefore exact and do not depend on the speed of the machine (or on a timer).
/// </summary>
internal sealed class SimulatedLink
{
    private const int Chunk = 16 * 1024;

    private readonly VirtualClock _clock;
    private readonly long _bytesPerSecond;
    private readonly TimeSpan _roundTrip;
    private readonly object _sharedLock = new();
    private long _sharedCursor;

    public SimulatedLink(VirtualClock clock, long bytesPerSecond, TimeSpan roundTrip)
    {
        _clock = clock;
        _bytesPerSecond = bytesPerSecond;
        _roundTrip = roundTrip;
    }

    public string BaseUrl => "http://peer.sim:8096";

    public ConcurrentQueue<long> Sizes { get; } = new();

    public long FailAbove { get; set; } = long.MaxValue;

    public ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(new Connection(this));

    // The link is one pipe for every connection: a chunk takes bytes / rate after the pipe is free, and the clock moves to the end of it.
    private void Transfer(int bytes)
    {
        var slice = (long)(bytes * 1_000_000_000.0 / _bytesPerSecond);
        long end;
        lock (_sharedLock)
        {
            _sharedCursor = Math.Max(_sharedCursor, _clock.GetTimestamp()) + slice;
            end = _sharedCursor;
        }

        _clock.AdvanceTo(end);
    }

    private sealed class Response
    {
        public byte[] Head { get; init; } = [];

        public int HeadPosition { get; set; }

        public byte[]? Inline { get; init; }

        public int InlinePosition { get; set; }

        public long Remaining { get; set; }
    }

    private sealed class Connection : Stream
    {
        private readonly SimulatedLink _link;
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly Queue<Response> _pending = new();
        private readonly StringBuilder _request = new();
        private Response? _current;
        private bool _closed;

        public Connection(SimulatedLink link) => _link = link;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _request.Append(Encoding.ASCII.GetString(buffer.Span));
            var text = _request.ToString();
            int end;
            while ((end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
            {
                Answer(text[..text.IndexOf("\r\n", StringComparison.Ordinal)]);
                text = text[(end + 4)..];
            }

            _request.Clear().Append(text);
            return ValueTask.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_closed)
                    {
                        return 0;
                    }

                    await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
                    lock (_pending)
                    {
                        _current = _pending.Count > 0 ? _pending.Dequeue() : null;
                    }

                    if (_current is null)
                    {
                        return 0; // closed
                    }
                }

                var r = _current;
                if (r.HeadPosition < r.Head.Length)
                {
                    var n = Math.Min(buffer.Length, r.Head.Length - r.HeadPosition);
                    r.Head.AsMemory(r.HeadPosition, n).CopyTo(buffer);
                    r.HeadPosition += n;
                    FinishIfDone(r);
                    return n;
                }

                if (r.Inline is { } inline && r.InlinePosition < inline.Length)
                {
                    var n = Math.Min(buffer.Length, inline.Length - r.InlinePosition);
                    inline.AsMemory(r.InlinePosition, n).CopyTo(buffer);
                    r.InlinePosition += n;
                    FinishIfDone(r);
                    return n;
                }

                if (r.Remaining > 0)
                {
                    var n = (int)Math.Min(Math.Min(buffer.Length, Chunk), r.Remaining);
                    buffer.Span[..n].Clear();
                    r.Remaining -= n;
                    _link.Transfer(n);
                    FinishIfDone(r);
                    return n;
                }

                _current = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            _arrived.Release();
            base.Dispose(disposing);
        }

        private void FinishIfDone(Response r)
        {
            if (r.HeadPosition >= r.Head.Length && (r.Inline is null || r.InlinePosition >= r.Inline.Length) && r.Remaining <= 0)
            {
                _current = null;
            }
        }

        private void Answer(string requestLine)
        {
            var parts = requestLine.Split(' ');
            var target = parts.Length > 1 ? parts[1] : string.Empty;
            Response response;
            _link._clock.Advance(_link._roundTrip); // the request and the first byte of the answer each cross the link
            if (target.StartsWith("/System/Ping", StringComparison.OrdinalIgnoreCase))
            {
                var body = Encoding.ASCII.GetBytes("Jellyfin Server");
                response = new Response { Head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n"), Inline = body };
            }
            else if (target.StartsWith("/Playback/BitrateTest", StringComparison.OrdinalIgnoreCase))
            {
                var query = target.IndexOf('?', StringComparison.Ordinal) >= 0 ? target[(target.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
                var size = 0L;
                foreach (var pair in query.Split('&'))
                {
                    if (pair.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
                    {
                        size = long.Parse(pair[5..], System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                _link.Sizes.Enqueue(size);
                response = size > _link.FailAbove
                    ? new Response { Head = Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n") }
                    : new Response { Head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {size}\r\n\r\n"), Remaining = size };
            }
            else
            {
                response = new Response { Head = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n") };
            }

            lock (_pending)
            {
                _pending.Enqueue(response);
            }

            _arrived.Release();
        }
    }
}
