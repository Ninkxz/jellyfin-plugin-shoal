using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Sharing;

/// <summary>What the Overview needs to know about a friend right now.</summary>
public interface IShareLiveSource
{
    /// <summary>Reads what is known about one share.</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The live information (never null).</returns>
    ShareLiveInfo Live(string shareId);
}

/// <summary>
/// Invites and approvals of the shares (K22, DESIGN 4.1b steps 2 and 3). The share service asks it; until it exists nothing is offered.
/// </summary>
public interface IShareInvites
{
    /// <summary>Reads the invite state of a share: none, active (with its end) or expired. Never the secret.</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>An object with <c>state</c> and, when active, <c>expiresUtc</c>.</returns>
    object InviteState(string shareId);

    /// <summary>Creates a new invite (replacing the old one) or cancels the current one.</summary>
    /// <param name="share">The share.</param>
    /// <param name="create">True to create.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and, for a new invite, <c>{invite, expiresUtc}</c> once.</returns>
    Task<(ShareOutcome Outcome, object? Body)> InviteAsync(ShareState share, bool create, CancellationToken cancellationToken);

    /// <summary>Forgets the share's invite and approval whatever their state (used when the share is revoked).</summary>
    /// <param name="shareId">The share.</param>
    void Forget(string shareId);

    /// <summary>Reads the pending approval (never the safety code).</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The approval, or null when none is pending.</returns>
    object? Approval(string shareId);

    /// <summary>Approves with the typed safety code, or denies.</summary>
    /// <param name="share">The share.</param>
    /// <param name="request">The decision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the answer.</returns>
    Task<(ShareOutcome Outcome, object? Body)> DecideAsync(ShareState share, ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>No invites yet.</summary>
public sealed class NoShareInvites : IShareInvites
{
    /// <inheritdoc />
    public object InviteState(string shareId) => new { state = "none" };

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> InviteAsync(ShareState share, bool create, CancellationToken cancellationToken) => Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.NotBuilt, null));

    /// <inheritdoc />
    public void Forget(string shareId)
    {
    }

    /// <inheritdoc />
    public object? Approval(string shareId) => null;

    /// <inheritdoc />
    public Task<(ShareOutcome Outcome, object? Body)> DecideAsync(ShareState share, ApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult<(ShareOutcome, object?)>((ShareOutcome.Conflict, null));
}

/// <summary>
/// The Peer role's share service (K21; DESIGN 4.1b, 4.5): the sharing account with its full policy and device lock, the device sweep, drift
/// repair, pause and resume, and revocation. Libraries under the StubRoot (or Shoal's own) are refused, so friends never chain.
/// </summary>
public sealed class ShareService : IShareService, IShareLiveSource
{
    /// <summary>The sweep and drift repair interval (DESIGN 4.1b steps 4 and 5).</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly IStateStore _state;
    private readonly IShareUserSource _users;
    private readonly ILibrarySource _libraries;
    private readonly IShareInvites _invites;
    private readonly TimeProvider _time;
    private readonly ShareLocks _locks;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, int> _unexpected = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts and devices.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="invites">Invites and approvals.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    public ShareService(IStateStore state, IShareUserSource users, ILibrarySource libraries, IShareInvites invites, TimeProvider time, ILogger<ShareService> logger, ShareLocks? locks = null)
        : this(state, users, libraries, invites, time, (ILogger)logger, locks)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareService"/> class (tests: any logger).
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="users">Jellyfin's accounts and devices.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="invites">Invites and approvals.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="locks">The per-share locks shared with rotation, approval, pause and revoke (a private set when none is given).</param>
    internal ShareService(IStateStore state, IShareUserSource users, ILibrarySource libraries, IShareInvites invites, TimeProvider time, ILogger logger, ShareLocks? locks = null)
    {
        _locks = locks ?? new ShareLocks();
        _state = state;
        _users = users;
        _libraries = libraries;
        _invites = invites;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public object List() => new
    {
        shares = Shares().Select(s => new
        {
            handle = s.ShareId,
            friendName = s.FriendName,
            status = Status(s),
            libraryIds = s.LibraryIds,
            libraryNames = Names(s),
            maxBitrateMbps = s.MaxBitrateMbps,
            maxConcurrentTranscodes = s.MaxConcurrentTranscodes,
            unexpectedDeviceCount = _unexpected.GetValueOrDefault(s.ShareId)
        }).ToList(),
        availableLibraries = Available()
    };

    /// <inheritdoc />
    public object? Read(string shareId)
    {
        if (Find(shareId) is not { } s)
        {
            return null;
        }

        var devices = _users.Exists(s.ShareUserId) ? _users.Devices(s.ShareUserId) : [];
        return new
        {
            handle = s.ShareId,
            friendName = s.FriendName,
            status = Status(s),
            shareUserName = string.IsNullOrEmpty(s.ShareUserName) ? null : s.ShareUserName,
            sinceUtc = s.CreatedUtc == default ? (DateTime?)null : s.CreatedUtc,
            libraryIds = s.LibraryIds,
            maxBitrateMbps = s.MaxBitrateMbps,
            maxConcurrentTranscodes = s.MaxConcurrentTranscodes,
            lastRotatedUtc = s.LastRotatedUtc,
            rotationCount = s.RotationCount,
            rotationFailed = s.RotationFailedUtc is not null,
            deviceCount = devices.Count,
            devices = devices.Select(d => new
            {
                deviceHandle = ShareDevices.Handle(d.DeviceId),
                name = d.Name,
                app = d.App,
                version = d.Version,
                firstSeenUtc = d.FirstSeenUtc,
                lastSeenUtc = d.LastSeenUtc,
                expected = s.ExpectedDeviceIds.Contains(d.DeviceId, StringComparer.Ordinal),
                signedOut = false
            }).ToList(),
            invite = _invites.InviteState(s.ShareId),
            approval = _invites.Approval(s.ShareId),
            availableLibraries = Available(),
            hardeningMode = _state.Current.PeerRole.HardeningMode.ToString()
        };
    }

    /// <inheritdoc />
    public async Task<(ShareOutcome Outcome, object? Body)> CreateAsync(ShareRequest request, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return (ShareOutcome.Conflict, null);
        }

        var name = (request.FriendName ?? string.Empty).Trim();
        if (name.Length == 0 || Shares().Any(s => string.Equals(s.FriendName, name, StringComparison.OrdinalIgnoreCase)) || !LibrariesAllowed(request.LibraryIds ?? []))
        {
            return (ShareOutcome.Invalid, null);
        }

        var share = new ShareState
        {
            ShareId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            FriendName = name,
            ShareUserName = "shoal-" + Slug(name) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2)),
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            LibraryIds = [.. request.LibraryIds ?? []],
            MaxBitrateMbps = request.MaxBitrateMbps,
            MaxConcurrentTranscodes = request.MaxConcurrentTranscodes ?? 4,
            Enabled = true
        };

        // A random password nobody ever sees: the account can only be used through a token Home is given (DESIGN 4.1b step 1).
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // The account work is not cancelled halfway (a client that went away must not leave a half-made account), and the account is
        // recorded in the state as soon as it exists: whatever fails after that is either rolled back below or repaired by the sweep (Warden M3).
        try
        {
            share.ShareUserId = await _users.CreateAsync(share.ShareUserName, password, CancellationToken.None).ConfigureAwait(false);
            await _state.UpdateAsync(s => s.PeerRole.Shares.Add(share), CancellationToken.None).ConfigureAwait(false);
            await _users.ApplyPolicyAsync(share.ShareUserId, PolicyOf(share, _state.Current), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever failed, the half-made share is rolled back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The sharing account could not be created: {Error}", ex.GetType().Name);
            await RollBackCreateAsync(share).ConfigureAwait(false);
            return (ShareOutcome.Invalid, null);
        }

        _logger.LogInformation("A share was created");
        return (ShareOutcome.Ok, new { handle = share.ShareId, friendName = share.FriendName, libraryIds = share.LibraryIds });
    }

    /// <inheritdoc />
    public async Task<ShareOutcome> UpdateAsync(string shareId, ShareRequest request, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share)
        {
            return ShareOutcome.NotFound;
        }

        if (!LibrariesAllowed(request.LibraryIds ?? []))
        {
            return ShareOutcome.Invalid;
        }

        await _state.UpdateAsync(
            s =>
            {
                var target = s.PeerRole.Shares.First(x => x.ShareId == shareId);
                target.LibraryIds = [.. request.LibraryIds ?? []];
                target.MaxBitrateMbps = request.MaxBitrateMbps;
                target.MaxConcurrentTranscodes = request.MaxConcurrentTranscodes ?? target.MaxConcurrentTranscodes;
            },
            cancellationToken).ConfigureAwait(false);
        await RepairAsync(Find(shareId) ?? share, cancellationToken).ConfigureAwait(false);
        return ShareOutcome.Ok;
    }

    /// <inheritdoc />
    public async Task<ShareOutcome> SetEnabledAsync(string shareId, bool enabled, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share)
        {
            return ShareOutcome.NotFound;
        }

        using var held = await _locks.HoldAsync(shareId, cancellationToken).ConfigureAwait(false); // not while a rotation or an approval of this share runs (Warden 10 M2)
        if (enabled)
        {
            // Resume first sweeps: only Home's device may be left to come back to life (DESIGN 4.1b step 6).
            await SweepOneAsync(share, cancellationToken).ConfigureAwait(false);
        }

        await _state.UpdateAsync(s => s.PeerRole.Shares.First(x => x.ShareId == shareId).Enabled = enabled, cancellationToken).ConfigureAwait(false);
        if (_users.Exists(share.ShareUserId))
        {
            await _users.ApplyPolicyAsync(share.ShareUserId, PolicyOf(Find(shareId) ?? share, _state.Current), cancellationToken).ConfigureAwait(false);
        }

        return ShareOutcome.Ok;
    }

    /// <inheritdoc />
    public async Task<(ShareOutcome Outcome, object? Body)> RevokeAsync(string shareId, string? confirmName, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share)
        {
            return (ShareOutcome.NotFound, null);
        }

        // The typed name is compared with the stored name before anything happens.
        if (!string.Equals(confirmName, share.FriendName, StringComparison.Ordinal))
        {
            return (ShareOutcome.ConfirmName, null);
        }

        using var held = await _locks.HoldAsync(shareId, cancellationToken).ConfigureAwait(false);

        // The invite goes first (an approval in flight is dropped), and the share is recorded as paused before any sign-out:
        // a revoke that fails halfway must not be undone by the drift repair (Warden M4).
        _invites.Forget(shareId);
        await _state.UpdateAsync(s => s.PeerRole.Shares.First(x => x.ShareId == shareId).Enabled = false, cancellationToken).ConfigureAwait(false);
        share = Find(shareId) ?? share;
        var disabled = false;
        var signedOut = 0;
        var remaining = 0;
        var deleted = false;
        if (_users.Exists(share.ShareUserId))
        {
            // Disable first (no new tokens), sign out every device, check none is left (once more if so), then delete (DESIGN 4.5).
            try
            {
                await _users.ApplyPolicyAsync(share.ShareUserId, PolicyOf(share, _state.Current) with { Disabled = true }, cancellationToken).ConfigureAwait(false);
                disabled = true;
            }
            catch (InvalidOperationException)
            {
                disabled = false;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var devices = _users.Devices(share.ShareUserId);
                foreach (var device in devices)
                {
                    await _users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, cancellationToken).ConfigureAwait(false);
                    signedOut++;
                }

                remaining = _users.Devices(share.ShareUserId).Count;
                if (remaining == 0)
                {
                    break;
                }
            }

            deleted = await TryDeleteAsync(share.ShareUserId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            disabled = true;
            deleted = true;
        }

        if (deleted)
        {
            await _state.UpdateAsync(s => s.PeerRole.Shares.RemoveAll(x => x.ShareId == shareId), cancellationToken).ConfigureAwait(false);
            _unexpected.TryRemove(shareId, out _);
        }

        if (deleted)
        {
            _logger.LogInformation("A share was revoked (accountDeleted={Deleted}, devicesLeft={Left})", deleted, remaining);
        }
        else
        {
            _logger.LogError("A share was revoked but its account could not be deleted; the share stays listed as paused (devicesLeft={Left})", remaining);
        }

        return (ShareOutcome.Ok, new { disabled, signedOut, remaining, deleted });
    }

    /// <inheritdoc />
    public async Task<(ShareOutcome Outcome, object? Body)> InviteAsync(string shareId, bool create, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share)
        {
            return (ShareOutcome.NotFound, null);
        }

        // A paused share gets no new invite (Warden M1): resuming it is a decision of its own.
        return create && !share.Enabled ? (ShareOutcome.Conflict, null) : await _invites.InviteAsync(share, create, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public object? Approval(string shareId) => Find(shareId) is null ? null : _invites.Approval(shareId) ?? new { state = "none" };

    /// <inheritdoc />
    public async Task<(ShareOutcome Outcome, object? Body)> DecideAsync(string shareId, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share)
        {
            return (ShareOutcome.NotFound, null);
        }

        // Approving mints a token and unlocks a device: not while the share is paused. Denying is always fine.
        return request.Approve && !share.Enabled ? (ShareOutcome.Conflict, null) : await _invites.DecideAsync(share, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ShareOutcome> RemoveDeviceAsync(string shareId, string deviceHandle, CancellationToken cancellationToken)
    {
        if (Find(shareId) is not { } share || !_users.Exists(share.ShareUserId))
        {
            return ShareOutcome.NotFound;
        }

        var device = _users.Devices(share.ShareUserId).FirstOrDefault(d => string.Equals(ShareDevices.Handle(d.DeviceId), deviceHandle, StringComparison.Ordinal));
        if (device is null)
        {
            return ShareOutcome.NotFound;
        }

        await _users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, cancellationToken).ConfigureAwait(false);
        return ShareOutcome.Ok;
    }

    /// <inheritdoc />
    public ShareLiveInfo Live(string shareId)
    {
        if (Find(shareId) is not { } share)
        {
            return new ShareLiveInfo();
        }

        if (!_users.Exists(share.ShareUserId))
        {
            return new ShareLiveInfo { AccessRemoved = true };
        }

        var devices = _users.Devices(share.ShareUserId);
        var activity = _users.Activity(share.ShareUserId);
        return new ShareLiveInfo
        {
            DeviceCount = devices.Count,
            UnexpectedDeviceCount = _unexpected.GetValueOrDefault(shareId),
            LastSeenUtc = devices.Count == 0 ? null : devices.Max(d => d.LastSeenUtc),
            PlayingNow = activity.Playing,
            TranscodingNow = activity.Transcoding
        };
    }

    /// <summary>
    /// One pass of the device sweep and the drift repair over every share (DESIGN 4.1b steps 4 and 5). Run every 60 seconds and when a
    /// sharing account is updated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task SweepAllAsync(CancellationToken cancellationToken)
    {
        foreach (var share in Shares().ToList())
        {
            if (!_users.Exists(share.ShareUserId))
            {
                continue;
            }

            await SweepOneAsync(share, cancellationToken).ConfigureAwait(false);
            await RepairAsync(share, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings every sharing account's policy in line now (Warden 10 L5): run right after the library tags change (a library created, removed or moved), so a new
    /// library's tag reaches the friends' accounts at once instead of at the next 60-second sweep.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when every share was checked.</returns>
    public async Task RepairPoliciesAsync(CancellationToken cancellationToken)
    {
        foreach (var share in Shares().ToList())
        {
            await RepairAsync(share, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a Jellyfin user is one of the sharing accounts.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>True for a sharing account.</returns>
    public bool IsShareUser(Guid userId) => Shares().Any(s => s.ShareUserId == userId);

    /// <summary>The policy a share's account must have.</summary>
    /// <param name="share">The share.</param>
    /// <param name="state">The state (the library tags to block), or null for the hide-all tag alone.</param>
    /// <returns>The wanted policy.</returns>
    internal static SharePolicy PolicyOf(ShareState share, ShoalState? state = null) =>
        new(!share.Enabled, share.LibraryIds, share.MaxBitrateMbps is { } mbps ? checked(mbps * 1_000_000) : null, share.ExpectedDeviceIds, ShareBlockedTags(state));

    /// <summary>
    /// The tags a sharing account never sees (K45, the ShareService rule): <c>Shoal</c>, which every stub carries, and the tag of every Shoal library, so the titles
    /// Home added into a main library stay hidden even from a share of that main library. Friends never chain.
    /// </summary>
    /// <param name="state">The state, or null.</param>
    /// <returns>The tags.</returns>
    internal static IReadOnlyList<string> ShareBlockedTags(ShoalState? state) =>
        [LibraryFolders.HideAllTag, .. (state?.Home.Libraries ?? []).Where(static l => !l.IsLegacy).Select(static l => LibraryFolders.LibTag(l.LibKey)).Order(StringComparer.Ordinal)];

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Where(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9')]);
        return slug.Length == 0 ? "friend" : slug[..Math.Min(slug.Length, 20)];
    }

    private string Status(ShareState share) => !_users.Exists(share.ShareUserId) ? "AccessRemoved" : share.Enabled ? "Active" : "Paused";

    private List<ShareState> Shares() => _state.Status == StateStatus.Ready ? _state.Current.PeerRole.Shares : [];

    private ShareState? Find(string shareId) => Shares().FirstOrDefault(s => string.Equals(s.ShareId, shareId, StringComparison.Ordinal));

    private List<string> Names(ShareState share)
    {
        var known = _libraries.List().ToDictionary(static l => l.Id, static l => l.Name);
        return [.. share.LibraryIds.Select(id => known.TryGetValue(id, out var n) ? n : string.Empty).Where(static n => n.Length > 0)];
    }

    private List<object> Available()
    {
        var state = _state.Current;
        var all = _libraries.List();
        var shoalIds = VisibilityService.ShoalLibraries(state, all).Select(static l => l.Id).ToHashSet();

        // A library that comes from a friend is listed but cannot be picked, and the page says why (DESIGN v2.6.2; POST Shares still refuses it).
        return
        [
            .. all.Where(l => !UnderStubRoot(state, l) || shoalIds.Contains(l.Id)).Select(l =>
            {
                var federated = shoalIds.Contains(l.Id);
                return (object)new { id = l.Id, name = l.Name, type = l.CollectionType.Length > 0 ? l.CollectionType : "movies", selectable = !federated, shareable = !federated, reason = federated ? "federated" : null };
            }),
        ];
    }

    private bool LibrariesAllowed(IReadOnlyList<Guid> ids)
    {
        var state = _state.Current;
        var all = _libraries.List();
        var shoalIds = VisibilityService.ShoalLibraries(state, all).Select(static l => l.Id).ToHashSet();
        return ids.Distinct().All(id => all.FirstOrDefault(l => l.Id == id) is { } l && !shoalIds.Contains(id) && !UnderStubRoot(state, l));
    }

    // A library under, or containing, the StubRoot would let a friend share what Shoal made from another friend (W§A-20.1). One exception (K45): a main library
    // that holds the recorded folder of a Shoal library with placement Main stays shareable; that folder is not counted. The friend still never sees those titles:
    // the sharing account blocks the Shoal tag and every library tag (ShareBlockedTags), and CallerContext never admits a sharing account.
    private static bool UnderStubRoot(ShoalState state, LibraryInfo library)
    {
        var stubRoot = state.StubRoot;
        if (string.IsNullOrWhiteSpace(stubRoot))
        {
            return false;
        }

        // Full paths with links resolved, compared on separator boundaries; a path that cannot be resolved is refused (Warden M5).
        try
        {
            var root = Canonical(stubRoot);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sep = System.IO.Path.DirectorySeparatorChar;
            var mainFolders = state.Home.Libraries
                .Where(static l => !l.IsLegacy && l.Placement == LibraryPlacement.Main)
                .Select(l => LibraryFolders.PathOf(stubRoot, l))
                .OfType<string>()
                .Select(Canonical)
                .ToList();
            var others = library.Locations.Select(Canonical).Where(path => !mainFolders.Any(m => string.Equals(m, path, comparison))).ToList();
            if (others.Count == 0 && library.Locations.Count > 0)
            {
                return true; // nothing but Shoal folders: never a library of the admin's own
            }

            return others.Any(path => string.Equals(path, root, comparison) || path.StartsWith(root + sep, comparison) || root.StartsWith(path + sep, comparison));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static string Canonical(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            for (var d = new System.IO.DirectoryInfo(full); d is not null; d = d.Parent)
            {
                if (d.LinkTarget is null || d.ResolveLinkTarget(true) is not { } target)
                {
                    continue;
                }

                var rest = System.IO.Path.GetRelativePath(d.FullName, full);
                full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rest == "." ? target.FullName : System.IO.Path.Combine(target.FullName, rest)));
                changed = true;
                break;
            }

            if (!changed)
            {
                break;
            }
        }

        return full;
    }

    private async Task SweepOneAsync(ShareState share, CancellationToken cancellationToken)
    {
        if (!_users.Exists(share.ShareUserId))
        {
            return;
        }

        foreach (var device in _users.Devices(share.ShareUserId).Where(d => !share.ExpectedDeviceIds.Contains(d.DeviceId, StringComparer.Ordinal)).ToList())
        {
            await _users.RemoveDeviceAsync(share.ShareUserId, device.DeviceId, cancellationToken).ConfigureAwait(false);
            _unexpected.AddOrUpdate(share.ShareId, 1, static (_, n) => n + 1);
            _logger.LogWarning("An unexpected device of a sharing account was signed out");
        }
    }

    private async Task RepairAsync(ShareState share, CancellationToken cancellationToken)
    {
        if (!_users.Exists(share.ShareUserId))
        {
            return;
        }

        // A library that has since come to lie under the StubRoot (a moved folder, a new link) is taken off the share.
        var state = _state.Current;
        var all = _libraries.List();
        var banned = share.LibraryIds.Where(id => all.FirstOrDefault(l => l.Id == id) is { } l && UnderStubRoot(state, l)).ToList();
        if (banned.Count > 0)
        {
            await _state.UpdateAsync(s => s.PeerRole.Shares.First(x => x.ShareId == share.ShareId).LibraryIds = [.. s.PeerRole.Shares.First(x => x.ShareId == share.ShareId).LibraryIds.Except(banned)], cancellationToken).ConfigureAwait(false);
            share = Find(share.ShareId) ?? share;
            _logger.LogWarning("A library under the stub root was taken off a share");
        }

        var wanted = PolicyOf(share, _state.Current);
        if (!_users.PolicyMatches(share.ShareUserId, wanted))
        {
            await _users.ApplyPolicyAsync(share.ShareUserId, wanted, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("A sharing account's policy was edited outside Shoal and has been repaired");
        }
    }

    private async Task RollBackCreateAsync(ShareState share)
    {
        try
        {
            await _state.UpdateAsync(s => s.PeerRole.Shares.RemoveAll(x => x.ShareId == share.ShareId), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The account is still deleted below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("The share record could not be removed: {Error}", ex.GetType().Name);
        }

        if (share.ShareUserId != Guid.Empty)
        {
            await TryDeleteAsync(share.ShareUserId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryDeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await _users.DeleteAsync(userId, cancellationToken).ConfigureAwait(false);
            return !_users.Exists(userId);
        }
#pragma warning disable CA1031 // A failed delete is reported, not thrown (the sweep and the owner see the account).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("A sharing account could not be deleted: {Error}", ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>
/// Runs the share sweep and the drift repair every 60 seconds (DESIGN 4.1b steps 4 and 5).
/// </summary>
public sealed class ShareSweeper : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ShareService _shares;
    private readonly InviteBook _invites;
    private readonly MediaBrowser.Controller.Library.IUserManager _users;
    private readonly ILogger<ShareSweeper> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareSweeper"/> class.
    /// </summary>
    /// <param name="shares">The share service.</param>
    /// <param name="invites">The invites (uncollected tokens are signed out here).</param>
    /// <param name="users">The user manager (a change to a sharing account wakes the sweep).</param>
    /// <param name="logger">The logger.</param>
    public ShareSweeper(ShareService shares, InviteBook invites, MediaBrowser.Controller.Library.IUserManager users, ILogger<ShareSweeper> logger)
    {
        _shares = shares;
        _invites = invites;
        _users = users;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether a sweep has been asked for and has not run yet.</summary>
    internal bool WakePending => _wake.CurrentCount > 0;

    /// <inheritdoc />
    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    /// <summary>Wakes the sweep early because a user changed (a no-op for anyone but a sharing account).</summary>
    /// <param name="userId">The user.</param>
    internal void UserChanged(Guid userId)
    {
        if (!_shares.IsShareUser(userId))
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sweep is already due.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The sweep runs every 60 seconds and also soon after a sharing account changes (DESIGN 4.1b step 4; Warden L4).
        void OnUpdated(object? sender, Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> e) => UserChanged(e.Argument.Id);
        _users.OnUserUpdated += OnUpdated;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await _wake.WaitAsync(ShareService.SweepInterval, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); // let a burst of changes settle
                }

                try
                {
                    await _shares.SweepAllAsync(stoppingToken).ConfigureAwait(false);
                    await _invites.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A failed pass must not stop the next one.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning("The share sweep failed: {Error}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _users.OnUserUpdated -= OnUpdated;
        }
    }
}
