using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A secret store that lives in memory, for tests that are not about the secret store.
/// </summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, PeerCredential> _entries = new(StringComparer.Ordinal);

    public SecretStoreStatus Status { get; set; } = SecretStoreStatus.Ready;

    public SecretStoreInfo Info => new() { Status = Status };

    public IReadOnlyCollection<string>? PeerKeys => Status == SecretStoreStatus.Ready ? _entries.Keys : null;

    public PeerCredential? TryGet(string peerKey) => Status == SecretStoreStatus.Ready && _entries.TryGetValue(peerKey, out var c) ? c : null;

    public Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default)
    {
        _entries[peerKey] = credential;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(_entries.Remove(peerKey));

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>
/// K1: the spike's playback path reads the peer from the state store and the secret store, and fails closed.
/// </summary>
public sealed class PeerDirectoryTests
{
    private const string Token = "00112233445566778899aabbccddeeff";

    private static async Task<(StateStore State, MemorySecretStore Secrets, PeerDirectory Directory)> SetUp(TempDirectory dir)
    {
        var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Token, "shoal-1a2b3c4d5e6f-4b0c1d2e", DateTime.UtcNow, PairingMethod.Qc));
        return (state, secrets, new PeerDirectory(state, secrets));
    }

    [Fact]
    public async Task APairedEnabledPeer_IsFound_WithItsDeviceIdAndToken()
    {
        using var dir = new TempDirectory();
        var (state, _, directory) = await SetUp(dir);
        using var _ = state;

        var peer = directory.Find(StateSamples.PeerKeyA);

        Assert.NotNull(peer);
        Assert.Equal("shoal-1a2b3c4d5e6f-4b0c1d2e", peer.DeviceId);
        Assert.Equal(new Uri("http://hops.tail1234.ts.net:8096"), peer.BaseUri);
        Assert.Equal(Token, peer.Token);
        Assert.DoesNotContain(Token, peer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownDisabledOrCredentialLessPeers_AreNotFound()
    {
        using var dir = new TempDirectory();
        var (state, secrets, directory) = await SetUp(dir);
        using var _ = state;

        Assert.Null(directory.Find(StateSamples.PeerKeyB));
        Assert.Null(directory.Find(string.Empty));

        await state.UpdateAsync(s => s.Home.Peers[0].Enabled = false);
        Assert.Null(directory.Find(StateSamples.PeerKeyA));

        await state.UpdateAsync(s => s.Home.Peers[0].Enabled = true);
        Assert.NotNull(directory.Find(StateSamples.PeerKeyA));
        await secrets.RemoveAsync(StateSamples.PeerKeyA);
        Assert.Null(directory.Find(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task AnUnreadableSecretStore_RefusesEveryPeer()
    {
        using var dir = new TempDirectory();
        var (state, secrets, directory) = await SetUp(dir);
        using var _ = state;

        secrets.Status = SecretStoreStatus.Unreadable;

        Assert.Null(directory.Find(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task RecoveryMode_RefusesEveryPeer()
    {
        using var dir = new TempDirectory();
        var (state, secrets, _) = await SetUp(dir);
        state.Dispose();
        File.WriteAllText(new ShoalPaths(dir.Path).StateFile, "garbage");
        File.Delete(new ShoalPaths(dir.Path).StateBackup);
        using var recovering = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);

        Assert.Equal(StateStatus.Recovery, recovering.Status);
        Assert.Null(new PeerDirectory(recovering, secrets).Find(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task AnInvalidUrl_IsRefused()
    {
        using var dir = new TempDirectory();
        var (state, _, directory) = await SetUp(dir);
        using var _ = state;

        await state.UpdateAsync(s => s.Home.Peers[0].BaseUrl = "ftp://hops.example/");
        Assert.Null(directory.Find(StateSamples.PeerKeyA));

        await state.UpdateAsync(s => s.Home.Peers[0].BaseUrl = "not a url");
        Assert.Null(directory.Find(StateSamples.PeerKeyA));
    }

    [Fact]
    public void TheSecretStoreContract_NeverPrintsTheToken()
    {
        var credential = new PeerCredential(Token, "shoal-1a2b3c4d5e6f-4b0c1d2e", DateTime.UtcNow, PairingMethod.Invite);

        Assert.DoesNotContain(Token, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, System.Text.Json.JsonSerializer.Serialize(new { credential = credential.ToString() }), StringComparison.Ordinal);
    }
}
