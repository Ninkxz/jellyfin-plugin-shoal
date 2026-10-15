using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 10 L4: the in-memory token of a rotation (kept while the secret file is not writable) must not shadow the credential of a later pairing.
/// </summary>
public sealed class PeerDirectoryOverrideTests
{
    private const string DeviceId = "shoal-a1b2c3d4e5f6-4b0c1d2e";
    private const string Old = "00000000000000000000000000000001";
    private const string Rotated = "00000000000000000000000000000002";
    private const string Fresh = "00000000000000000000000000000003";

    [Fact]
    public async Task TheOverride_AppliesToItsOwnPairing_AndIsDroppedByARePair()
    {
        using var rig = new PairingRig(time: new ManualTimeProvider());
        await rig.State.UpdateAsync(StateSamples.Fill);
        var paired = new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc);
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Old, DeviceId, paired, PairingMethod.Invite));
        var directory = new PeerDirectory(rig.State, rig.Secrets);
        Assert.Equal(Old, directory.Find(StateSamples.PeerKeyA)!.Token);

        directory.SetActiveToken(StateSamples.PeerKeyA, Rotated); // the file write failed after a rotation

        Assert.Equal(Rotated, directory.Find(StateSamples.PeerKeyA)!.Token);
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Rotated, DeviceId, paired, PairingMethod.Invite)); // the rotation's own write: same pairing
        Assert.Equal(Rotated, directory.Find(StateSamples.PeerKeyA)!.Token);

        directory.SetActiveToken(StateSamples.PeerKeyA, "00000000000000000000000000000009");
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Fresh, DeviceId, paired.AddDays(3), PairingMethod.Invite)); // a re-pair

        Assert.Equal(Fresh, directory.Find(StateSamples.PeerKeyA)!.Token); // the fresh credential wins
        Assert.Equal(Fresh, directory.Find(StateSamples.PeerKeyA)!.Token); // and stays: the stale override is gone
    }

    [Fact]
    public async Task ARemovedPeer_LeavesNoOverrideBehind_ForAPeerAddedAgain()
    {
        using var rig = new PairingRig(time: new ManualTimeProvider());
        await rig.State.UpdateAsync(StateSamples.Fill);
        var paired = new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc);
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Old, DeviceId, paired, PairingMethod.Invite));
        var directory = new PeerDirectory(rig.State, rig.Secrets);
        directory.SetActiveToken(StateSamples.PeerKeyA, Rotated);

        await rig.Secrets.RemoveAsync(StateSamples.PeerKeyA);
        Assert.Null(directory.Find(StateSamples.PeerKeyA));
        await rig.Secrets.PutAsync(StateSamples.PeerKeyA, new PeerCredential(Fresh, DeviceId, paired.AddDays(1), PairingMethod.Invite));

        Assert.Equal(Fresh, directory.Find(StateSamples.PeerKeyA)!.Token);
    }
}
