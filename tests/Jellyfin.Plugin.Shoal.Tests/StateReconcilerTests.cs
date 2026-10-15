using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K1: the read-only startup reconciliation (DESIGN 3.7): orphans in any direction are listed, nothing is repaired.
/// </summary>
public sealed class StateReconcilerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shoal-reconcile", "stubs");

    private static ShoalState StateWithOnePeer()
    {
        var state = new ShoalState { StubRoot = Root };
        state.Home.Peers.Add(new PeerState
        {
            PeerKey = StateSamples.PeerKeyA,
            DisplayName = "Hops",
            Pairing = new PairingInfo { Method = PairingMethod.Qc },
            Libraries = { new LibraryMapping { MappingKey = "m1", Subfolder = "movies", LocalLibraryName = "Hops Movies" } }
        });
        return state;
    }

    private static LibraryPaths HopsLibrary(string name = "Hops Movies") =>
        new(name, [Path.Combine(Root, StateSamples.PeerKeyA, "movies")]);

    private static ReconcileInputs Inputs(
        string[]? secrets = null,
        string[]? folders = null,
        params LibraryPaths[] libraries) =>
        new(secrets ?? [StateSamples.PeerKeyA], folders ?? [StateSamples.PeerKeyA], libraries);

    [Fact]
    public void APeerThatIsStillPairing_OrWhosePairingStopped_IsNotExpectedToHaveASecret()
    {
        var state = new ShoalState { StubRoot = Root };
        state.Home.Peers.Add(new PeerState
        {
            PeerKey = StateSamples.PeerKeyA,
            DisplayName = "Hops",
            Enabled = false,
            PendingPairing = new PairingProgress { Phase = PairingPhase.AwaitingConfirm }
        });
        state.Home.Peers.Add(new PeerState { PeerKey = StateSamples.PeerKeyB, DisplayName = "Pints", Enabled = false });

        var report = StateReconciler.Compute(state, new ReconcileInputs([], [StateSamples.PeerKeyA, StateSamples.PeerKeyB], []));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void EverythingLinedUp_IsClean()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(libraries: HopsLibrary()));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void AnEmptyState_WithNothingOnDisk_IsClean()
    {
        var report = StateReconciler.Compute(new ShoalState(), new ReconcileInputs([], [], []));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void APeerWithoutASecret_IsReported()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(secrets: [], libraries: HopsLibrary()));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReconcileKind.PeerWithoutSecret, finding.Kind);
        Assert.Equal(StateSamples.PeerKeyA, finding.PeerKey);
    }

    [Fact]
    public void AnUnreadableSecretStore_SkipsTheSecretChecks_InsteadOfReportingEverything()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), new ReconcileInputs(null, [StateSamples.PeerKeyA], [HopsLibrary()]));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void APeerWithoutAFolder_IsReported()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(folders: [], libraries: HopsLibrary()));

        Assert.Equal(ReconcileKind.PeerWithoutFolder, Assert.Single(report.Findings).Kind);
    }

    [Fact]
    public void AMappedLibraryThatDoesNotExist_IsReported_WithItsName()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs());

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReconcileKind.LibraryMissing, finding.Kind);
        Assert.Equal("Hops Movies", finding.Detail);
    }

    [Fact]
    public void ALibraryWithMoreThanTheStubFolder_DoesNotCountAsTheMappedLibrary()
    {
        var wide = new LibraryPaths("Hops Movies", [Path.Combine(Root, StateSamples.PeerKeyA, "movies"), Path.Combine(Path.GetTempPath(), "other")]);

        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(libraries: wide));

        Assert.Contains(report.Findings, f => f.Kind == ReconcileKind.LibraryMissing);
    }

    [Fact]
    public void ASecretForAnUnknownPeer_IsAnOrphan()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(secrets: [StateSamples.PeerKeyA, StateSamples.PeerKeyB], libraries: HopsLibrary()));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReconcileKind.SecretWithoutPeer, finding.Kind);
        Assert.Equal(StateSamples.PeerKeyB, finding.PeerKey);
    }

    [Fact]
    public void AFolderForAnUnknownPeer_IsAnOrphan()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(folders: [StateSamples.PeerKeyA, StateSamples.PeerKeyB], libraries: HopsLibrary()));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReconcileKind.FolderWithoutPeer, finding.Kind);
        Assert.Equal(StateSamples.PeerKeyB, finding.PeerKey);
    }

    [Fact]
    public void ALibraryUnderTheStubRootWithoutAPeer_MeansARestoreWasDetected()
    {
        // After a Jellyfin backup restore the library definition exists but the Shoal data does not.
        var restored = new LibraryPaths("Hops Movies", [Path.Combine(Root, StateSamples.PeerKeyB, "movies")]);

        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(libraries: [HopsLibrary(), restored]));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReconcileKind.LibraryWithoutPeer, finding.Kind);
        Assert.Equal("Hops Movies", finding.Detail);
    }

    [Fact]
    public void ALibraryOutsideTheStubRoot_IsNotShoalsBusiness()
    {
        var other = new LibraryPaths("Family Movies", [Path.Combine(Path.GetTempPath(), "family")]);

        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(libraries: [HopsLibrary(), other]));

        Assert.True(report.IsClean);
    }

    [Fact]
    public void AnEmptyStubRoot_MeansNoLibraryCanMatch()
    {
        var state = StateWithOnePeer();
        state.StubRoot = string.Empty;

        var report = StateReconciler.Compute(state, Inputs(libraries: HopsLibrary()));

        Assert.Equal([ReconcileKind.LibraryMissing], report.Findings.Select(f => f.Kind).ToArray());
    }

    [Fact]
    public void FindingsNeverCarryAnythingButHandlesAndNames()
    {
        var report = StateReconciler.Compute(StateWithOnePeer(), Inputs(secrets: [], folders: [], libraries: new List<LibraryPaths>().ToArray()));

        Assert.All(report.Findings, f =>
        {
            Assert.NotNull(f.PeerKey);
            Assert.DoesNotContain("token", f.Detail ?? string.Empty, System.StringComparison.OrdinalIgnoreCase);
        });
    }
}
