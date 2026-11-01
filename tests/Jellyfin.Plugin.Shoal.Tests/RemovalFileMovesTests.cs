using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalFileMovesTests
{
    [WindowsFact]
    public async Task WindowsMutationRefusesBeforeMovingOrChangingAnyBytes()
    {
        using var dir = new TempDirectory();
        var file = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var snapshot = await new RemovalFiles().ProbeAsync(dir.Path, file, default);
        var target = dir.Combine("holding");
        Directory.CreateDirectory(target);
        var error = await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().MoveAsync(snapshot, target, "job.mp4", default));
        Assert.Equal("removalPlatformUnsupported", error.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [LinuxFact]
    public async Task NativeMoveAndRestorePreserveExactBytesAndObjectIdentity()
    {
        using var dir = new TempDirectory();
        var source = dir.Combine("library");
        var holding = dir.Combine("holding");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(holding);
        var file = Path.Combine(source, "movie.mp4");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 1000001).Select(i => (byte)i).ToArray());
        var files = new RemovalFiles();
        var before = await files.ProbeAsync(source, file, default);
        var held = await files.MoveAsync(before, holding, "job.mp4", default);
        Assert.False(File.Exists(file));
        Assert.Equal(before.Sha256, held.Sha256);
        var restored = await files.MoveAsync(held, source, "movie.mp4", default);
        Assert.Equal(before.Sha256, restored.Sha256);
        Assert.False(File.Exists(held.FullPath));
        Assert.Equal(new[] { file }, Directory.GetFiles(source));
    }

    [LinuxFact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        using var dir = new TempDirectory();
        var source = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var target = dir.Combine("other.mp4");
        await File.WriteAllBytesAsync(target, [4, 5, 6]);
        var before = await new RemovalFiles().ProbeAsync(dir.Path, source, default);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().MoveAsync(before, dir.Path, "other.mp4", default));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target));
    }

    [LinuxFact]
    public async Task ChangedSourceAndUnsafeTargetNamesAreRefusedBeforeRename()
    {
        using var dir = new TempDirectory();
        var source = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var before = await new RemovalFiles().ProbeAsync(dir.Path, source, default);
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().MoveAsync(before, dir.Path, "held.mp4", default));
        foreach (var name in new[] { "..", "../outside", "sub/movie.mp4", "sub\\movie.mp4" })
        {
            await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().MoveAsync(before, dir.Path, name, default));
        }

        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(dir.Combine("held.mp4")));
    }

    [LinuxFact]
    public async Task FaultsBeforeAndAfterRenameKeepAllBytesAtKnownNames()
    {
        foreach (var boundary in Enum.GetValues<RemovalMovePoint>())
        {
            using var dir = new TempDirectory();
            var source = dir.Combine("movie.mp4");
            var held = dir.Combine("held.mp4");
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            var snapshot = await new RemovalFiles().ProbeAsync(dir.Path, source, default);
            var files = new RemovalFiles(point => { if (point == boundary) throw new IOException("injected"); });
            await Assert.ThrowsAsync<IOException>(() => files.MoveAsync(snapshot, dir.Path, "held.mp4", default));
            var survivor = boundary == RemovalMovePoint.BeforeRename ? source : held;
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(survivor));
            Assert.Equal(new[] { survivor }, Directory.GetFiles(dir.Path));
        }
    }

    [LinuxFact]
    public async Task ANameSwapAtRenameIsDetectedWithoutDeletingEitherObject()
    {
        using var dir = new TempDirectory();
        var file = dir.Combine("movie.mp4");
        var retained = dir.Combine("retained.mp4");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var snapshot = await new RemovalFiles().ProbeAsync(dir.Path, file, default);
        var files = new RemovalFiles(point =>
        {
            if (point != RemovalMovePoint.BeforeRename) return;
            File.Move(file, retained);
            File.WriteAllBytes(file, [4, 5, 6]);
        });
        await Assert.ThrowsAsync<IOException>(() => files.MoveAsync(snapshot, dir.Path, "held.mp4", default));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(retained));
        var other = File.Exists(file) ? file : dir.Combine("held.mp4");
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(other));
    }

    [Fact]
    public async Task CancellationBeforeMoveNeverTouchesEitherName()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("movie.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await new RemovalFiles().ProbeAsync(dir.Path, path, default);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemovalFiles().MoveAsync(before, dir.Path, "held.mp4", source.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(dir.Combine("held.mp4")));
    }
}
