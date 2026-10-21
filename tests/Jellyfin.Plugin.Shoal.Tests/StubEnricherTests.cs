using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// 2.9.2 K6: the database item carries the group-aware label and the friend studios ("Shared by {name}"), and an item that already has them is not saved.
/// </summary>
public sealed class StubEnricherTests
{
    private const string Tail = "May buffer or be unavailable sometimes. If it won't play, try again later.";

    private static async Task<(Guid Item, CatalogEntry Entry)> SyncOneAsync(SyncRig rig, FriendLabelTests.LabelItems items)
    {
        rig.Peer.Add("The Matrix", 1999, tmdb: 603);
        await rig.SyncAsync();
        var entry = rig.Entries.Single();
        var item = Guid.NewGuid();
        items.Items[item] = (Path.Combine(rig.LibraryFolder, entry.StubRelPath!.Replace('/', Path.DirectorySeparatorChar)), "A hacker.", ["Shoal"]);
        items.Studios[item] = ["Warner Bros."];
        return (item, entry);
    }

    private static GroupBook GroupOf(SyncRig rig, CatalogEntry entry)
    {
        var book = new GroupBook(new MemoryGroupStore(), NullLogger.Instance);
        book.Update(doc =>
        {
            doc.Groups.Add(new TitleGroup
            {
                GroupKey = "g000000000000001",
                Kind = GroupKind.Movie,
                MetaOwner = SyncRig.PeerKey,
                Members =
                [
                    new GroupMember { PeerKey = SyncRig.PeerKey, PeerItemId = entry.PeerItemId },
                    new GroupMember { PeerKey = StateSamples.PeerKeyB, PeerItemId = "0123456789abcdef0123456789abcdef" },
                ],
                Copies = [new GroupCopy { StubKey = entry.StubKey, FolderRel = entry.StubRelPath!.Split('/')[0] }],
            });
            return true;
        });
        return book;
    }

    [Fact]
    public async Task OneServer_ItemNamesItsFriend_AndTheFriendIsTheOnlyStudio()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, _) = await SyncOneAsync(rig, items);

                Assert.Equal(1, await enricher.ApplyLabelsAsync());

                Assert.Equal("From Hops's server. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Hops"], items.Studios[item]);
            }
        }
    }

    [Fact]
    public async Task GroupedTitle_LabelNamesBothFriends()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, entry) = await SyncOneAsync(rig, items);
                await rig.AddPeerAsync(StateSamples.PeerKeyB, "Reef", 50, DateTime.UtcNow);
                enricher.Groups = GroupOf(rig, entry);

                Assert.Equal(1, await enricher.ApplyLabelsAsync());

                // Play priority: Reef's 50 beats Hops's default 100.
                Assert.Equal("From Reef's and Hops's servers. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Reef", "Shared by Hops"], items.Studios[item]);

                // The tier comes first: Hops set to Always prefer leads, whatever the number says.
                await rig.State.UpdateAsync(static s => s.Home.Peers.Single(p => p.DisplayName == "Hops").PriorityTier = PriorityTier.Prefer);
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal("From Hops's and Reef's servers. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Hops", "Shared by Reef"], items.Studios[item]);

                // The alias is the name shown.
                await rig.State.UpdateAsync(static s => s.Home.Peers.Single(p => p.DisplayName == "Reef").Alias = "Pat");
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops", "Shared by Pat"], items.Studios[item]);
            }
        }
    }

    [Fact]
    public async Task NoChange_NoSave()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, _) = await SyncOneAsync(rig, items);
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(1, items.Saves);

                items.Studios[item] = [.. items.Studios[item]]; // the same names in a new list (core hands out a fresh array)
                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal(1, items.Saves);

                items.Studios[item] = ["Shared by Hops", "Warner Bros."]; // a provider added one: the friend stays the only studio
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[item]);
            }
        }
    }

    // Warden 18 M1: the access of each friend's movie mapping (the rig's libraries are legacy folders, so the mapping holds the access).
    private static Task SetAccessAsync(SyncRig rig, string peerKey, LibraryAccess access) =>
        rig.State.UpdateAsync(s => s.Home.Peers.Single(p => p.PeerKey == peerKey).Libraries.Single(m => m.CollectionType == "movies").Access = access);

    private static LibraryAccess Selected(params Guid[] users) => new() { Mode = AccessMode.Selected, SelectedUserIds = [.. users] };

    private static LibraryAccess Everyone(bool restricted) => new() { Mode = AccessMode.Everyone, IncludeRestricted = restricted };

    [Fact]
    public async Task GroupedTitle_AFriendWhoseLibraryIsClosedToThisItemsUsers_IsNotNamed()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, entry) = await SyncOneAsync(rig, items);
                await rig.AddPeerAsync(StateSamples.PeerKeyB, "Reef", 50, DateTime.UtcNow);
                enricher.Groups = GroupOf(rig, entry);
                var family = Guid.NewGuid();
                await SetAccessAsync(rig, SyncRig.PeerKey, Everyone(restricted: true));
                await SetAccessAsync(rig, StateSamples.PeerKeyB, Selected(family));

                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal("From Hops's server. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Hops"], items.Studios[item]);

                // "Everyone except restricted profiles" does not cover "everyone": a restricted profile sees Hops's copy only.
                await SetAccessAsync(rig, StateSamples.PeerKeyB, Everyone(restricted: false));
                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[item]);

                // A selection that misses one of this item's users does not cover it either.
                var guest = Guid.NewGuid();
                await SetAccessAsync(rig, SyncRig.PeerKey, Selected(family, guest));
                await SetAccessAsync(rig, StateSamples.PeerKeyB, Selected(family));
                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[item]);
            }
        }
    }

    [Fact]
    public async Task GroupedTitle_AFriendWithTheSameOrWiderAccess_IsNamed()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, entry) = await SyncOneAsync(rig, items);
                await rig.AddPeerAsync(StateSamples.PeerKeyB, "Reef", 50, DateTime.UtcNow);
                enricher.Groups = GroupOf(rig, entry);
                var family = Guid.NewGuid();
                await SetAccessAsync(rig, SyncRig.PeerKey, Selected(family));
                await SetAccessAsync(rig, StateSamples.PeerKeyB, Selected(family));

                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal("From Reef's and Hops's servers. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Reef", "Shared by Hops"], items.Studios[item]);

                // Wider: Reef open to everyone, restricted profiles included.
                await SetAccessAsync(rig, StateSamples.PeerKeyB, Everyone(restricted: true));
                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Reef", "Shared by Hops"], items.Studios[item]);
            }
        }
    }

    [Fact]
    public async Task GroupedTitle_AMemberInARestrictedSharedLibrary_IsNotNamed_AndAMainPlacementNeedsTheSameMainLibrary()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, entry) = await SyncOneAsync(rig, items);
                await rig.AddPeerAsync(StateSamples.PeerKeyB, "Reef", 50, DateTime.UtcNow);
                var book = GroupOf(rig, entry);
                const string ReefLib = "00000000000000b1";
                book.Update(doc =>
                {
                    doc.Groups[0].Members[1].LibKey = ReefLib;
                    return true;
                });
                enricher.Groups = book;
                var family = Guid.NewGuid();
                await SetAccessAsync(rig, SyncRig.PeerKey, Selected(family));
                var reefLibrary = new ShoalLibrary { LibKey = ReefLib, Name = "Reef Movies", PathRel = "libs/" + ReefLib, Feeds = [StateSamples.PeerKeyB], Access = Selected() };
                await rig.State.UpdateAsync(s => s.Home.Libraries.Add(reefLibrary));

                // The member's library is known by its key: nobody selected there, so Reef is not named.
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[item]);

                // Open to everyone, but added into a main library whose own access the enricher cannot read: still not named.
                await rig.State.UpdateAsync(s =>
                {
                    var l = s.Home.Libraries.Single(x => x.LibKey == ReefLib);
                    l.Access = Everyone(restricted: true);
                    l.Placement = LibraryPlacement.Main;
                    l.JellyfinLibraryId = Guid.NewGuid();
                });
                Assert.Equal(0, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Hops"], items.Studios[item]);

                // Its own Jellyfin library, open to everyone: named.
                await rig.State.UpdateAsync(s => s.Home.Libraries.Single(x => x.LibKey == ReefLib).Placement = LibraryPlacement.Shoal);
                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal(["Shared by Reef", "Shared by Hops"], items.Studios[item]);
            }
        }
    }

    [Fact]
    public async Task OneServer_ARestrictedLibrary_StillNamesItsOwnFriend()
    {
        var (rig, _, index, items, enricher) = await FriendLabelTests.EnricherRigAsync();
        await using (rig)
        {
            using (index)
            {
                var (item, _) = await SyncOneAsync(rig, items);
                await SetAccessAsync(rig, SyncRig.PeerKey, Selected(Guid.NewGuid()));

                Assert.Equal(1, await enricher.ApplyLabelsAsync());
                Assert.Equal("From Hops's server. " + Tail + "\n\nA hacker.", items.Items[item].Overview);
                Assert.Equal(["Shared by Hops"], items.Studios[item]);
            }
        }
    }
}
