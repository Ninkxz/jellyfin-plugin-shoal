using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Cleans a peer image after the header checks of DESIGN 5.4 and before it is stored (DESIGN v2.6.7, K32). The default passes the bytes through.
/// </summary>
public interface IImageSanitizer
{
    /// <summary>
    /// Cleans one image.
    /// </summary>
    /// <param name="input">The image bytes; they already passed the magic and dimension checks.</param>
    /// <param name="spec">What the image is for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result; <see cref="ImageSanitizeResult.Ok"/> false means the image is refused.</returns>
    Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// What an image is for and how large it may become (DESIGN v2.6.7).
/// </summary>
/// <param name="Kind">The kind of image.</param>
/// <param name="MaxWidth">The widest the result may be, or null for no resize.</param>
/// <param name="MaxHeight">The tallest the result may be, or null for no resize.</param>
public sealed record ImageSpec(ImageKind Kind, int? MaxWidth = null, int? MaxHeight = null);

/// <summary>
/// The outcome of <see cref="IImageSanitizer.SanitizeAsync"/>.
/// </summary>
/// <param name="Ok">True when <paramref name="Bytes"/> may be stored.</param>
/// <param name="Bytes">The image to store (a fresh JPEG for the child process, the input for the pass-through).</param>
public sealed record ImageSanitizeResult(bool Ok, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// The default <see cref="IImageSanitizer"/>: what passed the header checks is stored as it is (v1).
/// </summary>
public sealed class PassThroughImageSanitizer : IImageSanitizer
{
    /// <inheritdoc />
    public Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken) =>
        Task.FromResult(new ImageSanitizeResult(true, input));
}
