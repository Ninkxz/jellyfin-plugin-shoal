using System;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The image formats Shoal accepts (DESIGN 5.4).
/// </summary>
public enum ImageFormat
{
    /// <summary>Not an accepted format.</summary>
    None,

    /// <summary>JPEG, stored as <c>.jpg</c>.</summary>
    Jpeg,

    /// <summary>PNG, stored as <c>.png</c>.</summary>
    Png,
}

/// <summary>
/// Why an image was refused.
/// </summary>
public enum ImageRefusal
{
    /// <summary>The image is acceptable.</summary>
    None,

    /// <summary>The magic bytes are not JPEG or PNG: WebP, GIF, SVG, BMP, HTML and everything else.</summary>
    UnknownFormat,

    /// <summary>The header is damaged, truncated or not in the expected order, so the dimensions cannot be read.</summary>
    Malformed,

    /// <summary>The dimensions are over the limits (4000 wide, 6000 high, 16 megapixels).</summary>
    TooLargeDimensions,
}

/// <summary>
/// A validated image header.
/// </summary>
/// <param name="Format">The format, from the magic bytes.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public readonly record struct ImageInfo(ImageFormat Format, int Width, int Height)
{
    /// <summary>Gets the file extension for the format, from the magic bytes and never from the peer.</summary>
    public string Extension => Format == ImageFormat.Png ? ".png" : ".jpg";
}

/// <summary>
/// Reads the format and size of an image from its header without decoding it (DESIGN 5.4). The format comes from the magic bytes. JPEG
/// dimensions come from walking the marker segments by their declared lengths from the start of the file to the first SOF marker, never
/// by scanning for <c>FFC0..FFCF</c>: an EXIF thumbnail inside APP1 carries its own SOF, and a scan would read the thumbnail's small
/// size and pass an 8000 x 8000 image (W2 N-12). PNG dimensions come from the first chunk, which must be <c>IHDR</c>.
/// </summary>
public static class ImageProbe
{
    /// <summary>The widest accepted image.</summary>
    public const int MaxWidth = 4000;

    /// <summary>The tallest accepted image.</summary>
    public const int MaxHeight = 6000;

    /// <summary>The most pixels an accepted image has.</summary>
    public const long MaxPixels = 16_000_000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Inspects an image.
    /// </summary>
    /// <param name="data">The whole file (or at least its header).</param>
    /// <param name="info">The format and size when the image is acceptable.</param>
    /// <returns><see cref="ImageRefusal.None"/> when the image is acceptable, otherwise the reason.</returns>
    public static ImageRefusal Inspect(ReadOnlySpan<byte> data, out ImageInfo info) => Inspect(data, out info, MaxWidth, MaxHeight, MaxPixels);

    /// <summary>
    /// Inspects an image against limits of the caller's choosing (a trickplay tile is a mosaic and may be wider than a poster).
    /// </summary>
    /// <param name="data">The whole file (or at least its header).</param>
    /// <param name="info">The format and size when the image is acceptable.</param>
    /// <param name="maxWidth">The widest accepted image.</param>
    /// <param name="maxHeight">The tallest accepted image.</param>
    /// <param name="maxPixels">The most pixels accepted.</param>
    /// <returns><see cref="ImageRefusal.None"/> when the image is acceptable, otherwise the reason.</returns>
    public static ImageRefusal Inspect(ReadOnlySpan<byte> data, out ImageInfo info, int maxWidth, int maxHeight, long maxPixels)
    {
        info = default;
        ImageFormat format;
        ImageRefusal read;
        long width;
        long height;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            format = ImageFormat.Jpeg;
            read = ReadJpeg(data, out width, out height);
        }
        else if (data.Length >= PngSignature.Length && data[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            format = ImageFormat.Png;
            read = ReadPng(data, out width, out height);
        }
        else
        {
            return ImageRefusal.UnknownFormat;
        }

        if (read != ImageRefusal.None)
        {
            return read;
        }

        if (width < 1 || height < 1)
        {
            return ImageRefusal.Malformed;
        }

        if (width > maxWidth || height > maxHeight || width * height > maxPixels)
        {
            return ImageRefusal.TooLargeDimensions;
        }

        info = new ImageInfo(format, (int)width, (int)height);
        return ImageRefusal.None;
    }

    private static ImageRefusal ReadPng(ReadOnlySpan<byte> d, out long width, out long height)
    {
        width = 0;
        height = 0;
        if (d.Length < 24)
        {
            return ImageRefusal.Malformed;
        }

        var length = ReadUInt32(d, 8);
        if (length != 13 || d[12] != (byte)'I' || d[13] != (byte)'H' || d[14] != (byte)'D' || d[15] != (byte)'R')
        {
            return ImageRefusal.Malformed; // IHDR must be the first chunk
        }

        width = ReadUInt32(d, 16);
        height = ReadUInt32(d, 20);
        return ImageRefusal.None;
    }

    private static ImageRefusal ReadJpeg(ReadOnlySpan<byte> d, out long width, out long height)
    {
        width = 0;
        height = 0;
        var i = 2; // after SOI
        while (true)
        {
            if (i >= d.Length || d[i] != 0xFF)
            {
                return ImageRefusal.Malformed; // segments follow each other; bytes between them are never scanned
            }

            while (i < d.Length && d[i] == 0xFF)
            {
                i++; // fill bytes
            }

            if (i >= d.Length)
            {
                return ImageRefusal.Malformed;
            }

            var marker = d[i++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue; // standalone markers carry no length
            }

            if (marker is 0x00 or 0xD8 or 0xD9 or 0xDA)
            {
                return ImageRefusal.Malformed; // SOS or the end comes before any frame header
            }

            if (i + 2 > d.Length)
            {
                return ImageRefusal.Malformed;
            }

            var length = (d[i] << 8) | d[i + 1];
            if (length < 2 || i + length > d.Length)
            {
                return ImageRefusal.Malformed;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8)
                {
                    return ImageRefusal.Malformed;
                }

                height = (d[i + 3] << 8) | d[i + 4];
                width = (d[i + 5] << 8) | d[i + 6];
                return d[i + 7] == 0 ? ImageRefusal.Malformed : ImageRefusal.None;
            }

            i += length;
        }
    }

    private static long ReadUInt32(ReadOnlySpan<byte> d, int offset) =>
        ((long)d[offset] << 24) | ((long)d[offset + 1] << 16) | ((long)d[offset + 2] << 8) | d[offset + 3];
}
