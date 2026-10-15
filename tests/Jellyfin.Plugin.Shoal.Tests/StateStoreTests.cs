using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K1 acceptance (DESIGN 11.2): round trip, a crash in the middle of a write, corrupt files, recovery mode, newer schema,
/// file modes. T-REG-3 at unit level.
/// </summary>
public sealed class StateStoreTests
{
    private static ShoalPaths PathsOf(TempDirectory dir) => new(dir.Path);

    private static StateStore Open(TempDirectory dir, TimeProvider? time = null, Action<AtomicStage>? hook = null) =>
        new(PathsOf(dir), NullLogger.Instance, time ?? new ManualTimeProvider(), hook);

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    [Fact]
    public void NewInstall_IsReady_AndWritesNothingUntilTheFirstChange()
    {
        using var dir = new TempDirectory();

        using var store = Open(dir);

        Assert.Equal(StateStatus.Ready, store.Status);
        Assert.Equal(StateSource.Fresh, store.LoadInfo.Source);
        Assert.Empty(store.Current.Home.Peers);
        Assert.False(File.Exists(PathsOf(dir).StateFile));
    }

    [Fact]
    public async Task RoundTrip_AFullStateSurvivesARestart()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        using var reopened = Open(dir);

        Assert.Equal(StateStatus.Ready, reopened.Status);
        Assert.Equal(StateSource.Primary, reopened.LoadInfo.Source);
        var peer = Assert.Single(reopened.Current.Home.Peers);
        Assert.Equal("Hops", peer.DisplayName);
        Assert.Equal(PeerNetwork.Tailnet, peer.Network);
        Assert.Equal(PairingMethod.Qc, peer.Pairing!.Method);
        var access = Assert.Single(peer.Libraries).Access;
        Assert.Equal(AccessMode.Everyone, access.Mode);
        Assert.Equal([StateSamples.AdminUser, StateSamples.OtherUser], access.SelectedUserIds);
        Assert.True(access.IncludeRestricted);
        Assert.Equal(HardeningMode.Enforce, reopened.Current.PeerRole.HardeningMode);
        Assert.Equal("shoal-1a2b3c4d5e6f-9a8b7c6d", Assert.Single(reopened.Current.PeerRole.Shares).ExpectedDeviceIds[0]);
        Assert.Equal(1, reopened.Current.Generation);
        Assert.True(StateJson.Serialize(reopened.Current).AsSpan().SequenceEqual(Bytes(PathsOf(dir).StateFile)));
    }

    [Fact]
    public async Task FileFormat_MatchesTheDesignSample()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);

        using var json = JsonDocument.Parse(Bytes(PathsOf(dir).StateFile));
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        Assert.Equal(1, root.GetProperty("generation").GetInt64());
        Assert.Equal("2026-10-14T21:00:00Z", root.GetProperty("writtenUtc").GetString());
        var peer = root.GetProperty("home").GetProperty("peers")[0];
        Assert.Equal("Tailnet", peer.GetProperty("network").GetString());
        Assert.Equal("qc", peer.GetProperty("pairing").GetProperty("method").GetString());
        Assert.Equal("K7QM", peer.GetProperty("pairing").GetProperty("checkString").GetString());
        var access = peer.GetProperty("libraries")[0].GetProperty("access");
        Assert.Equal("Everyone", access.GetProperty("mode").GetString());
        Assert.Equal("11111111222233334444555555555555", access.GetProperty("selectedUserIds")[0].GetString());
        Assert.True(root.GetProperty("home").GetProperty("visibility").GetProperty("managedUsers").TryGetProperty("aaaaaaaabbbbccccddddeeeeeeeeeeee", out _));
        Assert.Equal("Enforce", root.GetProperty("peerRole").GetProperty("hardeningMode").GetString());
    }

    [Fact]
    public async Task EveryChange_IncrementsTheGeneration_AndNeverChangesAnEarlierSnapshot()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        var before = store.Current;

        var after = await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);

        Assert.Equal(HardeningMode.Enforce, before.PeerRole.HardeningMode);
        Assert.Equal(HardeningMode.LogOnly, after.PeerRole.HardeningMode);
        Assert.Same(after, store.Current);
        Assert.Equal(1, before.Generation);
        Assert.Equal(2, after.Generation);
    }

    [Theory]
    [InlineData("TempWritten")]
    [InlineData("BeforeReplace")]
    public async Task CrashInTheMiddleOfAWrite_KeepsTheOldState(string stageName)
    {
        var crashAt = Enum.Parse<AtomicStage>(stageName);
        using var dir = new TempDirectory();
        var crash = false;
        using var store = Open(dir, hook: stage =>
        {
            if (crash && stage == crashAt)
            {
                throw new SimulatedCrashException();
            }
        });
        await store.UpdateAsync(StateSamples.Fill);
        var before = Bytes(PathsOf(dir).StateFile);

        crash = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.Off));

        Assert.Equal(before, Bytes(PathsOf(dir).StateFile));
        Assert.Equal(HardeningMode.Enforce, store.Current.PeerRole.HardeningMode);
        Assert.NotEmpty(Directory.GetFiles(PathsOf(dir).Root, "state.json.tmp-*")); // a real crash leaves its temp file behind

        using var restarted = Open(dir);
        Assert.Equal(StateStatus.Ready, restarted.Status);
        Assert.Equal(StateSource.Primary, restarted.LoadInfo.Source);
        Assert.Equal(HardeningMode.Enforce, restarted.Current.PeerRole.HardeningMode);
        Assert.Equal(1, restarted.Current.Generation);
        Assert.Empty(Directory.GetFiles(PathsOf(dir).Root, "state.json.tmp-*")); // and the restart cleans it up
    }

    [Fact]
    public async Task APartlyWrittenTempFile_IsIgnored()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        File.WriteAllText(PathsOf(dir).StateFile + ".tmp-deadbeef0000", "{\"schema\":2,\"gener");

        using var restarted = Open(dir);

        Assert.Equal(StateSource.Primary, restarted.LoadInfo.Source);
        Assert.Equal(1, restarted.Current.Generation);
    }

    [Fact]
    public async Task CorruptStateJson_UsesTheBak_AndKeepsTheCorruptFileByteForByte()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
            await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly); // generation 2; the .bak holds generation 1
        }

        var garbage = Encoding.UTF8.GetBytes("{ this is not json \0\u0001 SECRET-GARBAGE");
        File.WriteAllBytes(PathsOf(dir).StateFile, garbage);

        using var restarted = Open(dir);

        Assert.Equal(StateStatus.Ready, restarted.Status);
        Assert.Equal(StateSource.Backup, restarted.LoadInfo.Source);
        Assert.Equal("state.json.bak", restarted.LoadInfo.RestoredFrom);
        Assert.Equal(1, restarted.Current.Generation);
        Assert.Equal(HardeningMode.Enforce, restarted.Current.PeerRole.HardeningMode);
        Assert.True(restarted.LoadInfo.AccessReviewRequired);
        var quarantined = Path.Combine(PathsOf(dir).Root, restarted.LoadInfo.QuarantinedAs!);
        Assert.Equal(garbage, Bytes(quarantined));
        Assert.StartsWith("state.json.corrupt-", restarted.LoadInfo.QuarantinedAs, StringComparison.Ordinal);
        Assert.Equal(1, JsonDocument.Parse(Bytes(PathsOf(dir).StateFile)).RootElement.GetProperty("generation").GetInt64()); // repaired

        var next = await restarted.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.Off);
        Assert.Equal(2, next.Generation);
    }

    [Fact]
    public async Task RestoringFromACopy_TightensAccess_AndAsksForAReview()
    {
        // Warden N-9 / T-REG-5: the copy may predate the last change (for example a library that was tightened from Everyone to
        // Selected), so a copy never brings back a wider state than the admin has confirmed.
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill); // Everyone, share enabled
            await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);
        }

        File.WriteAllText(PathsOf(dir).StateFile, "garbage");

        using var restarted = Open(dir);

        var access = restarted.Current.Home.Peers[0].Libraries[0].Access;
        Assert.Equal(AccessMode.Selected, access.Mode);
        Assert.Equal([StateSamples.AdminUser, StateSamples.OtherUser], access.SelectedUserIds); // the remembered selection is kept
        Assert.True(access.IncludeRestricted);
        Assert.False(Assert.Single(restarted.Current.PeerRole.Shares).Enabled);
        Assert.True(restarted.LoadInfo.AccessReviewRequired);
        Assert.Contains(restarted.LoadInfo.Notes, n => n.Contains("Only selected users", StringComparison.Ordinal) && n.Contains("share(s) paused", StringComparison.Ordinal));
        using var onDisk = JsonDocument.Parse(Bytes(PathsOf(dir).StateFile)); // and the repaired file says so too
        Assert.Equal("Selected", onDisk.RootElement.GetProperty("home").GetProperty("peers")[0].GetProperty("libraries")[0].GetProperty("access").GetProperty("mode").GetString());
        using var again = Open(dir);
        Assert.False(again.LoadInfo.AccessReviewRequired); // a later start reads the repaired primary and does not nag again
        Assert.Equal(AccessMode.Selected, again.Current.Home.Peers[0].Libraries[0].Access.Mode);
    }

    [Fact]
    public async Task ANormalStart_NeverChangesAccess()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        using var restarted = Open(dir);

        Assert.False(restarted.LoadInfo.AccessReviewRequired);
        Assert.Equal(AccessMode.Everyone, restarted.Current.Home.Peers[0].Libraries[0].Access.Mode);
        Assert.True(restarted.Current.PeerRole.Shares[0].Enabled);
    }

    [Fact]
    public async Task AFileSystemWithoutHardLinks_StillSavesAndKeepsABak()
    {
        // Warden N-9: File.Replace needs a hard link for the backup on Unix; some network mounts refuse it.
        using var dir = new TempDirectory();
        using var store = new StateStore(PathsOf(dir), NullLogger.Instance, new ManualTimeProvider(), null, (_, _, _) => throw new IOException("no hard links here"));
        await store.UpdateAsync(StateSamples.Fill);
        var first = Bytes(PathsOf(dir).StateFile);

        await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);

        Assert.Equal(2, store.Current.Generation);
        Assert.Equal(first, Bytes(PathsOf(dir).StateBackup));
        using var json = JsonDocument.Parse(Bytes(PathsOf(dir).StateFile));
        Assert.Equal(2, json.RootElement.GetProperty("generation").GetInt64());
        Assert.Empty(Directory.GetFiles(PathsOf(dir).Root, "*.tmp-*"));
    }

    [Fact]
    public async Task MissingStateJson_WithABak_IsRestoredWithoutQuarantine()
    {
        // What a crash between the two renames of File.Replace on Linux leaves behind.
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
            await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);
        }

        File.Delete(PathsOf(dir).StateFile);

        using var restarted = Open(dir);

        Assert.Equal(StateSource.Backup, restarted.LoadInfo.Source);
        Assert.Null(restarted.LoadInfo.QuarantinedAs);
        Assert.True(File.Exists(PathsOf(dir).StateFile));
    }

    [Fact]
    public async Task BothCorrupt_EntersRecoveryMode_AndLeavesTheFilesByteIdentical()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
            await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);
        }

        var paths = PathsOf(dir);
        File.WriteAllText(paths.StateFile, "garbage one");
        File.WriteAllText(paths.StateBackup, "{\"schema\":2,\"generation\":");
        var dailyBefore = Directory.GetFiles(paths.BackupsDir).ToDictionary(f => f, f => Bytes(f));

        using var restarted = Open(dir);

        Assert.Equal(StateStatus.Recovery, restarted.Status);
        Assert.Equal(RecoveryReason.Unreadable, restarted.LoadInfo.Recovery);
        Assert.Empty(restarted.Current.Home.Peers); // every peer counts as disabled
        Assert.Equal("garbage one", File.ReadAllText(paths.StateFile));
        Assert.Equal("{\"schema\":2,\"generation\":", File.ReadAllText(paths.StateBackup));
        Assert.Equal(dailyBefore.Keys.Order(), Directory.GetFiles(paths.BackupsDir).Order());
        Assert.Empty(Directory.GetFiles(paths.Root, "*.corrupt-*"));
        var recovery = await Assert.ThrowsAsync<StateRecoveryException>(() => restarted.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.Off));
        Assert.Equal(RecoveryReason.Unreadable, recovery.Reason);
        Assert.Equal("garbage one", File.ReadAllText(paths.StateFile)); // still not overwritten
    }

    [Fact]
    public async Task InRecovery_TheDailyCopiesAreOfferedButNeverUsedByThemselves()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        var paths = PathsOf(dir);
        File.WriteAllText(paths.StateFile, "garbage");
        File.Delete(paths.StateBackup);

        using var restarted = Open(dir);

        Assert.Equal(StateStatus.Recovery, restarted.Status);
        var daily = Assert.Single(restarted.LoadInfo.Candidates, c => c.FileName.StartsWith("backups/", StringComparison.Ordinal));
        Assert.True(daily.Usable);
        Assert.Equal("backups/state-20261014.json", daily.FileName);
    }

    [Fact]
    public async Task RestoreFromAFile_InRecovery_MakesTheStateUsableAgain_AndKeepsTheCorruptPrimary()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        var paths = PathsOf(dir);
        File.WriteAllText(paths.StateFile, "garbage");
        File.Delete(paths.StateBackup);
        using var restarted = Open(dir);
        Assert.Equal(StateStatus.Recovery, restarted.Status);
        StateChangedEventArgs? raised = null;
        restarted.Changed += (_, e) => raised = e;

        await restarted.RestoreFromAsync("backups/state-20261014.json");

        Assert.Equal(StateStatus.Ready, restarted.Status);
        Assert.Equal("Hops", Assert.Single(restarted.Current.Home.Peers).DisplayName);
        Assert.Equal(AccessMode.Selected, restarted.Current.Home.Peers[0].Libraries[0].Access.Mode);
        Assert.True(restarted.LoadInfo.AccessReviewRequired);
        Assert.Equal("backups/state-20261014.json", restarted.LoadInfo.RestoredFrom);
        Assert.Equal("garbage", File.ReadAllText(Path.Combine(paths.Root, restarted.LoadInfo.QuarantinedAs!)));
        Assert.NotNull(raised);
        var next = await restarted.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.Off);
        Assert.Equal(2, next.Generation);
    }

    [Theory]
    [InlineData("../state.json")]
    [InlineData("backups/../state.json.bak")]
    [InlineData("state.json")]
    [InlineData("backups/state-19990101.json")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("")]
    public async Task Restore_RefusesNamesThatAreNotUsableCandidates(string name)
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        var before = Bytes(PathsOf(dir).StateFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreFromAsync(name));

        Assert.Equal(before, Bytes(PathsOf(dir).StateFile));
    }

    [Fact]
    public async Task StartOver_MovesTheFilesAside_AndBeginsEmpty()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        var paths = PathsOf(dir);
        File.WriteAllText(paths.StateFile, "garbage primary");
        File.WriteAllText(paths.StateBackup, "garbage backup");
        File.Delete(Directory.GetFiles(paths.BackupsDir).Single());
        using var restarted = Open(dir);
        Assert.Equal(StateStatus.Recovery, restarted.Status);

        await restarted.StartOverAsync();

        Assert.Equal(StateStatus.Ready, restarted.Status);
        Assert.Empty(restarted.Current.Home.Peers);
        Assert.Equal(1, restarted.Current.Generation);
        var moved = Directory.GetFiles(paths.Root, "*.corrupt-*").Select(File.ReadAllText).Order().ToArray();
        Assert.Equal(["garbage backup", "garbage primary"], moved);
        using var again = Open(dir);
        Assert.Equal(StateStatus.Ready, again.Status);
    }

    [Fact]
    public async Task NewerSchema_IsRecoveryMode_NeverDowngraded_EvenWithAnOlderBak()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
            await store.UpdateAsync(s => s.PeerRole.HardeningMode = HardeningMode.LogOnly);
        }

        var paths = PathsOf(dir);
        var newer = Encoding.UTF8.GetBytes("{\"schema\":3,\"generation\":99,\"somethingNew\":{}}");
        File.WriteAllBytes(paths.StateFile, newer);
        var bak = Bytes(paths.StateBackup);

        using var restarted = Open(dir);

        Assert.Equal(StateStatus.Recovery, restarted.Status);
        Assert.Equal(RecoveryReason.NewerSchema, restarted.LoadInfo.Recovery);
        Assert.Equal(newer, Bytes(paths.StateFile));
        Assert.Equal(bak, Bytes(paths.StateBackup));
        Assert.Empty(Directory.GetFiles(paths.Root, "*.corrupt-*"));
        await Assert.ThrowsAsync<StateRecoveryException>(() => restarted.UpdateAsync(s => s.StubRoot = "/x"));
    }

    [Fact]
    public async Task OldSchema_IsNotUsed()
    {
        using var dir = new TempDirectory();
        using (var store = Open(dir))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        File.WriteAllText(PathsOf(dir).StateFile, "{\"schema\":1}");
        File.Delete(PathsOf(dir).StateBackup);

        using var restarted = Open(dir);

        Assert.Equal(StateStatus.Recovery, restarted.Status);
        Assert.Contains(restarted.LoadInfo.Candidates, c => c.FileName == "state.json" && c.Problem == "unsupported schema");
    }

    public static TheoryData<string, string> HostileDocuments() => new()
    {
        { "empty", string.Empty },
        { "null", "null" },
        { "array", "[]" },
        { "number schema as text", "{\"schema\":\"2\"}" },
        { "null section", "{\"schema\":2,\"home\":null}" },
        { "null list", "{\"schema\":2,\"home\":{\"peers\":null}}" },
        { "null element", "{\"schema\":2,\"home\":{\"peers\":[null]}}" },
        { "duplicate member", "{\"schema\":2,\"schema\":2}" },
        { "trailing comma", "{\"schema\":2,}" },
        { "comment", "{\"schema\":2 /* x */}" },
        { "number as enum", "{\"schema\":2,\"peerRole\":{\"hardeningMode\":1}}" },
        { "unknown enum name", "{\"schema\":2,\"peerRole\":{\"hardeningMode\":\"Maximum\"}}" },
        { "traversal in peer key", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"../../etc\"}]}}" },
        { "short peer key", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"abc\"}]}}" },
        { "upper case peer key", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"4B0C1D2E3F405162738495A6B7C8D9E0\"}]}}" },
        { "duplicate peer key", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"4b0c1d2e3f405162738495a6b7c8d9e0\"},{\"peerKey\":\"4b0c1d2e3f405162738495a6b7c8d9e0\"}]}}" },
        { "traversal in subfolder", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"4b0c1d2e3f405162738495a6b7c8d9e0\",\"libraries\":[{\"mappingKey\":\"a\",\"subfolder\":\"..\"}]}]}}" },
        { "separator in subfolder", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"4b0c1d2e3f405162738495a6b7c8d9e0\",\"libraries\":[{\"mappingKey\":\"a\",\"subfolder\":\"a/b\"}]}]}}" },
        { "duplicate mapping key", "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"4b0c1d2e3f405162738495a6b7c8d9e0\",\"libraries\":[{\"mappingKey\":\"a\",\"subfolder\":\"x\"},{\"mappingKey\":\"a\",\"subfolder\":\"y\"}]}]}}" },
        { "bad id", "{\"schema\":2,\"peerRole\":{\"shares\":[{\"shareId\":\"a\",\"shareUserId\":\"not-a-guid\"}]}}" },
        { "nesting 5000 deep", "{\"schema\":2,\"x\":" + new string('[', 5000) + new string(']', 5000) + "}" },
    };

    [Theory]
    [MemberData(nameof(HostileDocuments))]
    public async Task HostileStateDocuments_AreUnreadable_AndNeverOverwritten(string label, string document)
    {
        _ = label;
        using var dir = new TempDirectory();
        var paths = PathsOf(dir);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, document);

        using var store = Open(dir);

        Assert.Equal(StateStatus.Recovery, store.Status);
        Assert.Equal(document, File.ReadAllText(paths.StateFile));
        await Assert.ThrowsAsync<StateRecoveryException>(() => store.UpdateAsync(s => s.StubRoot = "x"));
        Assert.Equal(document, File.ReadAllText(paths.StateFile));
    }

    [Fact]
    public void AHugeStateFile_IsUnreadable()
    {
        using var dir = new TempDirectory();
        var paths = PathsOf(dir);
        Directory.CreateDirectory(paths.Root);
        using (var stream = File.Create(paths.StateFile))
        {
            stream.SetLength(StateJson.MaxBytes + 1);
        }

        using var store = Open(dir);

        Assert.Equal(StateStatus.Recovery, store.Status);
        Assert.Contains(store.LoadInfo.Candidates, c => c.FileName == "state.json" && c.Problem == "file is too large");
    }

    [Fact]
    public void CandidateProblems_NeverRepeatFileContent()
    {
        using var dir = new TempDirectory();
        var paths = PathsOf(dir);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, "{\"schema\":2,\"home\":{\"peers\":[{\"peerKey\":\"SECRET-TOKEN-IN-A-BAD-KEY\"}]}}");

        using var store = Open(dir);

        Assert.All(store.LoadInfo.Candidates, c => Assert.DoesNotContain("SECRET", c.Problem ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMalformedSecuritySetting_DisablesThePeer_InsteadOfDroppingTheSetting()
    {
        using var dir = new TempDirectory();
        using (var seed = Open(dir))
        {
            await seed.UpdateAsync(StateSamples.Fill);
        }

        var paths = PathsOf(dir);
        var text = File.ReadAllText(paths.StateFile).Replace("0123456789abcdef0123456789abcdef", "NOT-A-SERVER-ID", StringComparison.Ordinal);
        File.WriteAllText(paths.StateFile, text);

        using var store = Open(dir);

        Assert.Equal(StateStatus.Ready, store.Status);
        var peer = Assert.Single(store.Current.Home.Peers);
        Assert.False(peer.Enabled);
        Assert.Equal("NOT-A-SERVER-ID", peer.PinnedServerId); // the setting is kept as the admin left it
        Assert.Contains(store.LoadInfo.Notes, n => n.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutOfRangeNumbers_AreClamped_AndTextIsCleaned()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);

        await store.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.Home.Peers[0].MaxConcurrentStreams = 5000;
            s.Home.Peers[0].Priority = -5;
            s.Home.Peers[0].DisplayName = "  Ho\u0000ps\u202E\n  ";
            s.PeerRole.Shares[0].MaxConcurrentTranscodes = 0;
        });

        var peer = store.Current.Home.Peers[0];
        Assert.Equal(16, peer.MaxConcurrentStreams);
        Assert.Equal(0, peer.Priority);
        Assert.Equal("Hops", peer.DisplayName);
        Assert.Equal(1, store.Current.PeerRole.Shares[0].MaxConcurrentTranscodes);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("ABCDEF0123456789")]
    [InlineData("zz0123456789abcd")]
    public async Task AChangeWithAnInvalidPeerKey_IsRefused_AndNothingIsWritten(string peerKey)
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        var before = Bytes(PathsOf(dir).StateFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(s => s.Home.Peers.Add(new PeerState { PeerKey = peerKey })));

        Assert.Equal(before, Bytes(PathsOf(dir).StateFile));
        Assert.Single(store.Current.Home.Peers);
        Assert.Equal(1, store.Current.Generation);
    }

    [Fact]
    public async Task AMutationThatThrows_ChangesNothing()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        var before = Bytes(PathsOf(dir).StateFile);

        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(s =>
        {
            s.PeerRole.HardeningMode = HardeningMode.Off;
            throw new FormatException();
        }));

        Assert.Equal(before, Bytes(PathsOf(dir).StateFile));
        Assert.Equal(HardeningMode.Enforce, store.Current.PeerRole.HardeningMode);
    }

    [Fact]
    public async Task ChangedEvent_IsRaisedAfterTheWrite_AndAFailingHandlerCannotBreakTheWriter()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        var seen = new List<long>();
        store.Changed += (_, _) => throw new InvalidOperationException("subscriber bug");
        store.Changed += (_, e) => seen.Add(e.State.Generation);

        await store.UpdateAsync(StateSamples.Fill);

        // The first handler throws; the writer does not see it and the second handler is still called.
        Assert.Equal(1, store.Current.Generation);
        Assert.Equal([1L], seen);
    }

    [Fact]
    public async Task ParallelChanges_AreSerialized_AndAllOfThemLand()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => store.UpdateAsync(s => s.Home.Peers.Add(new PeerState
        {
            PeerKey = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
            DisplayName = "P" + i,
            Enabled = false
        }))));

        Assert.Equal(40, store.Current.Home.Peers.Count);
        Assert.Equal(40, store.Current.Generation);
        using var reopened = Open(dir);
        Assert.Equal(40, reopened.Current.Home.Peers.Count);
        Assert.Equal(40, reopened.Current.Generation);
    }

    [Fact]
    public async Task DailyCopy_IsWrittenOncePerUtcDay_AndOnlyTheNewestSevenAreKept()
    {
        using var dir = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        using var store = Open(dir, clock);
        var paths = PathsOf(dir);

        await store.UpdateAsync(s => s.StubRoot = "first");
        var firstOfDay = Bytes(Path.Combine(paths.BackupsDir, "state-20261001.json"));
        clock.Advance(TimeSpan.FromHours(2));
        await store.UpdateAsync(s => s.StubRoot = "second");

        Assert.Equal(firstOfDay, Bytes(Path.Combine(paths.BackupsDir, "state-20261001.json"))); // the first write of the day stays
        Assert.Single(Directory.GetFiles(paths.BackupsDir));

        for (var day = 2; day <= 10; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            await store.UpdateAsync(s => s.StubRoot = "day" + day);
        }

        var kept = Directory.GetFiles(paths.BackupsDir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(7, kept.Length);
        Assert.Equal("state-20261004.json", kept[0]);
        Assert.Equal("state-20261010.json", kept[^1]);
    }

    [Fact]
    public async Task NoSecretEverReachesStateJson_OrItsCopies()
    {
        // Peers hold no token: the secret store is separate (DESIGN 3.2a). The model has no field for one, so a hostile
        // document that carries one is simply not read into the model, and a rewrite drops it.
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        var paths = PathsOf(dir);
        var text = File.ReadAllText(paths.StateFile).Replace("\"displayName\"", "\"token\": \"SEKRIT-TOKEN-VALUE\", \"displayName\"", StringComparison.Ordinal);
        File.WriteAllText(paths.StateFile, text);
        using var reopened = Open(dir);

        await reopened.UpdateAsync(s => s.StubRoot = "x");

        foreach (var file in Directory.GetFiles(paths.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".corrupt-", StringComparison.Ordinal) && !f.EndsWith(".bak", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("SEKRIT", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFileWhereTheDataFolderShouldBe_IsRecoveryMode_NotACrash()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "shoal"), "I am a file");

        using var store = Open(dir);

        Assert.Equal(StateStatus.Recovery, store.Status);
        Assert.Equal(RecoveryReason.StorageUnavailable, store.LoadInfo.Recovery);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task OnLinux_FoldersAre0700_AndFilesAre0600()
    {
        using var dir = new TempDirectory();
        using var store = Open(dir);
        await store.UpdateAsync(StateSamples.Fill);
        await store.UpdateAsync(s => s.StubRoot = "y");
        var paths = PathsOf(dir);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.Root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.BackupsDir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.StateFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.StateBackup));
        Assert.All(Directory.GetFiles(paths.BackupsDir), f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f)));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void OnLinux_ADirectoryThatWasTooOpen_IsTightenedTo0700()
    {
        using var dir = new TempDirectory();
        var paths = PathsOf(dir);
        Directory.CreateDirectory(paths.Root);
        File.SetUnixFileMode(paths.Root, (UnixFileMode)0b111_111_111);

        using var store = Open(dir);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.Root));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void OnLinux_TheDirectoryCanBeFlushed()
    {
        using var dir = new TempDirectory();

        Assert.True(NativeFsync.SyncDirectory(dir.Path));
    }
}
