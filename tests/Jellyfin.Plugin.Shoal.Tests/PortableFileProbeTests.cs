using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K1: the portable probe used for SMB/CIFS and every other filesystem. Cross-platform (runs on the Windows dev machine).</summary>
public sealed class PortableFileProbeTests
{
    private static byte[] Movie(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    [Fact]
    public async Task HashEqualsTheSha256OfEveryByteAcrossSeveralBuffers()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("Sintel (2010)", "Sintel.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Movie((5 * 1024 * 1024 / 2) + 7); // crosses the 1 MiB read buffer, not a multiple of it
        await File.WriteAllBytesAsync(path, bytes);

        var snapshot = await new PortableFileProbe().ProbeAsync(dir.Path, path, default);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), snapshot.Sha256);
        Assert.Equal(bytes.Length, snapshot.Length);
        Assert.Equal(Path.GetFullPath(path), snapshot.FullPath);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Path)), snapshot.Root);
        Assert.Equal(PortableFileProbe.Identity(bytes.Length, File.GetLastWriteTimeUtc(path).Ticks), snapshot.Identity);
        Assert.StartsWith("p:" + bytes.Length + ":", snapshot.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrailingSeparatorOnTheRootIsTheSameRoot()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(4096));
        var snapshot = await new PortableFileProbe().ProbeAsync(dir.Path + Path.DirectorySeparatorChar, path, default);
        Assert.Equal(4096, snapshot.Length);
    }

    [Fact]
    public async Task AFileWhoseWriteTimeChangesWhileReadingIsRefused()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(64 * 1024));
        var probe = new PortableFileProbe(RemovalPolicy.HashingDeadlineFor, _ =>
        {
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
            return Task.CompletedTask;
        });
        var refused = await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, path, default));
        Assert.Equal("mediaFileUnavailable", refused.Message);
    }

    [Fact]
    public async Task AFileThatGrowsWhileReadingIsRefused()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(64 * 1024));
        var stamp = File.GetLastWriteTimeUtc(path);
        var probe = new PortableFileProbe(RemovalPolicy.HashingDeadlineFor, async token =>
        {
            await File.AppendAllTextAsync(path, "x", token);
            File.SetLastWriteTimeUtc(path, stamp); // same time, other length: the length alone must refuse
        });
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, path, default));
    }

    [Fact]
    public async Task AFileReplacedWhileReadingIsRefused()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(64 * 1024));
        var probe = new PortableFileProbe(RemovalPolicy.HashingDeadlineFor, _ =>
        {
            File.Delete(path);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, path, default));
    }

    [Fact]
    public async Task PathsOutsideOrEqualToTheRootAreRefused()
    {
        using var dir = new TempDirectory();
        var root = Directory.CreateDirectory(dir.Combine("Movies")).FullName;
        var sibling = Directory.CreateDirectory(dir.Combine("Movies2")).FullName;
        var outside = Path.Combine(sibling, "movie.mp4");
        await File.WriteAllBytesAsync(outside, Movie(1024));
        var inside = Path.Combine(root, "movie.mp4");
        await File.WriteAllBytesAsync(inside, Movie(1024));
        var probe = new PortableFileProbe();

        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(root, outside, default)); // prefix trick: Movies2 is not inside Movies
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(root, Path.Combine(root, "..", "Movies2", "movie.mp4"), default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(root, root, default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync("Movies", inside, default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(root, "movie.mp4", default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(string.Empty, inside, default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(root, inside + "\0", default));
        Assert.Equal(1024, (await probe.ProbeAsync(root, inside, default)).Length);
    }

    [Fact]
    public async Task DevicePathsAndAlternateStreamsAreRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(1024));
        var probe = new PortableFileProbe();
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(@"\\?\" + dir.Path, @"\\?\" + path, default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, path + ":hidden", default));
    }

    [Fact]
    public async Task EmptyMissingAndDirectoryTargetsAreRefused()
    {
        using var dir = new TempDirectory();
        var empty = dir.Combine("empty.mp4");
        await File.WriteAllBytesAsync(empty, []);
        var folder = Directory.CreateDirectory(dir.Combine("folder.mp4")).FullName;
        var probe = new PortableFileProbe();
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, empty, default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, dir.Combine("missing.mp4"), default));
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(dir.Path, folder, default));
    }

    [Fact]
    public async Task ALinkedFolderBelowTheRootIsRefused()
    {
        using var dir = new TempDirectory();
        var root = Directory.CreateDirectory(dir.Combine("Movies")).FullName;
        var elsewhere = Directory.CreateDirectory(dir.Combine("elsewhere")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(elsewhere, "movie.mp4"), Movie(1024));
        var link = Path.Combine(root, "Sintel (2010)");
        LinkDirectory(link, elsewhere);

        await Assert.ThrowsAsync<IOException>(() => new PortableFileProbe().ProbeAsync(root, Path.Combine(link, "movie.mp4"), default));
    }

    [Fact]
    public async Task ALinkedFileIsRefused()
    {
        using var dir = new TempDirectory();
        var target = dir.Combine("target.mp4");
        await File.WriteAllBytesAsync(target, Movie(1024));
        var link = dir.Combine("link.mp4");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return; // file symbolic links need a privilege on this Windows machine; the Linux run covers it
        }

        await Assert.ThrowsAsync<IOException>(() => new PortableFileProbe().ProbeAsync(dir.Path, link, default));
    }

    [Fact]
    public async Task TheDeadlineIsScaledByTheFileLengthAndEndsTheHash()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(3 * 1024 * 1024));
        long seen = -1;
        var probe = new PortableFileProbe(length => { seen = length; return TimeSpan.Zero; }, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(dir.Path, path, default));
        Assert.Equal(3 * 1024 * 1024, seen);
    }

    [Fact]
    public async Task CallerCancellationIsHonoured()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, Movie(1024));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PortableFileProbe().ProbeAsync(dir.Path, path, cancelled.Token));
    }

    [Fact]
    public void TheDefaultDeadlineIsTwoMinutesPlusOnePerGibibyteUpToThirtyFour()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), RemovalPolicy.HashingDeadlineFor(1));
        Assert.Equal(TimeSpan.FromMinutes(22), RemovalPolicy.HashingDeadlineFor(20L << 30));
        Assert.Equal(TimeSpan.FromMinutes(34), RemovalPolicy.HashingDeadlineFor(RemovalPolicy.MaxFileSizeBytes));
    }

    // 2.10.4 (owner): delete like Jellyfin handles files up to exactly 100 GiB; the legacy recoverable removal keeps 32 GiB.
    [Fact]
    public void TheDeleteSizeLimitIs100GibAndTheLegacyLimitStays32Gib()
    {
        Assert.Equal(107_374_182_400L, RemovalPolicy.MaxDeleteFileBytes);
        Assert.True(RemovalPolicy.DeleteSizeAllowed(100L << 30));
        Assert.False(RemovalPolicy.DeleteSizeAllowed((100L << 30) + 1));
        Assert.True(RemovalPolicy.DeleteSizeAllowed(1));
        Assert.False(RemovalPolicy.DeleteSizeAllowed(0));
        Assert.Equal(32L << 30, RemovalPolicy.MaxFileSizeBytes);
        Assert.Equal(TimeSpan.FromMinutes(34), RemovalPolicy.HashingDeadlineFor(RemovalPolicy.MaxFileSizeBytes)); // legacy deadline unchanged
    }

    [Theory]
    [InlineData(0L, 300)] // base 5 min
    [InlineData(1L, 375)] // one started GiB: + 75 s
    [InlineData(1L << 30, 375)]
    [InlineData((1L << 30) + 1, 450)]
    [InlineData(32L << 30, 2700)] // 45 min (was 34)
    [InlineData(90L << 30, 7050)] // 117.5 min
    [InlineData(100L << 30, 7800)] // 130 min, the hard maximum
    [InlineData((100L << 30) + 1, 7800)]
    [InlineData(long.MaxValue, 7800)]
    public void TheDeleteHashDeadlineScalesWithSize(long bytes, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RemovalPolicy.DeleteHashingDeadlineFor(bytes));
        Assert.Equal(TimeSpan.FromMinutes(130), RemovalPolicy.DeleteHashingMaximum);
    }

    [Theory]
    [InlineData(17_000_000L)] // 17 MB/s, the slow share the owner named: 100 GiB needs about 105 minutes
    [InlineData(15_000_000L)]
    public void A100GibFileOnASlowShareFitsTheDeadline(long bytesPerSecond)
    {
        var needed = TimeSpan.FromSeconds((double)RemovalPolicy.MaxDeleteFileBytes / bytesPerSecond);
        Assert.True(RemovalPolicy.DeleteHashingDeadlineFor(RemovalPolicy.MaxDeleteFileBytes) > needed, $"needs {needed}");
        Assert.True(RemovalPolicy.DeleteHashingDeadlineFor(20L << 30) > TimeSpan.FromSeconds((double)(20L << 30) / bytesPerSecond));
    }

    [Fact]
    public void ThePortableProbeUsesTheDeleteDeadline()
    {
        var deadline = (Func<long, TimeSpan>)typeof(PortableFileProbe).GetField("_deadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(new PortableFileProbe())!;
        Assert.Equal(RemovalPolicy.DeleteHashingDeadlineFor(100L << 30), deadline(100L << 30));
    }

    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }
}
