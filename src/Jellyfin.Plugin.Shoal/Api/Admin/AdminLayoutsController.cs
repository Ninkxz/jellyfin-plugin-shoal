using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShoalLibrary = Jellyfin.Plugin.Shoal.State.ShoalLibrary;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>The body of POST and PUT Shoal/Admin/Layouts/Libraries.</summary>
internal sealed record LayoutLibraryRequest
{
    /// <summary>Gets the name (create, rename).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the type (create): movies or tvshows.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Gets the placement (create).</summary>
    [JsonPropertyName("placement")]
    public LibraryPlacement? Placement { get; init; }

    /// <summary>Gets the main library to add into (create, placement Main).</summary>
    [JsonPropertyName("mainLibraryId")]
    public Guid? MainLibraryId { get; init; }

    /// <summary>Gets a new access (update).</summary>
    [JsonPropertyName("access")]
    public AccessRequest? Access { get; init; }

    /// <summary>Gets the new order of every library (PUT on the collection).</summary>
    [JsonPropertyName("order")]
    public List<string>? Order { get; init; }
}

/// <summary>The body of POST Shoal/Admin/Layouts/Preview and Apply.</summary>
internal sealed record LayoutPresetRequest
{
    /// <summary>Gets the preset.</summary>
    [JsonPropertyName("preset")]
    public LayoutPreset? Preset { get; init; }

    /// <summary>Gets the plan hash the Preview answered (Apply only; required, Warden 10 L3).</summary>
    [JsonPropertyName("planHash")]
    public string? PlanHash { get; init; }

    /// <summary>Gets the main library the admin picked per type for Combine into my libraries (keys <c>movies</c>, <c>tvshows</c>), or null.</summary>
    [JsonPropertyName("targets")]
    public Dictionary<string, Guid>? Targets { get; init; }
}

/// <summary>The body of PUT Shoal/Admin/Layouts/Default.</summary>
internal sealed record LayoutDefaultRequest
{
    /// <summary>Gets the preset new connections follow.</summary>
    [JsonPropertyName("preset")]
    public LayoutPreset? Preset { get; init; }

    /// <summary>Gets, per type, the library new connections join (null clears it).</summary>
    [JsonPropertyName("defaultTargets")]
    public Dictionary<string, string?>? DefaultTargets { get; init; }
}

/// <summary>The body of PUT Shoal/Admin/Connections/{peerKey}/Library.</summary>
internal sealed record ConnectionLibraryRequest
{
    /// <summary>Gets the type whose library changes: movies or tvshows.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Gets the existing target library, or null for a new one.</summary>
    [JsonPropertyName("libKey")]
    public string? LibKey { get; init; }

    /// <summary>Gets the new library to create when <see cref="LibKey"/> is null.</summary>
    [JsonPropertyName("newLibrary")]
    public LayoutLibraryRequest? NewLibrary { get; init; }
}

/// <summary>The body of PUT Shoal/Admin/Users/{id}/FriendTitles.</summary>
internal sealed record FriendTitlesRequest
{
    /// <summary>Gets a value indicating whether friends' titles are hidden from the account.</summary>
    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }
}

/// <summary>
/// Library layouts (PLAN v2.9 K44/K45): the Shoal libraries, their placement, access and order, the presets with a preview, moving a connection to another library,
/// the move job, and per-account "Hide friends' titles". Admin only (<c>RequiresElevation</c>); every answer is plain JSON the page shows through text only.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminLayoutsController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId"; // core's InternalClaimTypes.UserId

    private readonly IStateStore _state;
    private readonly LibraryLayoutService _layout;
    private readonly AccessService _access;
    private readonly LayoutJobService _jobs;
    private readonly LayoutPresetService _presets;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminLayoutsController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="layout">The library layout.</param>
    /// <param name="access">The access rules.</param>
    /// <param name="jobs">The move job.</param>
    /// <param name="presets">The presets.</param>
    public AdminLayoutsController(IStateStore state, LibraryLayoutService layout, AccessService access, LayoutJobService jobs, LayoutPresetService presets)
    {
        _state = state;
        _layout = layout;
        _access = access;
        _jobs = jobs;
        _presets = presets;
    }

    /// <summary>
    /// The layout: the preset, the default library of each type, every library in order (with its connections and access), the accounts that hide friends'
    /// titles, and the move job.
    /// </summary>
    /// <returns>200 or 409.</returns>
    [HttpGet("Layouts/Libraries")]
    [Produces("application/json")]
    public IActionResult GetLibraries()
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        var state = _state.Current;
        var shareUsers = VisibilityService.ShareUserIds(state);
        var order = state.Home.LibraryOrder;
        var libraries = state.Home.Libraries.OrderBy(l => order.IndexOf(l.LibKey) is var i && i >= 0 ? i : int.MaxValue).Select(l => Describe(state, l, shareUsers)).ToList();
        return AdminReply.Json(StatusCodes.Status200OK, new
        {
            layout = new { preset = state.Home.Layout.Default.ToString(), defaultTargets = state.Home.Layout.DefaultTargets },
            libraries,
            order,
            hiddenFriendTitles = state.Home.Visibility.HiddenFriendTitles,
            mainLibrariesSavingNfo = _layout.Options?.MainLibrariesSavingNfo() ?? [], // Pilot 15 R6-MAIN-WARN: the page that shows Main placement names them too
            shoalLibrariesNotLocked = _layout.Options?.ShoalLibrariesNotLocked() ?? [], // Warden 12 L2
            mainLibrariesMissing = _layout.Options?.MainLibrariesMissing() ?? [], // Warden 13 L5
            job = _jobs.Status(),
        });
    }

    /// <summary>
    /// Creates a library (placement Shoal: its own library; Main: added into a main library, hidden by tag from every account it does not admit).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>201 with the key, or 400, 409.</returns>
    [HttpPost("Layouts/Libraries")]
    [Produces("application/json")]
    public async Task<IActionResult> CreateLibrary(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<LayoutLibraryRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Name is null || body.Type is null || body.Placement is not { } placement || !Enum.IsDefined(placement))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user (Warden 10 L3)
        }

        var result = await _layout.CreateAsync(new LibraryCreateRequest(body.Name, body.Type, placement, body.MainLibraryId), admin, cancellationToken).ConfigureAwait(false);
        return result.Ok ? AdminReply.Json(StatusCodes.Status201Created, new { libKey = result.LibKey }) : Refusal(result.Error);
    }

    /// <summary>
    /// Sets the library order: every library once, first first.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 or 409.</returns>
    [HttpPut("Layouts/Libraries")]
    [Produces("application/json")]
    public async Task<IActionResult> SetOrder(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<LayoutLibraryRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Order is not { } order)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var result = await _layout.SetOrderAsync(order, cancellationToken).ConfigureAwait(false);
        return result.Ok ? AdminReply.Json(StatusCodes.Status200OK, new { order = _state.Current.Home.LibraryOrder }) : Refusal(result.Error);
    }

    /// <summary>
    /// Renames a library and/or changes who may open it. A change that would admit a restricted profile needs the ratings acknowledgement.
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with who gained and lost access, or 400, 404, 409.</returns>
    [HttpPut("Layouts/Libraries/{libKey}")]
    [Produces("application/json")]
    public async Task<IActionResult> UpdateLibrary(string libKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!_state.Current.Home.Libraries.Any(l => l.LibKey == libKey))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<LayoutLibraryRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body is null || (body.Name is null && body.Access is null) || (body.Access?.Mode is { } mode && !Enum.IsDefined(mode)))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user (Warden 10 L3)
        }

        // Warden 12 L4: a name that would be refused is refused before the access change is committed.
        if (body.Name is { } checkName && _layout.CheckRename(libKey, checkName) is var nameError && nameError != LayoutError.None)
        {
            return Refusal(nameError);
        }

        AccessImpact? impact = null;
        if (body.Access is { } access)
        {
            using var held = _layout.Lock.TryEnter();
            if (held is null)
            {
                return AdminReply.Error(StatusCodes.Status409Conflict, "busy");
            }

            var set = await _access.SetLibraryAsync(libKey, new AccessChange(access.Mode, access.SelectedUserIds, access.IncludeRestricted, access.RatingsAcknowledged), admin, cancellationToken).ConfigureAwait(false);
            if (!set.Ok)
            {
                return set.Error switch
                {
                    AccessError.RecoveryMode => AdminReply.Error(StatusCodes.Status409Conflict, "recovery"),
                    AccessError.UnknownLibrary => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
                    AccessError.ShareUser => AdminReply.Error(StatusCodes.Status400BadRequest, "shareUser"),
                    AccessError.UnknownUser => AdminReply.Error(StatusCodes.Status400BadRequest, "unknownUser"),
                    AccessError.AcknowledgementRequired => AdminReply.Error(StatusCodes.Status400BadRequest, "acknowledgementRequired"),
                    _ => AdminReply.Error(StatusCodes.Status400BadRequest, "invalid"),
                };
            }

            impact = set.Impact;
        }

        if (body.Name is { } name)
        {
            var renamed = await _layout.RenameAsync(libKey, name, cancellationToken).ConfigureAwait(false);
            if (!renamed.Ok)
            {
                if (impact is not null)
                {
                    // Warden 12 L4: the access change is already in; the error says so, with who gained and lost access.
                    return AdminReply.Json(ErrorStatus(renamed.Error), new { error = ErrorWord(renamed.Error), accessChanged = true, gaining = impact.Gaining, losing = impact.Losing });
                }

                return Refusal(renamed.Error);
            }
        }

        var state = _state.Current;
        var library = state.Home.Libraries.First(l => l.LibKey == libKey);
        return AdminReply.Json(StatusCodes.Status200OK, new { library = Describe(state, library, VisibilityService.ShareUserIds(state)), gaining = impact?.Gaining ?? [], losing = impact?.Losing ?? [] });
    }

    /// <summary>
    /// Deletes a library that no connection feeds.
    /// </summary>
    /// <param name="libKey">The library.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 404 or 409.</returns>
    [HttpDelete("Layouts/Libraries/{libKey}")]
    [Produces("application/json")]
    public async Task<IActionResult> DeleteLibrary(string libKey, CancellationToken cancellationToken)
    {
        var result = await _layout.DeleteAsync(libKey, cancellationToken).ConfigureAwait(false);
        return result.Ok ? AdminReply.Json(StatusCodes.Status200OK, new { removed = true }) : Refusal(result.Error);
    }

    /// <summary>
    /// What applying a preset would do: libraries created and removed, titles collapsing, users losing access. Nothing changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 or 409.</returns>
    [HttpPost("Layouts/Preview")]
    [Produces("application/json")]
    public async Task<IActionResult> Preview(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<LayoutPresetRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Preset is not { } preset || !Enum.IsDefined(preset))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TargetsValid(body.Targets))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        return _presets.Preview(preset, body.Targets) is { } preview ? AdminReply.Json(StatusCodes.Status200OK, preview) : AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
    }

    /// <summary>
    /// Applies a preset: the move job starts with the plan the preview showed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>202 with the job, or 400, 409.</returns>
    [HttpPost("Layouts/Apply")]
    [Produces("application/json")]
    public async Task<IActionResult> Apply(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<LayoutPresetRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Preset is not { } preset || !Enum.IsDefined(preset))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user (Warden 10 L3)
        }

        if (!TargetsValid(body.Targets))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var error = await _presets.ApplyAsync(preset, admin, body.PlanHash, body.Targets, cancellationToken).ConfigureAwait(false);
        return error == LayoutJobError.None ? AdminReply.Json(StatusCodes.Status202Accepted, new { job = _jobs.Status() }) : JobRefusal(error);
    }

    /// <summary>
    /// Moves a connection's titles of one type into another library (an existing one, or a new one). Local and immediate; the move job runs in the background.
    /// </summary>
    /// <param name="peerKey">The connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>202 with the job, or 400, 404, 409.</returns>
    [HttpPut("Connections/{peerKey}/Library")]
    [Produces("application/json")]
    public async Task<IActionResult> MoveConnection(string peerKey, CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (!_state.Current.Home.Peers.Any(p => p.PeerKey == peerKey))
        {
            return AdminReply.Error(StatusCodes.Status404NotFound, "notFound");
        }

        var body = await AdminReply.ReadAsync<ConnectionLibraryRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Type is not ("movies" or "tvshows"))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        if (!TryAdmin(out var admin))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired"); // an API key has no admin user (Warden 10 L3)
        }

        LibraryCreateRequest? create = null;
        if (body.LibKey is null)
        {
            if (body.NewLibrary?.Name is not { } name || body.NewLibrary.Placement is not { } placement || !Enum.IsDefined(placement))
            {
                return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
            }

            create = new LibraryCreateRequest(name, body.Type, placement, body.NewLibrary.MainLibraryId);
        }

        var error = await _jobs.StartAsync([new MoveRequest(peerKey, body.Type, body.LibKey, create)], admin, "move", cancellationToken).ConfigureAwait(false);
        return error == LayoutJobError.None ? AdminReply.Json(StatusCodes.Status202Accepted, new { job = _jobs.Status() }) : JobRefusal(error);
    }

    /// <summary>
    /// The running or last move job: phase, progress, the accounts that lost access, and the error that stopped it.
    /// </summary>
    /// <returns>200.</returns>
    [HttpGet("Layouts/Job")]
    [Produces("application/json")]
    public IActionResult GetJob() => AdminReply.Json(StatusCodes.Status200OK, _jobs.Status());

    /// <summary>
    /// The default layout picker: which preset new connections follow and, per type, the library they join. Nothing moves (use Preview/Apply for that).
    /// Body <c>{ preset, defaultTargets?: { movies?: libKey|null, tvshows?: libKey|null } }</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 with the layout, 400, 404 (unknown target) or 409.</returns>
    [HttpPut("Layouts/Default")]
    [Produces("application/json")]
    public async Task<IActionResult> SetDefault(CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<LayoutDefaultRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Preset is not { } preset || !Enum.IsDefined(preset))
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var result = await _layout.SetDefaultAsync(preset, body.DefaultTargets, cancellationToken).ConfigureAwait(false);
        return result.Ok
            ? AdminReply.Json(StatusCodes.Status200OK, new { preset = _state.Current.Home.Layout.Default, defaultTargets = _state.Current.Home.Layout.DefaultTargets })
            : Refusal(result.Error);
    }

    /// <summary>
    /// Runs an unfinished (failed or interrupted) move job again from its last checkpoint (Warden 10 M2).
    /// </summary>
    /// <returns>202 with the job, 404 (nothing unfinished) or 409 (<c>recovery</c>, <c>busy</c>, <c>syncing</c>: try again in a moment).</returns>
    [HttpPost("Layouts/Job/Resume")]
    [Produces("application/json")]
    public IActionResult ResumeJob()
    {
        var error = _jobs.TryResume();
        return error == LayoutJobError.None ? AdminReply.Json(StatusCodes.Status202Accepted, new { job = _jobs.Status() }) : JobRefusal(error);
    }

    /// <summary>
    /// Gives up an unfinished move job that does not run now (Warden 10 M2): nothing is undone; the moved connections are synced again; layout changes and syncs
    /// may run again.
    /// </summary>
    /// <returns>200 with the job, 404 (nothing unfinished) or 409 (<c>busy</c>: it runs now).</returns>
    [HttpPost("Layouts/Job/Abandon")]
    [Produces("application/json")]
    public IActionResult AbandonJob()
    {
        var error = _jobs.Abandon();
        return error == LayoutJobError.None ? AdminReply.Json(StatusCodes.Status200OK, new { job = _jobs.Status() }) : JobRefusal(error);
    }

    /// <summary>
    /// "Hide friends' titles" for one account (the global <c>Shoal</c> tag in its BlockedTags; playback refuses them too).
    /// </summary>
    /// <param name="id">The account.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400, 404 or 409.</returns>
    [HttpPut("Users/{id}/FriendTitles")]
    [Produces("application/json")]
    public async Task<IActionResult> SetFriendTitles(Guid id, CancellationToken cancellationToken)
    {
        var body = await AdminReply.ReadAsync<FriendTitlesRequest>(Request, cancellationToken).ConfigureAwait(false);
        if (body?.Hidden is not { } hidden || id == Guid.Empty)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "invalid");
        }

        var result = await _layout.SetFriendTitlesHiddenAsync(id, hidden, cancellationToken).ConfigureAwait(false);
        return result.Ok ? AdminReply.Json(StatusCodes.Status200OK, new { hidden }) : Refusal(result.Error);
    }

    private static object Describe(ShoalState state, ShoalLibrary library, HashSet<Guid> shareUsers)
    {
        var access = LibraryFolders.AccessOf(state, library);
        var connections = LibraryFolders.ConnectionsOf(library);
        return new
        {
            libKey = library.LibKey,
            name = library.Name,
            type = library.Type,
            placement = library.Placement.ToString(),
            legacy = library.IsLegacy,
            jellyfinLibraryId = library.JellyfinLibraryId,
            connections = connections.Select(key => new { peerKey = key, name = state.Home.Peers.FirstOrDefault(p => p.PeerKey == key)?.ShownName() ?? string.Empty }).ToList(),
            access = new LibraryAccessDto(access.Mode, [.. access.SelectedUserIds.Where(id => !shareUsers.Contains(id))], access.IncludeRestricted),
        };
    }

    private static ShoalResult Refusal(LayoutError error) => AdminReply.Error(ErrorStatus(error), ErrorWord(error));

    private static string ErrorWord(LayoutError error) => error switch
    {
        LayoutError.RecoveryMode => "recovery",
        LayoutError.NotFound => "notFound",
        LayoutError.NameInUse => "nameInUse",
        LayoutError.TooMany => "tooMany",
        LayoutError.NotEmpty => "notEmpty",
        LayoutError.Legacy => "legacy",
        LayoutError.Busy => "busy",
        LayoutError.NoStubRoot => "noStubRoot",
        LayoutError.MainLibraryInvalid => "mainLibraryInvalid",
        LayoutError.ShareUser => "shareUser",
        LayoutError.Failed => "failed",
        _ => "invalid",
    };

    private static int ErrorStatus(LayoutError error) => error switch
    {
        LayoutError.RecoveryMode => StatusCodes.Status409Conflict,
        LayoutError.NotFound => StatusCodes.Status404NotFound,
        LayoutError.NameInUse => StatusCodes.Status409Conflict,
        LayoutError.TooMany => StatusCodes.Status409Conflict,
        LayoutError.NotEmpty => StatusCodes.Status409Conflict,
        LayoutError.Legacy => StatusCodes.Status409Conflict,
        LayoutError.Busy => StatusCodes.Status409Conflict,
        LayoutError.NoStubRoot => StatusCodes.Status409Conflict,
        LayoutError.MainLibraryInvalid => StatusCodes.Status400BadRequest,
        LayoutError.ShareUser => StatusCodes.Status400BadRequest,
        LayoutError.Failed => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest,
    };

    private static ShoalResult JobRefusal(LayoutJobError error) => error switch
    {
        LayoutJobError.RecoveryMode => AdminReply.Error(StatusCodes.Status409Conflict, "recovery"),
        LayoutJobError.NotFound => AdminReply.Error(StatusCodes.Status404NotFound, "notFound"),
        LayoutJobError.Syncing => AdminReply.Error(StatusCodes.Status409Conflict, "syncing"),
        LayoutJobError.Busy => AdminReply.Error(StatusCodes.Status409Conflict, "busy"),
        LayoutJobError.CreateFailed => AdminReply.Error(StatusCodes.Status409Conflict, "createFailed"),
        LayoutJobError.PlanChanged => AdminReply.Error(StatusCodes.Status409Conflict, "planChanged"),
        LayoutJobError.TargetRequired => AdminReply.Error(StatusCodes.Status409Conflict, "targetRequired"),
        _ => AdminReply.Error(StatusCodes.Status400BadRequest, "invalid"),
    };

    // Only movies and tvshows, each a real id.
    private static bool TargetsValid(Dictionary<string, Guid>? targets) =>
        targets is null || (targets.Count <= 2 && targets.All(static t => t.Key is "movies" or "tvshows" && t.Value != Guid.Empty));

    private bool TryAdmin(out Guid admin)
    {
        admin = Guid.Empty;
        return User.FindFirst(UserIdClaim)?.Value is { } value && Guid.TryParse(value, out admin) && admin != Guid.Empty;
    }
}
