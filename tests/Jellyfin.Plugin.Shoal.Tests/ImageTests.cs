using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Builds image headers for tests. Only the header matters to Shoal: nothing is ever decoded.
/// </summary>
internal static class ImageBuilder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Jpeg(int width, int height, int totalSize = 0, bool withExifThumbnail = false, byte sofMarker = 0xC0, bool fakeSofInComment = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (withExifThumbnail)
        {
            var thumbnail = new List<byte> { 0xFF, 0xD8 };
            thumbnail.AddRange(Sof(0xC0, 160, 120));
            thumbnail.AddRange([0xFF, 0xD9]);
            var exif = new List<byte>([(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0]);
            exif.AddRange(thumbnail);
            bytes.AddRange([0xFF, 0xE1, (byte)((exif.Count + 2) >> 8), (byte)((exif.Count + 2) & 0xFF)]);
            bytes.AddRange(exif);
        }

        if (fakeSofInComment)
        {
            var payload = Sof(0xC0, 16, 16).Concat(Sof(0xC2, 32, 32)).ToArray();
            bytes.AddRange([0xFF, 0xFE, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF)]);
            bytes.AddRange(payload);
        }

        bytes.AddRange(Sof(sofMarker, width, height));
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        while (bytes.Count < totalSize - 2)
        {
            bytes.Add(0x55);
        }

        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    public static byte[] Sof(byte marker, int width, int height) =>
        [0xFF, marker, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)(height & 0xFF), (byte)(width >> 8), (byte)(width & 0xFF), 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];

    public static byte[] Png(long width, long height, int totalSize = 0)
    {
        var bytes = new List<byte>(PngSignature);
        bytes.AddRange(UInt32(13));
        bytes.AddRange("IHDR"u8.ToArray());
        bytes.AddRange(UInt32(width));
        bytes.AddRange(UInt32(height));
        bytes.AddRange([8, 2, 0, 0, 0]);
        bytes.AddRange([0, 0, 0, 0]); // CRC, not checked
        var padding = Math.Max(0, totalSize - bytes.Count - 24);
        bytes.AddRange(UInt32(padding));
        bytes.AddRange("IDAT"u8.ToArray());
        bytes.AddRange(new byte[padding]);
        bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(UInt32(0));
        bytes.AddRange("IEND"u8.ToArray());
        bytes.AddRange([0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }

    public static byte[] WebP() => Encoding.ASCII.GetBytes("RIFF \0\0\0WEBPVP8 \u0014\0\0\0").Concat(new byte[40]).ToArray();

    private static byte[] UInt32(long value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}

/// <summary>
/// K9: the header probe (DESIGN 5.4): F-CAP-04, F-CAP-05, F-CAP-06 and the dimension limits.
/// </summary>
public sealed class ImageProbeTests
{
    [Fact]
    public void AJpegAndAPng_AreRecognizedFromTheirMagicBytes_WithTheirSize()
    {
        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(ImageBuilder.Jpeg(600, 900), out var jpeg));
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 600, 900), jpeg);
        Assert.Equal(".jpg", jpeg.Extension);

        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(ImageBuilder.Png(1280, 720), out var png));
        Assert.Equal(new ImageInfo(ImageFormat.Png, 1280, 720), png);
        Assert.Equal(".png", png.Extension);
    }

    [Fact]
    public void F_CAP_04_AWebP_IsRejected()
    {
        Assert.Equal(ImageRefusal.UnknownFormat, ImageProbe.Inspect(ImageBuilder.WebP(), out _));
    }

    [Theory]
    [InlineData("GIF89a\u0001\0\u0001\0")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("BM\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("<html><body>not an image</body></html>")]
    [InlineData("%PDF-1.7")]
    [InlineData("")]
    [InlineData("ÿ")]
    public void EverythingElse_IsRejected(string content)
    {
        Assert.Equal(ImageRefusal.UnknownFormat, ImageProbe.Inspect(Encoding.Latin1.GetBytes(content), out _));
    }

    [Fact]
    public void F_CAP_05_An8000By8000Png_IsRejectedFromTheHeaderAlone()
    {
        var header = ImageBuilder.Png(8000, 8000).AsSpan(0, 33).ToArray(); // nothing after the IHDR: there is nothing to decode

        Assert.Equal(ImageRefusal.TooLargeDimensions, ImageProbe.Inspect(header, out _));
    }

    [Theory]
    [InlineData(4000, 4000, true)]
    [InlineData(3000, 5333, true)]
    [InlineData(1, 1, true)]
    [InlineData(4001, 100, false)]
    [InlineData(100, 6001, false)]
    [InlineData(4000, 4001, false)]
    [InlineData(4000, 6000, false)]
    [InlineData(8000, 8000, false)]
    [InlineData(65535, 65535, false)]
    public void TheDimensionLimits_AreWidth4000_Height6000_And16Megapixels(int width, int height, bool accepted)
    {
        foreach (var image in new[] { ImageBuilder.Jpeg(width, height), ImageBuilder.Png(width, height) })
        {
            var result = ImageProbe.Inspect(image, out _);
            Assert.Equal(accepted ? ImageRefusal.None : ImageRefusal.TooLargeDimensions, result);
        }
    }

    [Fact]
    public void F_CAP_06_AnExifThumbnailsSof_IsNotTheImagesSize()
    {
        // The EXIF thumbnail (160 x 120) comes first in APP1; a byte scan would read it and pass an 8000 x 8000 image.
        var big = ImageBuilder.Jpeg(8000, 8000, withExifThumbnail: true);

        Assert.Equal(ImageRefusal.TooLargeDimensions, ImageProbe.Inspect(big, out _));
    }

    [Fact]
    public void F_CAP_06_SofBytesInsideAnotherSegment_AreSkippedByTheWalk()
    {
        var image = ImageBuilder.Jpeg(600, 900, withExifThumbnail: true, fakeSofInComment: true);

        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(image, out var info));
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 600, 900), info);
    }

    [Theory]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xC3)]
    [InlineData((byte)0xC5)]
    [InlineData((byte)0xCF)]
    public void AnySofVariant_IsAFrameHeader(byte marker)
    {
        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(ImageBuilder.Jpeg(640, 480, sofMarker: marker), out var info));
        Assert.Equal(640, info.Width);
    }

    [Theory]
    [InlineData((byte)0xC4)]
    [InlineData((byte)0xC8)]
    [InlineData((byte)0xCC)]
    public void DhtJpgAndDacAreNotFrameHeaders(byte marker)
    {
        // They share the 0xC0..0xCF range but carry tables, not sizes; the walk goes past them and finds nothing.
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(ImageBuilder.Jpeg(640, 480, sofMarker: marker), out _));
    }

    [Fact]
    public void FillBytesBeforeAMarker_AreAccepted()
    {
        var plain = ImageBuilder.Jpeg(640, 480);
        var withFill = plain.Take(2).Concat([(byte)0xFF, (byte)0xFF, (byte)0xFF]).Concat(plain.Skip(2).Skip(1)).ToArray();

        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(withFill, out var info));
        Assert.Equal(640, info.Width);
    }

    [Fact]
    public void DamagedJpegHeaders_AreMalformed()
    {
        var good = ImageBuilder.Jpeg(640, 480);
        var sofAt = Array.IndexOf(good, (byte)0xC0) - 1;

        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(good.Take(20).ToArray(), out _)); // truncated inside APP0
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(good.Take(sofAt + 6).ToArray(), out _)); // truncated inside SOF
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([0xFF, 0xD8, 0xFF], out _));
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0], out _)); // scan before any frame header
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([0xFF, 0xD8, 0xFF, 0xD9], out _));
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0, 0], out _)); // length < 2
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0, 0], out _)); // length past the data
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect([.. good.Take(20), 0x00, .. good.Skip(20)], out _)); // junk between segments is never scanned
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(ImageBuilder.Jpeg(0, 480), out _));
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(ImageBuilder.Jpeg(640, 0), out _));
    }

    [Fact]
    public void DamagedPngHeaders_AreMalformed()
    {
        var good = ImageBuilder.Png(640, 480);

        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(good.Take(20).ToArray(), out _));
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(ImageBuilder.Png(0, 480), out _));
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(ImageBuilder.Png(640, 0), out _));

        var notFirst = good.ToArray();
        notFirst[12] = (byte)'t'; // the first chunk is not IHDR
        notFirst[13] = (byte)'E';
        notFirst[14] = (byte)'X';
        notFirst[15] = (byte)'t';
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(notFirst, out _));

        var wrongLength = good.ToArray();
        wrongLength[11] = 12;
        Assert.Equal(ImageRefusal.Malformed, ImageProbe.Inspect(wrongLength, out _));
    }

    [Fact]
    public void AHugePngDimension_DoesNotOverflow()
    {
        Assert.Equal(ImageRefusal.TooLargeDimensions, ImageProbe.Inspect(ImageBuilder.Png(0xFFFFFFFFL, 0xFFFFFFFFL), out _));
    }
}

/// <summary>
/// K9: the disk budget (DESIGN 5.10).
/// </summary>
public sealed class DiskQuotaTests
{
    [Fact]
    public void ARequestThatFits_IsCounted_AndOneThatDoesNot_IsRefusedAndMarksTheQuotaExhausted()
    {
        var quota = new DiskQuota(1000);

        Assert.True(quota.TryReserve(600));
        Assert.False(quota.Exhausted);
        Assert.False(quota.TryReserve(401));
        Assert.True(quota.Exhausted);
        Assert.Equal(600, quota.UsedBytes);
    }

    [Fact]
    public void ExhaustionSticks_EvenWhenALaterRequestWouldFit()
    {
        var quota = new DiskQuota(1000);
        quota.TryReserve(900);
        quota.TryReserve(500);

        Assert.True(quota.Exhausted);
        Assert.True(quota.TryReserve(50)); // a caller that skips the Exhausted check could still add; the fetcher does not
        Assert.True(quota.Exhausted);
    }

    [Fact]
    public void AReplacement_OnlyCountsTheDifference()
    {
        var quota = new DiskQuota(1000, usedBytes: 900);

        Assert.True(quota.TryReserve(300, replacing: 250));
        Assert.Equal(950, quota.UsedBytes);
        Assert.True(quota.TryReserve(100, replacing: 400));
        Assert.Equal(650, quota.UsedBytes);
    }

    [Fact]
    public void AFullQuota_IsExhausted_AtOnce()
    {
        Assert.True(new DiskQuota(1000, usedBytes: 1000).Exhausted);
        Assert.False(new DiskQuota(1000, usedBytes: 999).Exhausted);
    }

    [Fact]
    public void Stubs_AreCountedButNeverRefused()
    {
        var quota = new DiskQuota(100);

        quota.Add(500);

        Assert.Equal(500, quota.UsedBytes);
        quota.Add(-1000);
        Assert.Equal(0, quota.UsedBytes);
    }

    [Theory]
    [InlineData(2, 2L * 1073741824)]
    [InlineData(0, 1L * 1073741824)]
    [InlineData(50, 50L * 1073741824)]
    [InlineData(1000, 50L * 1073741824)]
    public void TheSettingInGigabytes_IsClampedTo1To50(int gigabytes, long expected)
    {
        Assert.Equal(expected, DiskQuota.LimitFromGigabytes(gigabytes));
    }
}

/// <summary>
/// K9 acceptance for ImageFetcher: T-SYNC-7 (hostile images), serial fetching, and T-SYNC-10 (F-CAP-01 scaled down).
/// </summary>
public sealed class ImageFetcherTests : IAsyncLifetime
{
    private const string Tag = "abc123";
    private static readonly Guid ItemId = Guid.Parse("0123456789abcdef0123456789abcdef");

    private readonly TempDirectory _temp = new();
    private readonly PeerClient _client = new(() => "Home", () => "0.1.0.0", TimeProvider.System) { EnforceAddressPolicy = false };
    private readonly ConcurrentQueue<string> _queries = new();
    private WebApplication? _app;
    private string _stubRoot = string.Empty;
    private string _folder = string.Empty;
    private int _hits;
    private int _active;
    private int _maxActive;

    public Func<string, (int Status, string? ContentType, byte[] Body, bool Chunked, int DelayMs)> Respond { get; set; } =
        _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 2000), false, 0);

    public IImageSanitizer? Sanitizer { get; set; }

    private ImageFetcher Fetcher => new(_client, NullLogger<ImageFetcher>.Instance, Sanitizer);

    private PeerEndpoint Peer { get; set; } = null!;

    public async Task InitializeAsync()
    {
        _stubRoot = _temp.Combine("stubs");
        _folder = Path.Combine(_stubRoot, "peerkey", "Movies", "The Matrix (1999) [tmdbid-603]");
        Directory.CreateDirectory(_folder);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Map("/{**path}", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _hits);
            _queries.Enqueue(ctx.Request.Path + ctx.Request.QueryString.Value);
            var now = Interlocked.Increment(ref _active);
            InterlockedMax(ref _maxActive, now);
            try
            {
                var (status, contentType, body, chunked, delay) = Respond(ctx.Request.Path);
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }

                ctx.Response.StatusCode = status;
                if (contentType is not null)
                {
                    ctx.Response.ContentType = contentType;
                }

                if (!chunked)
                {
                    ctx.Response.ContentLength = body.Length;
                }

                try
                {
                    await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        });
        await _app.StartAsync();
        var url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Peer = new PeerEndpoint("00000000000000000000000000000abc", "Hops", new Uri(url), "shoal-1a2b3c4d5e6f-4b0c1d2e", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _client.Dispose();
        _temp.Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private Task<ImageFetchResult> FetchAsync(DiskQuota? quota = null, ImageKind kind = ImageKind.Poster, ArtQuality art = ArtQuality.Standard, string? tag = Tag, string? folder = null) =>
        Fetcher.FetchAsync(Peer, ItemId, kind, tag, art, _stubRoot, folder ?? _folder, quota ?? new DiskQuota(1L << 30), CancellationToken.None);

    // --- the happy path and the request

    [Fact]
    public async Task APoster_IsRequestedAsJpg600Wide_AndStoredNextToTheStub()
    {
        var quota = new DiskQuota(1L << 30);

        var result = await FetchAsync(quota);

        Assert.Equal(ImageOutcome.Stored, result.Outcome);
        Assert.Equal(Path.Combine(_folder, "poster.jpg"), result.Path);
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 600, 900), result.Info);
        Assert.Equal(2000, new FileInfo(result.Path!).Length);
        Assert.Equal(2000, quota.UsedBytes);
        Assert.Equal($"/Items/{ItemId:N}/Images/Primary?format=Jpg&maxWidth=600&quality=85&tag=abc123", _queries.Single());
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));
    }

    [Theory]
    [InlineData(ImageKind.Poster, ArtQuality.Standard, "Primary?format=Jpg&maxWidth=600&quality=85")]
    [InlineData(ImageKind.Poster, ArtQuality.High, "Primary?format=Jpg&maxWidth=1000&quality=85")]
    [InlineData(ImageKind.Backdrop, ArtQuality.Standard, "Backdrop?format=Jpg&maxWidth=1280&quality=80")]
    [InlineData(ImageKind.Backdrop, ArtQuality.High, "Backdrop?format=Jpg&maxWidth=1920&quality=80")]
    public async Task TheSizeFollowsTheArtQuality(ImageKind kind, ArtQuality art, string expected)
    {
        var result = await FetchAsync(kind: kind, art: art);

        Assert.Equal(ImageOutcome.Stored, result.Outcome);
        Assert.Equal($"/Items/{ItemId:N}/Images/{expected}&tag=abc123", _queries.Single());
        Assert.Equal(kind == ImageKind.Poster ? "poster.jpg" : "backdrop.jpg", Path.GetFileName(result.Path));
    }

    // --- the image sandbox hook (DESIGN v2.6.7, K32)

    [Fact]
    public async Task TheSanitizer_RunsAfterTheHeaderChecks_AndItsBytesAreWhatIsStored()
    {
        var specs = new List<ImageSpec>();
        var cleaned = ImageBuilder.Jpeg(300, 450, 900);
        Sanitizer = new RecordingSanitizer(specs, new ImageSanitizeResult(true, cleaned));
        var quota = new DiskQuota(1L << 30);

        var result = await FetchAsync(quota, kind: ImageKind.Backdrop, art: ArtQuality.High);

        Assert.Equal(ImageOutcome.Stored, result.Outcome);
        Assert.Equal(new ImageSpec(ImageKind.Backdrop, 1920), specs.Single());
        Assert.Equal(cleaned, await File.ReadAllBytesAsync(result.Path!));
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 300, 450), result.Info);
        Assert.Equal(900, quota.UsedBytes);
    }

    [Fact]
    public async Task ARefusedImage_StoresNothing_AndTouchesNoQuota_AsAFailedHeaderCheckDoes()
    {
        Sanitizer = new RecordingSanitizer([], new ImageSanitizeResult(false, ReadOnlyMemory<byte>.Empty));
        var quota = new DiskQuota(1L << 30);

        var result = await FetchAsync(quota);

        Assert.Equal(ImageOutcome.SandboxRejected, result.Outcome);
        Assert.Null(result.Path);
        Assert.Equal(0, quota.UsedBytes);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task ASanitizerThatReturnsGarbage_IsRefused_NotStored()
    {
        Sanitizer = new RecordingSanitizer([], new ImageSanitizeResult(true, "<html>"u8.ToArray()));

        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.SandboxRejected, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task TheSanitizer_IsNeverAsked_WhenTheHeaderCheckAlreadyFailed()
    {
        var specs = new List<ImageSpec>();
        Sanitizer = new RecordingSanitizer(specs, new ImageSanitizeResult(true, ReadOnlyMemory<byte>.Empty));
        Respond = _ => (200, "image/jpeg", ImageBuilder.WebP(), false, 0);

        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.Refused, result.Outcome);
        Assert.Empty(specs);
    }

    private sealed class RecordingSanitizer : IImageSanitizer
    {
        private readonly List<ImageSpec> _specs;
        private readonly ImageSanitizeResult _result;

        public RecordingSanitizer(List<ImageSpec> specs, ImageSanitizeResult result)
        {
            _specs = specs;
            _result = result;
        }

        public Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken)
        {
            _specs.Add(spec);
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task APngAnswer_IsStoredAsPng_AndAStaleJpgIsRemoved()
    {
        var quota = new DiskQuota(1L << 30);
        Assert.Equal(ImageOutcome.Stored, (await FetchAsync(quota)).Outcome);
        Assert.True(File.Exists(Path.Combine(_folder, "poster.jpg")));
        Respond = _ => (200, "image/png", ImageBuilder.Png(600, 900, 3000), false, 0);

        var result = await FetchAsync(quota);

        Assert.Equal(ImageOutcome.Stored, result.Outcome);
        Assert.Equal(".png", Path.GetExtension(result.Path));
        Assert.False(File.Exists(Path.Combine(_folder, "poster.jpg")));
        Assert.Equal(3000, quota.UsedBytes); // the replaced file was given back
    }

    [Fact]
    public async Task ARefreshOfTheSameImage_ReplacesItInPlace_AndTheQuotaDoesNotGrow()
    {
        var quota = new DiskQuota(1L << 30);
        await FetchAsync(quota);
        await FetchAsync(quota);

        Assert.Equal(2000, quota.UsedBytes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("tag-1")]
    [InlineData("abc\"><x>")]
    public async Task AnItemWithoutAValidTag_MakesNoRequest(string? tag)
    {
        var result = await FetchAsync(tag: tag);

        Assert.Equal(ImageOutcome.Skipped, result.Outcome);
        Assert.Equal(0, _hits);
    }

    // --- T-SYNC-7: hostile images

    [Fact]
    public async Task HtmlServedAsAnImage_IsRejected_WhateverItIsCalled()
    {
        Respond = _ => (200, "text/html", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), false, 0);
        Assert.Equal(ImageOutcome.WrongContentType, (await FetchAsync()).Outcome);

        Respond = _ => (200, "image/jpeg", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), false, 0);
        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.Refused, result.Outcome);
        Assert.Equal(ImageRefusal.UnknownFormat, result.Refusal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task F_CAP_04_AWebP_IsRejected_AsImageJpegAndAsImageWebp()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.WebP(), false, 0);
        var disguised = await FetchAsync();
        Respond = _ => (200, "image/webp", ImageBuilder.WebP(), false, 0);
        var honest = await FetchAsync();

        Assert.Equal(ImageOutcome.Refused, disguised.Outcome);
        Assert.Equal(ImageRefusal.UnknownFormat, disguised.Refusal);
        Assert.Equal(ImageOutcome.WrongContentType, honest.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("image/bmp")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task OnlyJpegAndPngContentTypes_AreAccepted(string? contentType)
    {
        Respond = _ => (200, contentType, ImageBuilder.Jpeg(600, 900, 2000), false, 0);

        Assert.Equal(ImageOutcome.WrongContentType, (await FetchAsync()).Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task APngLabelledJpeg_AndTheOtherWayRound_AreRejected()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Png(600, 900, 2000), false, 0);
        Assert.Equal(ImageOutcome.WrongContentType, (await FetchAsync()).Outcome);
        Respond = _ => (200, "image/png", ImageBuilder.Jpeg(600, 900, 2000), false, 0);
        Assert.Equal(ImageOutcome.WrongContentType, (await FetchAsync()).Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task F_CAP_05_An8000By8000Png_IsRejected_AndNothingIsWritten()
    {
        Respond = _ => (200, "image/png", ImageBuilder.Png(8000, 8000, 5000), false, 0);

        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.Refused, result.Outcome);
        Assert.Equal(ImageRefusal.TooLargeDimensions, result.Refusal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task F_CAP_06_AJpegWithAThumbnailInExif_ButHugeInReality_IsRejected()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(8000, 8000, 5000, withExifThumbnail: true), false, 0);

        var result = await FetchAsync();

        Assert.Equal(ImageRefusal.TooLargeDimensions, result.Refusal);
    }

    [Fact]
    public async Task AFiftyMegabyteImage_IsRejected_WhetherItDeclaresItsLengthOrNot()
    {
        var fifty = ImageBuilder.Jpeg(600, 900, 50 * 1024 * 1024);
        Respond = _ => (200, "image/jpeg", fifty, false, 0);
        var declared = await FetchAsync();
        Respond = _ => (200, "image/jpeg", fifty, true, 0);
        var chunked = await FetchAsync();

        Assert.Equal(ImageOutcome.TooLarge, declared.Outcome);
        Assert.Equal(ImageOutcome.TooLarge, chunked.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task APosterIsCappedAt2Megabytes_AndABackdropAt4()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, (3 * 1024 * 1024)), false, 0);
        Assert.Equal(ImageOutcome.TooLarge, (await FetchAsync(kind: ImageKind.Poster)).Outcome);
        Assert.Equal(ImageOutcome.Stored, (await FetchAsync(kind: ImageKind.Backdrop)).Outcome);

        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, (5 * 1024 * 1024)), true, 0);
        Assert.Equal(ImageOutcome.TooLarge, (await FetchAsync(kind: ImageKind.Backdrop)).Outcome);
    }

    [Fact]
    public async Task AnythingButA200_IsNoImage()
    {
        Respond = _ => (404, "text/plain", "not found"u8.ToArray(), false, 0);
        Assert.Equal(ImageOutcome.NotOk, (await FetchAsync()).Outcome);
        Respond = _ => (500, "image/jpeg", ImageBuilder.Jpeg(600, 900, 2000), false, 0);
        Assert.Equal(ImageOutcome.NotOk, (await FetchAsync()).Outcome);
        Respond = _ => (206, "image/jpeg", ImageBuilder.Jpeg(600, 900, 2000), false, 0);
        Assert.Equal(ImageOutcome.NotOk, (await FetchAsync()).Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task ARedirect_IsAPeerError_AndNotFollowed()
    {
        Respond = _ => (302, null, [], false, 0);

        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.PeerError, result.Outcome);
        Assert.Equal(PeerFailureKind.Redirect, result.Failure);
        Assert.Equal(1, _hits);
    }

    [Fact]
    public async Task APeerThatIsGone_IsAPeerError_NotAnException()
    {
        await _app!.StopAsync();

        var result = await FetchAsync();

        Assert.Equal(ImageOutcome.PeerError, result.Outcome);
    }

    // --- serial

    [Fact]
    public async Task ImagesAreFetchedOneAtATime_WhoeverAsks()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 2000), false, 40);
        var quota = new DiskQuota(1L << 30);
        var fetcher = Fetcher;

        var folders = Enumerable.Range(0, 8).Select(i => Directory.CreateDirectory(Path.Combine(_stubRoot, "peerkey", "Movies", "Movie " + i)).FullName).ToList();
        var results = await Task.WhenAll(folders.Select(f => fetcher.FetchAsync(Peer, ItemId, ImageKind.Poster, Tag, ArtQuality.Standard, _stubRoot, f, quota, CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(ImageOutcome.Stored, r.Outcome));
        Assert.Equal(8, _hits);
        Assert.Equal(1, _maxActive);
    }

    // --- T-SYNC-10: F-CAP-01 scaled down

    [Fact]
    public async Task T_SYNC_10_ImagesStopAtTheQuota_AndTheNextOnesMakeNoRequest()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 1_000_000), false, 0);
        var quota = new DiskQuota(3_500_000);
        var stored = 0;
        var firstRefusal = -1;

        for (var i = 0; i < 12; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_stubRoot, "peerkey", "Movies", "Movie " + i)).FullName;
            var result = await FetchAsync(quota, folder: folder);
            if (result.Outcome == ImageOutcome.Stored)
            {
                stored++;
            }
            else if (firstRefusal < 0)
            {
                Assert.Equal(ImageOutcome.QuotaReached, result.Outcome);
                firstRefusal = i;
            }
            else
            {
                Assert.Equal(ImageOutcome.QuotaReached, result.Outcome);
            }
        }

        Assert.Equal(3, stored);
        Assert.Equal(3, firstRefusal);
        Assert.True(quota.Exhausted);
        Assert.Equal(3_000_000, quota.UsedBytes);
        Assert.Equal(4, _hits); // the refusal itself needed the download to know the size; later titles asked nobody
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_stubRoot, "peerkey", "Movies"), "poster.jpg", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task AQuotaThatIsAlreadyFull_MakesNoRequestAtAll()
    {
        var result = await FetchAsync(new DiskQuota(1000, usedBytes: 1000));

        Assert.Equal(ImageOutcome.QuotaReached, result.Outcome);
        Assert.Equal(0, _hits);
    }

    // --- paths

    [Fact]
    public async Task AFolderOutsideTheStubRoot_IsNeverWritten()
    {
        var outside = Directory.CreateDirectory(_temp.Combine("elsewhere")).FullName;
        var quota = new DiskQuota(1L << 30);

        var result = await FetchAsync(quota, folder: outside);

        Assert.Equal(ImageOutcome.UnsafePath, result.Outcome);
        Assert.Empty(Directory.GetFiles(outside));
        Assert.Equal(0, quota.UsedBytes);
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_GivesBackTheQuota()
    {
        var quota = new DiskQuota(1L << 30);

        var result = await FetchAsync(quota, folder: Path.Combine(_stubRoot, "peerkey", "Movies", "Missing"));

        Assert.Equal(ImageOutcome.WriteFailed, result.Outcome);
        Assert.Equal(0, quota.UsedBytes);
    }

    [Fact]
    public async Task ACancelledFetch_Throws_ItIsNotAnOutcome()
    {
        Respond = _ => (200, "image/jpeg", ImageBuilder.Jpeg(600, 900, 2000), false, 2000);
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fetcher.FetchAsync(Peer, ItemId, ImageKind.Poster, Tag, ArtQuality.Standard, _stubRoot, _folder, new DiskQuota(1L << 30), cts.Token));
    }

    [Fact]
    public void ThePathHasNoUserData_OnlyTheValidatedIdAndTag()
    {
        var path = ImageFetcher.PathFor(ItemId, ImageKind.Poster, Tag, ArtQuality.Standard);

        Assert.Equal($"/Items/0123456789abcdef0123456789abcdef/Images/Primary?format=Jpg&maxWidth=600&quality=85&tag=abc123", path);
        Assert.All(path, static c => Assert.InRange(c, '!', '~'));
    }
}
