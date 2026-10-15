using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K2.1 (Warden review 03, pre-K5 items 2 and 4; DESIGN 4.4, 8.2, T47): a change of peer host drops the stored token first and is
/// refused while the secret store is unreadable.
/// </summary>
public sealed class PeerAddressServiceTests
{
    private const string Token = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";
    private const string Device = "shoal-1a2b3c4d5e6f-4b0c1d2e";

    private static PeerCredential Credential() => new(Token, Device, DateTime.UtcNow, PairingMethod.Qc);

    [Fact]
    public async Task AHostChange_DropsTheTokenBeforeTheNewAddressIsWritten()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var spy = new OrderSpy(state);
        await spy.PutAsync(StateSamples.PeerKeyA, Credential());
        var service = new PeerAddressService(state, spy);

        var dropped = await service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "http://evil.tail1234.ts.net:8096");

        Assert.True(dropped);
        Assert.Equal("http://hops.tail1234.ts.net:8096", spy.BaseUrlWhenRemoved);
        Assert.Equal("http://evil.tail1234.ts.net:8096", state.Current.Home.Peers[0].BaseUrl);
        Assert.Null(spy.TryGet(StateSamples.PeerKeyA));
    }

    [Theory]
    [InlineData("https://hops.tail1234.ts.net:8920")]
    [InlineData("http://HOPS.tail1234.ts.net:8096/jellyfin")]
    [InlineData("http://hops.tail1234.ts.net")]
    public async Task ASchemePortOrPathChange_KeepsTheToken(string newUrl)
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, Credential());
        var service = new PeerAddressService(state, secrets);

        var dropped = await service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, newUrl);

        Assert.False(dropped);
        Assert.Equal(newUrl.ToLowerInvariant(), state.Current.Home.Peers[0].BaseUrl);
        Assert.NotNull(secrets.TryGet(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task AHostChange_IsRefusedWhileTheSecretStoreIsUnreadable_AndNothingChanges()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using var state = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        using var secrets = new SecretStore(paths, state, NullLogger.Instance, name => name == SecretStore.KeyVariable ? "hunter2" : null, null, null, null);
        Assert.Equal(SecretStoreStatus.Unreadable, secrets.Status);
        var service = new PeerAddressService(state, secrets);

        var failure = await Assert.ThrowsAsync<PeerAddressChangeException>(() => service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "http://other.tail1234.ts.net:8096"));

        Assert.Equal(PeerAddressChangeProblem.SecretsUnreadable, failure.Problem);
        Assert.Equal("http://hops.tail1234.ts.net:8096", state.Current.Home.Peers[0].BaseUrl);
        Assert.DoesNotContain("other.tail1234", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortChange_IsStillAllowedWhileTheSecretStoreIsUnreadable()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using var state = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        using var secrets = new SecretStore(paths, state, NullLogger.Instance, name => name == SecretStore.KeyVariable ? "hunter2" : null, null, null, null);
        var service = new PeerAddressService(state, secrets);

        Assert.False(await service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "http://hops.tail1234.ts.net:8097"));

        Assert.Equal("http://hops.tail1234.ts.net:8097", state.Current.Home.Peers[0].BaseUrl);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096", AddressProblem.Blocked)]
    [InlineData("http://2130706433", AddressProblem.NonCanonicalIp)]
    [InlineData("http://example.com", AddressProblem.WrongNetwork)]
    [InlineData("http://user:pw@hops.tail1234.ts.net", AddressProblem.UserInfo)]
    [InlineData("http://hops.tail1234.ts.net/?x=1", AddressProblem.Query)]
    public async Task TheNewAddressMustPassTheAddressPolicy_OrNothingChanges(string newUrl, AddressProblem expected)
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, Credential());
        var service = new PeerAddressService(state, secrets);

        var failure = await Assert.ThrowsAsync<PeerAddressChangeException>(() => service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, newUrl));

        Assert.Equal(PeerAddressChangeProblem.InvalidAddress, failure.Problem);
        Assert.Equal(expected, failure.Detail);
        Assert.Equal("http://hops.tail1234.ts.net:8096", state.Current.Home.Peers[0].BaseUrl);
        Assert.NotNull(secrets.TryGet(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task AHostChange_PutsThePeerInAuthFailed_SoTheAdminSeesRePair()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var secrets = new MemorySecretStore();
        await secrets.PutAsync(StateSamples.PeerKeyA, Credential());
        var registry = new PeerRegistry();
        registry.SetStatus(StateSamples.PeerKeyA, PeerStatus.Online);
        var service = new PeerAddressService(state, secrets, registry);

        await service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "http://hops.tail1234.ts.net:8097");
        Assert.Equal(PeerStatus.Online, registry.StatusOf(StateSamples.PeerKeyA));

        await service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "http://other.tail1234.ts.net:8097");
        Assert.Equal(PeerStatus.AuthFailed, registry.StatusOf(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task UnknownPeersAndBadAddresses_AreRefused()
    {
        using var dir = new TempDirectory();
        using var state = new StateStore(new ShoalPaths(dir.Path), NullLogger.Instance, TimeProvider.System, null);
        await state.UpdateAsync(StateSamples.Fill);
        var service = new PeerAddressService(state, new MemorySecretStore());

        Assert.Equal(PeerAddressChangeProblem.UnknownPeer, (await Assert.ThrowsAsync<PeerAddressChangeException>(() => service.ChangeBaseUrlAsync(StateSamples.PeerKeyB, "http://x.ts.net"))).Problem);
        Assert.Equal(PeerAddressChangeProblem.InvalidAddress, (await Assert.ThrowsAsync<PeerAddressChangeException>(() => service.ChangeBaseUrlAsync(StateSamples.PeerKeyA, "not a url"))).Problem);
        Assert.Equal("http://hops.tail1234.ts.net:8096", state.Current.Home.Peers[0].BaseUrl);
    }

    [Theory]
    [InlineData("http://a.example/x", "https://A.EXAMPLE:8920/y", true)]
    [InlineData("http://a.example", "http://b.example", false)]
    [InlineData("http://100.64.0.1", "http://100.64.0.2", false)]
    [InlineData("http://[::1]:8096", "https://[::1]", true)]
    [InlineData("nope", "http://a.example", false)]
    public void SameHost_ComparesOnlyTheHost(string first, string second, bool expected)
    {
        Assert.Equal(expected, PeerAddressService.SameHost(first, second));
    }

    // Remembers what the state said when the token was dropped.
    private sealed class OrderSpy : ISecretStore
    {
        private readonly IStateStore _state;
        private readonly MemorySecretStore _inner = new();

        public OrderSpy(IStateStore state) => _state = state;

        public string? BaseUrlWhenRemoved { get; private set; }

        public SecretStoreStatus Status => _inner.Status;

        public SecretStoreInfo Info => _inner.Info;

        public IReadOnlyCollection<string>? PeerKeys => _inner.PeerKeys;

        public PeerCredential? TryGet(string peerKey) => _inner.TryGet(peerKey);

        public Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default) => _inner.PutAsync(peerKey, credential, cancellationToken);

        public Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default)
        {
            BaseUrlWhenRemoved = _state.Current.Home.Peers.First(p => p.PeerKey == peerKey).BaseUrl;
            return _inner.RemoveAsync(peerKey, cancellationToken);
        }

        public Task ResetAsync(CancellationToken cancellationToken = default) => _inner.ResetAsync(cancellationToken);
    }
}
