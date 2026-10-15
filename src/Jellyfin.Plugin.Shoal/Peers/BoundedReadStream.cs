using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The only way a peer's body is read (DESIGN 6.10, 8.8, Warden B-2). It counts every byte and fails past
/// <c>maxBytes</c> whatever <c>Content-Length</c> says (chunked bodies included), without ever reading more than one byte beyond the
/// cap; it fails when a single read waits longer than the idle timeout or the call budget runs out; and it treats a body shorter
/// than the declared length as an error. Failures are <see cref="PeerCallException"/>s with fixed text; a cancellation by the caller
/// stays a plain <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly TimeSpan _idleTimeout;
    private readonly long? _declaredLength;
#pragma warning disable CA2213 // The budget belongs to the PeerResponse that created this stream; it is disposed there.
    private readonly CallBudget _budget;
#pragma warning restore CA2213
    private readonly Action<PeerCallException>? _onFailure;
    private long _total;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoundedReadStream"/> class.
    /// </summary>
    /// <param name="inner">The raw body (taken over: it is disposed with this stream).</param>
    /// <param name="maxBytes">The most bytes that may be read.</param>
    /// <param name="idleTimeout">The longest a single read may wait.</param>
    /// <param name="declaredLength">The declared length (<c>Content-Length</c>), or null for a chunked body.</param>
    /// <param name="budget">The call budget (total deadline and caller cancellation).</param>
    /// <param name="onFailure">Called before a failure is thrown, so the caller can count transport failures (circuit breaker); may be null.</param>
    public BoundedReadStream(Stream inner, long maxBytes, TimeSpan idleTimeout, long? declaredLength, CallBudget budget, Action<PeerCallException>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _inner = inner;
        _maxBytes = maxBytes;
        _idleTimeout = idleTimeout;
        _declaredLength = declaredLength;
        _budget = budget;
        _onFailure = onFailure;
    }

    /// <summary>Gets the number of bytes read so far.</summary>
    public long BytesRead => _total;

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

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        // Never ask for more than one byte past the cap: a body of exactly the cap passes, one more byte fails, and nothing
        // beyond is ever buffered.
        var remaining = _maxBytes - _total;
        var allowed = remaining < buffer.Length ? (int)remaining + 1 : buffer.Length;
        int read;
        using (var phase = _budget.StartPhase(_idleTimeout, cancellationToken))
        {
            try
            {
                read = await _inner.ReadAsync(buffer[..allowed], phase.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_budget.Caller.IsCancellationRequested)
            {
                throw Fail(PeerFailureKind.Timeout);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw Fail(PeerFailureKind.Unreachable);
            }
        }

        if (read == 0)
        {
            if (_declaredLength is { } declared && _total < declared)
            {
                throw Fail(PeerFailureKind.BadResponse); // shorter than declared
            }

            return 0;
        }

        _total += read;
        if (_total > _maxBytes)
        {
            throw Fail(PeerFailureKind.TooLarge);
        }

        if (_declaredLength is { } limit && _total > limit)
        {
            throw Fail(PeerFailureKind.BadResponse); // longer than declared
        }

        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Peer bodies are read asynchronously only.");

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("Peer bodies are read asynchronously only.");

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
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private PeerCallException Fail(PeerFailureKind kind)
    {
        var exception = new PeerCallException(kind);
        _onFailure?.Invoke(exception);
        return exception;
    }
}
