using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K3b: the deletion audit file.</summary>
public sealed class LocalDeleteAuditTests
{
    private static LocalDeleteAuditEntry Entry(string item = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1") => new("f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6", item, Guid.NewGuid(), "11111111111111111111111111111111", "Sintel", 2010, 199749, "abababababababab", "/media/Movies/Sintel (2010)/Sintel.mkv");

    [Fact]
    public void StartedThenFinishedAreTwoJsonLinesWithTheOutcomeWords()
    {
        using var dir = new TempDirectory();
        var audit = new LocalDeleteAudit(dir.Path, new ManualTimeProvider());
        var entry = Entry();
        audit.Started(entry);
        audit.Finished(entry with { Outcome = LocalDeleteItemState.Done, Shared = LocalDeleteShared.Pending, History = LocalDeleteHistory.NotCarried });

        var lines = File.ReadAllLines(Path.Combine(dir.Path, LocalDeleteAudit.FileName)).Select(static l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("started", lines[0].GetProperty("event").GetString());
        Assert.False(lines[0].TryGetProperty("outcome", out _));
        Assert.Equal("finished", lines[1].GetProperty("event").GetString());
        Assert.Equal("done", lines[1].GetProperty("outcome").GetString());
        Assert.Equal("pending", lines[1].GetProperty("shared").GetString());
        Assert.Equal("notCarried", lines[1].GetProperty("history").GetString());
        Assert.Equal(entry.Path, lines[1].GetProperty("path").GetString());
        Assert.Equal(16, lines[1].GetProperty("sha256Prefix").GetString()!.Length);
        Assert.StartsWith("2026-10-14T21:00:00", lines[0].GetProperty("utc").GetString(), StringComparison.Ordinal);
    }

    // W23-6: a rotation renames the full file to the next number and never overwrites an older one, so no deletion record is lost.
    [Fact]
    public void RotationKeepsEveryOlderFileUnderItsOwnNumber()
    {
        using var dir = new TempDirectory();
        var audit = new LocalDeleteAudit(dir.Path, new ManualTimeProvider(), 2048, null);
        for (var i = 0; i < 40; i++)
        {
            audit.Started(Entry(i.ToString("x32")));
        }

        var file = Path.Combine(dir.Path, LocalDeleteAudit.FileName);
        var all = Directory.GetFiles(dir.Path);
        Assert.True(all.Length > 3);
        Assert.All(all, f => Assert.True(new FileInfo(f).Length <= 2048));
        var lines = all.SelectMany(File.ReadAllLines).Select(static l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(40, lines.Length); // every started line is still on disk
        Assert.All(lines, l => Assert.Equal("started", l.GetProperty("event").GetString()));
        for (var i = 0; i < 40; i++)
        {
            Assert.Single(lines, l => l.GetProperty("itemKey").GetString() == i.ToString("x32"));
        }

        Assert.Contains(39.ToString("x32"), File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains(0.ToString("x32"), File.ReadAllText(file + ".1"), StringComparison.Ordinal); // .1 is the oldest, higher numbers are newer
        Assert.Equal(1024 * 1024, LocalDeleteAudit.MaxBytes);
    }

    // W23-6: an old .1 left by an earlier build is kept; the next rotation takes the next free number.
    [Fact]
    public void AnOldNumberedFileIsNeverOverwritten()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, LocalDeleteAudit.FileName);
        File.WriteAllText(file + ".1", "old history");
        var audit = new LocalDeleteAudit(dir.Path, new ManualTimeProvider(), 1024, null);
        for (var i = 0; i < 12; i++)
        {
            audit.Started(Entry(i.ToString("x32")));
        }

        Assert.Equal("old history", File.ReadAllText(file + ".1"));
        Assert.True(File.Exists(file + ".2"));
    }

    // W23-6: the count is bounded: past KeepFiles old files, the oldest goes first and the newest history stays.
    [Fact]
    public void TheNumberOfOldFilesIsBounded()
    {
        using var dir = new TempDirectory();
        var audit = new LocalDeleteAudit(dir.Path, new ManualTimeProvider(), 1024, null) { KeepFiles = 3 };
        for (var i = 0; i < 60; i++)
        {
            audit.Started(Entry(i.ToString("x32")));
        }

        var file = Path.Combine(dir.Path, LocalDeleteAudit.FileName);
        Assert.Equal(4, Directory.GetFiles(dir.Path).Length); // the current file and three old ones
        Assert.False(File.Exists(file + ".1"));
        Assert.Contains(59.ToString("x32"), File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(20, LocalDeleteAudit.DefaultKeepFiles);
    }

    // W23-6: the file holds local paths and titles, so on Linux it is readable by the Jellyfin user only.
    [Fact]
    public void OnLinuxTheFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var audit = new LocalDeleteAudit(dir.Path, new ManualTimeProvider(), 1024, null);
        for (var i = 0; i < 6; i++)
        {
            audit.Started(Entry(i.ToString("x32")));
        }

        foreach (var f in Directory.GetFiles(dir.Path))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
        }
    }

    [Fact]
    public void AFailedStartedLineThrowsSoNothingIsDeletedButAFailedFinishedLineDoesNot()
    {
        using var dir = new TempDirectory();
        var audit = new LocalDeleteAudit(Path.Combine(dir.Path, "missing-folder"), new ManualTimeProvider());
        Assert.ThrowsAny<IOException>(() => audit.Started(Entry()));
        audit.Finished(Entry() with { Outcome = LocalDeleteItemState.NotConfirmed });
    }

    [Fact]
    public void AnEntryHasNoFieldForATokenOrKey()
    {
        var names = typeof(LocalDeleteAuditEntry).GetProperties().Select(static p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(names, n => n.Contains("token", StringComparison.Ordinal) || n.Contains("secret", StringComparison.Ordinal) || n.Contains("apikey", StringComparison.Ordinal));
    }
}
