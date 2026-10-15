using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Which image of a title.
/// </summary>
public enum ImageKind
{
    /// <summary>The poster (<c>Primary</c> on the peer), at most 2 MB.</summary>
    Poster,

    /// <summary>The backdrop, at most 4 MB.</summary>
    Backdrop,

    /// <summary>A trickplay tile (DESIGN v2.6.5): a mosaic of thumbnails, up to 8192 pixels on a side; never stored by <see cref="ImageFetcher"/>.</summary>
    Tile,
}

/// <summary>
/// How a fetch ended.
/// </summary>
public enum ImageOutcome
{
    /// <summary>The image was validated, counted against the quota and written.</summary>
    Stored,

    /// <summary>The title has no valid image tag, or the quota was already full: nothing was requested.</summary>
    Skipped,

    /// <summary>The quota is full (before or at the write): images stop for this peer, titles still import.</summary>
    QuotaReached,

    /// <summary>The peer did not answer 200.</summary>
    NotOk,

    /// <summary>The upstream <c>Content-Type</c> is not <c>image/jpeg</c> or <c>image/png</c>, or does not match the file's magic bytes.</summary>
    WrongContentType,

    /// <summary>The image is over its byte cap, declared or counted.</summary>
    TooLarge,

    /// <summary>The magic bytes or the dimensions were refused; see <see cref="ImageFetchResult.Refusal"/>.</summary>
    Refused,

    /// <summary>The call to the peer failed (unreachable, timeout, redirect, circuit open, trust lapsed); see <see cref="ImageFetchResult.Failure"/>.</summary>
    PeerError,

    /// <summary>The target path left the library or crossed a link: nothing was written.</summary>
    UnsafePath,

    /// <summary>The file could not be written; the quota was given back.</summary>
    WriteFailed,

    /// <summary>The image sandbox (DESIGN v2.6.7) refused the image: nothing was written, as for a failed header check.</summary>
    SandboxRejected,
}

/// <summary>
/// The result of one image fetch. Any outcome but <see cref="ImageOutcome.Stored"/> means "no image": Jellyfin shows its placeholder.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Path">The file that was written, for <see cref="ImageOutcome.Stored"/>.</param>
/// <param name="Info">The format and size of the stored image.</param>
/// <param name="Bytes">The size of the stored image.</param>
/// <param name="Refusal">Why the header was refused, for <see cref="ImageOutcome.Refused"/>.</param>
/// <param name="Failure">What went wrong, for <see cref="ImageOutcome.PeerError"/>.</param>
public sealed record ImageFetchResult(
    ImageOutcome Outcome,
    string? Path = null,
    ImageInfo? Info = null,
    long Bytes = 0,
    ImageRefusal Refusal = ImageRefusal.None,
    PeerFailureKind? Failure = null);

/// <summary>
/// Downloads one image of a peer item and stores it next to the stub (DESIGN 5.4, 5.10). Only JPEG and PNG are accepted: the upstream type,
/// the magic bytes and the header dimensions are all checked without decoding; the extension comes from the magic bytes. Fetching is serial
/// (one image at a time, whoever asks), and the disk quota is checked before the request and again before each write.
/// </summary>
public sealed class ImageFetcher : IDisposable
{
    private const string PosterName = "poster";
    private const string BackdropName = "backdrop";

    private readonly PeerClient _client;
    private readonly ILogger<ImageFetcher> _logger;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly IImageSanitizer _sanitizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageFetcher"/> class.
    /// </summary>
    /// <param name="client">The peer client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sanitizer">Cleans an image after the header checks (DESIGN v2.6.7); null stores it as it is.</param>
    public ImageFetcher(PeerClient client, ILogger<ImageFetcher> logger, IImageSanitizer? sanitizer = null)
    {
        _client = client;
        _logger = logger;
        _sanitizer = sanitizer ?? new PassThroughImageSanitizer();
    }

    /// <summary>
    /// Fetches, validates and stores one image.
    /// </summary>
    /// <param name="peer">The peer to ask.</param>
    /// <param name="peerItemId">The peer's item id (already sanitized).</param>
    /// <param name="kind">Poster or backdrop.</param>
    /// <param name="tag">The image tag from <see cref="MetadataSanitizer"/>, or null when the item has no image.</param>
    /// <param name="quality">The requested size.</param>
    /// <param name="stubRoot">The validated StubRoot.</param>
    /// <param name="folder">The title's existing folder under it.</param>
    /// <param name="quota">The peer's disk budget.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome; this method does not throw for anything a peer can cause.</returns>
    public async Task<ImageFetchResult> FetchAsync(
        PeerEndpoint peer,
        Guid peerItemId,
        ImageKind kind,
        string? tag,
        ArtQuality quality,
        string stubRoot,
        string folder,
        DiskQuota quota,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(quota);
        if (MetadataSanitizer.ImageTag(tag) is not { } safeTag || peerItemId == Guid.Empty)
        {
            return new ImageFetchResult(ImageOutcome.Skipped);
        }

        if (quota.Exhausted)
        {
            return new ImageFetchResult(ImageOutcome.QuotaReached);
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (quota.Exhausted)
            {
                return new ImageFetchResult(ImageOutcome.QuotaReached);
            }

            var downloaded = await DownloadAsync(peer, peerItemId, kind, safeTag, quality, cancellationToken).ConfigureAwait(false);
            if (downloaded.Failure is not null)
            {
                return downloaded.Failure;
            }

            // The header checks above stay as the cheap first filter; the sanitizer runs before the quota is touched (DESIGN v2.6.7).
            var cleaned = await _sanitizer.SanitizeAsync(downloaded.Bytes!, new ImageSpec(kind, WidthFor(kind, quality)), cancellationToken).ConfigureAwait(false);
            if (!cleaned.Ok)
            {
                _logger.LogDebug("An image from peer {Peer} was refused by the image sandbox", Redact.Id(peer.PeerKey));
                return new ImageFetchResult(ImageOutcome.SandboxRejected);
            }

            var bytes = downloaded.Bytes!;
            var info = downloaded.Info;
            if (!cleaned.Bytes.Span.SequenceEqual(bytes))
            {
                bytes = cleaned.Bytes.ToArray();
                var refusal = ImageProbe.Inspect(bytes, out info);
                if (refusal != ImageRefusal.None)
                {
                    return new ImageFetchResult(ImageOutcome.SandboxRejected, Refusal: refusal);
                }
            }

            return Store(peer, kind, bytes, info, stubRoot, folder, quota);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _serial.Dispose();

    /// <summary>
    /// The request path of an image (DESIGN 5.1): <c>format=Jpg</c> asks the peer for a JPEG; the tag is the already validated one.
    /// </summary>
    /// <param name="peerItemId">The peer's item id.</param>
    /// <param name="kind">Poster or backdrop.</param>
    /// <param name="tag">A tag of 1 to 64 letters and digits.</param>
    /// <param name="quality">The requested size.</param>
    /// <returns>The path and query.</returns>
    internal static string PathFor(Guid peerItemId, ImageKind kind, string tag, ArtQuality quality)
    {
        var (type, width, jpegQuality) = Sizes(kind, quality);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"/Items/{peerItemId:N}/Images/{type}?format=Jpg&maxWidth={width}&quality={jpegQuality}&tag={tag}");
    }

    private static int WidthFor(ImageKind kind, ArtQuality quality) => Sizes(kind, quality).Width;

    private static (string Type, int Width, int Quality) Sizes(ImageKind kind, ArtQuality quality) => (kind, quality) switch
    {
        (ImageKind.Poster, ArtQuality.Standard) => ("Primary", 600, 85),
        (ImageKind.Poster, _) => ("Primary", 1000, 85),
        (_, ArtQuality.Standard) => ("Backdrop", 1280, 80),
        _ => ("Backdrop", 1920, 80),
    };

    private async Task<(ImageFetchResult? Failure, byte[]? Bytes, ImageInfo Info)> DownloadAsync(
        PeerEndpoint peer,
        Guid peerItemId,
        ImageKind kind,
        string tag,
        ArtQuality quality,
        CancellationToken cancellationToken)
    {
        var profile = kind == ImageKind.Poster ? PeerCallProfile.PosterImage : PeerCallProfile.BackdropImage;
        var request = PeerRequest.GetMedia(PathFor(peerItemId, kind, tag, quality));
        try
        {
            using var response = await _client.SendAsync(peer, request, profile, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (new ImageFetchResult(ImageOutcome.NotOk), null, default);
            }

            if (response.MediaType is not ("image/jpeg" or "image/png"))
            {
                return (new ImageFetchResult(ImageOutcome.WrongContentType), null, default);
            }

            var bytes = await response.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
            var refusal = ImageProbe.Inspect(bytes, out var info);
            if (refusal != ImageRefusal.None)
            {
                _logger.LogDebug("An image from peer {Peer} was refused ({Reason})", Redact.Id(peer.PeerKey), refusal);
                return (new ImageFetchResult(ImageOutcome.Refused, Refusal: refusal), null, default);
            }

            var declaredPng = response.MediaType == "image/png";
            if (declaredPng != (info.Format == ImageFormat.Png))
            {
                return (new ImageFetchResult(ImageOutcome.WrongContentType), null, default);
            }

            return (null, bytes, info);
        }
        catch (PeerCallException ex)
        {
            _logger.LogDebug("An image fetch from peer {Peer} failed ({Kind})", Redact.Id(peer.PeerKey), ex.Kind);
            return (ex.Kind == PeerFailureKind.TooLarge
                ? new ImageFetchResult(ImageOutcome.TooLarge, Failure: ex.Kind)
                : new ImageFetchResult(ImageOutcome.PeerError, Failure: ex.Kind), null, default);
        }
    }

    private ImageFetchResult Store(PeerEndpoint peer, ImageKind kind, byte[] bytes, ImageInfo info, string stubRoot, string folder, DiskQuota quota)
    {
        var baseName = kind == ImageKind.Poster ? PosterName : BackdropName;
        var otherExtension = info.Format == ImageFormat.Png ? ".jpg" : ".png";
        if (!PathSanitizer.TryConfine(folder, baseName + info.Extension, out var target)
            || !PathSanitizer.TryConfine(folder, baseName + otherExtension, out var twin)
            || PathSanitizer.HasLinkBelow(stubRoot, target)
            || PathSanitizer.HasLinkBelow(stubRoot, twin))
        {
            _logger.LogWarning("An image path for peer {Peer} left its library and was not written", Redact.Id(peer.PeerKey));
            return new ImageFetchResult(ImageOutcome.UnsafePath);
        }

        var replacing = SizeOf(target) + SizeOf(twin);
        if (!quota.TryReserve(bytes.Length, replacing))
        {
            return new ImageFetchResult(ImageOutcome.QuotaReached);
        }

        try
        {
            AtomicFile.Write(target, bytes);
            if (File.Exists(twin))
            {
                File.Delete(twin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            quota.Add(replacing - bytes.Length);
            _logger.LogWarning("An image for peer {Peer} could not be written ({Error})", Redact.Id(peer.PeerKey), ex.GetType().Name);
            return new ImageFetchResult(ImageOutcome.WriteFailed);
        }

        return new ImageFetchResult(ImageOutcome.Stored, target, info, bytes.Length);
    }

    private static long SizeOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
