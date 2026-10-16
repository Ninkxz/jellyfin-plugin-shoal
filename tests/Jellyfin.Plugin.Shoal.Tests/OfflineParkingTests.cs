using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// <c>ParkOfflinePeers</c> (owner feedback 01, DESIGN 5.7): the libraries of a server that stays offline are taken off people's explicit library lists and put back
/// after it has been healthy for a while. Nothing is deleted and nothing moves, so watch progress stays. Renaming a library was rejected: Jellyfin derives the library id
/// from its path, so a rename would change every id the access rules use.
/// </summary>
public sealed class OfflineParkingTests
{
    private static async Task<(VisibilityRig Rig, AccountInfo Alice, AccountInfo Bob, AccountInfo Carol, AccountInfo Dave)> SetUpAsync()
    {
        var rig = new VisibilityRig(mode: AccessMode.Everyone);
        await rig.CreateLibraryAsync();
        await rig.SetAsync(new AccessChange(AccessMode.Everyone, null, null, false)); // creating a library starts it with its admin only
        var shoal = rig.ShoalId;
        var alice = rig.Accounts.Add("alice", all: false, folders: [rig.Movies.Id, shoal]);
        var bob = rig.Accounts.Add("bob", all: false, folders: [shoal]);
        var carol = rig.Accounts.Add("carol", all: true);
        var dave = rig.Accounts.Add("dave", all: false, folders: [rig.Shows.Id], restricted: true); // restricted profiles are not in "Everyone", so dave never had the library
        await rig.Visibility.ReconcileAsync();
        rig.Accounts.Writes.Clear();
        return (rig, alice, bob, carol, dave);
    }

    private static string[] Lists(VisibilityRig rig, params AccountInfo[] accounts) =>
        [.. accounts.Select(a => string.Join('+', rig.Accounts.Get(a.Id).EnabledFolders.OrderBy(static g => g)))];

    // ---------------- hide ----------------

    [Fact]
    public async Task Hiding_TakesTheLibraryOffEveryExplicitList_AndLeavesEverythingElseAlone()
    {
        var (rig, alice, bob, carol, dave) = await SetUpAsync();
        using (rig)
        {
            var shoal = rig.ShoalId;
            var removed = await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);

            Assert.Equal(2, removed);
            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey));
            Assert.DoesNotContain(shoal, rig.Accounts.Get(alice.Id).EnabledFolders);
            Assert.Contains(rig.Movies.Id, rig.Accounts.Get(alice.Id).EnabledFolders);
            Assert.Empty(rig.Accounts.Get(bob.Id).EnabledFolders);
            Assert.True(rig.Accounts.Get(carol.Id).EnableAllFolders); // "all libraries" is not touched
            Assert.Equal([rig.Shows.Id], rig.Accounts.Get(dave.Id).EnabledFolders);
            Assert.DoesNotContain(rig.Accounts.Writes, w => w.UserId == carol.Id || w.UserId == dave.Id);
            var record = rig.State.Current.Home.Visibility.Parked[VisibilityRig.PeerKey];
            Assert.Equal(new[] { (alice.Id, shoal), (bob.Id, shoal) }.ToHashSet(), record.Pairs.Select(static p => (p.UserId, p.LibraryId)).ToHashSet());
            Assert.Single(rig.Log.Lines, static l => l.Contains("see all libraries", StringComparison.Ordinal)); // said once
            Assert.DoesNotContain(rig.Libraries.Operations, static o => o.StartsWith("purge:", StringComparison.Ordinal) || o.StartsWith("remove:", StringComparison.Ordinal)); // nothing is deleted
            Assert.Single(rig.Libraries.List(), l => l.Id == shoal); // the library and its folders stay
        }
    }

    [Fact]
    public async Task WhileHidden_TheReconcileDoesNotPutTheLibraryBack_AndAHiddenOneDoesNotStopManagingAnAccount()
    {
        var (rig, alice, bob, _, _) = await SetUpAsync();
        using (rig)
        {
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            rig.Accounts.Writes.Clear();

            var result = await rig.Visibility.ReconcileAsync();

            Assert.Empty(rig.Accounts.Writes);
            Assert.DoesNotContain(result.Changes, static c => c.Kind == VisibilityChangeKind.StoppedManaging);
            Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(alice.Id).EnabledFolders);
            Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders);
        }
    }

    [Fact]
    public async Task AnAccountShoalManages_StaysManagedThroughHidingAndShowing()
    {
        var (rig, _, bob, _, _) = await SetUpAsync();
        using (rig)
        {
            await rig.State.UpdateAsync(s => s.Home.Visibility.ManagedUsers[bob.Id] = new ManagedUser { Since = DateTime.UtcNow, LastWrittenFoldersHash = VisibilityService.HashFolders(rig.Accounts.Get(bob.Id).EnabledFolders) });

            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            var during = await rig.Visibility.ReconcileAsync();
            Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders);
            Assert.DoesNotContain(during.Changes, static c => c.Kind == VisibilityChangeKind.StoppedManaging); // hiding is Shoal's own write, not an admin edit
            Assert.Contains(bob.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);

            await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);
            var after = await rig.Visibility.ReconcileAsync();
            var back = rig.Accounts.Get(bob.Id);
            Assert.True(back.EnableAllFolders || back.EnabledFolders.Contains(rig.ShoalId)); // fully admitted again: Shoal gives "all libraries" back, as it always does
            Assert.DoesNotContain(after.Changes, static c => c.Kind == VisibilityChangeKind.StoppedManaging);
        }
    }

    // ---------------- restore ----------------

    [Fact]
    public async Task Showing_PutsBackExactlyTheRecordedPairs_AndNothingChangesAfterwards()
    {
        var (rig, alice, bob, carol, dave) = await SetUpAsync();
        using (rig)
        {
            var before = Lists(rig, alice, bob, carol, dave);
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);

            var restored = await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);

            Assert.Equal(2, restored);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));
            Assert.Empty(rig.State.Current.Home.Visibility.Parked);
            Assert.Equal(before, Lists(rig, alice, bob, carol, dave));
            rig.Accounts.Writes.Clear();
            await rig.Visibility.ReconcileAsync();
            Assert.Empty(rig.Accounts.Writes); // a second reconcile has nothing to do
        }
    }

    [Fact]
    public async Task WhatAnAdminChangedInBetween_IsNotFoughtOver()
    {
        var (rig, alice, bob, _, dave) = await SetUpAsync();
        using (rig)
        {
            var frank = rig.Accounts.Add("frank", all: false, folders: [rig.ShoalId]);
            await rig.Visibility.ReconcileAsync();
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            var shoal = rig.ShoalId;

            rig.Accounts.Edit(alice.Id, false, [rig.Movies.Id, rig.Shows.Id]); // alice: another library added by the admin
            rig.Accounts.Edit(bob.Id, true, []); // bob: now sees all libraries
            rig.Accounts.Edit(frank.Id, false, [shoal]); // frank: the admin put the hidden library back by hand
            await rig.Visibility.ReconcileAsync();
            Assert.Contains(shoal, rig.Accounts.Get(frank.Id).EnabledFolders); // the reconcile leaves a library the admin restored
            rig.Accounts.Writes.Clear();

            await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);

            Assert.Equal([rig.Movies.Id, rig.Shows.Id, shoal], rig.Accounts.Get(alice.Id).EnabledFolders.OrderBy(g => g == rig.Movies.Id ? 0 : g == rig.Shows.Id ? 1 : 2));
            Assert.True(rig.Accounts.Get(bob.Id).EnableAllFolders);
            Assert.DoesNotContain(rig.Accounts.Writes, w => w.UserId == bob.Id); // bob was not written to
            Assert.Single(rig.Accounts.Get(frank.Id).EnabledFolders);
            Assert.Equal([rig.Shows.Id], rig.Accounts.Get(dave.Id).EnabledFolders);
        }
    }

    [Fact]
    public async Task AnAccountNoLongerAdmitted_DoesNotGetTheLibraryBack()
    {
        var (rig, alice, bob, _, _) = await SetUpAsync();
        using (rig)
        {
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            await rig.SetAsync(new AccessChange(AccessMode.Selected, [alice.Id, rig.Admin.Id], null, false)); // bob is out by the access rules now

            await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);

            Assert.Contains(rig.ShoalId, rig.Accounts.Get(alice.Id).EnabledFolders);
            Assert.DoesNotContain(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders);
        }
    }

    [Fact]
    public async Task ARestartInTheMiddle_KeepsTheRecord_AndTheNewProcessPutsTheLibraryBack()
    {
        var (rig, alice, bob, _, _) = await SetUpAsync();
        using (rig)
        {
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            rig.State.Dispose();

            using var reborn = new StateStore(new ShoalPaths(rig.Dir.Path), NullLogger.Instance, TimeProvider.System, null);
            using var visibility = new VisibilityService(reborn, rig.Libraries, rig.Accounts, TimeProvider.System, rig.Log);
            Assert.True(visibility.IsParked(VisibilityRig.PeerKey));
            Assert.Equal(2, reborn.Current.Home.Visibility.Parked[VisibilityRig.PeerKey].Pairs.Count);
            rig.Accounts.Writes.Clear();
            await visibility.ReconcileAsync();
            Assert.Empty(rig.Accounts.Writes); // the new process does not undo it either

            var restored = await visibility.UnparkPeerAsync(VisibilityRig.PeerKey);

            Assert.Equal(2, restored);
            Assert.Contains(rig.ShoalId, rig.Accounts.Get(alice.Id).EnabledFolders);
            Assert.Contains(rig.ShoalId, rig.Accounts.Get(bob.Id).EnabledFolders);
        }
    }

    [Fact]
    public async Task APeerWithNoLibrary_Or_AlreadyHidden_IsLeftAlone()
    {
        var (rig, _, _, _, _) = await SetUpAsync();
        using (rig)
        {
            Assert.Equal(0, await rig.Visibility.ParkPeerAsync("00112233445566778899aabbccddeeff"));
            Assert.Equal(2, await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey));
            Assert.Equal(0, await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey)); // already hidden: the record is not overwritten
            Assert.Equal(2, rig.State.Current.Home.Visibility.Parked[VisibilityRig.PeerKey].Pairs.Count);
            Assert.Equal(0, await rig.Visibility.UnparkPeerAsync("00112233445566778899aabbccddeeff"));
        }
    }

    // ---------------- when (the parker) ----------------

    private static async Task<(VisibilityRig Rig, OfflineParker Parker, ManualTimeProvider Time, Func<ParkingSettings> Set, Action<ParkingSettings> Change)> ParkerAsync(ParkingSettings settings)
    {
        var (rig, _, _, _, _) = await SetUpAsync();
        var time = new ManualTimeProvider();
        var current = settings;
        var parker = new OfflineParker(rig.Visibility, () => current, time, NullLogger.Instance);
        return (rig, parker, time, () => current, s => current = s);
    }

    [Fact]
    public async Task OfflineForHalfAnHour_HidesIt_AndNotBefore()
    {
        var (rig, parker, time, _, _) = await ParkerAsync(new ParkingSettings(true, 0.5));
        using (rig)
        {
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromMinutes(29));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));

            time.Advance(TimeSpan.FromMinutes(1));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);

            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey));
        }
    }

    [Fact]
    public async Task APeerThatFlaps_IsNeitherHidden_NorShownTooEarly()
    {
        var (rig, parker, time, _, _) = await ParkerAsync(new ParkingSettings(true, 0.5));
        using (rig)
        {
            // Offline for 20 minutes, a healthy minute, offline for 20 more: never 30 in a row.
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromMinutes(20));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);
            time.Advance(TimeSpan.FromMinutes(1));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromMinutes(20));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));

            // Hidden for real, then it blips back for 9 minutes, drops again, and is healthy for 9 more: still hidden.
            time.Advance(TimeSpan.FromMinutes(11));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);
            time.Advance(TimeSpan.FromMinutes(9));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromMinutes(1));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);
            time.Advance(TimeSpan.FromMinutes(9));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);
            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey));

            time.Advance(TimeSpan.FromMinutes(1)); // ten steady minutes
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Online, default);

            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));
        }
    }

    [Fact]
    public async Task DegradedSaysNothingAboutReachability_SoNeitherClockMoves()
    {
        var (rig, parker, time, _, _) = await ParkerAsync(new ParkingSettings(true, 0.5));
        using (rig)
        {
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromMinutes(20));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Degraded, default);
            time.Advance(TimeSpan.FromMinutes(11));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);

            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey)); // offline since minute 0, never broken by "degraded"
        }
    }

    [Fact]
    public async Task WithTheSettingOff_NothingIsHidden_AndTurningItOffShowsEverythingAgain()
    {
        var (rig, parker, time, _, change) = await ParkerAsync(new ParkingSettings(false, 0.5));
        using (rig)
        {
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            time.Advance(TimeSpan.FromHours(5));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey)); // the default is off

            change(new ParkingSettings(true, 0.5));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey));

            change(new ParkingSettings(false, 0.5));
            await parker.ObserveAsync(VisibilityRig.PeerKey, PeerStatus.Offline, default);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey)); // switched off while hidden: back at once
        }
    }

    // ---------------- settings ----------------

    [Fact]
    public void TheHoursMayBeFractional_AndAreClamped()
    {
        var defaults = new PluginConfiguration();
        Assert.False(defaults.ParkOfflinePeers);
        Assert.Equal(0.5, defaults.ParkAfterHours);
        Assert.Empty(SettingsValidator.Apply(defaults));

        var odd = new PluginConfiguration { ParkAfterHours = 0.01 };
        Assert.NotEmpty(SettingsValidator.Apply(odd));
        Assert.Equal(0.1, odd.ParkAfterHours);
        var nan = new PluginConfiguration { ParkAfterHours = double.NaN };
        SettingsValidator.Apply(nan);
        Assert.Equal(0.1, nan.ParkAfterHours);
        var big = new PluginConfiguration { ParkAfterHours = 99999 };
        SettingsValidator.Apply(big);
        Assert.Equal(8760, big.ParkAfterHours);
        var fine = new PluginConfiguration { ParkAfterHours = 1.5 };
        Assert.Empty(SettingsValidator.Apply(fine));
    }

    // ---------------- Warden 14 ----------------

    private static async Task MakeManagedAsync(VisibilityRig rig, AccountInfo account) =>
        await rig.State.UpdateAsync(s => s.Home.Visibility.ManagedUsers[account.Id] = new ManagedUser { Since = DateTime.UtcNow, LastWrittenFoldersHash = VisibilityService.HashFolders(rig.Accounts.Get(account.Id).EnabledFolders) });

    [Fact]
    public async Task M1_AnAdminWhoRemovedALibraryFromAManagedAccount_KeepsThatThroughHidingAndShowing()
    {
        var (rig, alice, _, _, _) = await SetUpAsync();
        using (rig)
        {
            rig.Accounts.Edit(alice.Id, false, [rig.Movies.Id, rig.Shows.Id, rig.ShoalId]);
            await MakeManagedAsync(rig, alice);
            rig.Accounts.Edit(alice.Id, false, [rig.Movies.Id, rig.ShoalId]); // the admin removed "Kids" (Shows) in the dashboard

            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);

            Assert.DoesNotContain(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys); // the list is the admin's now
            Assert.Equal([rig.Movies.Id], rig.Accounts.Get(alice.Id).EnabledFolders);
            await rig.Visibility.ReconcileAsync();
            Assert.DoesNotContain(rig.Shows.Id, rig.Accounts.Get(alice.Id).EnabledFolders); // not given back

            await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);
            await rig.Visibility.ReconcileAsync();
            var list = rig.Accounts.Get(alice.Id).EnabledFolders;
            Assert.Contains(rig.ShoalId, list);
            Assert.DoesNotContain(rig.Shows.Id, list);
        }
    }

    [Fact]
    public async Task M1_AnAdminEditWhileHidden_StopsTheManagingBeforeTheLibraryIsShownAgain()
    {
        var (rig, alice, _, _, _) = await SetUpAsync();
        using (rig)
        {
            rig.Accounts.Edit(alice.Id, false, [rig.Movies.Id, rig.Shows.Id, rig.ShoalId]);
            await MakeManagedAsync(rig, alice);
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            Assert.Contains(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys); // Shoal's own write keeps it managed
            rig.Accounts.Edit(alice.Id, false, [rig.Movies.Id]); // the admin removed Shows while the library was hidden

            await rig.Visibility.UnparkPeerAsync(VisibilityRig.PeerKey);

            Assert.DoesNotContain(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys); // stopped at once, not only at the next reconcile
            await rig.Visibility.ReconcileAsync();
            Assert.DoesNotContain(alice.Id, rig.State.Current.Home.Visibility.ManagedUsers.Keys);
            Assert.DoesNotContain(rig.Shows.Id, rig.Accounts.Get(alice.Id).EnabledFolders);
        }
    }

    [Fact]
    public async Task L1_AnAccountEditedInTheDashboardDuringTheRun_IsNotOverwritten()
    {
        var (rig, alice, bob, _, _) = await SetUpAsync();
        using (rig)
        {
            var edited = new[] { rig.Movies.Id, rig.Shows.Id, rig.ShoalId };
            var target = rig.Accounts.ListCalls + 2; // the first call reads the accounts, the next re-reads alice just before her write
            rig.Accounts.OnList = n =>
            {
                if (n == target)
                {
                    rig.Accounts.OnList = null;
                    rig.Accounts.Edit(alice.Id, false, edited);
                }
            };

            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);

            Assert.DoesNotContain(rig.Accounts.Writes, w => w.UserId == alice.Id);
            Assert.Equal(edited.OrderBy(g => g), rig.Accounts.Get(alice.Id).EnabledFolders.OrderBy(g => g));
            Assert.Empty(rig.Accounts.Get(bob.Id).EnabledFolders); // the other account was hidden as usual
        }
    }

    [Fact]
    public async Task L2_AHiddenServerThatIsDisabledOrRemoved_OrTheSettingTurnedOff_IsShownAgain()
    {
        var (rig, parker, _, _, change) = await ParkerAsync(new ParkingSettings(true, 0.5));
        using (rig)
        {
            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            await parker.SweepAsync([VisibilityRig.PeerKey], default);
            Assert.True(rig.Visibility.IsParked(VisibilityRig.PeerKey)); // enabled and the setting on: stays hidden

            await parker.SweepAsync([], default); // disabled or removed
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));

            await rig.Visibility.ParkPeerAsync(VisibilityRig.PeerKey);
            change(new ParkingSettings(false, 0.5)); // setting off
            await parker.SweepAsync([VisibilityRig.PeerKey], default);
            Assert.False(rig.Visibility.IsParked(VisibilityRig.PeerKey));
            Assert.Contains(rig.ShoalId, rig.Accounts.Get(rig.Accounts.List().First(a => a.Name == "alice").Id).EnabledFolders);
        }
    }
}
