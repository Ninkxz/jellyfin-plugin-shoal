using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Peers;

/// <summary>
/// The JSON rules for everything a peer sends: depth 32, no comments, no trailing commas, property names matched without regard to
/// case (Jellyfin writes PascalCase), unknown members skipped (DESIGN 6.10).
/// </summary>
public static class PeerJson
{
    /// <summary>The deepest nesting that is parsed.</summary>
    public const int MaxDepth = 32;

    /// <summary>Gets the options every peer document is parsed with.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        MaxDepth = MaxDepth,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };
}

/// <summary>
/// A peer's answer: the status and a small, fixed set of header values, and a body that can only be read through
/// <see cref="BoundedReadStream"/> (DESIGN 6.10, 8.6). Peer headers that are not exposed here (<c>Set-Cookie</c>, <c>Location</c>,
/// <c>Server</c>, <c>Link</c>, any <c>X-*</c>) cannot be forwarded by accident. The body is available only for 200 and 206 answers;
/// for anything else <see cref="HasBody"/> is false and nothing is read. Dispose the response when done.
/// </summary>
public sealed class PeerResponse : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpResponseMessage _message;
    private readonly CallBudget _budget;
    private readonly PeerCallProfile _profile;
    private readonly Action<PeerCallException>? _onBodyFailure;
    private BoundedReadStream? _body;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerResponse"/> class.
    /// </summary>
    /// <param name="message">The response; taken over.</param>
    /// <param name="budget">The call budget; taken over.</param>
    /// <param name="profile">The deadlines and cap of the call.</param>
    /// <param name="expectBody">False for answers to HEAD and for 5xx.</param>
    /// <param name="onBodyFailure">Called when reading the body fails (circuit breaker bookkeeping), or null.</param>
    internal PeerResponse(HttpResponseMessage message, CallBudget budget, PeerCallProfile profile, bool expectBody, Action<PeerCallException>? onBodyFailure)
    {
        _message = message;
        _budget = budget;
        _profile = profile;
        _onBodyFailure = onBodyFailure;
        HasBody = expectBody && message.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
    }

    /// <summary>Gets the HTTP status.</summary>
    public HttpStatusCode StatusCode => _message.StatusCode;

    /// <summary>Gets a value indicating whether the status is 2xx.</summary>
    public bool IsSuccess => (int)_message.StatusCode is >= 200 and < 300;

    /// <summary>Gets a value indicating whether there is a body to read (a 200 or 206 answer to a request that expects one).</summary>
    public bool HasBody { get; }

    /// <summary>Gets the media type, lower case, parameters removed (the charset is never used), or null.</summary>
    public string? MediaType => _message.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();

    /// <summary>Gets the declared length of the body, or null for a chunked body.</summary>
    public long? ContentLength => _message.Content.Headers.ContentLength;

    /// <summary>Gets <c>Content-Range</c> as text (re-serialized by the handler that uses it, DESIGN 6.3), or null.</summary>
    public string? ContentRange => _message.Content.Headers.ContentRange?.ToString();

    /// <summary>Gets <c>Accept-Ranges</c> as text, or null.</summary>
    public string? AcceptRanges => _message.Headers.AcceptRanges.Count == 0 ? null : string.Join(", ", _message.Headers.AcceptRanges);

    /// <summary>Gets <c>ETag</c> as text, or null.</summary>
    public string? ETag => _message.Headers.ETag?.ToString();

    /// <summary>Gets <c>Last-Modified</c>, or null.</summary>
    public DateTimeOffset? LastModified => _message.Content.Headers.LastModified;

    /// <summary>Gets <c>Retry-After</c> in whole seconds (0 to 3600), or null.</summary>
    public int? RetryAfterSeconds
    {
        get
        {
            var value = _message.Headers.RetryAfter;
            if (value?.Delta is { } delta)
            {
                return (int)Math.Clamp(delta.TotalSeconds, 0, 3600);
            }

            return value?.Date is { } date ? (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 3600) : null;
        }
    }

    /// <summary>
    /// Opens the body for streaming. It fails when the declared length is over the cap, or absent where the call needs one.
    /// </summary>
    /// <returns>The bounded stream; it is owned by this response.</returns>
    /// <exception cref="PeerCallException">The declared length is over the cap (<see cref="PeerFailureKind.TooLarge"/>) or missing (<see cref="PeerFailureKind.BadResponse"/>).</exception>
    /// <exception cref="InvalidOperationException">There is no body, or it was already opened.</exception>
    public Stream OpenBody()
    {
        if (!HasBody)
        {
            throw new InvalidOperationException("This answer has no body.");
        }

        if (_body is not null)
        {
            throw new InvalidOperationException("The body was already opened.");
        }

        var declared = ContentLength;
        if (declared > _profile.MaxBytes)
        {
            throw Report(new PeerCallException(PeerFailureKind.TooLarge));
        }

        if (declared is null && _profile.RequireDeclaredLength)
        {
            throw Report(new PeerCallException(PeerFailureKind.BadResponse));
        }

#pragma warning disable RS0030 // The one place a peer body stream is taken from HttpContent; it is wrapped at once.
        var raw = _message.Content.ReadAsStream(_budget.Token);
#pragma warning restore RS0030
        _body = new BoundedReadStream(raw, _profile.MaxBytes, _profile.Idle, declared, _budget, _onBodyFailure);
        return _body;
    }

    /// <summary>
    /// Reads the whole body into memory under the call's cap and deadlines.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes (empty when there is no body).</returns>
    /// <exception cref="PeerCallException">The body is too large, too slow, short or otherwise unacceptable.</exception>
    public async Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default)
    {
        if (!HasBody)
        {
            return [];
        }

        var stream = OpenBody();
        using var buffer = new MemoryStream(capacity: (int)Math.Min(ContentLength ?? 4096, 1024 * 1024));
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the whole body as text. It is always decoded as strict UTF-8; the peer's declared charset is never consulted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The text (empty when there is no body).</returns>
    /// <exception cref="PeerCallException">The body is unacceptable, or not valid UTF-8.</exception>
    public async Task<string> ReadUtf8Async(CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Report(new PeerCallException(PeerFailureKind.BadResponse));
        }
    }

    /// <summary>
    /// Reads the body as a JSON document of type <typeparamref name="T"/>, under <see cref="PeerJson.Options"/> (depth 32). The
    /// media type must be <c>application/json</c>, whatever charset it names.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document.</returns>
    /// <exception cref="PeerCallException">The media type, the size, the nesting or the content is unacceptable.</exception>
    public async Task<T> ReadJsonAsync<T>(CancellationToken cancellationToken = default)
        where T : class
    {
        if (!HasBody || !string.Equals(MediaType, "application/json", StringComparison.Ordinal))
        {
            throw Report(new PeerCallException(PeerFailureKind.BadResponse));
        }

        var stream = OpenBody();
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, PeerJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw Report(new PeerCallException(PeerFailureKind.BadResponse));
        }
        catch (JsonException)
        {
            throw Report(new PeerCallException(PeerFailureKind.BadResponse));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _body?.Dispose();
        _message.Dispose();
        _budget.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_body is not null)
        {
            await _body.DisposeAsync().ConfigureAwait(false);
        }

        _message.Dispose();
        _budget.Dispose();
    }

    private PeerCallException Report(PeerCallException exception)
    {
        _onBodyFailure?.Invoke(exception);
        return exception;
    }
}
