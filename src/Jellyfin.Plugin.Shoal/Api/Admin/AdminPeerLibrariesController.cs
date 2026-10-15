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
    public AdminPeerLibrariesController(IStateStore state, IPeerEndpoints peers, PeerCatalogReader reader, ShoalLibraryService libraries, AccessService access, CatalogStore catalogs, IPeerLiveStatus live, ILogger<AdminPeerLibrariesController> logger)
    {
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
                var selectable = IsMovies(view);
                var count = selectable ? await _reader.CountMoviesAsync(endpoint, view.Id, cancellationToken).ConfigureAwait(false) : 0;
                result.Add(new RemoteLibraryDto(key, view.Name, TypeWord(view.CollectionType), count, selectable, peer.Libraries.Any(l => l.MappingKey == key)));
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

        if (!Path.IsPathRooted(_state.Current.StubRoot))
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
            if (view is null || !IsMovies(view))
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
            var name = MetadataSanitizer.DisplayName(string.IsNullOrWhiteSpace(request.LocalLibraryName) ? peer.DisplayName + " " + view.Name : request.LocalLibraryName);
            if (name.Length == 0)
            {
                return Partial(StatusCodes.Status400BadRequest, "invalid", added);
            }

            await _state.UpdateAsync(
                s => s.Home.Peers.First(p => p.PeerKey == handle).Libraries.Add(new LibraryMapping
                {
                    MappingKey = key,
                    RemoteName = view.Name,
                    CollectionType = "movies",
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

        var folder = VisibilityService.MappingPath(_state.Current.StubRoot, handle, mapping.Subfolder);
        await _libraries.RemoveAsync(handle, mappingKey, cancellationToken).ConfigureAwait(false);
        await ForgetMappingAsync(handle, mappingKey, cancellationToken).ConfigureAwait(false);
        ForgetCatalog(handle, mappingKey);
        DeleteStubs(folder);
        return AdminReply.Json(StatusCodes.Status200OK, new { removed = true });
    }

    private static bool IsMovies(PeerView view) => string.Equals(view.CollectionType, "movies", StringComparison.Ordinal);

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
internal sealed record RemoteLibraryDto(string MappingKey, string Name, string Type, int Count, bool Selectable, bool Added);

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
