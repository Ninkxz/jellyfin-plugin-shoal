using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Describes how a child is started: the program and its arguments for a given ffmpeg path and ffmpeg argument list.
/// </summary>
/// <param name="encoderPath">The host's ffmpeg.</param>
/// <param name="ffmpegArguments">The ffmpeg arguments (a list, never a shell string).</param>
/// <returns>The program to start and its arguments.</returns>
public delegate (string File, IReadOnlyList<string> Arguments) ChildCommand(string encoderPath, IReadOnlyList<string> ffmpegArguments);

/// <summary>
/// How one child run ended.
/// </summary>
internal enum ChildRunKind
{
    /// <summary>The child produced a valid image.</summary>
    Ok,

    /// <summary>The child could not be started (ffmpeg is missing).</summary>
    CouldNotStart,

    /// <summary>The child ran and failed, timed out or produced a bad image.</summary>
    Failed,
}

/// <summary>
/// Where the host's ffmpeg is (DESIGN v2.6.7). A seam so the sanitizer can be tested without a server.
/// </summary>
public interface IEncoderLocator
{
    /// <summary>Gets the path of the host's ffmpeg, or null or empty when the host has none.</summary>
    string? EncoderPath { get; }
}

/// <summary>
/// The sandbox's state, for Status and the settings page.
/// </summary>
public interface IImageSandboxStatus
{
    /// <summary>
    /// Reads the current state; never blocks and never starts a process.
    /// </summary>
    /// <returns>The state.</returns>
    ImageSandboxStatus GetStatus();

    /// <summary>
    /// Runs the self-test again in the background (after the switch was turned on).
    /// </summary>
    void RequestSelfTest();
}

/// <summary>
/// The result of one child run.
/// </summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Output">The image, for <see cref="ChildRunKind.Ok"/>.</param>
internal readonly record struct ChildRunResult(ChildRunKind Kind, byte[] Output);

/// <summary>
/// The host's ffmpeg, as Jellyfin found it (<see cref="IMediaEncoder.EncoderPath"/>).
/// </summary>
public sealed class MediaEncoderLocator : IEncoderLocator
{
    private readonly IMediaEncoder _encoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaEncoderLocator"/> class.
    /// </summary>
    /// <param name="encoder">The host's media encoder.</param>
    public MediaEncoderLocator(IMediaEncoder encoder) => _encoder = encoder;

    /// <inheritdoc />
    public string? EncoderPath => _encoder.EncoderPath;
}

/// <summary>
/// What the image sandbox reports to the admin page (DESIGN v2.6.7: <c>imageSandbox {state, limits, rejected}</c>).
/// </summary>
/// <param name="State"><c>active</c>, <c>unavailable</c> (the self-test failed or ffmpeg is missing) or <c>off</c> (the switch).</param>
/// <param name="Limits"><c>full</c> (address space, CPU time, no file writes, as well as time and size) or <c>partial</c> (time and size only).</param>
/// <param name="Reason">Why the sandbox is unavailable (<c>ffmpegMissing</c>, <c>selfTestFailed</c>), or null.</param>
/// <param name="Rejected">How many images the sandbox has refused since the server started.</param>
public sealed record ImageSandboxStatus(string State, string Limits, string? Reason, long Rejected);

/// <summary>
/// Re-encodes a peer image in a resource-limited child process (DESIGN v2.6.7, K32). The peer's bytes are decoded by the host's ffmpeg
/// under a wall clock, an output cap and, on Linux where <c>prlimit</c> exists, an address-space, CPU and file-size limit; what is stored is a
/// fresh baseline JPEG with the metadata stripped. This contains a decoder crash or a decompression bomb and keeps the decoder out of the
/// Jellyfin process. It is not a privilege sandbox. One child runs at a time. When the switch is off, or the self-test shows that ffmpeg
/// cannot be used while enabled, new images are refused until it recovers. Disabling it explicitly retains the original bytes.
/// </summary>
public sealed class ChildImageSanitizer : IImageSanitizer, IImageSandboxStatus, IDisposable
{
    /// <summary>The most output bytes read from a child.</summary>
    public const int MaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>The address-space limit of a child on Linux, in bytes.</summary>
    public const long AddressSpaceBytes = 805_306_368;

    /// <summary>The CPU-time limit of a child on Linux, in seconds.</summary>
    public const int CpuSeconds = 6;

    /// <summary>The most stderr bytes read at a time; they are discarded.</summary>
    public const int MaxStderrBytes = 4096;

    /// <summary>The longest a child may run.</summary>
    public static readonly TimeSpan DefaultWallClock = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan RetestAfter = TimeSpan.FromSeconds(60);

    private readonly IEncoderLocator _encoder;
    private readonly Func<bool> _enabled;
    private readonly string _workRoot;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _wallClock;
    private readonly ChildCommand? _command;
    private readonly string? _prlimit;
    private readonly SemaphoreSlim _syncLane = new(1, 1); // background sync images and the self-test: one child at a time
    private readonly SemaphoreSlim _tileLane = new(1, 1); // trickplay tiles, which a person waits for: never behind a sync image (Warden 10 L3)
    private readonly HashSet<Process> _live = [];
    private readonly Lock _liveLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _rejected;
    private int _testScheduled;
    private volatile bool _disposed;
    private volatile int _availability; // 0 untested, 1 available, 2 unavailable
    private volatile string? _reason;
    private long _unavailableAtTicks;
    private int _failures; // consecutive failed self-tests or starts; two make the sandbox unavailable (Warden 10 L2)

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildImageSanitizer"/> class.
    /// </summary>
    /// <param name="encoder">The host's ffmpeg.</param>
    /// <param name="enabled">Reads the <c>ReencodeImages</c> switch.</param>
    /// <param name="workRoot">The folder under which every child gets a fresh empty working directory.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="wallClock">The wall clock of one child; the default is 8 seconds.</param>
    /// <param name="command">Replaces how the child is launched; tests only.</param>
    public ChildImageSanitizer(IEncoderLocator encoder, Func<bool> enabled, string workRoot, TimeProvider time, ILogger<ChildImageSanitizer> logger, TimeSpan? wallClock = null, ChildCommand? command = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        _encoder = encoder;
        _enabled = enabled;
        _workRoot = workRoot;
        _time = time;
        _logger = logger;
        _wallClock = wallClock ?? DefaultWallClock;
        _command = command;
        _prlimit = command is null ? FindPrlimit() : null;
    }

    /// <summary>Gets the number of images refused since the start.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>
    /// The ffmpeg arguments for one image (DESIGN v2.6.7). Nothing here comes from the peer: the decoder is chosen from Home's own magic check
    /// and the width from Home's settings.
    /// </summary>
    /// <param name="input">The format found by <see cref="ImageProbe"/>.</param>
    /// <param name="maxWidth">The widest the result may be, or null for no resize.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> BuildArguments(ImageFormat input, int? maxWidth)
    {
        var filter = maxWidth is { } width and > 0
            ? string.Create(CultureInfo.InvariantCulture, $"scale='min({width},iw)':-2:flags=lanczos,format=yuvj420p")
            : "format=yuvj420p";
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
            "-protocol_whitelist", "pipe",
            "-f", "image2pipe", "-c:v", input == ImageFormat.Png ? "png" : "mjpeg", "-i", "pipe:0",
            "-frames:v", "1", "-an", "-sn", "-dn", "-map_metadata", "-1",
            "-vf", filter,
            "-c:v", "mjpeg", "-q:v", "3", "-f", "image2pipe", "pipe:1",
        ];
    }

    /// <summary>
    /// Puts <c>prlimit</c> in front of the ffmpeg command (Linux): address space, CPU time, no file writes, no core dumps, few descriptors.
    /// </summary>
    /// <param name="prlimit">The path of <c>prlimit</c>.</param>
    /// <param name="encoder">The path of ffmpeg.</param>
    /// <param name="ffmpegArguments">The ffmpeg arguments.</param>
    /// <returns>The program to start and its arguments.</returns>
    public static (string File, IReadOnlyList<string> Arguments) WrapWithPrlimit(string prlimit, string encoder, IReadOnlyList<string> ffmpegArguments)
    {
        List<string> arguments =
        [
            string.Create(CultureInfo.InvariantCulture, $"--as={AddressSpaceBytes}"),
            string.Create(CultureInfo.InvariantCulture, $"--cpu={CpuSeconds}"),
            "--fsize=0", "--core=0", "--nofile=32", "--", encoder,
        ];
        arguments.AddRange(ffmpegArguments);
        return (prlimit, arguments);
    }

    /// <inheritdoc />
    public ImageSandboxStatus GetStatus()
    {
        var limits = _command is null && _prlimit is not null ? "full" : "partial";
        if (!_enabled())
        {
            return new ImageSandboxStatus("off", limits, null, Rejected);
        }

        return _availability == 2
            ? new ImageSandboxStatus("unavailable", limits, _reason, Rejected)
            : new ImageSandboxStatus("active", limits, null, Rejected);
    }

    /// <inheritdoc />
    public void RequestSelfTest()
    {
        if (Interlocked.CompareExchange(ref _testScheduled, 1, 0) != 0)
        {
            return; // one test at a time
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SelfTestAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Volatile.Write(ref _testScheduled, 0);
            }
        });
    }

    /// <summary>
    /// Re-encodes a built-in 64 by 64 image and checks the result. A failure makes the sandbox unavailable until the next test.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the child works.</returns>
    public async Task<bool> SelfTestAsync(CancellationToken cancellationToken)
    {
        await _syncLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SelfTestLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLane.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ImageSanitizeResult> SanitizeAsync(ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var passThrough = new ImageSanitizeResult(true, input);
        if (_disposed)
        {
            return Refuse();
        }

        if (!_enabled())
        {
            return passThrough;
        }

        if (InspectFor(spec.Kind, input.Span, out var info) != ImageRefusal.None)
        {
            Interlocked.Increment(ref _rejected);
            return Refuse();
        }

        // A tile is waited for by a person (DESIGN v2.6.5): it has its own lane, so it never queues behind a sync image, and it never runs the self-test on the
        // request path. When the sandbox is not known to work, the test is started in the background; until it passes the tile is refused.
        var tile = spec.Kind == ImageKind.Tile;
        if (tile && _availability != 1)
        {
            if (_availability == 2 && _time.GetUtcNow().UtcTicks - Interlocked.Read(ref _unavailableAtTicks) < RetestAfter.Ticks)
            {
                Interlocked.Increment(ref _rejected);
                return Refuse();
            }

            RequestSelfTest();
            Interlocked.Increment(ref _rejected);
            return Refuse();
        }

        var lane = tile ? _tileLane : _syncLane;
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!tile)
            {
                switch (await EnsureAvailableLockedAsync(cancellationToken).ConfigureAwait(false))
                {
                    case false:
                        Interlocked.Increment(ref _rejected);
                        return Refuse();
                    case null:
                        Interlocked.Increment(ref _rejected); // one failure is not yet proof: this image waits for the next sync, it is not stored unsanitized
                        return Refuse();
                }
            }

            var run = await RunAsync(input, BuildArguments(info.Format, spec.MaxWidth), spec, cancellationToken).ConfigureAwait(false);
            switch (run.Kind)
            {
                case ChildRunKind.Ok:
                    _failures = 0;
                    return new ImageSanitizeResult(true, run.Output);
                case ChildRunKind.CouldNotStart:
                    NoteFailure("ffmpegMissing");
                    Interlocked.Increment(ref _rejected);
                    return Refuse();
                default:
                    Interlocked.Increment(ref _rejected);
                    return Refuse();
            }
        }
        finally
        {
            lane.Release();
        }
    }

    /// <summary>
    /// Kills a running child and refuses further work.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        KillAll();
        _shutdown.Dispose();
        _syncLane.Dispose();
        _tileLane.Dispose();
    }

    /// <summary>
    /// Kills every running child (plugin shutdown).
    /// </summary>
    public void KillAll()
    {
        Process[] running;
        lock (_liveLock)
        {
            running = [.. _live];
        }

        foreach (var process in running)
        {
            KillTree(process);
        }
    }

    /// <summary>Builds the image of the self-test.</summary>
    /// <returns>A 64 by 64 PNG.</returns>
    internal static byte[] BuildSelfTestPng()
    {
        // 64 by 64, 8-bit grey, a gradient: built here so no binary blob ships.
        const int Size = 64;
        var raw = new byte[Size * (Size + 1)];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                raw[(y * (Size + 1)) + 1 + x] = (byte)((x * 4) ^ (y * 2));
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, [0, 0, 0, Size, 0, 0, 0, Size, 8, 0, 0, 0, 0]);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        var crc = Crc32(type, data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        stream.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var part in new[] { first.ToArray(), second.ToArray() })
        {
            foreach (var b in part)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
        }

        return ~crc;
    }

    private static string? FindPrlimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (var candidate in new[] { "/usr/bin/prlimit", "/bin/prlimit", "/usr/local/bin/prlimit" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or already disposed.
        }
    }

    private static ImageSanitizeResult Refuse() => new(false, ReadOnlyMemory<byte>.Empty);

    // True: usable. False: known unavailable (new images are refused). Null: not decided yet (one failure so far).
    private async Task<bool?> EnsureAvailableLockedAsync(CancellationToken cancellationToken)
    {
        if (_availability == 1)
        {
            return true;
        }

        if (_availability == 2 && _time.GetUtcNow().UtcTicks - Interlocked.Read(ref _unavailableAtTicks) < RetestAfter.Ticks)
        {
            return false;
        }

        if (await SelfTestLockedAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return _availability == 2 ? false : null;
    }

    // One failed start or self-test (a busy host can miss the 8 second clock) leaves the sandbox undecided; the second in a row makes it unavailable.
    private bool NoteFailure(string reason)
    {
        if (Interlocked.Increment(ref _failures) >= 2)
        {
            MarkUnavailable(reason);
            return true;
        }

        if (_availability == 1)
        {
            _availability = 0;
        }

        _logger.LogDebug("The image sandbox failed once ({Reason}); it is tested again before the next image", reason);
        return false;
    }

    private async Task<bool> SelfTestLockedAsync(CancellationToken cancellationToken)
    {
        var png = BuildSelfTestPng();
        var spec = new ImageSpec(ImageKind.Poster, 32, null);
        var run = await RunAsync(png, BuildArguments(ImageFormat.Png, 32), spec, cancellationToken).ConfigureAwait(false);
        if (run.Kind == ChildRunKind.Ok)
        {
            _availability = 1;
            _reason = null;
            _failures = 0;
            _logger.LogInformation("The image sandbox self-test passed");
            return true;
        }

        NoteFailure(run.Kind == ChildRunKind.CouldNotStart ? "ffmpegMissing" : "selfTestFailed");
        return false;
    }

    private void MarkUnavailable(string reason)
    {
        _reason = reason;
        Interlocked.Exchange(ref _unavailableAtTicks, _time.GetUtcNow().UtcTicks);
        _availability = 2;
        _logger.LogWarning("The image sandbox is unavailable ({Reason}); new images are refused until it recovers", reason);
    }

    private async Task<ChildRunResult> RunAsync(ReadOnlyMemory<byte> input, IReadOnlyList<string> ffmpegArguments, ImageSpec spec, CancellationToken cancellationToken)
    {
        var encoder = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoder) || (Path.IsPathRooted(encoder) && !File.Exists(encoder)))
        {
            return new ChildRunResult(ChildRunKind.CouldNotStart, []);
        }

        var (file, arguments) = Launch(encoder, ffmpegArguments);
        string? workDir = null;
        Process? process = null;
        try
        {
            workDir = Path.Combine(_workRoot, "img-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
            SecureDirectory.Ensure(workDir);

            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment.Clear();
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot)
            {
                start.Environment["SystemRoot"] = systemRoot;
            }

            process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new ChildRunResult(ChildRunKind.CouldNotStart, []);
            }

            lock (_liveLock)
            {
                _live.Add(process);
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The limits that matter are the clock and the output cap.
            }

            return await SupervisedAsync(process, input, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The image sandbox could not prepare its working directory ({Error})", ex.GetType().Name);
            return new ChildRunResult(ChildRunKind.Failed, []);
        }
        finally
        {
            if (process is not null)
            {
                lock (_liveLock)
                {
                    _live.Remove(process);
                }

                KillTree(process);
                process.Dispose();
            }

            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leftover empty folder under the Shoal data folder is harmless.
                }
            }
        }
    }

    private async Task<ChildRunResult> SupervisedAsync(Process process, ReadOnlyMemory<byte> input, ImageSpec spec, CancellationToken cancellationToken)
    {
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        clock.CancelAfter(_wallClock);
        var token = clock.Token;
        var writing = WriteInputAsync(process.StandardInput.BaseStream, input, token);
        var reading = ReadOutputAsync(process.StandardOutput.BaseStream, token);
        var draining = DrainAsync(process.StandardError.BaseStream, token);
        try
        {
            var (output, overflow) = await reading.ConfigureAwait(false);
            if (overflow)
            {
                KillTree(process);
                _logger.LogDebug("The image sandbox cut a child that wrote too much");
                return new ChildRunResult(ChildRunKind.Failed, []);
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(writing, draining).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("The image sandbox child exited with code {Code}", process.ExitCode);
                return new ChildRunResult(ChildRunKind.Failed, []);
            }

            return Check(output, spec) ? new ChildRunResult(ChildRunKind.Ok, output) : new ChildRunResult(ChildRunKind.Failed, []);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("The image sandbox killed a child after the wall clock");
            return new ChildRunResult(ChildRunKind.Failed, []);
        }
        finally
        {
            KillTree(process);
            try
            {
                await Task.WhenAll(writing, reading, draining).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The child is dead; a pipe reader that does not return is abandoned.
            }
        }
    }

    // A trickplay tile is a mosaic: up to 8192 on a side and 16 megapixels (DESIGN v2.6.5). Posters and backdrops keep the 5.4 limits.
    private static ImageRefusal InspectFor(ImageKind kind, ReadOnlySpan<byte> data, out ImageInfo info) =>
        kind == ImageKind.Tile
            ? ImageProbe.Inspect(data, out info, 8192, 8192, ImageProbe.MaxPixels)
            : ImageProbe.Inspect(data, out info);

    private static bool Check(byte[] output, ImageSpec spec)
    {
        if (output.Length == 0 || InspectFor(spec.Kind, output, out var info) != ImageRefusal.None || info.Format != ImageFormat.Jpeg)
        {
            return false;
        }

        return (spec.MaxWidth is not { } maxWidth || info.Width <= maxWidth) && (spec.MaxHeight is not { } maxHeight || info.Height <= maxHeight);
    }

    private (string File, IReadOnlyList<string> Arguments) Launch(string encoder, IReadOnlyList<string> ffmpegArguments)
    {
        if (_command is not null)
        {
            return _command(encoder, ffmpegArguments);
        }

        return _prlimit is null ? (encoder, ffmpegArguments) : WrapWithPrlimit(_prlimit, encoder, ffmpegArguments);
    }

    private static async Task WriteInputAsync(Stream stdin, ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        try
        {
            await stdin.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child closed its input early; its exit code and output decide.
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<(byte[] Output, bool Overflow)> ReadOutputAsync(Stream stdout, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stdout.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            if (buffer.Length + read > MaxOutputBytes)
            {
                return ([], true);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Stream stderr, CancellationToken cancellationToken)
    {
        // Only the first 4 KB are read into memory, and they are discarded: stderr is never logged.
        var chunk = new byte[MaxStderrBytes];
        try
        {
            while (await stderr.ReadAsync(chunk, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
