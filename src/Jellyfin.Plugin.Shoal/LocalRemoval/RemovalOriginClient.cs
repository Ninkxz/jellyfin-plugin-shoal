using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Progress;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Fresh authenticated source proof and bounded admitted direct-media request.</summary>
internal interface IRemovalOriginClient
{
    /// <summary>Fresh-opens the remote origin; never uses a cached proof.</summary>
    /// <param name="target">The server-held movie selection.</param>
    /// <param name="cancellationToken">Operation cancellation.</param>
    /// <returns>The checked physical-source proof.</returns>
    Task<RemovalOriginDto> ProveAsync(RemovalTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// 2.10.4 W23-3: reads <paramref name="length"/> bytes at <paramref name="offset"/> of the friend's copy through the friend's normal static stream,
    /// so Home can compare ranges it picks at random with its own file (a reported hash alone is not enough). No new friend route.
    /// </summary>
    /// <param name="target">The server-held movie selection.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="length">1 to <see cref="RemovalOriginClient.SampleBytes"/> bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Exactly the requested bytes.</returns>
    Task<byte[]> ReadRangeAsync(RemovalTarget target, long offset, int length, CancellationToken cancellationToken) => throw new NotSupportedException("originUnavailable");
}

/// <summary>Uses only the established peer client, credentials, address policy and TLS trust checks.</summary>
internal sealed class RemovalOriginClient : IRemovalOriginClient
{
    /// <summary>The most bytes one challenge range reads.</summary>
    internal const int SampleBytes = 4096;

    private static readonly PeerCallProfile SampleProfile = new("removal-sample", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), SampleBytes, RequireDeclaredLength: true, CountsTowardBreaker: false);

    private static readonly PeerCallProfile MediaProfile = new("removal-media-proof", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), 1024, RequireDeclaredLength: true);
    private readonly IPeerEndpoints _peers;
    private readonly PeerClient _client;

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginClient"/> class.</summary>
    /// <param name="peers">Current revocable peer credentials.</param>
    /// <param name="client">The established bounded authenticated transport.</param>
    public RemovalOriginClient(IPeerEndpoints peers, PeerClient client)
    {
        _peers = peers;
        _client = client;
    }

    /// <inheritdoc />
    public async Task<RemovalOriginDto> ProveAsync(RemovalTarget target, CancellationToken cancellationToken)
    {
        var endpoint = _peers.Find(target.PeerKey) ?? throw Refused();
        try
        {
            using var response = await _client.SendAsync(endpoint, PeerRequest.Get("/Shoal/Share/RemovalOrigin/" + target.PeerItemId.ToString("N")), HashProfileFor(target.Bytes), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RemovalOriginRateLimitedException(response.RetryAfterSeconds);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Refused();
            }

            var proof = await response.ReadJsonAsync<RemovalOriginDto>(cancellationToken).ConfigureAwait(false);
            if (proof.V != 1 || !IsDigest(proof.ContentSha256) || !IsDigest(proof.Generation)
                || proof.ProviderIds is null || proof.ProviderIds.Count is < 1 or > 3
                || !KeepMyPlacePolicy.Matches(new(KeepMyPlaceKind.Movie, target.ProviderIds), new(KeepMyPlaceKind.Movie, proof.ProviderIds)))
            {
                throw Refused(); // the friend confirmed nothing usable about this movie
            }

            // Lab fix (Pilot 2 check 3): a valid proof of the same movie whose file has another length or runtime is the identical-content
            // proof failing, not an unreachable friend. Still refused; only the word differs (an item refusal, the batch goes on).
            if (proof.Length != target.Bytes || proof.RuntimeTicks != target.RuntimeTicks)
            {
                throw new InvalidOperationException("contentNotEquivalent");
            }

            if (!RemovalPolicy.DeleteSizeAllowed(proof.Length))
            {
                throw Refused();
            }

            var expected = Math.Min(1024, proof.Length);
            var range = "bytes=0-" + (expected - 1).ToString(CultureInfo.InvariantCulture);
            using var media = await _client.SendAsync(endpoint, PeerRequest.GetMedia("/Videos/" + target.PeerItemId.ToString("N") + "/stream?Static=true", range), MediaProfile, cancellationToken).ConfigureAwait(false);
            var contentRange = "bytes 0-" + (expected - 1).ToString(CultureInfo.InvariantCulture) + "/" + proof.Length.ToString(CultureInfo.InvariantCulture);
            if (media.StatusCode != HttpStatusCode.PartialContent || media.ContentLength != expected || media.ContentRange != contentRange
                || media.MediaType is not { } type || (!type.StartsWith("video/", StringComparison.Ordinal) && type != "application/octet-stream")
                || (await media.ReadBytesAsync(cancellationToken).ConfigureAwait(false)).LongLength != expected)
            {
                throw Refused();
            }

            return proof;
        }
        catch (Exception ex) when (ex is PeerCallException or JsonException or NotSupportedException)
        {
            throw Refused();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadRangeAsync(RemovalTarget target, long offset, int length, CancellationToken cancellationToken)
    {
        if (length is < 1 or > SampleBytes || offset < 0 || offset + length > target.Bytes)
        {
            throw Refused();
        }

        var endpoint = _peers.Find(target.PeerKey) ?? throw Refused();
        var last = offset + length - 1;
        var range = "bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var media = await _client.SendAsync(endpoint, PeerRequest.GetMedia("/Videos/" + target.PeerItemId.ToString("N") + "/stream?Static=true", range), SampleProfile, cancellationToken).ConfigureAwait(false);
            if (media.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new RemovalOriginRateLimitedException(media.RetryAfterSeconds);
            }

            var contentRange = "bytes " + offset.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture) + "/" + target.Bytes.ToString(CultureInfo.InvariantCulture);
            if (media.StatusCode != HttpStatusCode.PartialContent || media.ContentLength != length || media.ContentRange != contentRange)
            {
                throw Refused();
            }

            var bytes = await media.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length == length ? bytes : throw Refused();
        }
        catch (Exception ex) when (ex is PeerCallException or NotSupportedException)
        {
            throw Refused();
        }
    }

    /// <summary>
    /// The friend-proof call profile for a file length (2.10.4 K1). The friend sends its headers only after hashing, so
    /// <see cref="PeerCallProfile.Headers"/> is the friend's size-scaled hashing deadline plus 10 s, and the whole call 10 s more (the 2.10.3 margins
    /// around a fixed 2 minute hash: 130 s and 140 s). PeerClient has no upper limit on either (gate S7 read: CallBudget and an infinite HttpClient
    /// timeout); the friend's own ceiling is 130 minutes (100 GiB).
    /// </summary>
    /// <param name="bytes">The local file length, which the friend's file must equal.</param>
    /// <returns>The profile.</returns>
    internal static PeerCallProfile HashProfileFor(long bytes)
    {
        var headers = RemovalPolicy.DeleteHashingDeadlineFor(bytes) + TimeSpan.FromSeconds(10);

        // Scout 04 C4: a long hash that times out must not open the friend's circuit breaker for normal playback (connect failures still count).
        return new("removal-origin", TimeSpan.FromSeconds(5), headers, TimeSpan.FromSeconds(10), headers + TimeSpan.FromSeconds(10), 16 * 1024, CountsTowardBreaker: false);
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidOperationException Refused() => new("originUnavailable");
}

/// <summary>The friend answered 429: message <c>originRateLimited</c> (the word every caller already maps) plus the friend's Retry-After.</summary>
internal sealed class RemovalOriginRateLimitedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="RemovalOriginRateLimitedException"/> class.</summary>
    /// <param name="retryAfterSeconds">The friend's Retry-After in seconds, or null when it sent none.</param>
    public RemovalOriginRateLimitedException(int? retryAfterSeconds)
        : base("originRateLimited")
    {
        RetryAfter = retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginRateLimitedException"/> class without a Retry-After.</summary>
    public RemovalOriginRateLimitedException()
        : this((int?)null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginRateLimitedException"/> class without a Retry-After.</summary>
    /// <param name="message">Ignored text; the word is fixed.</param>
    public RemovalOriginRateLimitedException(string message)
        : this((int?)null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RemovalOriginRateLimitedException"/> class without a Retry-After.</summary>
    /// <param name="message">Ignored text; the word is fixed.</param>
    /// <param name="innerException">The cause.</param>
    public RemovalOriginRateLimitedException(string message, Exception innerException)
        : base("originRateLimited", innerException)
    {
    }

    /// <summary>Gets how long the friend asked Home to wait, or null.</summary>
    public TimeSpan? RetryAfter { get; }
}
