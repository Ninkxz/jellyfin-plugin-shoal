using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalPurgeJournalTests
{
    private static RemovalJob Ready(string path)
    {
        var root = Path.Combine(path, "library");
        var recovery = Path.Combine(path, "holding");
        var id = Guid.NewGuid().ToString("N");
        var original = new RemovalFileSnapshot(Path.Combine(root, "movie.mp4"), root, 3, new string('a', 64), "l:1:2:3:4:8000|" + new string('b', 64));
        var context = JsonSerializer.Serialize(new { target = new { root, path = original.FullPath, recoveryRoot = recovery }, baseline = Array.Empty<object>(), sharedItemId = Guid.Empty, sharedAtRestore = (object?)null });
        return new(id, RemovalPhase.ReplacementReady, "Test movie", original,
            original with { Root = recovery, FullPath = Path.Combine(recovery, id + ".media") }, context, DateTimeOffset.UtcNow);
    }

    private static RemovalJob PreparedPurge(RemovalJob ready)
    {
        var operation = Guid.NewGuid().ToString("N");
        return ready with { Phase = RemovalPhase.PurgePrepared, Purge = new(operation, ".purge-" + ready.JobId + "-" + operation + ".media", Guid.NewGuid(), DateTimeOffset.UtcNow, new string('c', 64)) };
    }

    [Fact]
    public void LegacyRecordShapeStaysSchemaOneWithAllHistoricalFieldsRequired()
    {
        using var dir = new TempDirectory();
        var ready = Ready(dir.Path);
        var json = JsonNode.Parse(RemovalJournal.Encode(ready, 3))!;
        Assert.Equal(1, json["schema"]!.GetValue<int>());
        Assert.Null(json["job"]!["purge"]);
        Assert.Equal(ready, RemovalJournal.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())).Job);
        foreach (var field in new[] { "jobId", "phase", "title", "original", "held", "contextJson", "createdUtc" })
        {
            var missing = json.DeepClone();
            missing["job"]!.AsObject().Remove(field);
            Assert.Throws<IOException>(() => RemovalJournal.Decode(Encoding.UTF8.GetBytes(missing.ToJsonString())));
        }
    }

    [Fact]
    public void PurgeRecordHasExplicitStrictSchemaTwoAndRoundTripsPrivateAuthority()
    {
        using var dir = new TempDirectory();
        var purge = PreparedPurge(Ready(dir.Path));
        var bytes = RemovalJournal.Encode(purge, 4);
        Assert.Equal(2, JsonNode.Parse(bytes)!["schema"]!.GetValue<int>());
        Assert.Equal(purge, RemovalJournal.Decode(bytes).Job);
        foreach (var variant in new[] { "missingPurge", "missingActor", "unknownAuthority", "wrongStage", "missingHeld", "legacySchema" })
        {
            var json = JsonNode.Parse(bytes)!;
            switch (variant)
            {
                case "missingPurge": json["job"]!.AsObject().Remove("purge"); break;
                case "missingActor": json["job"]!["purge"]!.AsObject().Remove("adminId"); break;
                case "unknownAuthority": json["job"]!["purge"]!["arbitraryPath"] = "not allowed"; break;
                case "wrongStage": json["job"]!["purge"]!["stageName"] = "../outside"; break;
                case "missingHeld": json["job"]!["held"] = null; break;
                default: json["schema"] = 1; break;
            }

            Assert.Throws<IOException>(() => RemovalJournal.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
    }

    [Fact]
    public void PurgePhasesCannotReuseGenericRestoreOrRecoveryTransitions()
    {
        foreach (var phase in new[] { RemovalPhase.PurgePrepared, RemovalPhase.PurgeCommitting, RemovalPhase.Purged, RemovalPhase.PurgeOutcomeUnknown })
        {
            Assert.False(RemovalPolicy.CanTransition(phase, RemovalPhase.Restored));
            Assert.False(RemovalPolicy.CanTransition(phase, RemovalPhase.NeedsRecovery));
            // 2.10.3 W20-5: PurgeCommitting may also return to ReplacementReady, only through the start-up sweep's proved rollback (ValidateSuccessor binds it).
            // 2.10.3 W22-2: and PurgeOutcomeUnknown, only when the next start proves the exact object is still there. Purged never returns.
            Assert.Equal(phase is not RemovalPhase.Purged, RemovalPolicy.CanTransition(phase, RemovalPhase.ReplacementReady));
        }

        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.ReplacementReady, RemovalPhase.PurgePrepared));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.PurgePrepared, RemovalPhase.PurgeCommitting));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.PurgeCommitting, RemovalPhase.Purged));
        Assert.False(RemovalPolicy.CanTransition(RemovalPhase.PurgePrepared, RemovalPhase.Purged));
    }

    private static RemovalJob LinuxReady(string path, string ctime = "200:7", bool legacy = false)
    {
        var ready = Ready(path);
        var identity = (legacy ? "l:8:1:30:" : "l:") + "12345:81a4:1:3:100:5:" + ctime + "|" + new string('b', 64);
        return ready with { Original = ready.Original with { Identity = identity }, Held = ready.Held! with { Identity = identity } };
    }

    private static RemovalJob RolledBack(RemovalJob prepared, string ctime) => prepared with
    {
        Phase = RemovalPhase.ReplacementReady,
        Purge = null,
        Held = prepared.Held! with { Identity = RemovalJournal.PersistentIdentity(prepared.Held.Identity.Split('|')[0]).Split(':')[..7] is var head ? string.Join(':', head) + ":" + ctime + "|" + prepared.Held.Identity.Split('|')[1] : string.Empty },
    };

    [Fact]
    public void PersistedIdentityIgnoresRestartUnstableDeviceAndMountButNothingElse()
    {
        Assert.Equal("l:12345:81a4:1:3:100:5:200:7", RemovalJournal.PersistentIdentity("l:252:12:4042:12345:81a4:1:3:100:5:200:7"));
        Assert.Equal(RemovalJournal.PersistentIdentity("l:252:12:4042:12345:81a4:1:3:100:5:200:7"), RemovalJournal.PersistentIdentity("l:252:13:3864:12345:81a4:1:3:100:5:200:7"));
        Assert.Equal("l:77:41ed", RemovalJournal.PersistentIdentity("l:252:12:4042:77:41ed"));
        Assert.Equal("l:12345:81a4:1:3:100:5:200:7", RemovalJournal.PersistentIdentity("l:12345:81a4:1:3:100:5:200:7"));
        Assert.StartsWith("w:", RemovalJournal.PersistentIdentity("w:1:2:3:20:1:3:0:0:0:0"), StringComparison.Ordinal);

        using var dir = new TempDirectory();
        var journaledLegacy = LinuxReady(dir.Path, legacy: true).Held!; // written before the change, mount ID 4042 embedded
        var afterRestart = journaledLegacy with { Identity = "l:12345:81a4:1:3:100:5:200:7|" + new string('9', 64) };
        Assert.True(RemovalJournal.SameHeld(journaledLegacy, afterRestart));
        var current = LinuxReady(dir.Path).Held!;
        Assert.True(RemovalJournal.SameHeld(current, current with { }));
        foreach (var changed in new[]
        {
            afterRestart with { Identity = "l:99999:81a4:1:3:100:5:200:7|" + new string('9', 64) },
            afterRestart with { Identity = "l:12345:81a4:1:4:100:5:200:7|" + new string('9', 64) },
            afterRestart with { Identity = "l:12345:81a4:1:3:101:5:200:7|" + new string('9', 64) },
            afterRestart with { Identity = "l:12345:81a4:1:3:100:5:201:7|" + new string('9', 64) },
            afterRestart with { Sha256 = new string('d', 64) },
            afterRestart with { Length = 4 },
            afterRestart with { FullPath = afterRestart.FullPath + ".other" },
        })
        {
            Assert.False(RemovalJournal.SameHeld(journaledLegacy, changed));
        }

        // Current-format evidence has no restart-unstable fields left, so it must match exactly: a swapped
        // directory (different ancestry) or a changed file is refused.
        Assert.False(RemovalJournal.SameHeld(current, current with { Identity = current.Identity.Split('|')[0] + "|" + new string('e', 64) }));
        Assert.False(RemovalJournal.SameHeld(current, current with { Identity = "l:12345:81a4:1:3:100:5:200:8|" + new string('b', 64) }));
        Assert.False(RemovalJournal.SameHeld(current, journaledLegacy));
    }

    [Fact]
    public void LegacyHeldRecordCanRollBackToCurrentFormatEvidence()
    {
        using var dir = new TempDirectory();
        var prepared = PreparedPurge(LinuxReady(dir.Path, legacy: true));
        var rolled = prepared with { Phase = RemovalPhase.ReplacementReady, Purge = null, Held = prepared.Held! with { Identity = "l:12345:81a4:1:3:100:5:300:1|" + new string('f', 64) } };
        RemovalJournal.ValidateSuccessor(prepared, rolled);
        Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(prepared, rolled with { Held = rolled.Held! with { Identity = "l:54321:81a4:1:3:100:5:300:1|" + new string('f', 64) } }));
    }

    [Fact]
    public void PreCommitRollbackIsAStrictSchemaOneSuccessorThatOnlyMayAdvanceCtime()
    {
        using var dir = new TempDirectory();
        var prepared = PreparedPurge(LinuxReady(dir.Path));
        var rolled = RolledBack(prepared, "300:9");
        Assert.NotEqual(prepared.Held, rolled.Held);
        RemovalJournal.ValidateSuccessor(prepared, rolled);
        var bytes = RemovalJournal.Encode(rolled, 6);
        Assert.Equal(1, JsonNode.Parse(bytes)!["schema"]!.GetValue<int>());
        Assert.Equal(rolled, RemovalJournal.Decode(bytes).Job);
        RemovalJournal.ValidateSuccessor(rolled, PreparedPurge(rolled));

        string Swap(int field, string value)
        {
            var parts = rolled.Held!.Identity.Split('|');
            var native = parts[0].Split(':');
            native[field] = value;
            return string.Join(':', native) + "|" + parts[1];
        }

        foreach (var bad in new[]
        {
            rolled with { Purge = prepared.Purge },
            rolled with { ContextJson = rolled.ContextJson.Replace("sharedItemId\":\"" + Guid.Empty, "sharedItemId\":\"" + Guid.NewGuid(), StringComparison.Ordinal) },
            rolled with { Held = rolled.Held! with { Sha256 = new string('d', 64) } },
            rolled with { Held = rolled.Held! with { Identity = Swap(1, "999") } },
            rolled with { Held = rolled.Held! with { Identity = Swap(5, "101") } },
            rolled with { Held = rolled.Held! with { Identity = Swap(3, "2") } },
            rolled with { Held = rolled.Held! with { Identity = prepared.Held!.Identity.Split('|')[0] + "|" + new string('e', 64) } },
            rolled with { Held = rolled.Held! with { Identity = rolled.Held.Identity.Split('|')[0] + "|short" } },
            rolled with { Held = rolled.Held! with { FullPath = rolled.Held.FullPath + ".other" } },
            rolled with { Title = "Other" },
            rolled with { Phase = RemovalPhase.Restored },
        })
        {
            Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(prepared, bad));
        }

        foreach (var phase in new[] { RemovalPhase.Purged })
        {
            Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(prepared with { Phase = phase }, rolled));
        }

        // 2.10.3 W20-5 (Pilot P30-KILL-STUCK: killed while PurgeCommitting, file intact at its stage name): the start-up sweep's return is bound exactly
        // like the pre-commit rollback, from PurgeCommitting too; every bad successor above is still refused from there.
        var committing = prepared with { Phase = RemovalPhase.PurgeCommitting };
        RemovalJournal.ValidateSuccessor(prepared, committing);
        RemovalJournal.ValidateSuccessor(committing, rolled);
        // W22-2: the same binding from PurgeOutcomeUnknown (an older Unknown, or a NoUnlink commit, whose file the next start proves intact).
        var unknown = committing with { Phase = RemovalPhase.PurgeOutcomeUnknown };
        RemovalJournal.ValidateSuccessor(committing, unknown);
        RemovalJournal.ValidateSuccessor(unknown, rolled);
        Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(unknown, unknown));
        foreach (var bad in new[] { rolled with { Purge = prepared.Purge }, rolled with { Held = rolled.Held! with { Sha256 = new string('d', 64) } }, rolled with { Title = "Other" }, rolled with { Phase = RemovalPhase.Restored } })
        {
            Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(committing, bad));
            Assert.Throws<IOException>(() => RemovalJournal.ValidateSuccessor(unknown, bad));
        }
    }

    [Fact]
    public void HashingDeadlineScalesBySizeWithinBounds()
    {
        Assert.Equal(RemovalPolicy.HashingDeadline, RemovalPolicy.HashingDeadlineFor(0));
        Assert.Equal(RemovalPolicy.HashingDeadline + TimeSpan.FromMinutes(32), RemovalPolicy.HashingDeadlineFor(RemovalPolicy.MaxFileSizeBytes + 1)); // 2.10.4 (Scout 04 C3): capped, no longer the base
        Assert.Equal(RemovalPolicy.HashingDeadline + TimeSpan.FromMinutes(1), RemovalPolicy.HashingDeadlineFor(1));
        Assert.Equal(RemovalPolicy.HashingDeadline + TimeSpan.FromMinutes(32), RemovalPolicy.HashingDeadlineFor(RemovalPolicy.MaxFileSizeBytes));
    }

    [LinuxFact]
    public async Task RolledBackJobKeepsOtherJobsReadableAndRestorable()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var rolledJob = LinuxReady(dir.Path);
        var other = LinuxReady(dir.Path);
        using (var journal = new RemovalJournal(dir.Path))
        {
            foreach (var job in new[] { rolledJob, other })
            {
                await journal.SaveAsync(job with { Phase = RemovalPhase.Prepared, Held = null }, default);
                await journal.SaveAsync(job with { Phase = RemovalPhase.Quarantined }, default);
                await journal.SaveAsync(job, default);
            }

            var prepared = PreparedPurge(rolledJob);
            await journal.SaveAsync(prepared, default);
            rolledJob = RolledBack(prepared, "400:2");
            await journal.SaveAsync(rolledJob, default);
        }

        using var reopened = new RemovalJournal(dir.Path);
        var jobs = await reopened.LoadAsync(default);
        Assert.Equal(2, jobs.Count);
        Assert.Contains(rolledJob, jobs);
        Assert.Contains(other, jobs);
        await reopened.SaveAsync(other with { Phase = RemovalPhase.Restored, Held = null }, default);
        await reopened.EnsureCanPurgeAsync(rolledJob.JobId, default);
    }

    [LinuxFact]
    public async Task RepeatedRollbacksStayBoundedAndKeepRestoreHeadroom()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ready = LinuxReady(dir.Path);
        using (var journal = new RemovalJournal(dir.Path))
        {
            await journal.SaveAsync(ready with { Phase = RemovalPhase.Prepared, Held = null }, default);
            await journal.SaveAsync(ready with { Phase = RemovalPhase.Quarantined }, default);
            await journal.SaveAsync(ready, default);
            var attempts = 0;
            while (true)
            {
                try
                {
                    await journal.EnsureCanPurgeAsync(ready.JobId, default);
                }
                catch (IOException error)
                {
                    Assert.Equal("removalJournalFull", error.Message);
                    break;
                }

                var prepared = PreparedPurge(ready);
                await journal.SaveAsync(prepared, default);
                ready = RolledBack(prepared, (300 + attempts) + ":1");
                await journal.SaveAsync(ready, default);
                attempts++;
            }

            Assert.Equal(28, attempts);
            Assert.Equal(59, Directory.GetFiles(dir.Path).Length);
        }

        using var reopened = new RemovalJournal(dir.Path);
        Assert.Equal(ready, Assert.Single(await reopened.LoadAsync(default)));
        var refreshed = ready with { ContextJson = ready.ContextJson.Replace("\"sharedAtRestore\":null", "\"sharedAtRestore\":[]", StringComparison.Ordinal) };
        await reopened.SaveAsync(refreshed, default);
        await reopened.SaveAsync(refreshed with { Phase = RemovalPhase.Restored, Held = null }, default);
        Assert.Equal(RemovalPhase.Restored, Assert.Single(await reopened.LoadAsync(default)).Phase);
    }

    [LinuxFact]
    public async Task NativeJournalRetainsIntentAndRefusesAuthorityMutationOrTerminalRewrite()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var journal = new RemovalJournal(dir.Path);
        var ready = Ready(dir.Path);
        await journal.SaveAsync(ready with { Phase = RemovalPhase.Prepared, Held = null }, default);
        await journal.SaveAsync(ready with { Phase = RemovalPhase.Quarantined }, default);
        await journal.SaveAsync(ready, default);
        await journal.EnsureCanPurgeAsync(ready.JobId, default);
        var prepared = PreparedPurge(ready);
        await journal.SaveAsync(prepared, default);
        await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(prepared with { Phase = RemovalPhase.PurgeCommitting, Purge = prepared.Purge! with { AdminId = Guid.NewGuid() } }, default));
        var committing = prepared with { Phase = RemovalPhase.PurgeCommitting };
        await journal.SaveAsync(committing, default);
        var purged = committing with { Phase = RemovalPhase.Purged };
        await journal.SaveAsync(purged, default);
        Assert.Equal(purged, Assert.Single(await journal.LoadAsync(default)));
        await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(purged with { Phase = RemovalPhase.Restored }, default));
        await Assert.ThrowsAsync<IOException>(() => journal.EnsureCanPurgeAsync(purged.JobId, default));
    }

    [LinuxFact]
    public async Task CapacityReservesCompletePurgeBeforeFirstMutation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDirectory();
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var journal = new RemovalJournal(dir.Path);
        var ready = Ready(dir.Path);
        await journal.SaveAsync(ready with { Phase = RemovalPhase.Prepared, Held = null }, default);
        await journal.SaveAsync(ready with { Phase = RemovalPhase.Quarantined }, default);
        await journal.SaveAsync(ready, default);
        for (var generation = 4; generation <= 61; generation++)
        {
            ready = ready with { ContextJson = ready.ContextJson.Replace(JsonDocument.Parse(ready.ContextJson).RootElement.GetProperty("sharedItemId").GetGuid().ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal) };
            await journal.SaveAsync(ready, default);
        }

        var error = await Assert.ThrowsAsync<IOException>(() => journal.EnsureCanPurgeAsync(ready.JobId, default));
        Assert.Equal("removalJournalFull", error.Message);
        await Assert.ThrowsAsync<IOException>(() => journal.SaveAsync(PreparedPurge(ready), default));
        Assert.Equal(RemovalPhase.ReplacementReady, Assert.Single(await journal.LoadAsync(default)).Phase);
        Assert.Equal(61, Directory.GetFiles(dir.Path).Length);
    }
}
