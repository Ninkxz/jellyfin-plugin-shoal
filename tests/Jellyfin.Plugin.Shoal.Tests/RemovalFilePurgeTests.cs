using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalFilePurgeTests
{
    private static async Task<RemovalJob> Fixture(string path)
    {
        var root = Directory.CreateDirectory(Path.Combine(path, "library")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(path, "holding")).FullName;
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(recovery, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Path.Combine(root, "disposable.mp4");
        await File.WriteAllTextAsync(originalPath, "generated disposable native purge test data");
        var files = new RemovalFiles();
        var original = await files.ProbeAsync(root, originalPath, default);
        var id = Guid.NewGuid().ToString("N");
        var held = await files.MoveAsync(original, recovery, id + ".media", default);
        var context = JsonSerializer.Serialize(new { target = new { root, path = originalPath, recoveryRoot = recovery }, baseline = Array.Empty<object>(), sharedItemId = Guid.Empty, sharedAtRestore = (object?)null });
        var operation = Guid.NewGuid().ToString("N");
        return new(id, RemovalPhase.PurgePrepared, "Disposable native test", original, held, context, DateTimeOffset.UtcNow.AddSeconds(-1),
            new(operation, ".purge-" + id + "-" + operation + ".media", Guid.NewGuid(), DateTimeOffset.UtcNow, new string('c', 64)));
    }

    [WindowsFact]
    public async Task UnsupportedPlatformRefusesBeforeAnyFileOrMetadataAccess()
    {
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<IOException>(() => new RemovalFiles().StagePurgeAsync(null!, default));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [LinuxFact]
    public async Task OnlyExactLiveStageCanBeCommittedAndReplayNeverUnlinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        var job = await Fixture(dir.Path);
        var unrelated = Path.Combine(job.Held!.Root, "unrelated");
        await File.WriteAllTextAsync(unrelated, "keep unrelated bytes");
        var files = new RemovalFiles();
        using var stage = await files.StagePurgeAsync(job, default);
        Assert.False(File.Exists(job.Original.FullPath));
        Assert.False(File.Exists(job.Held.FullPath));
        Assert.Equal(job.Held.Sha256, stage.Snapshot.Sha256);
        Assert.Equal(RemovalPurgeResult.UnlinkedAndFlushed, await RemovalFiles.CommitPurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, stage, () => { }, default));
        Assert.False(File.Exists(stage.Snapshot.FullPath));
        Assert.Equal("keep unrelated bytes", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(RemovalPurgeResult.NoUnlink, await RemovalFiles.CommitPurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, stage, () => { }, default));
    }

    [Fact]
    public async Task MissingFinalAuthorityCallbackRefusesBeforeBoundNativeOperation()
    {
        var calls = 0;
        using var stage = new RemovalPurgeStage(null!, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(RemovalPurgeResult.UnlinkedAndFlushed);
        }, () => { });
        Assert.Equal(RemovalPurgeResult.NoUnlink, await RemovalFiles.CommitPurgeAsync(null!, stage, null!, default));
        Assert.Equal(0, calls);
    }

    [LinuxFact]
    public async Task FinalAuthorityRunsAfterHashAndCanRefuseWithoutUnlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var variant in new[] { "authorityThrows", "originalAppears", "bytesChangeBeforeHash", "success" })
        {
            using var dir = new TempDirectory();
            var job = await Fixture(dir.Path);
            var nativeCheckStarted = false;
            var callbacks = 0;
            var stagedPath = Path.Combine(job.Held!.Root, job.Purge!.StageName);
            using var stage = await new RemovalFiles().StagePurgeAsync(job, default, point =>
            {
                if (point != RemovalPurgePoint.BeforeUnlink) return;
                nativeCheckStarted = true;
                if (variant == "bytesChangeBeforeHash") File.WriteAllText(stagedPath, "changed before final hash");
            });
            var result = await RemovalFiles.CommitPurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, stage, () =>
            {
                Assert.True(nativeCheckStarted);
                callbacks++;
                if (variant == "authorityThrows") throw new InvalidOperationException("changed authorization");
                if (variant == "originalAppears") File.WriteAllText(job.Original.FullPath, "occupied during final authority");
            }, default);
            Assert.Equal(variant == "success" ? RemovalPurgeResult.UnlinkedAndFlushed : RemovalPurgeResult.NoUnlink, result);
            Assert.Equal(variant == "bytesChangeBeforeHash" ? 0 : 1, callbacks);
            Assert.Equal(variant != "success", File.Exists(stagedPath));
        }
    }

    [LinuxFact]
    public async Task RenameFaultsCollisionsSwapsAndOccupiedOriginalNeverUnlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var variant in new[] { "collision", "occupiedOriginal", "beforeRename", "afterRename", "stageFlush", "sourceSwap", "stageSwap", "changedBytes", "canceled" })
        {
            using var dir = new TempDirectory();
            var job = await Fixture(dir.Path);
            var held = job.Held!;
            var stagedPath = Path.Combine(held.Root, job.Purge!.StageName);
            var kept = Path.Combine(held.Root, "kept-disposable");
            if (variant == "collision") await File.WriteAllTextAsync(stagedPath, "collision bytes");
            if (variant == "occupiedOriginal") await File.WriteAllTextAsync(job.Original.FullPath, "occupied original bytes");
            using var canceled = new CancellationTokenSource();
            if (variant == "canceled") canceled.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(() => new RemovalFiles().StagePurgeAsync(job, canceled.Token, point =>
            {
                if ((variant == "beforeRename" && point == RemovalPurgePoint.BeforeRename)
                    || (variant == "afterRename" && point == RemovalPurgePoint.AfterRename)
                    || (variant == "stageFlush" && point == RemovalPurgePoint.BeforeStageFlush)) throw new IOException("injected");
                if ((variant == "sourceSwap" && point == RemovalPurgePoint.BeforeRename) || (variant == "stageSwap" && point == RemovalPurgePoint.AfterRename))
                {
                    var name = variant == "sourceSwap" ? held.FullPath : stagedPath;
                    File.Move(name, kept);
                    File.WriteAllText(name, "replacement object must survive");
                }

                if (variant == "changedBytes" && point == RemovalPurgePoint.AfterRename) File.WriteAllText(stagedPath, "changed staged bytes must survive");
            }));
            Assert.NotEmpty(Directory.GetFiles(held.Root));
            if (variant is "sourceSwap" or "stageSwap")
            {
                Assert.Equal("generated disposable native purge test data", await File.ReadAllTextAsync(kept));
                Assert.Equal("replacement object must survive", await File.ReadAllTextAsync(variant == "sourceSwap" ? held.FullPath : stagedPath));
            }

            if (variant == "collision") Assert.Equal("collision bytes", await File.ReadAllTextAsync(stagedPath));
            if (variant == "occupiedOriginal") Assert.Equal("occupied original bytes", await File.ReadAllTextAsync(job.Original.FullPath));
        }
    }

    [LinuxFact]
    public async Task CommitFaultsPreserveTruthAndFixedNameSwapsAreRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var variant in new[] { "beforeUnlink", "afterUnlink", "directoryFlush", "swap", "changedBytes", "originalAppears", "wrongAuthority" })
        {
            using var dir = new TempDirectory();
            var job = await Fixture(dir.Path);
            var stagedPath = Path.Combine(job.Held!.Root, job.Purge!.StageName);
            var kept = Path.Combine(job.Held.Root, "kept-disposable");
            using var stage = await new RemovalFiles().StagePurgeAsync(job, default, point =>
            {
                if ((variant == "beforeUnlink" && point == RemovalPurgePoint.BeforeUnlink)
                    || (variant == "afterUnlink" && point == RemovalPurgePoint.AfterUnlink)
                    || (variant == "directoryFlush" && point == RemovalPurgePoint.BeforeDirectoryFlush)) throw new IOException("injected");
                if (point != RemovalPurgePoint.BeforeUnlink) return;
                if (variant == "swap")
                {
                    File.Move(stagedPath, kept);
                    File.WriteAllText(stagedPath, "replacement object must survive");
                }

                if (variant == "changedBytes") File.WriteAllText(stagedPath, "changed bytes must survive");
                if (variant == "originalAppears") File.WriteAllText(job.Original.FullPath, "occupied original must survive");
            });
            var committing = job with { Phase = RemovalPhase.PurgeCommitting };
            if (variant == "wrongAuthority") committing = committing with { Purge = committing.Purge! with { AdminId = Guid.NewGuid() } };
            var result = await RemovalFiles.CommitPurgeAsync(committing, stage, () => { }, default);
            var afterUnlink = variant is "afterUnlink" or "directoryFlush";
            Assert.Equal(afterUnlink ? RemovalPurgeResult.UnlinkOutcomeUnknown : RemovalPurgeResult.NoUnlink, result);
            Assert.Equal(!afterUnlink, File.Exists(stagedPath));
            if (variant == "swap")
            {
                Assert.Equal("generated disposable native purge test data", await File.ReadAllTextAsync(kept));
                Assert.Equal("replacement object must survive", await File.ReadAllTextAsync(stagedPath));
            }
        }
    }

    private const string Bytes = "generated disposable native purge test data";

    // Every byte of the generated original must still exist under some name: rollback never unlinks.
    private static async Task AssertOriginalSurvives(string root, params string[] extra)
    {
        var contents = new List<string>();
        foreach (var path in Directory.GetFiles(root).Concat(extra.Where(File.Exists)))
        {
            if (new FileInfo(path).LinkTarget is null) contents.Add(await File.ReadAllTextAsync(path));
        }

        Assert.Contains(Bytes, contents);
    }

    [LinuxFact]
    public async Task LiveStageRollsBackOnceToTheExactHeldObjectAndCanBeReviewedAgain()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        var job = await Fixture(dir.Path);
        var files = new RemovalFiles();
        var stagedPath = Path.Combine(job.Held!.Root, job.Purge!.StageName);
        RemovalUnstageOutcome outcome;
        using (var stage = await files.StagePurgeAsync(job, default))
        {
            Assert.True(File.Exists(stagedPath));
            outcome = await files.UnstagePurgeAsync(job, stage, default);
            Assert.Equal(RemovalUnstageResult.Restored, outcome.Result);
            Assert.Equal(RemovalUnstageResult.NotRestored, (await files.UnstagePurgeAsync(job, stage, default)).Result);
            Assert.Equal(RemovalPurgeResult.NoUnlink, await RemovalFiles.CommitPurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, stage, () => { }, default));
        }

        Assert.False(File.Exists(stagedPath));
        Assert.Equal(Bytes, await File.ReadAllTextAsync(job.Held.FullPath));
        Assert.True(RemovalJournal.SameHeldAcrossRename(job.Held, outcome.Held!));
        Assert.Equal(outcome.Held, await files.ProbeAsync(job.Held.Root, job.Held.FullPath, default));
        RemovalFiles.ValidateRecoveryInventory(job.Held.Root, new HashSet<string> { job.JobId });
        var rolled = job with { Phase = RemovalPhase.ReplacementReady, Purge = null, Held = outcome.Held };
        RemovalJournal.ValidateSuccessor(job, rolled);

        var operation = Guid.NewGuid().ToString("N");
        var retry = rolled with { Phase = RemovalPhase.PurgePrepared, Purge = job.Purge with { OperationId = operation, StageName = ".purge-" + job.JobId + "-" + operation + ".media" } };
        using var again = await files.StagePurgeAsync(retry, default);
        Assert.Equal(RemovalPurgeResult.UnlinkedAndFlushed, await RemovalFiles.CommitPurgeAsync(retry with { Phase = RemovalPhase.PurgeCommitting }, again, () => { }, default));
        Assert.Empty(Directory.GetFiles(job.Held.Root));
    }

    [LinuxFact]
    public async Task AttemptedCommitMakesRollbackImpossible()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        var job = await Fixture(dir.Path);
        var files = new RemovalFiles();
        using var stage = await files.StagePurgeAsync(job, default);
        Assert.Equal(RemovalPurgeResult.NoUnlink, await RemovalFiles.CommitPurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, stage, () => throw new InvalidOperationException("planChanged"), default));
        Assert.Equal(RemovalUnstageResult.NotRestored, (await files.UnstagePurgeAsync(job, stage, default)).Result);
        Assert.Equal(RemovalUnstageResult.NotRestored, (await files.UnstagePurgeAsync(job with { Phase = RemovalPhase.PurgeCommitting }, null, default)).Result);
        Assert.True(File.Exists(Path.Combine(job.Held!.Root, job.Purge!.StageName)));
        Assert.False(File.Exists(job.Held.FullPath));
    }

    [LinuxFact]
    public async Task LiveRollbackFaultsSwapsSymlinksAndOccupiedNamesNeverOverwriteOrUnlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var variant in new[] { "beforeUnstage", "afterUnstageRename", "unstageFlush", "occupiedHeld", "symlinkHeld", "stageSwap", "symlinkStage", "changedBytes", "originalAppears", "wrongAuthority", "canceled", "disposed", "swappedRecoveryDir" })
        {
            using var dir = new TempDirectory();
            var job = await Fixture(dir.Path);
            var held = job.Held!;
            var stagedPath = Path.Combine(held.Root, job.Purge!.StageName);
            var kept = Path.Combine(dir.Path, "kept-disposable");
            var files = new RemovalFiles();
            var stage = await files.StagePurgeAsync(job, default, point =>
            {
                if ((variant == "beforeUnstage" && point == RemovalPurgePoint.BeforeUnstage)
                    || (variant == "afterUnstageRename" && point == RemovalPurgePoint.AfterUnstageRename)
                    || (variant == "unstageFlush" && point == RemovalPurgePoint.BeforeUnstageFlush)) throw new IOException("injected");
                if (point != RemovalPurgePoint.BeforeUnstage) return;
                if (variant == "occupiedHeld") File.WriteAllText(held.FullPath, "occupying held bytes must survive");
                if (variant == "symlinkHeld") File.CreateSymbolicLink(held.FullPath, kept);
                if (variant is "stageSwap" or "symlinkStage")
                {
                    File.Move(stagedPath, kept);
                    if (variant == "stageSwap") File.WriteAllText(stagedPath, "replacement object must survive");
                    else File.CreateSymbolicLink(stagedPath, kept);
                }

                if (variant == "changedBytes") File.AppendAllText(stagedPath, " changed");
                if (variant == "originalAppears") File.WriteAllText(job.Original.FullPath, "occupied original must survive");
                if (variant == "swappedRecoveryDir")
                {
                    var old = held.Root + "-displaced";
                    Directory.Move(held.Root, old);
                    Directory.CreateDirectory(held.Root);
                    if (OperatingSystem.IsLinux()) File.SetUnixFileMode(held.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.Move(Path.Combine(old, job.Purge.StageName), stagedPath);
                }
            });
            using var canceled = new CancellationTokenSource();
            if (variant == "canceled") canceled.Cancel();
            if (variant == "disposed") stage.Dispose();
            var target = variant == "wrongAuthority" ? job with { Purge = job.Purge with { AdminId = Guid.NewGuid() } } : job;
            var outcome = await files.UnstagePurgeAsync(target, stage, canceled.Token);
            stage.Dispose();
            Assert.Equal(RemovalUnstageResult.NotRestored, outcome.Result);
            Assert.Null(outcome.Held);
            if (variant != "changedBytes") await AssertOriginalSurvives(held.Root, kept); // changedBytes edits the only copy in place
            switch (variant)
            {
                case "afterUnstageRename" or "unstageFlush":
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(held.FullPath)); // renamed back but unproved: caller stays Unknown
                    Assert.False(File.Exists(stagedPath));
                    break;
                case "occupiedHeld":
                    Assert.Equal("occupying held bytes must survive", await File.ReadAllTextAsync(held.FullPath));
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(stagedPath));
                    break;
                case "symlinkHeld":
                    Assert.NotNull(new FileInfo(held.FullPath).LinkTarget);
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(stagedPath));
                    break;
                case "stageSwap":
                    Assert.Equal("replacement object must survive", await File.ReadAllTextAsync(stagedPath));
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(kept));
                    Assert.False(File.Exists(held.FullPath));
                    break;
                case "symlinkStage":
                    Assert.NotNull(new FileInfo(stagedPath).LinkTarget);
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(kept));
                    Assert.False(File.Exists(held.FullPath));
                    break;
                case "changedBytes":
                    Assert.Equal(Bytes + " changed", await File.ReadAllTextAsync(stagedPath));
                    Assert.False(File.Exists(held.FullPath));
                    break;
                case "originalAppears":
                    Assert.Equal("occupied original must survive", await File.ReadAllTextAsync(job.Original.FullPath));
                    Assert.True(File.Exists(stagedPath));
                    break;
                default:
                    Assert.Equal(Bytes, await File.ReadAllTextAsync(stagedPath));
                    Assert.False(File.Exists(held.FullPath));
                    break;
            }
        }
    }

    [LinuxFact]
    public async Task FailedStagingIsProbedAndOnlyAnUnambiguousExactObjectIsRestored()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var variant in new[] { "nothingMoved", "afterRename", "stageFlush", "bothNames", "neitherName", "heldReplaced", "symlinkStage", "recoverFault", "notPrepared", "swappedRecoveryDir", "swappedParentDir", "swappedRecoveryDirNothingMoved" })
        {
            using var dir = new TempDirectory();
            var job = await Fixture(dir.Path);
            var held = job.Held!;
            var stagedPath = Path.Combine(held.Root, job.Purge!.StageName);
            var kept = Path.Combine(dir.Path, "kept-disposable");
            var files = new RemovalFiles();
            var failAt = variant is "nothingMoved" or "heldReplaced" or "swappedRecoveryDirNothingMoved" ? RemovalPurgePoint.BeforeRename : variant == "stageFlush" ? RemovalPurgePoint.BeforeStageFlush : RemovalPurgePoint.AfterRename;
            await Assert.ThrowsAnyAsync<Exception>(() => files.StagePurgeAsync(job, default, point =>
            {
                if (point == failAt) throw new IOException("injected");
            }));
            if (variant == "bothNames") File.WriteAllText(held.FullPath, "second object must survive");
            if (variant == "neitherName") File.Move(stagedPath, kept);
            if (variant == "heldReplaced")
            {
                File.Move(held.FullPath, kept);
                File.WriteAllText(held.FullPath, Bytes);
            }

            if (variant == "symlinkStage")
            {
                File.Move(stagedPath, kept);
                File.CreateSymbolicLink(stagedPath, kept);
            }

            var displaced = Path.Combine(Path.GetDirectoryName(dir.Path)!, Path.GetFileName(dir.Path) + "-displaced");
            if (variant is "swappedRecoveryDir" or "swappedRecoveryDirNothingMoved")
            {
                // Same path, same names, new recovery directory inode: ancestry must refuse it.
                var old = held.Root + "-displaced";
                Directory.Move(held.Root, old);
                Directory.CreateDirectory(held.Root);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(held.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var entry in Directory.GetFiles(old)) File.Move(entry, Path.Combine(held.Root, Path.GetFileName(entry)));
                Directory.Delete(old);
            }

            if (variant == "swappedParentDir")
            {
                // Same recovery directory object under a replaced parent directory.
                Directory.Move(dir.Path, displaced);
                Directory.CreateDirectory(dir.Path);
                Directory.Move(Path.Combine(displaced, "library"), Path.Combine(dir.Path, "library"));
                Directory.Move(Path.Combine(displaced, "holding"), held.Root);
            }

            var target = variant == "notPrepared" ? job with { Phase = RemovalPhase.PurgeCommitting } : job;
            var outcome = await files.RecoverPurgeStageAsync(target, default, point =>
            {
                if (variant == "recoverFault" && point == RemovalPurgePoint.AfterUnstageRename) throw new IOException("injected");
            });
            var restored = variant is "nothingMoved" or "afterRename" or "stageFlush";
            Assert.Equal(restored ? RemovalUnstageResult.Restored : RemovalUnstageResult.NotRestored, outcome.Result);
            await AssertOriginalSurvives(held.Root, kept);
            if (restored)
            {
                Assert.Equal(Bytes, await File.ReadAllTextAsync(held.FullPath));
                Assert.False(File.Exists(stagedPath));
                Assert.True(RemovalJournal.SameHeldAcrossRename(held, outcome.Held!));
                if (variant == "nothingMoved") Assert.Equal(held, outcome.Held); // proved by identity and ancestry, no re-hash
                RemovalFiles.ValidateRecoveryInventory(held.Root, new HashSet<string> { job.JobId });
            }

            if (variant == "bothNames")
            {
                Assert.Equal("second object must survive", await File.ReadAllTextAsync(held.FullPath));
                Assert.Equal(Bytes, await File.ReadAllTextAsync(stagedPath));
            }

            if (variant == "heldReplaced") Assert.Equal(Bytes, await File.ReadAllTextAsync(kept));
            if (variant == "symlinkStage") Assert.NotNull(new FileInfo(stagedPath).LinkTarget);
            if (variant is "notPrepared" or "recoverFault") Assert.True(File.Exists(stagedPath) || File.Exists(held.FullPath));
            if (variant is "swappedRecoveryDir" or "swappedParentDir") Assert.Equal(Bytes, await File.ReadAllTextAsync(stagedPath));
            if (variant == "swappedRecoveryDirNothingMoved") Assert.Equal(Bytes, await File.ReadAllTextAsync(held.FullPath));
            if (Directory.Exists(displaced)) Directory.Delete(displaced, recursive: true);
        }
    }
}
