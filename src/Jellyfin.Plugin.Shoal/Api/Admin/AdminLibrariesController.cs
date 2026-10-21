using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// The libraries Shoal made and the accounts that may see them, for the admin page (Libraries and access, and the server detail).
/// </summary>
public interface ILibraryView
{
    /// <summary>Builds the whole picture.</summary>
    /// <param name="currentUserId">The asking administrator, or null.</param>
    /// <returns>The libraries and accounts.</returns>
    object Build(Guid? currentUserId);

    /// <summary>Builds the picture for one server.</summary>
    /// <param name="peerKey">The server's handle.</param>
    /// <param name="currentUserId">The asking administrator, or null.</param>
    /// <returns>The libraries of the server and the accounts.</returns>
    (IReadOnlyList<object> Libraries, IReadOnlyList<object> Users) ForPeer(string peerKey, Guid? currentUserId);
}

/// <summary>Who can see a library (ui-spec 13).</summary>
/// <param name="Mode">Selected or Everyone.</param>
/// <param name="SelectedUserIds">The saved selection (kept while the mode is Everyone).</param>
/// <param name="IncludeRestricted">Whether profiles with parental limits are admitted in Everyone mode.</param>
internal sealed record LibraryAccessDto(AccessMode Mode, IReadOnlyList<Guid> SelectedUserIds, bool IncludeRestricted);

/// <summary>An account whose real access differs from Shoal's rule.</summary>
/// <param name="UserId">The local account.</param>
/// <param name="Kind">extraAccess, missingAccess or unmanaged.</param>
internal sealed record DriftDto(Guid UserId, string Kind);

/// <summary>One library Shoal made from a friend's server.</summary>
/// <param name="MappingKey">The page's id for it: the server handle, a dot and the mapping key.</param>
/// <param name="LocalLibraryName">The library's name here.</param>
/// <param name="RemoteName">The library's name on the friend's server (text only).</param>
/// <param name="ServerHandle">The server's handle.</param>
/// <param name="ServerName">The server's name (text only).</param>
/// <param name="Count">Titles with a stub (the same as LocalCount; kept for older pages).</param>
/// <param name="LastSync">The last sync of the server (the catalog is per server), with a skipped "Sync now" after it.</param>
/// <param name="HeldRemovals">Removals on hold in this library.</param>
/// <param name="HeldAdditions">Additions on hold in this library.</param>
/// <param name="Access">Who can see it.</param>
/// <param name="Drift">Accounts whose access differs from the rule.</param>
/// <param name="RemoteCount">Titles (movies or series) the friend lists in this library at the last sync. Always LocalCount + HiddenByDedup + the additions a guard holds back.</param>
/// <param name="LocalCount">Titles of this library that have a stub on this server. Same number as Count.</param>
/// <param name="HiddenByDedup">Titles with no stub because this server, or a better-ranked friend, carries them already.</param>
/// <param name="DedupWinner">The friend that carries most of the hidden titles, or null (this server itself, or none hidden).</param>
internal sealed record LibraryDto(string MappingKey, string LocalLibraryName, string RemoteName, string ServerHandle, string ServerName, int Count, SyncInfoDto? LastSync, int HeldRemovals, int HeldAdditions, LibraryAccessDto Access, IReadOnlyList<DriftDto> Drift, int RemoteCount = 0, int LocalCount = 0, int HiddenByDedup = 0, string? DedupWinner = null)
{
    /// <summary>Gets the mapping's type: <c>movies</c> or <c>tvshows</c> (2.9.3 K3).</summary>
    public string Type { get; init; } = "movies";

    /// <summary>Gets who really can see the titles: the shared library's access when the connection feeds one of the type, else the mapping's own (share users left out).</summary>
    public LibraryAccessDto? EffectiveAccess { get; init; }

    /// <summary>Gets the key (16 lower hex) of the library that governs the titles: the fed library, else the mapping's legacy library, else null.</summary>
    public string? LibKey { get; init; }

    /// <summary>Gets the name of that library (text only), else the mapping's local library name.</summary>
    public string LibraryName { get; init; } = string.Empty;

    /// <summary>Gets where the titles are: <c>Shoal</c> (a shared library of its own), <c>Main</c> (inside a main library) or <c>Legacy</c> (the mapping's own folder).</summary>
    public string Placement { get; init; } = "Legacy";

    /// <summary>Gets the name of the main library the titles are inside (placement Main only, text only), else null.</summary>
    public string? MainLibraryName { get; init; }
}

/// <summary>A local account the picker may list. Sharing accounts Shoal made for friends are never in the list.</summary>
/// <param name="Id">The local user id.</param>
/// <param name="Name">The account name (text only).</param>
/// <param name="ImageTag">Always null here; the page falls back to the initial.</param>
/// <param name="IsAdmin">Whether the account is an administrator.</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
/// <param name="AllLibraries">Whether the account has "all libraries".</param>
/// <param name="ManagedByShoal">Whether Shoal keeps this account's library list.</param>
/// <param name="Restricted">Whether the account has parental limits.</param>
internal sealed record UserDto(Guid Id, string Name, string? ImageTag, bool IsAdmin, bool IsDisabled, bool AllLibraries, bool ManagedByShoal, bool Restricted);

/// <summary>GET Shoal/Admin/Libraries.</summary>
/// <param name="Mappings">The libraries.</param>
/// <param name="Users">The accounts.</param>
/// <param name="CurrentUserId">The administrator who is asking.</param>
internal sealed record LibrariesDto(IReadOnlyList<LibraryDto> Mappings, IReadOnlyList<UserDto> Users, Guid? CurrentUserId);

/// <summary>The body of PUT Shoal/Admin/Libraries/(id)/Access.</summary>
internal sealed record AccessRequest
{
    /// <summary>Gets the new mode, or null to keep it.</summary>
    [JsonPropertyName("mode")]
    public AccessMode? Mode { get; init; }

    /// <summary>Gets the new selection, or null to keep the saved one.</summary>
    [JsonPropertyName("selectedUserIds")]
    public List<Guid>? SelectedUserIds { get; init; }

    /// <summary>Gets whether profiles with parental limits are admitted, or null to keep it.</summary>
    [JsonPropertyName("includeRestricted")]
    public bool? IncludeRestricted { get; init; }

    /// <summary>Gets a value indicating whether the admin confirmed that ratings come from the friend's server.</summary>
    [JsonPropertyName("ratingsAcknowledged")]
    public bool RatingsAcknowledged { get; init; }
}

/// <summary>
/// <see cref="ILibraryView"/> over the state, the catalogs, Jellyfin's libraries and accounts, and the visibility service.
/// </summary>
public sealed class AdminLibraryView : ILibraryView
{
    private readonly IStateStore _state;
    private readonly CatalogStore _catalogs;
    private readonly ILibrarySource _libraries;
    private readonly IAccountSource _accounts;
    private readonly VisibilityService _visibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminLibraryView"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="accounts">Jellyfin's accounts.</param>
    /// <param name="visibility">The visibility service (the drift list).</param>
    public AdminLibraryView(IStateStore state, CatalogStore catalogs, ILibrarySource libraries, IAccountSource accounts, VisibilityService visibility)
    {
        _state = state;
        _catalogs = catalogs;
        _libraries = libraries;
        _accounts = accounts;
        _visibility = visibility;
    }

    /// <summary>The page's id for a mapping: it is unique across servers, the mapping key alone is not.</summary>
    /// <param name="peerKey">The server.</param>
    /// <param name="mappingKey">The mapping.</param>
    /// <returns>The id.</returns>
    public static string Compose(string peerKey, string mappingKey) => peerKey + "." + mappingKey;

    /// <summary>Splits a page id again.</summary>
    /// <param name="id">The id.</param>
    /// <param name="peerKey">The server.</param>
    /// <param name="mappingKey">The mapping.</param>
    /// <returns>True when it has both parts.</returns>
    public static bool TrySplit(string id, out string peerKey, out string mappingKey)
    {
        var dot = id.IndexOf('.', StringComparison.Ordinal);
        peerKey = dot > 0 ? id[..dot] : string.Empty;
        mappingKey = dot > 0 ? id[(dot + 1)..] : string.Empty;
        return dot > 0 && mappingKey.Length > 0;
    }

    /// <inheritdoc />
    public object Build(Guid? currentUserId)
    {
        var (libraries, users) = Collect(null);
        return new LibrariesDto(libraries, users, currentUserId);
    }

    /// <inheritdoc />
    public (IReadOnlyList<object> Libraries, IReadOnlyList<object> Users) ForPeer(string peerKey, Guid? currentUserId)
    {
        var (libraries, users) = Collect(peerKey);
        return ([.. libraries], [.. users]);
    }

    private (List<LibraryDto> Libraries, List<UserDto> Users) Collect(string? onlyPeer)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return ([], []);
        }

        var state = _state.Current;
        var shareUsers = VisibilityService.ShareUserIds(state);
        var accounts = _accounts.List().Where(a => !shareUsers.Contains(a.Id)).ToList();
        var managed = state.Home.Visibility.ManagedUsers.Keys.ToHashSet();
        var drift = _visibility.Drift();
        var jellyfin = _libraries.List();
        var shoal = VisibilityService.ShoalLibraries(state, jellyfin);
        LibraryAccessDto Visible(LibraryAccess access) => new(access.Mode, [.. access.SelectedUserIds.Where(id => !shareUsers.Contains(id))], access.IncludeRestricted);
        var result = new List<LibraryDto>();
        foreach (var peer in state.Home.Peers.Where(p => onlyPeer is null || string.Equals(p.PeerKey, onlyPeer, StringComparison.Ordinal)))
        {
            var document = _catalogs.Load(peer.PeerKey).Document;
            var last = SyncInfoDto.From(PeerSyncInfo.Of(document.LastSync, _catalogs.LastSkipped(peer.PeerKey)));
            foreach (var mapping in peer.Libraries)
            {
                var entries = document.Entries.Where(e => string.Equals(e.MappingKey, mapping.MappingKey, StringComparison.Ordinal)).ToList();
                var counts = TitleCounts.Of(entries);
                var library = shoal.FirstOrDefault(l => l.PeerKey == peer.PeerKey && l.MappingKey == mapping.MappingKey);
                var differences = new List<DriftDto>();
                if (library is not null)
                {
                    foreach (var d in drift.Differences.Where(d => d.LibraryId == library.Id && !shareUsers.Contains(d.UserId)))
                    {
                        differences.Add(new DriftDto(d.UserId, d.HasAccess ? "extraAccess" : "missingAccess"));
                    }

                    if (differences.Count > 0)
                    {
                        differences.AddRange(drift.StoppedManaging.Where(id => !shareUsers.Contains(id)).Select(id => new DriftDto(id, "unmanaged")));
                    }
                }

                // 2.9.3 K3: the library that governs the titles. A fed library's access decides (LibraryFolders.AccessOfMapping); the mapping's own is dead then.
                var fed = LibraryFolders.FedBy(state, peer.PeerKey, mapping.CollectionType);
                var governing = fed ?? LibraryFolders.LegacyOf(state, peer.PeerKey, mapping);
                string? mainName = null;
                if (fed is { Placement: LibraryPlacement.Main } && MainAccess.MainIdOf(state, fed, jellyfin) is { } mainId && mainId != Guid.Empty)
                {
                    mainName = jellyfin.FirstOrDefault(l => l.Id == mainId)?.Name;
                }

                result.Add(new LibraryDto(
                    Compose(peer.PeerKey, mapping.MappingKey),
                    mapping.LocalLibraryName,
                    mapping.RemoteName,
                    peer.PeerKey,
                    peer.DisplayName,
                    counts.Local,
                    last,
                    entries.Count(e => e.State == EntryState.HeldRemoval),
                    HeldAdditionsOf(document, mapping.MappingKey),
                    Visible(mapping.Access),
                    differences,
                    counts.Remote,
                    counts.Local,
                    counts.Hidden,
                    counts.Hidden == 0 ? null : TitleCounts.Winner(_state, _catalogs, peer.PeerKey, entries))
                {
                    Type = mapping.CollectionType,
                    EffectiveAccess = Visible(LibraryFolders.AccessOfMapping(state, peer.PeerKey, mapping)),
                    LibKey = string.IsNullOrEmpty(governing?.LibKey) ? null : governing.LibKey,
                    LibraryName = governing?.Name is { Length: > 0 } name ? name : mapping.LocalLibraryName,
                    Placement = fed is null ? "Legacy" : fed.Placement.ToString(),
                    MainLibraryName = mainName,
                });
            }
        }

        var users = accounts.Select(a => new UserDto(a.Id, a.Name, null, a.IsAdministrator, a.IsDisabled, a.EnableAllFolders, managed.Contains(a.Id), a.IsRestricted)).ToList();
        return (result, users);
    }

    // 2.9.2 K3: held movies by their mapping, plus the held episodes of the mapping's series groups (no entry ever has the HeldAddition state).
    private static int HeldAdditionsOf(CatalogDocument document, string mappingKey) =>
        document.HeldAdditionInfo.Count(h => string.Equals(h.MappingKey, mappingKey, StringComparison.Ordinal))
        + document.HeldSeries.Where(h => string.Equals(h.MappingKey, mappingKey, StringComparison.Ordinal)).Sum(static h => h.Episodes);
}

/// <summary>
/// Libraries &amp; access (ui-spec 8.4, DESIGN 5.6, owner D3/D4): who can see each library Shoal made from a friend's server. Admin only.
/// Sharing accounts that Shoal made for friends are never listed and can never be selected.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminLibrariesController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId"; // core's InternalClaimTypes.UserId

    private readonly IStateStore _state;
    private readonly ILibraryView _view;
    private readonly AccessService _access;
    private readonly VisibilityService _visibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminLibrariesController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="view">The libraries and accounts view.</param>
    /// <param name="access">The access rules.</param>
    /// <param name="visibility">The visibility service.</param>
    public AdminLibrariesController(IStateStore state, ILibraryView view, AccessService access, VisibilityService visibility)
    {
        _state = state;
        _view = view;
        _access = access;
        _visibility = visibility;
    }

    /// <summary>
    /// Every library, its access and drift, and the accounts.
    /// </summary>
    /// <returns>The libraries and accounts.</returns>
    [HttpGet("Libraries")]
    [Produces("application/json")]
    public IActionResult GetLibraries() => AdminReply.Json(StatusCodes.Status200OK, _view.Build(CurrentUser()));

    /// <summary>
    /// Changes who can see one library. Stored and reconciled; nothing is written when the change is refused.
    /// </summary>
    /// <param name="id">The library's id (server handle, a dot, the mapping key).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new access, or 400, 404 or 409.</returns>
    [HttpPut("Libraries/{id}/Access")]
    public async Task<IActionResult> PutAccess(string id, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!AdminLibraryView.TrySplit(id, out var peerKey, out var mappingKey))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<AccessRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || (body.Mode is { } mode && !Enum.IsDefined(mode)))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var result = await _access.SetAsync(peerKey, mappingKey, new AccessChange(body.Mode, body.SelectedUserIds, body.IncludeRestricted, body.RatingsAcknowledged), CurrentUser() ?? Guid.Empty, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return result.Error switch
            {
                AccessError.RecoveryMode => AdminReply.Error(StatusCodes.Status409Conflict, "recovery"),
                AccessError.UnknownLibrary => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
                AccessError.SharedLibrary => AdminReply.Error(StatusCodes.Status409Conflict, "sharedLibrary"),
                AccessError.ShareUser => AdminReply.Error(StatusCodes.Status400BadRequest, "shareUser"),
                AccessError.UnknownUser => AdminReply.Error(StatusCodes.Status400BadRequest, "unknownUser"),
                AccessError.AcknowledgementRequired => AdminReply.Error(StatusCodes.Status400BadRequest, "acknowledgementRequired"),
                _ => AdminReply.Error(StatusCodes.Status400BadRequest, "invalid")
            };
        }

        var stored = _state.Current.Home.Peers.First(p => p.PeerKey == peerKey).Libraries.First(l => l.MappingKey == mappingKey).Access;
        return AdminReply.Json(StatusCodes.Status200OK, new { access = new LibraryAccessDto(stored.Mode, stored.SelectedUserIds, stored.IncludeRestricted) });
    }

    /// <summary>
    /// "Re-apply now": makes Jellyfin's real access match Shoal's rule again.
    /// </summary>
    /// <param name="id">The library's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 409.</returns>
    [HttpPost("Libraries/{id}/Reconcile")]
    public async Task<IActionResult> Reconcile(string id, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!AdminLibraryView.TrySplit(id, out var peerKey, out var mappingKey)
            || !_state.Current.Home.Peers.Any(p => p.PeerKey == peerKey && p.Libraries.Any(l => l.MappingKey == mappingKey)))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        await _visibility.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return AdminReply.Json(StatusCodes.Status200OK, new { });
    }

    private Guid? CurrentUser() => User.FindFirst(UserIdClaim)?.Value is { } value && Guid.TryParse(value, out var id) ? id : null;
}
