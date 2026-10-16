using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K5 / DESIGN 4.2a (v2.2): the pairing in progress is written on the peer record, survives a reopen of the store, holds no secret,
/// and a restart turns it into an interrupted abort from disk alone.
/// </summary>
public sealed class PendingPairingStateTests
{
    [Fact]
    public async Task ThePendingPairing_IsOnDisk_WithTheStatedNames_AndNoSecret()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        var started = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await store.UpdateAsync(state =>
            {
                StateSamples.Fill(state);
                state.Home.Peers[0].PendingPairing = new PairingProgress { Phase = PairingPhase.AwaitingConfirm, StartedUtc = started, StartedByUserId = StateSamples.AdminUser };
            });
        }

        var text = File.ReadAllText(paths.StateFile);
        using var document = JsonDocument.Parse(text);
        var pending = document.RootElement.GetProperty("home").GetProperty("peers")[0].GetProperty("pendingPairing");
        Assert.Equal("awaitingConfirm", pending.GetProperty("phase").GetString());
        Assert.Equal("invite", pending.GetProperty("method").GetString());
        Assert.False(pending.TryGetProperty("token", out _));
        Assert.False(pending.TryGetProperty("secret", out _));

        using var reopened = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(PairingPhase.AwaitingConfirm, reopened.Current.Home.Peers[0].PendingPairing!.Phase);
        Assert.Equal(started, reopened.Current.Home.Peers[0].PendingPairing!.StartedUtc);
    }

    [Theory]
    [InlineData(PairingPhase.BeforeApproval)] // the confirm phase is gone with the compare step (D17): a pairing is only ever before its approval while it waits
    public async Task AfterARealRestart_TheRecordOnDiskBecomesInterrupted_WithItsPhase(PairingPhase phase)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        using var side = new InviteHomeTests.PeerSide(peer);

        string peerKey;
        using (var first = InviteHomeTests.NewRig())
        {
            var (waiting, _) = await InviteHomeTests.BeginPendingAsync(first, peer, side);
            peerKey = waiting.PeerHandle;
            await first.Service.StopAsync(CancellationToken.None);
            await Task.Delay(100);
            File.Copy(first.Paths.StateFile, Path.Combine(Path.GetTempPath(), "shoal-state-" + peerKey + ".json"), overwrite: true);
        }

        // A new process: new stores over files that the old one left. Rebuild them from the copied state.
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        var copy = Path.Combine(Path.GetTempPath(), "shoal-state-" + peerKey + ".json");
        File.Copy(copy, paths.StateFile);
        File.Delete(copy);
        using var rig = new PairingRig();
        using var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(phase, store.Current.Home.Peers.Single().PendingPairing!.Phase);
        var health = new PeerHealthStore(paths, NullLogger.Instance);
        var registry = new PeerRegistry();
        using var service = new PairingService(store, rig.Secrets, rig.Client, registry, health, rig.Home, rig.Notifier, paths, NullLogger.Instance, TimeProvider.System, rig.Options);

        await service.StartAsync(CancellationToken.None);

        var abort = health.GetAbort(peerKey)!;
        Assert.Equal(PairingErrorKind.Interrupted, abort.Kind);
        Assert.Equal(phase, abort.Phase);
        Assert.Null(store.Current.Home.Peers.Single().PendingPairing);
        Assert.Contains("\"pendingPairing\": null", File.ReadAllText(paths.StateFile), StringComparison.Ordinal);
        Assert.Equal(PeerStatus.PairingAborted, registry.StatusOf(peerKey));
    }
}
