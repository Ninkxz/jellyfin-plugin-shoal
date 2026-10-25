using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Registrations;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A fixed ffmpeg location.
/// </summary>
internal sealed class FixedEncoder : IEncoderLocator
{
    public FixedEncoder(string? path) => EncoderPath = path;

    public string? EncoderPath { get; set; }
}

/// <summary>
/// Runs the image sandbox against a stand-in child (<c>Jellyfin.Plugin.Shoal.ChildStub</c>) so the process handling is tested without ffmpeg.
/// </summary>
internal sealed class SandboxRig : IDisposable
{
    public static readonly string StubDll = Path.Combine(AppContext.BaseDirectory, "Jellyfin.Plugin.Shoal.ChildStub.dll");

    public SandboxRig()
    {
        Temp = new TempDirectory();
        WorkRoot = Temp.Combine("work");
        Encoder = new FixedEncoder(StubDll);
    }

    public TempDirectory Temp { get; }

    public string WorkRoot { get; }

    public FixedEncoder Encoder { get; }

    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the stand-in's arguments for the next child.</summary>
    public string[] Mode { get; set; } = ["jpeg", "32", "32"];

    public CapturingLogger Log { get; } = new();

    public ChildImageSanitizer Make(TimeSpan? wallClock = null) =>
        new(Encoder, () => Enabled, WorkRoot, TimeProvider.System, new LoggerOf<ChildImageSanitizer>(Log), wallClock, (_, _) => (DotnetHost(), [StubDll, .. Mode]));

    public static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromSdk && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        if (Environment.ProcessPath is { } own && Path.GetFileNameWithoutExtension(own).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return own;
        }

        var root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task<string> ReadWhenWrittenAsync(string path)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The stand-in child never wrote " + path);
    }

    public static ImageSpec Poster(int? width = 600) => new(ImageKind.Poster, width);

    public void Dispose() => Temp.Dispose();
}

/// <summary>
/// A logger that writes to a <see cref="CapturingLogger"/>.
/// </summary>
internal sealed class LoggerOf<T> : ILogger<T>
{
    private readonly ILogger _inner;

    public LoggerOf(ILogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>
/// A fact that runs only where a real ffmpeg is on the PATH or in <c>SHOAL_FFMPEG</c>.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FindFfmpeg() is null)
        {
            Skip = "No ffmpeg on this machine (set SHOAL_FFMPEG to run it).";
        }
    }

    internal static string? FindFfmpeg()
    {
        if (Environment.GetEnvironmentVariable("SHOAL_FFMPEG") is { Length: > 0 } explicitPath && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// K32: T-IMG-1 to T-IMG-7 (DESIGN v2.6.7) against a stand-in child, plus the real ffmpeg where there is one.
/// </summary>
public sealed class ChildImageSanitizerTests
{
    private static readonly byte[] Poster = ImageBuilder.Jpeg(600, 900, 2000);

    [Fact]
    public void T_IMG_Arguments_AreAListWithNoPeerString_AndMatchTheDesign()
    {
        var jpeg = ChildImageSanitizer.BuildArguments(ImageFormat.Jpeg, 600);
        var png = ChildImageSanitizer.BuildArguments(ImageFormat.Png, null);

        Assert.Equal(["-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-protocol_whitelist", "pipe", "-f", "image2pipe", "-c:v", "mjpeg", "-i", "pipe:0",
            "-frames:v", "1", "-an", "-sn", "-dn", "-map_metadata", "-1", "-vf", "scale='min(600,iw)':-2:flags=lanczos,format=yuvj420p", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2pipe", "pipe:1"], jpeg);
        Assert.Equal("png", png[png.ToList().IndexOf("-c:v") + 1]);
        Assert.Equal("format=yuvj420p", png[png.ToList().IndexOf("-vf") + 1]);
        Assert.DoesNotContain(png, a => a.Contains(' ', StringComparison.Ordinal));
    }

    [Fact]
    public void T_IMG_Prlimit_GoesInFront_WithTheDocumentedLimits()
    {
        var (file, args) = ChildImageSanitizer.WrapWithPrlimit("/usr/bin/prlimit", "/usr/lib/jellyfin-ffmpeg/ffmpeg", ["-i", "pipe:0"]);

        Assert.Equal("/usr/bin/prlimit", file);
        Assert.Equal(["--as=805306368", "--cpu=6", "--fsize=0", "--core=0", "--nofile=32", "--", "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-i", "pipe:0"], args);
        Assert.DoesNotContain(args, a => a.StartsWith("--nproc", StringComparison.Ordinal));
    }

    [Fact]
    public void T_IMG_SelfTestPng_IsAValidSmallPng()
    {
        var png = ChildImageSanitizer.BuildSelfTestPng();

        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(png, out var info));
        Assert.Equal(new ImageInfo(ImageFormat.Png, 64, 64), info);
    }

    // --- T-IMG-1

    [Fact]
    public async Task T_IMG_1_ASelfTestThatPasses_MakesTheSandboxActive_AndTheImageIsReplaced()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();

        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.Equal("active", sanitizer.GetStatus().State);
        Assert.True(result.Ok);
        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(result.Bytes.Span, out var info));
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 32, 32), info);
        Assert.Equal(0, sanitizer.Rejected);
    }

    [Fact]
    public async Task T_IMG_1_TwoFailedSelfTestsInARow_MakeTheSandboxUnavailable_AndImagesStayRefused()
    {
        using var rig = new SandboxRig { Mode = ["exit1"] };
        using var sanitizer = rig.Make();

        var first = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        Assert.Equal("active", sanitizer.GetStatus().State); // one failure decides nothing
        var second = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(first.Ok); // waits for the next sync; never stored unsanitized
        Assert.False(second.Ok);
        Assert.True(second.Bytes.IsEmpty);
        var status = sanitizer.GetStatus();
        Assert.Equal(("unavailable", "selfTestFailed"), (status.State, status.Reason));
        Assert.Equal(2, status.Rejected);
    }

    [Fact]
    public async Task Warden10_L2_OneTransientFailure_DoesNotDisableTheSandbox()
    {
        using var rig = new SandboxRig { Mode = ["exit1"] };
        using var sanitizer = rig.Make();

        var transient = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        rig.Mode = ["jpeg", "32", "32"];
        var recovered = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        rig.Mode = ["exit1"];
        var again = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        var failedRun = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(transient.Ok);
        Assert.True(recovered.Ok);
        Assert.False(recovered.Bytes.Span.SequenceEqual(Poster)); // a fresh JPEG
        Assert.False(again.Ok);
        Assert.False(failedRun.Ok); // a bad run on a working sandbox is a refused image, not an outage
        Assert.Equal("active", sanitizer.GetStatus().State);
    }

    [Fact]
    public async Task Warden10_L2_AStartThatFailsOnce_AfterTheSandboxWorked_IsNotAnOutage()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var encoder = rig.Encoder.EncoderPath;
        rig.Encoder.EncoderPath = null;

        var once = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        rig.Encoder.EncoderPath = encoder;
        var back = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(once.Ok);
        Assert.True(back.Ok);
        Assert.False(back.Bytes.Span.SequenceEqual(Poster));
        Assert.Equal("active", sanitizer.GetStatus().State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public async Task T_IMG_1_FfmpegMissing_IsUnavailable_AndNeverReturnsRawPeerBytes(string? encoderPath)
    {
        using var rig = new SandboxRig();
        rig.Encoder.EncoderPath = encoderPath == "missing" ? Path.Combine(rig.Temp.Path, "no", "ffmpeg") : encoderPath;
        using var sanitizer = rig.Make();

        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.Bytes.IsEmpty);
        Assert.Equal(("unavailable", "ffmpegMissing"), (sanitizer.GetStatus().State, sanitizer.GetStatus().Reason));
    }

    [Fact]
    public async Task T_IMG_1_WithTheSwitchOff_NoProcessIsStarted_AndTheStateIsOff()
    {
        using var rig = new SandboxRig { Enabled = false, Mode = ["pid", Path.Combine(Path.GetTempPath(), "shoal-never-" + Guid.NewGuid().ToString("N"))] };
        using var sanitizer = rig.Make();

        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.Bytes.Span.SequenceEqual(Poster));
        Assert.Equal("off", sanitizer.GetStatus().State);
        Assert.False(Directory.Exists(rig.WorkRoot));
    }

    [Fact]
    public async Task T_IMG_1_AnUnavailableSandbox_IsTestedAgainAfterSixtySeconds()
    {
        using var rig = new SandboxRig { Mode = ["exit1"] };
        var clock = new ManualTimeProvider();
        using var sanitizer = new ChildImageSanitizer(rig.Encoder, () => true, rig.WorkRoot, clock, new LoggerOf<ChildImageSanitizer>(rig.Log), null, (_, _) => (SandboxRig.DotnetHost(), [SandboxRig.StubDll, .. rig.Mode]));

        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        Assert.Equal("unavailable", sanitizer.GetStatus().State);

        rig.Mode = ["jpeg", "32", "32"];
        clock.Advance(TimeSpan.FromSeconds(30));
        var early = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        Assert.False(early.Ok); // still unavailable, no new test yet and no raw bytes accepted
        Assert.True(early.Bytes.IsEmpty);

        clock.Advance(TimeSpan.FromSeconds(31));
        var later = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        Assert.False(later.Bytes.Span.SequenceEqual(Poster));
        Assert.Equal("active", sanitizer.GetStatus().State);
    }

    // --- T-IMG-2: hostile input and output

    [Theory]
    [InlineData("text")]
    [InlineData("truncated")]
    [InlineData("exit1")]
    public async Task T_IMG_2_ABadChild_RefusesTheImage_AndCountsIt(string badMode)
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        rig.Mode = [badMode];

        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, sanitizer.Rejected);
        Assert.Equal(1, sanitizer.GetStatus().Rejected);
        Assert.Equal("active", sanitizer.GetStatus().State);
    }

    [Fact]
    public async Task T_IMG_2_AnOutputWiderThanTheSpec_IsRefused()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        rig.Mode = ["jpeg", "700", "100"];

        var tooWide = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(600), CancellationToken.None);
        var fits = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(700), CancellationToken.None);

        Assert.False(tooWide.Ok);
        Assert.True(fits.Ok);
    }

    [Fact]
    public async Task T_IMG_2_AnInputThatFailsTheHeaderWalk_IsRefusedWithoutAChild()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();

        var result = await sanitizer.SanitizeAsync(ImageBuilder.WebP(), SandboxRig.Poster(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(rig.WorkRoot));
    }

    [Fact]
    public async Task T_IMG_2_ManyLargeInputs_DoNotRaiseThePluginsMemory()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var big = ImageBuilder.Jpeg(1000, 1000, 3_500_000);
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 12; i++)
        {
            Assert.True((await sanitizer.SanitizeAsync(big, SandboxRig.Poster(), CancellationToken.None)).Ok);
        }

        GC.Collect();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(after - before < 24L * 1024 * 1024, $"managed memory grew by {after - before} bytes");
    }

    [Fact]
    public async Task T_IMG_2_TheChild_GetsAnEmptyEnvironment_AndAFreshEmptyDirectory_RemovedAfterwards()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var report = rig.Temp.Combine("probe.txt");
        rig.Mode = ["probe", report];

        Assert.True((await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None)).Ok);

        var parts = (await SandboxRig.ReadWhenWrittenAsync(report)).Split('|');
        var names = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => n.ToUpperInvariant()).ToList();
        Assert.DoesNotContain("PATH", names);
        Assert.DoesNotContain("HOME", names);
        Assert.DoesNotContain("USERPROFILE", names);
        Assert.DoesNotContain(names, n => n.Contains("TOKEN", StringComparison.Ordinal) || n.Contains("KEY", StringComparison.Ordinal));
        Assert.True(names.Count <= 2, string.Join(',', names));
        Assert.Equal("0", parts[1]);
        Assert.StartsWith(Path.GetFullPath(rig.WorkRoot), Path.GetFullPath(parts[2]), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(rig.WorkRoot));
    }

    // --- T-IMG-3: the limits fire

    [Fact]
    public async Task T_IMG_3_AStalledChild_IsKilledAtTheWallClock()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make(TimeSpan.FromMilliseconds(1500));
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var pidFile = rig.Temp.Combine("pid.txt");
        rig.Mode = ["pid", pidFile];

        var watch = Stopwatch.StartNew();
        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        watch.Stop();

        Assert.False(result.Ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
        var pid = int.Parse(await SandboxRig.ReadWhenWrittenAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(300);
        Assert.False(SandboxRig.IsAlive(pid));
        Assert.Equal(1, sanitizer.Rejected);
    }

    [Fact]
    public async Task T_IMG_3_AnOutputOverFourMegabytes_IsCut_AndRefused()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        rig.Mode = ["flood"];

        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, sanitizer.Rejected);
    }

    [Fact]
    public async Task T_IMG_3_TheWholeTreeIsKilled_NotJustTheChild()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make(TimeSpan.FromSeconds(3));
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var pidFile = rig.Temp.Combine("grandchild.txt");
        rig.Mode = ["spawn", pidFile];

        var result = await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.False(result.Ok);
        var grandchild = int.Parse(await SandboxRig.ReadWhenWrittenAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(500);
        Assert.False(SandboxRig.IsAlive(grandchild));
    }

    [Fact]
    public async Task T_IMG_3_ACancelledCaller_StopsTheChild_AndThrows()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var pidFile = rig.Temp.Combine("cancel.txt");
        rig.Mode = ["pid", pidFile];
        using var cts = new CancellationTokenSource();

        var running = sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), cts.Token);
        var pid = int.Parse(await SandboxRig.ReadWhenWrittenAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Task.Delay(300);
        Assert.False(SandboxRig.IsAlive(pid));
    }

    // --- T-IMG-6: one child at a time

    [Fact]
    public async Task T_IMG_6_Children_RunOneAtATime()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var log = rig.Temp.Combine("serial.log");
        rig.Mode = ["log", log, "400"];

        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None)));

        Assert.All(all, r => Assert.True(r.Ok));
        var lines = File.ReadAllLines(log).Select(l => l.Split(' ')[0]).ToList();
        Assert.Equal(8, lines.Count);
        for (var i = 0; i < lines.Count; i += 2)
        {
            Assert.Equal(("start", "end"), (lines[i], lines[i + 1])); // never start, start, end, end
        }
    }

    // --- Warden 10 L3: a tile never queues behind a sync image

    private static readonly ImageSpec TileSpec = new(ImageKind.Tile);

    [Fact]
    public async Task Warden10_L3_ATile_IsNotHeldUp_ByASyncImageThatIsRunning()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var log = rig.Temp.Combine("lanes.log");
        rig.Mode = ["log", log, "3000"];
        var sync = sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        await SandboxRig.ReadWhenWrittenAsync(log); // the sync child has started
        rig.Mode = ["jpeg", "32", "32"];

        var watch = Stopwatch.StartNew();
        var tile = await sanitizer.SanitizeAsync(Poster, TileSpec, CancellationToken.None);
        watch.Stop();

        Assert.True(tile.Ok);
        Assert.False(tile.Bytes.Span.SequenceEqual(Poster)); // re-encoded
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(2500), $"the tile waited {watch.Elapsed}");
        Assert.DoesNotContain("end", File.ReadAllText(log), StringComparison.Ordinal); // the sync image was still running
        Assert.True((await sync).Ok);
    }

    [Fact]
    public async Task Warden10_L3_Tiles_AreSerialAmongThemselves()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var log = rig.Temp.Combine("tiles.log");
        rig.Mode = ["log", log, "300"];

        var all = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sanitizer.SanitizeAsync(Poster, TileSpec, CancellationToken.None)));

        Assert.All(all, r => Assert.True(r.Ok));
        var lines = File.ReadAllLines(log).Select(l => l.Split(' ')[0]).ToList();
        for (var i = 0; i < lines.Count; i += 2)
        {
            Assert.Equal(("start", "end"), (lines[i], lines[i + 1]));
        }
    }

    [Fact]
    public async Task Warden10_L3_ATile_NeverRunsTheSelfTestOnTheRequestPath()
    {
        using var rig = new SandboxRig { Mode = ["pid", Path.Combine(Path.GetTempPath(), "shoal-lane-" + Guid.NewGuid().ToString("N"))] };
        using var sanitizer = rig.Make(TimeSpan.FromSeconds(2));

        var watch = Stopwatch.StartNew();
        var undecided = await sanitizer.SanitizeAsync(Poster, TileSpec, CancellationToken.None);
        watch.Stop();

        Assert.False(undecided.Ok); // not tested yet: refused, never passed through unsanitized
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"the tile waited {watch.Elapsed}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public async Task Warden10_L3_AKnownUnavailableSandbox_RefusesATile_WithoutAnInlineRetest(int elapsedSeconds)
    {
        using var rig = new SandboxRig { Mode = ["exit1"] };
        var clock = new ManualTimeProvider();
        using var sanitizer = new ChildImageSanitizer(rig.Encoder, () => true, rig.WorkRoot, clock, new LoggerOf<ChildImageSanitizer>(rig.Log), TimeSpan.FromSeconds(2), (_, _) => (SandboxRig.DotnetHost(), [SandboxRig.StubDll, .. rig.Mode]));
        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        Assert.Equal("unavailable", sanitizer.GetStatus().State);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds)); // test both the cooldown and a due background retest
        rig.Mode = ["pid", Path.Combine(Path.GetTempPath(), "shoal-lane-" + Guid.NewGuid().ToString("N"))]; // a retest would hang until the clock

        var watch = Stopwatch.StartNew();
        var tile = await sanitizer.SanitizeAsync(Poster, TileSpec, CancellationToken.None);
        watch.Stop();

        Assert.False(tile.Ok);
        Assert.True(tile.Bytes.IsEmpty);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"the tile waited {watch.Elapsed}");
    }

    // --- T-IMG-7: nothing left after shutdown

    [Fact]
    public async Task T_IMG_7_NoChildIsLeft_AfterTheSanitizerIsDisposed()
    {
        using var rig = new SandboxRig();
        var sanitizer = rig.Make(TimeSpan.FromMinutes(1));
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var pidFile = rig.Temp.Combine("shutdown.txt");
        rig.Mode = ["pid", pidFile];

        var running = sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        var pid = int.Parse(await SandboxRig.ReadWhenWrittenAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(SandboxRig.IsAlive(pid));
        sanitizer.Dispose();

        await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        Assert.False(SandboxRig.IsAlive(pid));
        Assert.Empty(Directory.GetDirectories(rig.WorkRoot));
    }

    // --- the log never holds image bytes, stderr or paths of the peer

    [Fact]
    public async Task TheLog_NeverHoldsImageBytesOrStderr()
    {
        using var rig = new SandboxRig();
        using var sanitizer = rig.Make();
        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);
        rig.Mode = ["text"];
        await sanitizer.SanitizeAsync(Poster, SandboxRig.Poster(), CancellationToken.None);

        Assert.DoesNotContain("alert(1)", rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", rig.Log.All, StringComparison.Ordinal);
    }

    // --- registration (K24's seam is replaced by this feature)

    [Fact]
    public void TheRegistration_ReplacesThePassThrough_WithTheChildSanitizer_AndReportsStatus()
    {
        using var temp = new TempDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Jellyfin.Plugin.Shoal.Storage.ShoalPaths(temp.Path));
        services.AddSingleton<IEncoderLocator>(new FixedEncoder(null));
        services.AddShoalFoundation();
        services.AddShoalImageSandbox();
        using var provider = services.BuildServiceProvider();

        var sanitizer = provider.GetRequiredService<IImageSanitizer>();

        Assert.IsType<ChildImageSanitizer>(sanitizer);
        Assert.Same(sanitizer, provider.GetRequiredService<IImageSandboxStatus>());
        Assert.Equal("active", provider.GetRequiredService<IImageSandboxStatus>().GetStatus().State); // default config: the switch is on
    }

    // --- real ffmpeg, where there is one (T-IMG-2, T-IMG-4, T-IMG-5)

    [FfmpegFact]
    public async Task T_IMG_Real_APng_BecomesABaselineJpeg_WithinTheSpec()
    {
        using var rig = new SandboxRig();
        rig.Encoder.EncoderPath = FfmpegFactAttribute.FindFfmpeg();
        using var sanitizer = new ChildImageSanitizer(rig.Encoder, () => true, rig.WorkRoot, TimeProvider.System, new LoggerOf<ChildImageSanitizer>(rig.Log));

        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));
        var result = await sanitizer.SanitizeAsync(ChildImageSanitizer.BuildSelfTestPng(), new ImageSpec(ImageKind.Poster, 32), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(ImageRefusal.None, ImageProbe.Inspect(result.Bytes.Span, out var info));
        Assert.Equal(ImageFormat.Jpeg, info.Format);
        Assert.True(info.Width <= 32);
        Assert.False(result.Bytes.Span.IndexOf("Exif"u8) >= 0);
    }

    [FfmpegFact]
    public async Task T_IMG_Real_AHostileInput_IsRefused_AndLeavesNoChild()
    {
        using var rig = new SandboxRig();
        rig.Encoder.EncoderPath = FfmpegFactAttribute.FindFfmpeg();
        using var sanitizer = new ChildImageSanitizer(rig.Encoder, () => true, rig.WorkRoot, TimeProvider.System, new LoggerOf<ChildImageSanitizer>(rig.Log));
        Assert.True(await sanitizer.SelfTestAsync(CancellationToken.None));

        var result = await sanitizer.SanitizeAsync(ImageBuilder.Jpeg(600, 900, 5000), new ImageSpec(ImageKind.Poster, 600), CancellationToken.None); // a header with no image data

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetDirectories(rig.WorkRoot));
    }
}
