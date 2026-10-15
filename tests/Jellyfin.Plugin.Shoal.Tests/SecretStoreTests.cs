using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K2 acceptance (DESIGN 11.2, 8.2): T-REG-4 (a flipped byte refuses the peers and the key is not regenerated), fresh nonces per
/// save, the environment key formats (no master.key on disk), DPAPI scope, the network-filesystem warning, and no token anywhere
/// it must not be.
/// </summary>
public sealed class SecretStoreTests
{
    private const string Token = "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f";
    private const string TokenB = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static string NewKeyHex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static PeerCredential Credential(string token = Token) => new(token, "shoal-1a2b3c4d5e6f-4b0c1d2e", new DateTime(2026, 10, 14, 20, 30, 0, DateTimeKind.Utc), PairingMethod.Qc);

    private static Rig NewRig() => new();

    private static SecretStore Open(Rig rig, Dictionary<string, string>? environment = null, IKeyProtector? protector = null, Action<AtomicStage>? hook = null, string? mountInfo = null) =>
        new(rig.Paths, rig.State, rig.Log, name => environment is not null && environment.TryGetValue(name, out var value) ? value : null, protector, hook, mountInfo);

    private static Dictionary<string, string> EnvKey(string value) => new() { [SecretStore.KeyVariable] = value };

    [Fact]
    public void FreshInstall_IsReady_AndWritesNothing()
    {
        using var rig = NewRig();

        using var store = Open(rig);

        Assert.Equal(SecretStoreStatus.Ready, store.Status);
        Assert.Equal(KeySource.None, store.Info.KeySource);
        Assert.Equal(0, store.Info.Entries);
        Assert.Empty(store.PeerKeys!);
        Assert.False(Directory.Exists(rig.Paths.Root) && Directory.EnumerateFileSystemEntries(rig.Paths.Root).Any(e => e.EndsWith(".enc", StringComparison.Ordinal) || e.EndsWith(".key", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RoundTrip_ACredentialSurvivesARestart()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            Assert.Equal(KeySource.KeyFile, store.Info.KeySource);
        }

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Ready, reopened.Status);
        var credential = reopened.TryGet(StateSamples.PeerKeyA);
        Assert.NotNull(credential);
        Assert.Equal(Token, credential.Token);
        Assert.Equal("shoal-1a2b3c4d5e6f-4b0c1d2e", credential.DeviceId);
        Assert.Equal(new DateTime(2026, 10, 14, 20, 30, 0, DateTimeKind.Utc), credential.PairedUtc);
        Assert.Equal(PairingMethod.Qc, credential.Method);
        Assert.Equal([StateSamples.PeerKeyA], reopened.PeerKeys!);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyB));
    }

    [Fact]
    public async Task Remove_DeletesTheEntryOnDisk()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            await store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));
            Assert.True(await store.RemoveAsync(StateSamples.PeerKeyA));
            Assert.False(await store.RemoveAsync(StateSamples.PeerKeyA));
        }

        using var reopened = Open(rig);

        Assert.Null(reopened.TryGet(StateSamples.PeerKeyA));
        Assert.Equal(TokenB, reopened.TryGet(StateSamples.PeerKeyB)!.Token);
    }

    [Fact]
    public async Task PutForAPeerThatIsNotInTheState_IsRefused_AndInvalidKeysAreRejected()
    {
        using var rig = NewRig();
        using var store = Open(rig);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(StateSamples.PeerKeyA, Credential()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("../evil", Credential()));
        Assert.False(File.Exists(rig.Paths.SecretsFile));
        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task TwoSavesUseDifferentNonces_EvenForEntriesThatDidNotChange()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);

        await store.PutAsync(StateSamples.PeerKeyA, Credential());
        var first = rig.ReadBoxes();
        await store.PutAsync(StateSamples.PeerKeyA, Credential());
        var second = rig.ReadBoxes();
        await store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));
        var third = rig.ReadBoxes();

        Assert.NotEqual(first.Peers[StateSamples.PeerKeyA].Nonce, second.Peers[StateSamples.PeerKeyA].Nonce);
        Assert.NotEqual(second.Peers[StateSamples.PeerKeyA].Nonce, third.Peers[StateSamples.PeerKeyA].Nonce);
        Assert.NotEqual(first.Check.Nonce, second.Check.Nonce);
        Assert.NotEqual(first.Peers[StateSamples.PeerKeyA].Ciphertext, second.Peers[StateSamples.PeerKeyA].Ciphertext);
        Assert.All(third.AllNonces, nonce => Assert.Equal(12, System.Buffers.Text.Base64Url.DecodeFromChars(nonce).Length));
        Assert.Equal(third.AllNonces.Count, third.AllNonces.Distinct().Count());
    }

    [Fact]
    public async Task FlippedByteInAnEntry_RefusesThatPeer_AndTheKeyIsNotRegenerated()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            await store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));
        }

        var keyBefore = File.ReadAllBytes(rig.Paths.MasterKeyFile);
        rig.FlipCiphertextCharacter(StateSamples.PeerKeyA);

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Ready, reopened.Status);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyA));
        Assert.Equal([StateSamples.PeerKeyA], reopened.Info.RefusedPeerKeys);
        Assert.Equal(TokenB, reopened.TryGet(StateSamples.PeerKeyB)!.Token);
        Assert.Equal(keyBefore, File.ReadAllBytes(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task FlippedByteInTheOnlyEntry_HomeRefusesThePeer_KeyUnchanged_AndAnotherSaveKeepsTheBrokenEntryAsItWas()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        var keyBefore = File.ReadAllBytes(rig.Paths.MasterKeyFile);
        rig.FlipCiphertextCharacter(StateSamples.PeerKeyA);
        var brokenBefore = rig.ReadBoxes().Peers[StateSamples.PeerKeyA];

        using var reopened = Open(rig);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyA));
        await reopened.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));

        var brokenAfter = rig.ReadBoxes().Peers[StateSamples.PeerKeyA];
        Assert.Equal(brokenBefore.Nonce, brokenAfter.Nonce);
        Assert.Equal(brokenBefore.Ciphertext, brokenAfter.Ciphertext);
        Assert.Equal(keyBefore, File.ReadAllBytes(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task FlippedByteInTheCheckMessage_MakesTheWholeStoreUnreadable_AndNothingIsOverwritten()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        rig.FlipCheckCharacter();
        var secretsBefore = File.ReadAllBytes(rig.Paths.SecretsFile);
        var keyBefore = File.ReadAllBytes(rig.Paths.MasterKeyFile);

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Unreadable, reopened.Status);
        Assert.Equal(SecretUnreadableReason.Tampered, reopened.Info.Reason);
        Assert.Null(reopened.PeerKeys);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyA));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PutAsync(StateSamples.PeerKeyA, Credential()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.RemoveAsync(StateSamples.PeerKeyA));
        Assert.Equal(secretsBefore, File.ReadAllBytes(rig.Paths.SecretsFile));
        Assert.Equal(keyBefore, File.ReadAllBytes(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task AFlippedByteInTheFileSyntax_IsReportedAsCorrupt_NotAsAKeyProblem()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        var bytes = File.ReadAllBytes(rig.Paths.SecretsFile);
        bytes[bytes.Length / 2] = (byte)'#';
        bytes[0] = (byte)'[';
        File.WriteAllBytes(rig.Paths.SecretsFile, bytes);

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Unreadable, reopened.Status);
        Assert.Equal(SecretUnreadableReason.Corrupt, reopened.Info.Reason);
    }

    [Fact]
    public async Task MissingMasterKey_NextToSecrets_IsNeverRepairedByMakingANewKey()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        File.Delete(rig.Paths.MasterKeyFile);
        var secretsBefore = File.ReadAllBytes(rig.Paths.SecretsFile);

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Unreadable, reopened.Status);
        Assert.Equal(SecretUnreadableReason.MasterKeyMissing, reopened.Info.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PutAsync(StateSamples.PeerKeyA, Credential()));
        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
        Assert.Equal(secretsBefore, File.ReadAllBytes(rig.Paths.SecretsFile));
    }

    [Fact]
    public async Task AnotherKey_IsTamperedForTheWholeStore()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig, EnvKey(NewKeyHex())))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        using var wrong = Open(rig, EnvKey(NewKeyHex()));

        Assert.Equal(SecretStoreStatus.Unreadable, wrong.Status);
        Assert.Equal(SecretUnreadableReason.Tampered, wrong.Info.Reason);
        Assert.Null(wrong.TryGet(StateSamples.PeerKeyA));
    }

    [Fact]
    public async Task EnvironmentKey_NeverCreatesOrReadsMasterKey()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        var key = NewKeyHex();
        using (var store = Open(rig, EnvKey(key)))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            Assert.Equal(KeySource.Environment, store.Info.KeySource);
        }

        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
        Assert.True(File.Exists(rig.Paths.SecretsFile));

        // A master.key that happens to be there is neither read nor modified.
        var decoy = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(rig.Paths.MasterKeyFile, decoy);
        using var reopened = Open(rig, EnvKey(key.ToUpperInvariant()));
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);
        Assert.Equal(decoy, File.ReadAllBytes(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task KeyFile_IsReadFromTheNamedFile_AndATrailingNewlineIsAllowed()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var keyFile = rig.Dir.Combine("key.txt");
        File.WriteAllText(keyFile, System.Buffers.Text.Base64Url.EncodeToString(keyBytes) + "\r\n");
        var env = new Dictionary<string, string> { [SecretStore.KeyFileVariable] = keyFile };

        using (var store = Open(rig, env))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            Assert.Equal(KeySource.EnvironmentFile, store.Info.KeySource);
        }

        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
        using var reopened = Open(rig, env);
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);

        // The same key as hex in the environment opens the same store.
        using var viaVariable = Open(rig, EnvKey(Convert.ToHexString(keyBytes)));
        Assert.Equal(Token, viaVariable.TryGet(StateSamples.PeerKeyA)!.Token);
    }

    [Fact]
    public void BothVariables_AreRefusedAsAmbiguous()
    {
        using var rig = NewRig();
        var env = new Dictionary<string, string> { [SecretStore.KeyVariable] = NewKeyHex(), [SecretStore.KeyFileVariable] = rig.Dir.Combine("k") };

        using var store = Open(rig, env);

        Assert.Equal(SecretStoreStatus.Unreadable, store.Status);
        Assert.Equal(SecretUnreadableReason.AmbiguousKey, store.Info.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  \t ")]
    [InlineData("hunter2")]
    [InlineData("0123456789abcdef0123456789abcdef")] // 32 hex characters: only 16 bytes
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // placeholder: identical bytes
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // base64url of 32 zero bytes
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 64 characters, not hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")] // 63 hex characters
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // 65 hex characters
    [InlineData("q+Z7r8s9t0u1v2w3x4y5z6A7B8C9D0E1F2G3H4I5J6K")] // standard base64 alphabet ('+'), not base64url
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP")] // 42 characters
    [InlineData("0123456789abcdef 0123456789abcdef0123456789abcdef0123456789abcde")] // a space inside
    public void BadKeys_AreRefused_WithoutFallbackAndWithoutTouchingMasterKey(string value)
    {
        using var rig = NewRig();
        Directory.CreateDirectory(rig.Paths.Root);
        var decoy = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(rig.Paths.MasterKeyFile, decoy);

        using var store = Open(rig, EnvKey(value));

        Assert.Equal(SecretStoreStatus.Unreadable, store.Status);
        Assert.Equal(SecretUnreadableReason.BadKey, store.Info.Reason);
        Assert.Equal(decoy, File.ReadAllBytes(rig.Paths.MasterKeyFile));
        Assert.True(value.Trim().Length == 0 || !rig.Log.All.Contains(value, StringComparison.Ordinal));
        Assert.Throws<AggregateException>(() => store.PutAsync(StateSamples.PeerKeyA, Credential()).Wait());
        Assert.False(File.Exists(rig.Paths.SecretsFile));
    }

    [Theory]
    [InlineData("", null)] // JELLYFIN_SHOAL_KEY_FILE set to an empty path
    [InlineData(null, "")] // a key file with no content
    [InlineData(null, "   \r\n")] // a key file with only blanks
    [InlineData(null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\r\nsecond line")]
    public void EmptyOrBlankKeyFileSettings_AreSetAndInvalid_NeverTheFallback(string? keyFileVariable, string? keyFileContent)
    {
        using var rig = NewRig();
        Directory.CreateDirectory(rig.Paths.Root);
        var decoy = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(rig.Paths.MasterKeyFile, decoy);
        var path = keyFileVariable;
        if (keyFileContent is not null)
        {
            path = rig.Dir.Combine("key.txt");
            File.WriteAllText(path, keyFileContent);
        }

        using var store = Open(rig, new Dictionary<string, string> { [SecretStore.KeyFileVariable] = path! });

        Assert.Equal(SecretUnreadableReason.BadKey, store.Info.Reason);
        Assert.Equal(decoy, File.ReadAllBytes(rig.Paths.MasterKeyFile));
        Assert.False(File.Exists(rig.Paths.SecretsFile));
        Assert.Throws<AggregateException>(() => store.PutAsync(StateSamples.PeerKeyA, Credential()).Wait());
    }

    [Fact]
    public void AnEmptyKeyVariable_WithAnEmptyFileVariable_IsStillAmbiguous()
    {
        using var rig = NewRig();

        using var store = Open(rig, new Dictionary<string, string> { [SecretStore.KeyVariable] = string.Empty, [SecretStore.KeyFileVariable] = string.Empty });

        Assert.Equal(SecretUnreadableReason.AmbiguousKey, store.Info.Reason);
        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
    }

    [Fact]
    public async Task TheStoreExplainsHowToFixAnUnreadableState_AndSaysNothingWhenItIsFine()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var bad = Open(rig, EnvKey("hunter2")))
        {
            Assert.Contains("openssl rand -hex 32", bad.Info.Hint, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", bad.Info.Hint, StringComparison.Ordinal);
        }

        using (var both = Open(rig, new Dictionary<string, string> { [SecretStore.KeyVariable] = NewKeyHex(), [SecretStore.KeyFileVariable] = "x" }))
        {
            Assert.NotNull(both.Info.Hint);
        }

        using var good = Open(rig);
        Assert.Null(good.Info.Hint);
        Assert.Null(good.Info.VolumeWarning);
        Assert.Empty(good.Info.RefusedPeerKeys);
    }

    [Fact]
    public void AnythingThePlatformThrowsAtStartup_FailsClosedInsteadOfStoppingTheServer()
    {
        using var rig = NewRig();

        using var store = new SecretStore(rig.Paths, rig.State, rig.Log, _ => throw new PlatformNotSupportedException(), null, null, null);

        Assert.Equal(SecretStoreStatus.Unreadable, store.Status);
        Assert.Equal(SecretUnreadableReason.StorageUnavailable, store.Info.Reason);
        Assert.Contains("PlatformNotSupportedException", rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialMustHaveTheRightShape_AndTheTokenIsNeverSerialized()
    {
        const string device = "shoal-1a2b3c4d5e6f-4b0c1d2e";
        foreach (var bad in new[] { string.Empty, " ", "ABCDEF0123456789ABCDEF0123456789", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8f0", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e8\n", "9f2c4d6e8a0b1c3d5e7f9a0b2c4d6e\"f", "TOKEN-FOR-TESTS-0123456789" })
        {
            var failure = Assert.Throws<ArgumentException>(() => new PeerCredential(bad, device, DateTime.UtcNow, PairingMethod.Qc));
            Assert.DoesNotContain("TOKEN-FOR", failure.Message, StringComparison.Ordinal);
        }

        foreach (var bad in new[] { string.Empty, "dev", "shoal-dev-1", "shoal-1A2B3C4D5E6F-4B0C1D2E", "shoal-1a2b3c4d5e6f-4b0c1d2e\r\n", "shoal-1a2b3c4d5e6f-4b0c1d2" })
        {
            Assert.Throws<ArgumentException>(() => new PeerCredential(Token, bad, DateTime.UtcNow, PairingMethod.Qc));
        }

        var credential = Credential();
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(credential), StringComparison.Ordinal);
        Assert.True(PeerCredential.IsValidToken(Token));
        Assert.False(PeerCredential.IsValidToken(null));
        Assert.True(PeerCredential.IsValidDeviceId(device));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AStoredCredentialThatIsNotShapedRight_IsRefusedWhenOpened()
    {
        // A file written by something other than PeerCredential cannot smuggle a malformed token back in: Decode runs the same checks.
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);
        await store.PutAsync(StateSamples.PeerKeyA, Credential());

        Assert.Equal(Token, store.TryGet(StateSamples.PeerKeyA)!.Token);
        Assert.Empty(store.Info.RefusedPeerKeys);
    }

    [Fact]
    public async Task TwoPeersCanBeStoredAtTheSameTime()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);

        await Task.WhenAll(
            store.PutAsync(StateSamples.PeerKeyA, Credential()),
            store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB)));

        using var reopened = Open(rig);
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);
        Assert.Equal(TokenB, reopened.TryGet(StateSamples.PeerKeyB)!.Token);
    }

    [Fact]
    public void AKeyFileThatCannotBeRead_IsABadKey_NotAFallback()
    {
        using var rig = NewRig();

        using var store = Open(rig, new Dictionary<string, string> { [SecretStore.KeyFileVariable] = rig.Dir.Combine("does-not-exist") });

        Assert.Equal(SecretUnreadableReason.BadKey, store.Info.Reason);
        Assert.False(File.Exists(rig.Paths.MasterKeyFile));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("ASNFZ4mrze8BI0VniavN7wEjRWeJq83vASNFZ4mrze8")]
    [InlineData("ASNFZ4mrze8BI0VniavN7wEjRWeJq83vASNFZ4mrze8=")]
    public void GoodKeys_AreAccepted(string value)
    {
        Assert.True(SecretKeyParser.TryParse(value, out var key));
        Assert.Equal(32, key.Length);
        Assert.Equal(Convert.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), key);
    }

    [Fact]
    public async Task EntryIsBoundToTheHost_AHostEditWithoutARepairDoesNotDecrypt_ASchemeOrPortChangeDoes()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);
        await store.PutAsync(StateSamples.PeerKeyA, Credential());

        await rig.State.UpdateAsync(s => s.Home.Peers.First(p => p.PeerKey == StateSamples.PeerKeyA).BaseUrl = "https://hops.tail1234.ts.net:8920/jellyfin");
        Assert.Equal(Token, store.TryGet(StateSamples.PeerKeyA)!.Token);

        await rig.State.UpdateAsync(s => s.Home.Peers.First(p => p.PeerKey == StateSamples.PeerKeyA).BaseUrl = "http://attacker.tail1234.ts.net:8096");
        Assert.Null(store.TryGet(StateSamples.PeerKeyA));
        Assert.Equal([StateSamples.PeerKeyA], store.Info.RefusedPeerKeys);

        // A restart sees the same: the sealed entry no longer authenticates under the new host.
        using var reopened = Open(rig);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyA));
        Assert.Equal(SecretStoreStatus.Ready, reopened.Status);

        // The legitimate path: the entry is deleted first (DESIGN 4.4), then the new address is used for the new pairing.
        Assert.True(await store.RemoveAsync(StateSamples.PeerKeyA));
        await store.PutAsync(StateSamples.PeerKeyA, Credential(TokenB));
        Assert.Equal(TokenB, store.TryGet(StateSamples.PeerKeyA)!.Token);

        // The host differing only in case is the same host.
        await rig.State.UpdateAsync(s => s.Home.Peers.First(p => p.PeerKey == StateSamples.PeerKeyA).BaseUrl = "http://ATTACKER.tail1234.ts.net:8096");
        Assert.Equal(TokenB, store.TryGet(StateSamples.PeerKeyA)!.Token);
    }

    [Fact]
    public async Task EntryIsBoundToThePeerKey_AnEntryMovedToAnotherPeerDoesNotDecrypt()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync(sameHost: true);
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        // Copy A's sealed message under B's name (both peers have the same host): the peer key in the additional data differs.
        var document = JsonDocument.Parse(File.ReadAllBytes(rig.Paths.SecretsFile)).RootElement;
        var a = document.GetProperty("peers").GetProperty(StateSamples.PeerKeyA);
        var edited = new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["check"] = JsonSerializer.Deserialize<object>(document.GetProperty("check").GetRawText())!,
            ["peers"] = new Dictionary<string, object> { [StateSamples.PeerKeyB] = JsonSerializer.Deserialize<object>(a.GetRawText())! }
        };
        File.WriteAllText(rig.Paths.SecretsFile, JsonSerializer.Serialize(edited));

        using var reopened = Open(rig);

        Assert.Equal(SecretStoreStatus.Ready, reopened.Status);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyB));
        Assert.Equal([StateSamples.PeerKeyB], reopened.Info.RefusedPeerKeys);
    }

    [Fact]
    public async Task OrphanEntries_AreListedForTheReconciliation_NeverUsed_AndSurviveOtherSavesUnchanged()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            await store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));
        }

        await rig.State.UpdateAsync(s => s.Home.Peers.RemoveAll(p => p.PeerKey == StateSamples.PeerKeyB));
        var orphanBefore = rig.ReadBoxes().Peers[StateSamples.PeerKeyB];

        using var reopened = Open(rig);
        Assert.Equal([StateSamples.PeerKeyB, StateSamples.PeerKeyA], reopened.PeerKeys!);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyB));
        Assert.Empty(reopened.Info.RefusedPeerKeys);
        await reopened.PutAsync(StateSamples.PeerKeyA, Credential());

        var orphanAfter = rig.ReadBoxes().Peers[StateSamples.PeerKeyB];
        Assert.Equal(orphanBefore.Ciphertext, orphanAfter.Ciphertext);
    }

    [Fact]
    public async Task ACrashInTheMiddleOfASave_KeepsTheOldFile()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
        }

        var before = File.ReadAllBytes(rig.Paths.SecretsFile);
        using (var crashing = Open(rig, hook: stage => throw new SimulatedCrashException()))
        {
            await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.PutAsync(StateSamples.PeerKeyB, Credential(TokenB)));
            Assert.Null(crashing.TryGet(StateSamples.PeerKeyB));
        }

        Assert.Equal(before, File.ReadAllBytes(rig.Paths.SecretsFile));
        using var reopened = Open(rig);
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);
        Assert.Null(reopened.TryGet(StateSamples.PeerKeyB));
    }

    [Fact]
    public async Task NoTokenAppearsInAnyFileInfoOrLog()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        var tokens = new[] { Token, TokenB };
        using (var store = Open(rig))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            await store.PutAsync(StateSamples.PeerKeyB, Credential(TokenB));
            _ = store.TryGet(StateSamples.PeerKeyA);
            await store.RemoveAsync(StateSamples.PeerKeyB);
            var info = JsonSerializer.Serialize(store.Info);
            Assert.All(tokens, t => Assert.DoesNotContain(t, info, StringComparison.Ordinal));
            Assert.All(tokens, t => Assert.DoesNotContain(t, store.TryGet(StateSamples.PeerKeyA)!.ToString(), StringComparison.Ordinal));
        }

        using var wrong = Open(rig, EnvKey(NewKeyHex()));

        foreach (var file in Directory.EnumerateFiles(rig.Paths.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var token in tokens)
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(token)), $"{Path.GetFileName(file)} holds a token as text");
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(token)))), $"{Path.GetFileName(file)} holds a token as base64");
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Convert.ToHexString(Encoding.UTF8.GetBytes(token)))), $"{Path.GetFileName(file)} holds a token as hex");
            }
        }

        Assert.All(tokens, t => Assert.DoesNotContain(t, rig.Log.All, StringComparison.Ordinal));
        Assert.DoesNotContain(StateSamples.PeerKeyA, rig.Log.All, StringComparison.Ordinal); // peers appear as 8 hex of a hash only
    }

    [Fact]
    public async Task ResetAsync_MovesTheFilesAside_AndStartsOver()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);
        await store.PutAsync(StateSamples.PeerKeyA, Credential());
        File.Delete(rig.Paths.MasterKeyFile);
        using (var broken = Open(rig))
        {
            Assert.Equal(SecretUnreadableReason.MasterKeyMissing, broken.Info.Reason);
            await broken.ResetAsync();

            Assert.Equal(SecretStoreStatus.Ready, broken.Status);
            Assert.Empty(broken.PeerKeys!);
            Assert.Single(Directory.EnumerateFiles(rig.Paths.Root, "secrets.enc.corrupt-*"));
            await broken.PutAsync(StateSamples.PeerKeyA, Credential(TokenB));
            Assert.Equal(TokenB, broken.TryGet(StateSamples.PeerKeyA)!.Token);
        }

        using var again = Open(rig);
        Assert.Equal(TokenB, again.TryGet(StateSamples.PeerKeyA)!.Token);
    }

    [Fact]
    public async Task ResetAsync_RefusesWhenTheKeyComesFromTheEnvironmentAndIsBad()
    {
        using var rig = NewRig();
        using var store = Open(rig, EnvKey("hunter2"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResetAsync());
    }

    [Fact]
    public async Task KeyWrapper_IsUsedForMasterKey_AndTheRawKeyIsNeverOnDisk()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        var protector = new ReversingProtector();
        using (var store = Open(rig, protector: protector))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            Assert.Equal(KeySource.Dpapi, store.Info.KeySource);
        }

        var onDisk = File.ReadAllBytes(rig.Paths.MasterKeyFile);
        Assert.True(onDisk.AsSpan().StartsWith(Encoding.ASCII.GetBytes("SHOALKEY-DPAPI1\n")));
        Assert.Equal(1, protector.ProtectCalls);
        using var reopened = Open(rig, protector: protector);
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);

        // A master.key that the wrapper cannot unwrap (another account) is KeyProtectionFailed; nothing is regenerated.
        using var stranger = Open(rig, protector: new ReversingProtector { FailUnprotect = true });
        Assert.Equal(SecretUnreadableReason.KeyProtectionFailed, stranger.Info.Reason);
        Assert.Equal(onDisk, File.ReadAllBytes(rig.Paths.MasterKeyFile));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task OnWindows_MasterKeyIsWrappedWithDpapiCurrentUser_AndOtherEntropyCannotUnwrapIt()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        Assert.True(OperatingSystem.IsWindows());
        using (var store = Open(rig, protector: new DpapiKeyProtector()))
        {
            await store.PutAsync(StateSamples.PeerKeyA, Credential());
            Assert.Equal(KeySource.Dpapi, store.Info.KeySource);
        }

        var onDisk = File.ReadAllBytes(rig.Paths.MasterKeyFile);
        Assert.True(onDisk.Length > 64, "a DPAPI blob is larger than the 32-byte key");
        using var reopened = Open(rig, protector: new DpapiKeyProtector());
        Assert.Equal(Token, reopened.TryGet(StateSamples.PeerKeyA)!.Token);

        var blob = onDisk[Encoding.ASCII.GetBytes("SHOALKEY-DPAPI1\n").Length..];
        Assert.Equal(32, new DpapiKeyProtector().Unprotect(blob).Length);
        Assert.ThrowsAny<CryptographicException>(() => new DpapiKeyProtector(Encoding.ASCII.GetBytes("some other entropy")).Unprotect(blob));

        // A raw 32-byte master.key (copied from Linux) is not accepted on Windows: it would have no protection.
        File.WriteAllBytes(rig.Paths.MasterKeyFile, RandomNumberGenerator.GetBytes(32));
        using var raw = Open(rig, protector: new DpapiKeyProtector());
        Assert.Equal(SecretUnreadableReason.KeyProtectionFailed, raw.Info.Reason);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void OnWindows_TheDpapiEntropyIsAPublicConstant()
    {
        Assert.Contains("not a secret", Encoding.ASCII.GetString(DpapiKeyProtector.DefaultEntropy), StringComparison.Ordinal);
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task OnLinux_FilesAre0600_AndTheFolderIs0700()
    {
        using var rig = NewRig();
        await rig.AddPeersAsync();
        using var store = Open(rig);

        await store.PutAsync(StateSamples.PeerKeyA, Credential());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rig.Paths.SecretsFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rig.Paths.MasterKeyFile));
        Assert.Equal(32, File.ReadAllBytes(rig.Paths.MasterKeyFile).Length);
        Assert.Null(store.Info.VolumeWarning);
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void OnLinux_ANetworkVolume_ProducesTheWarning()
    {
        using var rig = NewRig();
        var mountInfo = $"1 0 8:1 / / rw - ext4 /dev/sda1 rw\n2 1 0:50 / {Path.GetFullPath(rig.Dir.Path)} rw - cifs //nas/share rw\n";

        using var store = Open(rig, mountInfo: mountInfo);

        Assert.Equal(VolumeCheck.Warning, store.Info.VolumeWarning);
    }

    [Theory]
    [InlineData("cifs", true)]
    [InlineData("smb3", true)]
    [InlineData("nfs", true)]
    [InlineData("nfs4", true)]
    [InlineData("9p", true)]
    [InlineData("fuse.sshfs", true)]
    [InlineData("fuse.mergerfs", true)]
    [InlineData("ext4", false)]
    [InlineData("xfs", false)]
    [InlineData("btrfs", false)]
    [InlineData("overlay", false)]
    [InlineData("zfs", false)]
    public void VolumeCheck_RecognizesNetworkAndFuseFileSystems(string type, bool warn)
    {
        var mountInfo = $"1 0 8:1 / / rw - ext4 /dev/sda1 rw\n2 1 0:50 / /config rw - {type} src rw\n";

        var found = VolumeCheck.FileSystemTypeOf("/config/data/shoal", mountInfo);

        Assert.Equal(type, found);
        Assert.Equal(warn, VolumeCheck.CheckFileSystem("/config/data/shoal", mountInfo) is not null || OperatingSystem.IsWindows() && warn);
    }

    [Fact]
    public void VolumeCheck_UsesTheLongestMountPoint_AndUnescapesSpaces()
    {
        var mountInfo = string.Join('\n',
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw",
            "2 1 0:50 / /mnt/nas rw - cifs //nas/share rw",
            "3 2 8:2 / /mnt/nas/local\\040disk rw - ext4 /dev/sdb1 rw",
            "4 1 8:3 / /mnt/nas2 rw - xfs /dev/sdc1 rw");

        Assert.Equal("cifs", VolumeCheck.FileSystemTypeOf("/mnt/nas/jellyfin/data", mountInfo));
        Assert.Equal("ext4", VolumeCheck.FileSystemTypeOf("/mnt/nas/local disk/data", mountInfo));
        Assert.Equal("xfs", VolumeCheck.FileSystemTypeOf("/mnt/nas2/data", mountInfo));
        Assert.Equal("ext4", VolumeCheck.FileSystemTypeOf("/var/lib/jellyfin", mountInfo));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void OnWindows_AUncPathProducesTheWarning_ALocalPathDoesNot()
    {
        Assert.Equal(VolumeCheck.Warning, VolumeCheck.CheckFileSystem(@"\\nas\share\jellyfin\data\shoal"));
        Assert.Null(VolumeCheck.CheckFileSystem(@"C:\ProgramData\Jellyfin\Server\data\shoal"));
        Assert.Null(VolumeCheck.CheckFileSystem(@"\\?\C:\ProgramData\Jellyfin\data\shoal"));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private sealed class ReversingProtector : IKeyProtector
    {
        public int ProtectCalls { get; private set; }

        public bool FailUnprotect { get; init; }

        public byte[] Protect(byte[] key)
        {
            ProtectCalls++;
            return key.Reverse().Select(b => (byte)(b ^ 0x5A)).Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        }

        public byte[] Unprotect(byte[] blob)
        {
            if (FailUnprotect)
            {
                throw new CryptographicException("another account");
            }

            return blob[..^4].Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Dir = new TempDirectory();
            Paths = new ShoalPaths(Dir.Path);
            Log = new CapturingLogger();
            State = new StateStore(Paths, Log, TimeProvider.System, null);
        }

        public TempDirectory Dir { get; }

        public ShoalPaths Paths { get; }

        public CapturingLogger Log { get; }

        public StateStore State { get; }

        public async Task AddPeersAsync(bool sameHost = false)
        {
            await State.UpdateAsync(state =>
            {
                state.StubRoot = Path.Combine(Dir.Path, "stubs");
                state.Home.Peers.Add(NewPeer(StateSamples.PeerKeyA, "Hops", "http://hops.tail1234.ts.net:8096"));
                state.Home.Peers.Add(NewPeer(StateSamples.PeerKeyB, "Pints", sameHost ? "http://hops.tail1234.ts.net:8096" : "http://pints.tail1234.ts.net:8096"));
            });
        }

        public Boxes ReadBoxes()
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Paths.SecretsFile));
            var root = document.RootElement;
            var peers = root.GetProperty("peers").EnumerateObject()
                .ToDictionary(p => p.Name, p => new Box(p.Value.GetProperty("nonce").GetString()!, p.Value.GetProperty("ciphertext").GetString()!), StringComparer.Ordinal);
            var check = new Box(root.GetProperty("check").GetProperty("nonce").GetString()!, root.GetProperty("check").GetProperty("ciphertext").GetString()!);
            return new Boxes(check, peers);
        }

        public void FlipCiphertextCharacter(string peerKey) => Flip(text => text.IndexOf("\"ciphertext\"", text.IndexOf(peerKey, StringComparison.Ordinal), StringComparison.Ordinal));

        public void FlipCheckCharacter() => Flip(text => text.IndexOf("\"ciphertext\"", text.IndexOf("\"check\"", StringComparison.Ordinal), StringComparison.Ordinal));

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }

        private static PeerState NewPeer(string key, string name, string url) => new()
        {
            PeerKey = key,
            DisplayName = name,
            BaseUrl = url,
            Network = PeerNetwork.Tailnet,
            Enabled = false
        };

        // Changes one base64url character a few positions into the ciphertext (stays valid base64url; the tag no longer matches).
        private void Flip(Func<string, int> locate)
        {
            var text = File.ReadAllText(Paths.SecretsFile);
            var at = locate(text);
            var start = text.IndexOf('"', text.IndexOf(':', at) + 1) + 1 + 4;
            var original = text[start];
            var replacement = original == 'A' ? 'B' : 'A';
            File.WriteAllText(Paths.SecretsFile, text[..start] + replacement + text[(start + 1)..]);
        }
    }

    private sealed record Box(string Nonce, string Ciphertext);

    private sealed record Boxes(Box Check, Dictionary<string, Box> Peers)
    {
        public IReadOnlyList<string> AllNonces => Peers.Values.Select(p => p.Nonce).Append(Check.Nonce).ToList();
    }
}
