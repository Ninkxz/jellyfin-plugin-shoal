using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// A read-only stream that reads its source ahead into a bounded buffer (direct play, a few MB per stream): a background task keeps the buffer full from the slow link,
/// the client reads from it. The buffer never holds more than its capacity (the reader is paused when it is full); a failure of the source reaches the reader as the
/// same exception (the response is then cut, never ended as if complete).
/// </summary>
#pragma warning disable CA2213 // The source belongs to the response that is disposed after this stream; the reader is the pipe's, completed in DisposeAsync.
internal sealed class ReadAheadBody : Stream
{
    private readonly Stream _source;
    private readonly Pipe _pipe;
    private readonly Stream _reader;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _producer;
    private readonly ReadAheadBudget _budget;
    private readonly long _reserved;
    private int _disposed;

    private ReadAheadBody(Stream source, int capacity, ReadAheadBudget budget, long reserved)
    {
        _source = source;
        _budget = budget;
        _reserved = reserved;
        _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: capacity, resumeWriterThreshold: Math.Max(1, capacity / 2), useSynchronizationContext: false));
        _reader = _pipe.Reader.AsStream();
        _producer = Task.Run(() => FillAsync(_stop.Token));
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Wraps a stream in a read-ahead buffer when the budget allows it.
    /// </summary>
    /// <param name="source">The stream (owned by its response; it is not disposed here).</param>
    /// <param name="capacity">The buffer size in bytes.</param>
    /// <param name="budget">The global budget, or null for none.</param>
    /// <returns>The buffered stream, or <paramref name="source"/> itself when the budget is spent.</returns>
    public static Stream Wrap(Stream source, int capacity, ReadAheadBudget? budget)
    {
        if (budget is null || capacity <= 0 || !budget.TryReserve(capacity))
        {
            return source;
        }

        return new ReadAheadBody(source, capacity, budget, capacity);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _reader.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _producer.ConfigureAwait(false); // the source belongs to a response that is disposed next: nothing may still read it
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        _budget.Release(_reserved);
        _stop.Dispose();
    }

    private async Task FillAsync(CancellationToken token)
    {
        var writer = _pipe.Writer;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(64 * 1024);
                var read = await _source.ReadAsync(memory, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                writer.Advance(read);
                var flushed = await writer.FlushAsync(token).ConfigureAwait(false);
                if (flushed.IsCompleted || flushed.IsCanceled)
                {
                    break;
                }
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever stopped the source reaches the reader as that exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
        }
    }
}
#pragma warning restore CA2213
