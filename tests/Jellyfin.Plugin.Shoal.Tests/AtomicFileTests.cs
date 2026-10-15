using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Shoal.Storage;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// The shared write protocol (DESIGN 3.7), including the fallback for file systems without hard links (Warden N-9).
/// </summary>
public sealed class AtomicFileTests
{
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void WritingANewFile_CreatesIt_AndLeavesNoTempFile()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");

        var outcome = AtomicFile.Write(path, B("one"));

        Assert.Equal(AtomicWriteOutcome.Normal, outcome);
        Assert.Equal("one", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void ReplacingAFile_KeepsThePreviousContentAsTheBackup()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        AtomicFile.Write(path, B("one"));

        var outcome = AtomicFile.Write(path, B("two"), path + ".bak");

        Assert.Equal(AtomicWriteOutcome.Normal, outcome);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void WithoutABackupPath_NoBackupIsMade()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("secrets.enc");
        AtomicFile.Write(path, B("one"));

        AtomicFile.Write(path, B("two"));

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void WhenTheFileSystemRefusesTheBackupLink_TheBackupIsMadeByCopy()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        AtomicFile.Write(path, B("one"));

        var outcome = AtomicFile.Write(path, B("two"), path + ".bak", replace: (_, _, _) => throw new IOException("no hard links"));

        Assert.Equal(AtomicWriteOutcome.BackupByCopy, outcome);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }

    [Fact]
    public void WhenEvenTheCopyFails_TheNewContentIsStillWritten()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        AtomicFile.Write(path, B("one"));
        Directory.CreateDirectory(path + ".bak"); // a folder where the backup file should go: copying onto it fails

        var outcome = AtomicFile.Write(path, B("two"), path + ".bak", replace: (_, _, _) => throw new IOException("no hard links"));

        Assert.Equal(AtomicWriteOutcome.BackupSkipped, outcome);
        Assert.Equal("two", File.ReadAllText(path));
    }

    [Fact]
    public void AFailureThatIsNotAboutTheBackup_LeavesTheTargetAndCleansUp()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        AtomicFile.Write(path, B("one"));

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, B("two"), path + ".bak", replace: (_, _, _) => throw new InvalidOperationException("boom")));

        Assert.Equal("one", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void ASimulatedCrash_LeavesTheTargetUntouched_AndTheTempFileBehind()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        AtomicFile.Write(path, B("one"));

        Assert.Throws<SimulatedCrashException>(() => AtomicFile.Write(path, B("two"), hook: _ => throw new SimulatedCrashException()));

        Assert.Equal("one", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path, "a.json.tmp-*"));
        Assert.Equal(1, AtomicFile.DeleteStaleTempFiles(dir.Path, "a.json"));
        Assert.Empty(Directory.GetFiles(dir.Path, "a.json.tmp-*"));
    }

    [Fact]
    public void StaleTempCleanup_OnlyTouchesTheNamedTarget()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.Combine("a.json.tmp-1"), "x");
        File.WriteAllText(dir.Combine("b.json.tmp-1"), "x");
        File.WriteAllText(dir.Combine("a.json"), "keep");

        var removed = AtomicFile.DeleteStaleTempFiles(dir.Path, "a.json");

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "a.json", "b.json.tmp-1" }, Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void TheTempFileNames_AreUnpredictable()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("a.json");
        var seen = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            Assert.Throws<SimulatedCrashException>(() => AtomicFile.Write(path, B("x"), hook: _ => throw new SimulatedCrashException()));
        }

        foreach (var file in Directory.GetFiles(dir.Path, "a.json.tmp-*"))
        {
            Assert.True(seen.Add(file));
        }

        Assert.Equal(20, seen.Count);
    }
}
