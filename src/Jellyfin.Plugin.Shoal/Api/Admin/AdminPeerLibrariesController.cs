using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// Choosing a friend's libraries (DESIGN 9.6, wizard step 5): list what the share user can see, add libraries (a mapping, then the local library
/// below the stub root, visible to the pairing admin only unless the admin picked more people, owner D3) and remove them again.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminPeerLibrariesController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId"; // core's InternalClaimTypes.UserId
    private const int MaxViews = 50;
    private const int MaxPerRequest = 20;

    private readonly IStateStore _state;
    private readonly IPeerEndpoints _peers;
    private readonly PeerCatalogReader _reader;
    private readonly ShoalLibraryService _libraries;
    private readonly AccessService _access;
    private readonly CatalogStore _catalogs;
    private readonly IPeerLiveStatus _live;
    private readonly ILogger<AdminPeerLibrariesController> _logger;
    private readonly StubRootInitializer? _stubRoots;
    private readonly ISettingsAccessor? _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPeerLibrariesController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="peers">The peers with their credentials.</param>
    /// <param name="reader">The catalog reader (the peer's views).</param>
    /// <param name="libraries">The local library service.</param>
    /// <param name="access">The access rules.</param>
    /// <param name="catalogs">The catalogs.</param>
    /// <param name="live">The live status (starts the first sync).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="stubRoots">Sets the default stub root when none is set, so a fresh install can add a library; none means the 409 stands.</param>
    /// <param name="settings">The plugin toggles (<c>TvShowsEnabled</c> decides whether a TV library can be picked), or null for off.</param>
    public AdminPeerLibrariesController(IStateStore state, IPeerEndpoints peers, PeerCatalogReader reader, ShoalLibraryService libraries, AccessService access, CatalogStore catalogs, IPeerLiveStatus live, ILogger<AdminPeerLibrariesController> logger, StubRootInitializer? stubRoots = null, ISettingsAccessor? settings = null)
    {
        _stubRoots = stubRoots;
        _settings = settings;
        _state = state;
        _peers = peers;
        _reader = reader;
        _libraries = libraries;
        _access = access;
        _catalogs = catalogs;
        _live = live;
        _logger = logger;
    }

    /// <summary>
    /// The mapping key of a remote library: the same library always gets the same key.
    /// </summary>
    /// <param name="remoteId">The library's id on the friend's server.</param>
    /// <returns>The key (letters and digits).</returns>
    public static string KeyOf(Guid remoteId) => "r" + remoteId.ToString("N")[..16];

    /// <summary>
    /// Lists the libraries the share user can see on a friend's server.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The libraries, or 404, 409 or 502.</returns>
    [HttpGet("Peers/{handle}/RemoteLibraries")]
    [Produces("application/json")]
    public async Task<IActionResult> RemoteLibraries(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == handle);
        var endpoint = peer is null ? null : _peers.Find(handle);
        if (peer is null || endpoint is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        try
        {
            var views = await _reader.ViewsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var result = new List<RemoteLibraryDto>();
            foreach (var view in views.Take(MaxViews))
            {
                var key = KeyOf(view.Id);
                var selectable = CanPick(view);
                var added = peer.Libraries.Any(l => l.MappingKey == key);
                if (IsTv(view) && selectable)
                {
                    // Two cheap Limit=0 calls: "N series, M episodes" (DESIGN v2.7.6).
                    var series = await _reader.CountAsync(endpoint, view.Id, "Series", cancellationToken).ConfigureAwait(false);
                    var episodes = await _reader.CountAsync(endpoint, view.Id, "Episode", cancellationToken).ConfigureAwait(false);
                    result.Add(WithLocal(new RemoteLibraryDto(key, view.Name, "tvshows", episodes, true, added, series, episodes, series), handle));
                    continue;
                }

                var count = selectable ? await _reader.CountMoviesAsync(endpoint, view.Id, cancellationToken).ConfigureAwait(false) : 0;
                result.Add(WithLocal(new RemoteLibraryDto(key, view.Name, TypeWord(view.CollectionType), count, selectable, added, 0, 0, count), handle));
            }

            return AdminReply.Json(StatusCodes.Status200OK, result);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The libraries of peer {Peer} could not be listed ({Kind})", Redact.Id(handle), ex.Kind);
            return AdminReply.Error(StatusCodes.Status502BadGateway, "unreachable");
        }
    }

    /// <summary>
    /// Adds libraries: for each, a mapping, the local library below the stub root and the first access (the pairing admin only unless picked otherwise).
    /// A first sync starts afterwards.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was added, or 400, 404, 409 or 502.</returns>
    [HttpPost("Peers/{handle}/Libraries")]
    [Produces("application/json")]
    public async Task<IActionResult> AddLibraries(string handle, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == handle);
        var endpoint = peer is null ? null : _peers.Find(handle);
        if (peer is null || endpoint is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<List<AddLibraryRequest>>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || body.Count is 0 or > MaxPerRequest || body.Any(static r => !ValidKey(r.MappingKey)) || body.Select(static r => r.MappingKey).Distinct(StringComparer.Ordinal).Count() != body.Count)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!Path.IsPathRooted(_state.Current.StubRoot) && !(_stubRoots is not null && await _stubRoots.EnsureAsync(cancellationToken).ConfigureAwait(false) && Path.IsPathRooted(_state.Current.StubRoot)))
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "noStubRoot");
        }

        IReadOnlyList<PeerView> views;
        try
        {
            views = await _reader.ViewsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (PeerCallException ex)
        {
            _logger.LogInformation("The libraries of peer {Peer} could not be listed ({Kind})", Redact.Id(handle), ex.Kind);
            return AdminReply.Error(StatusCodes.Status502BadGateway, "unreachable");
        }

        var wanted = new List<(AddLibraryRequest Request, PeerView View)>();
        foreach (var request in body)
        {
            var view = views.FirstOrDefault(v => KeyOf(v.Id) == request.MappingKey);
            if (view is null || !CanPick(view))
            {
                return AdminReply.Error(StatusCodes.Status400BadRequest, "unknownLibrary");
            }

            if (peer.Libraries.Any(l => l.MappingKey == request.MappingKey))
            {
                return AdminReply.Error(StatusCodes.Status409Conflict, "alreadyAdded");
            }

            wanted.Add((request, view));
        }

        var added = new List<object>();
        var hidden = new List<string>();
        foreach (var (request, view) in wanted)
        {
            var key = request.MappingKey!;
            var name = MetadataSanitizer.DisplayName(string.IsNullOrWhiteSpace(request.LocalLibraryName) ? peer.DisplayName + " " + (IsTv(view) ? "TV shows" : view.Name) : request.LocalLibraryName);
            if (name.Length == 0)
            {
                return Partial(StatusCodes.Status400BadRequest, "invalid", added);
            }

            await _state.UpdateAsync(
                s => s.Home.Peers.First(p => p.PeerKey == handle).Libraries.Add(new LibraryMapping
                {
                    MappingKey = key,
                    RemoteName = view.Name,
                    CollectionType = IsTv(view) ? "tvshows" : "movies",
                    LocalLibraryName = name,
                    Subfolder = key,
                    Import = true,
                }),
                cancellationToken).ConfigureAwait(false);

            var created = await _libraries.CreateAsync(handle, key, admin, cancellationToken).ConfigureAwait(false);
            if (!created.Ok)
            {
                await ForgetMappingAsync(handle, key, cancellationToken).ConfigureAwait(false);
                return Partial(
                    created.Error == LibraryCreateError.NameInUse ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError,
                    created.Error == LibraryCreateError.NameInUse ? "nameInUse" : "failed",
                    added);
            }

            if (created.SharedLibKey is { } sharedKey)
            {
                // K44: the titles went into a shared library; who sees it is that library's setting (Layouts screen), never changed from one friend's wizard.
                // Warden 10 M1: who sees this friend's titles now (informational; local account ids only).
                added.Add(new
                {
                    mappingKey = key,
                    localLibraryName = _state.Current.Home.Libraries.FirstOrDefault(l => l.LibKey == sharedKey)?.Name ?? name,
                    libKey = sharedKey,
                    gainedAccess = created.Gained?.Users ?? [],
                    gainedRestricted = created.Gained?.RestrictedUsers ?? [],
                });
                continue;
            }

            var picked = (request.SelectedUserIds ?? []).Where(static id => id != Guid.Empty).Distinct().ToList();
            if (picked.Count > 0 && !(picked.Count == 1 && picked[0] == admin))
            {
                var set = await _access.SetAsync(handle, key, new AccessChange(AccessMode.Selected, picked, false, request.RatingsAcknowledged), admin, cancellationToken).ConfigureAwait(false);
                if (!set.Ok)
                {
                    await _libraries.RemoveAsync(handle, key, cancellationToken).ConfigureAwait(false);
                    await ForgetMappingAsync(handle, key, cancellationToken).ConfigureAwait(false);
                    return Partial(StatusCodes.Status400BadRequest, set.Error == AccessError.AcknowledgementRequired ? "acknowledgementRequired" : "unknownUser", added);
                }
            }

            added.Add(new { mappingKey = key, localLibraryName = name });
        }

        _live.StartSync(handle);
        return AdminReply.Json(StatusCodes.Status200OK, new { added, hiddenLibraries = hidden });
    }

    /// <summary>
    /// Removes a library: the local library, its mapping, its catalog entries and its stub folder. The friend's server keeps its files.
    /// </summary>
    /// <param name="handle">The server's handle.</param>
    /// <param name="mappingKey">The mapping key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 409.</returns>
    [HttpDelete("Peers/{handle}/Libraries/{mappingKey}")]
    [Produces("application/json")]
    public async Task<IActionResult> RemoveLibrary(string handle, string mappingKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var peer = _state.Current.Home.Peers.FirstOrDefault(p => p.PeerKey == handle);
        var mapping = peer?.Libraries.FirstOrDefault(l => l.MappingKey == mappingKey);
        if (peer is null || mapping is null)
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var state = _state.Current;
        var shared = LibraryFolders.FedBy(state, handle, mapping.CollectionType);
        if (shared is not null)
        {
            // A shared library (K44): only this mapping's own title folders go; the library, its folder and the other connections' titles stay.
            if (LibraryFolders.PathOf(state.StubRoot, shared) is { } sharedFolder)
            {
                var entries = _catalogs.Load(handle).Document.Entries.Where(e => e.MappingKey == mappingKey).ToList();
                LibraryTree.DeleteRecorded(state.StubRoot, sharedFolder, entries, _logger);
            }

            var lastOfType = !peer.Libraries.Any(l => l.MappingKey != mappingKey && l.CollectionType == mapping.CollectionType);
            await ForgetMappingAsync(handle, mappingKey, cancellationToken).ConfigureAwait(false);
            if (lastOfType)
            {
                await _state.UpdateAsync(s => s.Home.Libraries.FirstOrDefault(l => l.LibKey == shared.LibKey)?.Feeds.RemoveAll(f => f == handle), cancellationToken).ConfigureAwait(false);
            }

            ForgetCatalog(handle, mappingKey);
            return AdminReply.Json(StatusCodes.Status200OK, new { removed = true });
        }

        var folder = VisibilityService.MappingPath(state.StubRoot, handle, mapping.Subfolder);
        await _libraries.RemoveAsync(handle, mappingKey, cancellationToken).ConfigureAwait(false);
        await ForgetMappingAsync(handle, mappingKey, cancellationToken).ConfigureAwait(false);
        ForgetCatalog(handle, mappingKey);
        DeleteStubs(folder);
        return AdminReply.Json(StatusCodes.Status200OK, new { removed = true });
    }

    // The same numbers the libraries list shows (see TitleCounts): for a library already added, how many titles are here and how many were left to another copy.
    private RemoteLibraryDto WithLocal(RemoteLibraryDto dto, string handle)
    {
        if (!dto.Added)
        {
            return dto;
        }

        var entries = _catalogs.Load(handle).Document.Entries;
        var counts = TitleCounts.Of(entries, dto.MappingKey);
        return dto with { LocalCount = counts.Local, HiddenByDedup = counts.Hidden, DedupWinner = counts.Hidden == 0 ? null : TitleCounts.Winner(_state, _catalogs, handle, entries, dto.MappingKey) };
    }

    private static bool IsMovies(PeerView view) => string.Equals(view.CollectionType, "movies", StringComparison.Ordinal);

    private static bool IsTv(PeerView view) => string.Equals(view.CollectionType, "tvshows", StringComparison.Ordinal);

    // A movie library always; a TV library only while TvShowsEnabled is on (DESIGN v2.7.6). Mixed or untyped views are never pickable.
    private bool CanPick(PeerView view) => IsMovies(view) || (IsTv(view) && _settings?.Current.TvShowsEnabled == true);

    private static string TypeWord(string collectionType) => collectionType switch
    {
        "movies" => "movies",
        "tvshows" => "tvshows",
        "music" => "music",
        _ => "other",
    };

    private static bool ValidKey(string? key) =>
        key is { Length: >= 2 and <= 64 } && key.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    private static ShoalResult Partial(int status, string kind, List<object> added) => new(status, new { error = kind, added });

    private bool TryAdmin(out Guid admin)
    {
        admin = Guid.Empty;
        return User.FindFirst(UserIdClaim)?.Value is { } value && Guid.TryParse(value, out admin) && admin != Guid.Empty;
    }

    private async Task ForgetMappingAsync(string handle, string mappingKey, CancellationToken cancellationToken)
    {
        await _state.UpdateAsync(s => s.Home.Peers.FirstOrDefault(p => p.PeerKey == handle)?.Libraries.RemoveAll(l => l.MappingKey == mappingKey), cancellationToken).ConfigureAwait(false);
    }

    private void ForgetCatalog(string handle, string mappingKey)
    {
        try
        {
            var load = _catalogs.Load(handle);
            if (load.Unreadable)
            {
                return;
            }

            load.Document.Entries.RemoveAll(e => e.MappingKey == mappingKey);
            load.Document.Libraries.RemoveAll(l => l.MappingKey == mappingKey);
            _catalogs.Save(load.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The catalog of a removed library could not be cleaned ({Error})", ex.GetType().Name);
        }
    }

    // Only the folder of the mapping, never through a link: the stubs are Shoal's own files.
    private void DeleteStubs(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !PathSanitizer.HasLinkBelow(_state.Current.StubRoot, folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The stub folder of a removed library could not be deleted ({Error})", ex.GetType().Name);
        }
    }
}

/// <summary>
/// One library of a friend's server as the wizard lists it.
/// </summary>
/// <param name="MappingKey">The key Shoal gives it (stable for the same remote library).</param>
/// <param name="Name">Its name on the friend's server (text only).</param>
/// <param name="Type">movies, tvshows, music or other.</param>
/// <param name="Count">How many titles it holds (movies only; 0 otherwise).</param>
/// <param name="Selectable">Whether it can be added (movies only in the MVP).</param>
/// <param name="Added">Whether it is added already.</param>
/// <param name="SeriesCount">For a TV library: how many series it holds, else 0.</param>
/// <param name="EpisodeCount">For a TV library: how many episodes it holds, else 0.</param>
/// <param name="RemoteCount">Titles (movies or series) the friend lists; the unit of LocalCount and HiddenByDedup (for TV, the series count, not Count).</param>
/// <param name="LocalCount">Titles of this library with a stub here (only for a library already added).</param>
/// <param name="HiddenByDedup">Titles with no stub because this server or a better-ranked friend carries them (only for a library already added).</param>
/// <param name="DedupWinner">The friend that carries most of the hidden titles, or null.</param>
internal sealed record RemoteLibraryDto(string MappingKey, string Name, string Type, int Count, bool Selectable, bool Added, int SeriesCount = 0, int EpisodeCount = 0, int RemoteCount = 0, int LocalCount = 0, int HiddenByDedup = 0, string? DedupWinner = null);

/// <summary>
/// One library the admin ticked in the wizard.
/// </summary>
internal sealed class AddLibraryRequest
{
    /// <summary>Gets the key from <c>RemoteLibraries</c>.</summary>
    public string? MappingKey { get; init; }

    /// <summary>Gets the name of the local library.</summary>
    public string? LocalLibraryName { get; init; }

    /// <summary>Gets the local users who may see it at first; empty means the pairing admin only.</summary>
    public List<Guid>? SelectedUserIds { get; init; }

    /// <summary>Gets a value indicating whether the admin acknowledged the ratings note.</summary>
    public bool RatingsAcknowledged { get; init; }
}
