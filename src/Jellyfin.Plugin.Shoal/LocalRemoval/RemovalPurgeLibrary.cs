using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>Fresh read-only shared-state proof for irreversible held-copy removal.</summary>
internal sealed partial class JellyfinRemovalLibrary
{
    /// <inheritdoc />
    public RemovalPurgeState VerifyPurgeState(RemovalTarget target, Guid sharedItemId, IReadOnlyList<RemovalUserState> baseline)
    {
        Ready();
        var generation = Interlocked.Read(ref _generation);
        var topology = PurgeTopology(target);
        RequireOriginalAbsent(target);
        if (sharedItemId == Guid.Empty || baseline.Count is < 1 or > 100 || baseline.Select(s => s.UserId).Distinct().Count() != baseline.Count
            || FindShared(target)?.Id != sharedItemId || PurgeBusy(target, sharedItemId))
        {
            throw Refused();
        }

        var item = _repository.RetrieveItem(sharedItemId);
        if (item is not Movie movie || movie.UserData is null || !movie.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || movie.RunTimeTicks != target.RuntimeTicks || !Matches(movie.ProviderIds, target.ProviderIds) || !SelectedOrigin(movie, target))
        {
            throw Refused();
        }

        var users = _users.GetUsers().Take(101).OrderBy(u => u.Id).ToArray();
        if (users.Length > 100 || users.Select(u => u.Id).Distinct().Count() != users.Length)
        {
            throw Refused();
        }

        // Deletion is stricter than Restore: a removed or disabled affected account
        // requires a new explicit recovery decision, never silent omission.
        foreach (var saved in baseline)
        {
            var user = users.SingleOrDefault(u => u.Id == saved.UserId);
            if (user is null || !Admitted(user, sharedItemId))
            {
                throw Refused();
            }
        }

        var history = new List<RemovalUserState>();
        foreach (var user in users.Where(Eligible))
        {
            var admitted = Admitted(user, sharedItemId);
            if (_visible.IsVisible(user.Id, target.LibraryId) && !admitted)
            {
                throw new InvalidOperationException("accessNotShared"); // P30 sibling: deleting would leave this viewer with nothing
            }

            if (admitted)
            {
                history.Add(Read(user, movie));
            }
        }

        // Include current account/policy facts as well as every supported personal
        // field; the coordinator compares this digest after asynchronous hashing.
        var fingerprint = Fingerprint(new { topology, sharedItemId, movie.Path, movie.RunTimeTicks, movie.ProviderIds, Users = users.Select(JellyfinCallerUsers.Describe).ToArray(), History = history });
        Ready();
        RequireOriginalAbsent(target);
        if (generation != Interlocked.Read(ref _generation) || topology != PurgeTopology(target) || PurgeBusy(target, sharedItemId))
        {
            throw Refused();
        }

        return new(fingerprint, history.ToArray());
    }

    /// <inheritdoc />
    public void VerifyPurgeAdministrator(Guid admin)
    {
        var user = _users.GetUserById(admin);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled) || !user.HasPermission(PermissionKind.IsAdministrator)
            || _state.Status != StateStatus.Ready || _state.Current.PeerRole.Shares.Any(s => s.ShareUserId == admin))
        {
            throw new InvalidOperationException("administratorRequired");
        }
    }

    private string PurgeTopology(RemovalTarget target)
    {
        var state = _state.Current;
        var peer = state.Home.Peers.SingleOrDefault(p => p.PeerKey == target.PeerKey && p.Enabled && p.Pairing?.Method == PairingMethod.Invite) ?? throw Refused();
        if (DestinationAccess(state, target) is null)
        {
            throw Refused();
        }

        var folders = _library.GetVirtualFolders();
        if (folders.SelectMany(v => v.Locations).Append(state.StubRoot).Any(root => !string.IsNullOrWhiteSpace(root)
            && (Inside(target.RecoveryRoot, root) || Inside(root, target.RecoveryRoot))))
        {
            throw Refused();
        }

        var catalog = _catalogs.LoadForPreview(target.PeerKey);
        var entries = catalog.Document.Entries.Where(e => e.Kind == EntryKind.Movie && e.MappingKey == target.MappingKey && Guid.TryParse(e.PeerItemId, out var id) && id == target.PeerItemId).Take(2).ToArray();
        if (catalog.Unreadable || catalog.Dropped != 0 || catalog.Document.LoadedWithLoss || !catalog.Document.CompletedOnce
            || catalog.Document.LastSync?.Result != SyncResultKind.Ok || entries.Length != 1 || entries[0].State != EntryState.Active || entries[0].RuntimeTicks != target.RuntimeTicks || !Matches(entries[0].ProviderIds, target.ProviderIds))
        {
            throw Refused();
        }

        return Fingerprint(new { state.Home, state.PeerRole.Shares, state.StubRoot, peer, Catalog = catalog.Document, Roots = folders });
    }

    private void RequireOriginalAbsent(RemovalTarget target)
    {
        try
        {
            _ = File.GetAttributes(target.Path);
            throw Refused();
        }
        catch (FileNotFoundException)
        {
            // Native staging separately pins and checks nofollow ancestry.
        }

        if (Indexed(target.LocalItemId) is not null)
        {
            throw Refused();
        }
    }

    private bool PurgeBusy(RemovalTarget target, Guid sharedItemId) => _sessions.Sessions.Any(s => s.NowPlayingItem is { } playing
        && (playing.Id == target.LocalItemId || playing.Id == sharedItemId || (Indexed(playing.Id) is { } movie && Matches(movie.ProviderIds, target.ProviderIds))));
}
