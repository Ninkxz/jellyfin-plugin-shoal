using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class LocalRemovalFileProbeTests
{
    [Fact]
    public async Task ActualRegularFileGetsCompleteDigestAndStableNativeAncestry()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        var bytes = Enumerable.Range(0, 1048577).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var first = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        var second = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        Assert.Equal(path, first.FullPath);
        Assert.Equal(dir.Path, first.Root);
        Assert.Equal(bytes.Length, first.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), first.Sha256);
        Assert.Equal(first, second);
        Assert.Contains(OperatingSystem.IsWindows() ? "w:" : "l:", first.Identity);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("empty")]
    public async Task MissingDirectoryAndEmptyInputsAreRefused(string kind)
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        if (kind == "directory") Directory.CreateDirectory(path);
        if (kind == "empty") await File.WriteAllBytesAsync(path, []);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, path, default));
    }

    [Theory]
    [InlineData("relativeRoot")]
    [InlineData("relativeFile")]
    [InlineData("outside")]
    [InlineData("prefixSibling")]
    [InlineData("rootItself")]
    public async Task PathMustBeAnAbsoluteDescendantOfItsAllowedRoot(string kind)
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("allowed");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var wrong = kind switch
        {
            "relativeRoot" => ("allowed", path),
            "relativeFile" => (root, "movie.mp4"),
            "outside" => (root, dir.Combine("outside.mp4")),
            "prefixSibling" => (root, dir.Combine("allowed-copy", "movie.mp4")),
            _ => (root, root),
        };
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(wrong.Item1, wrong.Item2, default));
    }

    [Fact]
    public async Task ADirectoryLinkAnywhereInTheAncestryIsRefused()
    {
        using var dir = new TempDirectory();
        var target = dir.Combine("target");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "movie.mp4"), [1, 2, 3]);
        var link = dir.Combine("link");
        LinkDirectory(link, target);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, Path.Combine(link, "movie.mp4"), default));
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(link, Path.Combine(link, "movie.mp4"), default));
    }

    [Fact]
    public async Task HardLinkedFilesAreRefusedEvenWithAnOrdinaryName()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var alias = dir.Combine("copy.mp4");
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(alias, path, IntPtr.Zero) : Link(path, alias) == 0);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, path, default));
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, alias, default));
    }

    [Fact]
    public async Task AReplacementAtTheSameNameChangesIdentityEvenWhenBytesAndMtimeMatch()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        var date = File.GetLastWriteTimeUtc(path);
        File.Move(path, dir.Combine("original.mp4"));
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, date);
        var after = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.NotEqual(before.Identity, after.Identity);
    }

    [Fact]
    public async Task AReplacementParentChangesPinnedAncestry()
    {
        using var dir = new TempDirectory();
        var root = dir.Combine("library");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await new RemovalFiles().ProbeAsync(root, path, default);
        Directory.Move(root, dir.Combine("retained"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var after = await new RemovalFiles().ProbeAsync(root, path, default);
        Assert.NotEqual(before.Identity, after.Identity);
    }

    [Fact]
    public async Task ModificationAndFreshOpenAreObservedOnEveryCall()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        await File.WriteAllBytesAsync(path, [3, 2, 1]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        var after = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.NotEqual(before.Identity, after.Identity);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotOpenOrChangeAFile()
    {
        using var dir = new TempDirectory();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemovalFiles().ProbeAsync(dir.Path, dir.Combine("absent.mp4"), source.Token));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task CancellationDuringHashingReleasesEveryNativeHandle()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, new byte[32 * 1024 * 1024]);
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemovalFiles().ProbeAsync(dir.Path, path, source.Token));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(32 * 1024 * 1024, exclusive.Length);
    }

    [WindowsFact]
    public async Task WindowsRejectsAnExistingWriterAndNetworkPaths()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, path, default));
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync("\\\\server\\share", "\\\\server\\share\\movie.mp4", default));
    }

    [LinuxFact]
    public async Task LinuxRefusesSymbolicFilesFifoAndOversizedSparseFiles()
    {
        using var dir = new TempDirectory();
        var target = dir.Combine("target.mp4");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        var link = dir.Combine("link.mp4");
        File.CreateSymbolicLink(link, target);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, link, default));
        var fifo = dir.Combine("fifo.mp4");
        Assert.Equal(0, MkFifo(fifo, 0x180));
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, fifo, default));
        var large = dir.Combine("large.mp4");
        using (var sparse = new FileStream(large, FileMode.CreateNew)) sparse.SetLength(RemovalPolicy.MaxFileSizeBytes + 1);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().ProbeAsync(dir.Path, large, default));
    }

    private static void LinkDirectory(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string target, string link);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
}
