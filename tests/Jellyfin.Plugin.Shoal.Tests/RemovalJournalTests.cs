using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalJournalTests
{
    private static RemovalJob Job(string basePath)
    {
        var originalRoot = Path.Combine(basePath, "library");
        var recovery = Path.Combine(basePath, "holding");
        var original = new RemovalFileSnapshot(Path.Combine(originalRoot, "movie.mp4"), originalRoot, 3, new string('a', 64), "l:1:2:3:4:8000|" + new string('b', 64));
        var context = JsonSerializer.Serialize(new { target = new { root = originalRoot, path = original.FullPath, recoveryRoot = recovery }, baseline = Array.Empty<object>(), sharedItemId = Guid.Empty, sharedAtRestore = (object?)null });
        return new(Guid.NewGuid().ToString("N"), RemovalPhase.Prepared, "Test movie", original, null, context, new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CompleteSchemaAndPrivateBindingRoundTripWithoutDroppingFields()
    {
        using var dir = new TempDirectory();
        var job = Job(dir.Path);
        var decoded = RemovalJournal.Decode(RemovalJournal.Encode(job, 1));
        Assert.Equal(job, decoded.Job);
        Assert.Equal(1, decoded.Generation);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("wrongSchema")]
    [InlineData("integerPhase")]
    [InlineData("unknownPhase")]
    [InlineData("nestedUnknown")]
    [InlineData("nestedMissing")]
    [InlineData("nullJob")]
    public void UnsupportedMissingOrUnknownFieldsNeverBecomeDefaults(string variant)
    {
        using var dir = new TempDirectory();
        var json = JsonNode.Parse(RemovalJournal.Encode(Job(dir.Path), 1))!.AsObject();
        switch (variant)
        {
            case "unknown": json["extra"] = true; break;
            case "missing": json.Remove("generation"); break;
            case "wrongSchema": json["schema"] = 2; break;
            case "integerPhase": json["job"]!["phase"] = 0; break;
            case "unknownPhase": json["job"]!["phase"] = "permanentPurge"; break;
            case "nestedUnknown": json["job"]!["original"]!["pathToken"] = "not allowed"; break;
            case "nestedMissing": json["job"]!["original"]!.AsObject().Remove("sha256"); break;
            default: json["job"] = null; break;
        }

        Assert.Throws<IOException>(() => RemovalJournal.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void UnsafeJobIdsDatesSnapshotsAndContextAreRefused()
    {
        using var dir = new TempDirectory();
        var job = Job(dir.Path);
        foreach (var bad in new[]
        {
            job with { JobId = "../outside" },
            job with { CreatedUtc = DateTimeOffset.MinValue },
            job with { Original = job.Original with { FullPath = Path.Combine(dir.Path, "outside.mp4") } },
            job with { Original = job.Original with { Sha256 = "partial" } },
            job with { Phase = RemovalPhase.Quarantined },
            job with { ContextJson = job.ContextJson.Replace("holding", "library", StringComparison.Ordinal) },
            job with { ContextJson = "{" },
            job with { ContextJson = job.ContextJson.Insert(1, "\"token\":\"not allowed\",") },
            job with { ContextJson = job.ContextJson.Replace("\"target\":{", "\"target\":{\"secret\":\"not allowed\",", StringComparison.Ordinal) },
        })
        {
            Assert.Throws<IOException>(() => RemovalJournal.Encode(bad, 1));
        }

        Assert.Throws<IOException>(() => RemovalJournal.Encode(job, 0));
        Assert.Throws<IOException>(() => RemovalJournal.Decode(new byte[128 * 1024 + 1]));
    }

    [Fact]
    public void DuplicatePropertiesCannotSilentlyReplaceADurableValue()
    {
        using var dir = new TempDirectory();
        var json = Encoding.UTF8.GetString(RemovalJournal.Encode(Job(dir.Path), 1));
        var duplicate = json.Replace("\"generation\":1", "\"generation\":1,\"generation\":2", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => RemovalJournal.Decode(Encoding.UTF8.GetBytes(duplicate)));
    }

    [WindowsFact]
    public async Task WindowsJournalFailsBeforeAnyMetadataWrite()
    {
        using var dir = new TempDirectory();
        using var journal = new RemovalJournal(dir.Path);
        await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(Job(dir.Path), default));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [LinuxFact]
    public async Task OwnedPendingRecordsAreNotAdoptedAndDoNotDisableKnownRestore()
    {
        if (!OperatingSystem.IsLinux()) return;

        foreach (var pendingKind in new[] { "empty", "partial", "complete" })
        {
            using var dir = new TempDirectory();
            File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var journal = new RemovalJournal(dir.Path);
            var prepared = Job(dir.Path);
            await journal.SaveAsync(prepared, default);
            var recovery = Path.Combine(dir.Path, "holding");
            var held = prepared.Original with { Root = recovery, FullPath = Path.Combine(recovery, prepared.JobId + ".media") };
            var known = prepared with { Phase = RemovalPhase.Quarantined, Held = held };
            await journal.SaveAsync(known, default);
            var payload = pendingKind == "empty" ? [] : pendingKind == "partial" ? Encoding.UTF8.GetBytes("{") : RemovalJournal.Encode(known with { Phase = RemovalPhase.Restored }, 3);
            var pending = Path.Combine(dir.Path, ".pending-" + prepared.JobId + "-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllBytesAsync(pending, payload);
            File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(known, Assert.Single(await journal.LoadAsync(default)));
            var refused = await Assert.ThrowsAsync<IOException>(() => journal.EnsureCanPlanAsync(default));
            Assert.Equal("recoveryRequired", refused.Message);
            await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(Job(dir.Path), default));
            await journal.SaveAsync(known with { Phase = RemovalPhase.Restored }, default);
            Assert.Equal(RemovalPhase.Restored, Assert.Single(await journal.LoadAsync(default)).Phase);
            Assert.Equal(payload, await File.ReadAllBytesAsync(pending));
            Assert.Equal(4, Directory.GetFiles(dir.Path).Length);
        }
    }

    [LinuxFact]
    public async Task ACorrectlyNamedPendingSymlinkIsNeverTrusted()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var journal = new RemovalJournal(dir.Path);
        var job = Job(dir.Path);
        await journal.SaveAsync(job, default);
        var target = Path.Combine(outside.Path, "untouched");
        await File.WriteAllTextAsync(target, "untouched");
        File.CreateSymbolicLink(Path.Combine(dir.Path, ".pending-" + job.JobId + "-" + Guid.NewGuid().ToString("N") + ".json"), target);
        await Assert.ThrowsAsync<IOException>(() => journal.LoadAsync(default));
        Assert.Equal("untouched", await File.ReadAllTextAsync(target));
    }

    [LinuxFact]
    public async Task RecoveryInventoryIsNonCreatingAndRefusesInvisibleOrUnsafeHeldFiles()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var dir = new TempDirectory();
        var root = Path.Combine(dir.Path, "recovery");
        RemovalFiles.ValidateRecoveryInventory(root, new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
        Assert.False(Directory.Exists(root));
        RemovalFiles.EnsureRecoveryDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(root, id + ".media");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var missing = Assert.Throws<IOException>(() => RemovalFiles.ValidateRecoveryInventory(root, new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal("recoveryRequired", missing.Message);
        var expected = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { id };
        RemovalFiles.ValidateRecoveryInventory(root, expected);
        var alias = Guid.NewGuid().ToString("N");
        File.CreateSymbolicLink(Path.Combine(root, alias + ".media"), file);
        expected.Add(alias);
        Assert.Throws<IOException>(() => RemovalFiles.ValidateRecoveryInventory(root, expected));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file));
    }

    [LinuxFact]
    public void RecoveryDirectoryCreationNeverChmodsAnExistingUnsafeDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var dir = new TempDirectory();
        var root = Path.Combine(dir.Path, "recovery");
        RemovalFiles.EnsureRecoveryDirectory(root);
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(privateMode, File.GetUnixFileMode(root));
        File.SetUnixFileMode(root, privateMode | UnixFileMode.GroupRead);
        Assert.Throws<IOException>(() => RemovalFiles.EnsureRecoveryDirectory(root));
        Assert.Equal(privateMode | UnixFileMode.GroupRead, File.GetUnixFileMode(root));
    }

    [LinuxFact]
    public async Task AppendRestartAndRestorePhasePreserveEveryEarlierGeneration()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var job = Job(dir.Path);
        using (var journal = new RemovalJournal(dir.Path))
        {
            await journal.SaveAsync(job, default);
            var recovery = Path.Combine(dir.Path, "holding");
            var held = job.Original with { Root = recovery, FullPath = Path.Combine(recovery, job.JobId + ".media") };
            await journal.SaveAsync(job with { Phase = RemovalPhase.Quarantined, Held = held }, default);
            await journal.SaveAsync(job with { Phase = RemovalPhase.Restored }, default);
        }

        using var restarted = new RemovalJournal(dir.Path);
        Assert.Equal(RemovalPhase.Restored, Assert.Single(await restarted.LoadAsync(default)).Phase);
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
    }

    [LinuxFact]
    public async Task CorruptOrUncertainFilesBlockRatherThanReturningAnEmptyJournal()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var journal = new RemovalJournal(dir.Path);
        var job = Job(dir.Path);
        await journal.SaveAsync(job, default);
        var original = Directory.GetFiles(dir.Path).Single();
        var bytes = await File.ReadAllBytesAsync(original);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, ".pending-unfinished"), "incomplete");
        await Assert.ThrowsAsync<IOException>(() => journal.LoadAsync(default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
        await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(job with { Phase = RemovalPhase.NeedsRecovery }, default));
    }

    [LinuxFact]
    public async Task EveryDurabilityFaultRetainsWrittenRecordsWithoutCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;

        foreach (var boundary in Enum.GetValues<RemovalJournalPoint>())
        {
            using var dir = new TempDirectory();
            File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var journal = new RemovalJournal(dir.Path, point => { if (point == boundary) throw new IOException("injected"); });
            await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(Job(dir.Path), default));
            Assert.All(Directory.GetFiles(dir.Path), file => Assert.NotEmpty(File.ReadAllBytes(file)));
        }
    }
}
