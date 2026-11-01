using System;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.Sync;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalHistoryTests
{
    private static readonly Guid User = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static RemovalUserState Original() => new(User, true, true, 80_000_000, 2, new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc), true, 8, true, 1, 2);
    private static RemovalUserState Pristine() => new(User, false, false, 0, 0, null, false, null, null, null, null);

    [Fact]
    public void FreshSharedDestinationReceivesEveryOriginalPersonalField()
    {
        var baseline = Original();
        Assert.Equal(baseline, RemovalHistory.Handoff(baseline, Pristine()));
        Assert.Equal(baseline, RemovalHistory.Handoff(baseline, baseline));
    }

    [Fact]
    public void ExplicitSharedResetIsNotMistakenForMissingHistory()
    {
        var reset = Pristine() with { Observed = true };
        Assert.Throws<InvalidOperationException>(() => RemovalHistory.Handoff(Original(), reset));
    }

    [Fact]
    public void ExistingDifferentSharedHistoryPreventsAStaleHandoff()
    {
        Assert.Throws<InvalidOperationException>(() => RemovalHistory.Handoff(Original(), Original() with { Position = 90_000_000 }));
    }

    [Fact]
    public void UndoCarriesNewerSharedProgressInsteadOfOldLocalResume()
    {
        var baseline = Original();
        var newer = baseline with { Position = 0, PlayCount = 3, Favorite = true };
        Assert.Equal(newer, RemovalHistory.Restore(baseline, baseline, newer));
    }

    [Fact]
    public void UndoCarriesAnExplicitSharedResetWithoutResurrectingWatchedState()
    {
        var reset = Pristine() with { Observed = true, Favorite = true, Rating = 6 };
        Assert.Equal(reset, RemovalHistory.Restore(Original(), Original(), reset));
    }

    [Fact]
    public void UndoDoesNotOverwriteIndependentEditsToRestoredLocalHistory()
    {
        var baseline = Original();
        var local = baseline with { Position = 120_000_000, Favorite = false, Rating = 3 };
        Assert.Equal(local, RemovalHistory.Restore(baseline, local, baseline with { PlayCount = 3 }));
    }

    [Fact]
    public void MissingSharedItemStillRestoresOriginalPersonalState()
    {
        Assert.Equal(Original(), RemovalHistory.Restore(Original(), Pristine(), null));
    }

    [Fact]
    public void MissingHydratedRowDoesNotMeanNewerProviderHistoryIsPristine()
    {
        var current = Original() with { Observed = false, Position = 110_000_000, Favorite = true, Rating = 9 };
        Assert.Equal(current, RemovalHistory.Restore(Original(), current, Pristine() with { Observed = true }));
    }

    [Fact]
    public void CheckedOriginCannotAuthorizeAnAdditionalUncheckedBackup()
    {
        var item = Guid.NewGuid();
        var checkedOrigin = new GroupMember { PeerKey = "checked", PeerItemId = item.ToString("N") };
        Assert.True(RemovalSelection.UniqueOrigin([checkedOrigin], "checked", item));
        Assert.False(RemovalSelection.UniqueOrigin([checkedOrigin, new GroupMember { PeerKey = "unchecked", PeerItemId = Guid.NewGuid().ToString("N") }], "checked", item));
        Assert.False(RemovalSelection.UniqueOrigin([checkedOrigin], "other", item));
        Assert.False(RemovalSelection.UniqueOrigin([checkedOrigin], "checked", Guid.NewGuid()));
    }

    [Fact]
    public void NoPersonalStateCanCrossBetweenUsers()
    {
        var other = Original() with { UserId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => RemovalHistory.Handoff(Original(), other));
        Assert.Throws<InvalidOperationException>(() => RemovalHistory.Restore(Original(), Original(), other));
    }
}
