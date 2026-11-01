using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>
/// 2.10.4 K4: "delete like Jellyfin" for the elevated local administrator (docs/plans/atlas-delete-like-jellyfin-2.10.4.md Table 2 rows 1 to 7;
/// shapes in tests/contracts/local-delete.*.json). No path, token, key or peer item id is in any reply.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
[RequestSizeLimit(1024)]
public sealed class AdminLocalDeleteController : ControllerBase
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal) { "invalidRequest", "busy", "disabled", "candidateChanged", "administratorRequired", "notFound" };

    private readonly LocalDeleteService _service;
    private readonly ISettingsAccessor _settings;
    private readonly IStateStore _state;

    /// <summary>Initializes a new instance of the <see cref="AdminLocalDeleteController"/> class.</summary>
    /// <param name="service">The deletion batch service.</param>
    /// <param name="settings">The switch.</param>
    /// <param name="state">Whether Shoal's state is ready.</param>
    public AdminLocalDeleteController(LocalDeleteService service, ISettingsAccessor settings, IStateStore state)
    {
        _service = service;
        _settings = settings;
        _state = state;
    }

    /// <summary>Route 1: deletable movies for a friend and held originals from earlier versions.</summary>
    /// <param name="handle">The friend.</param>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>The panel state.</returns>
    [HttpGet("Peers/{handle}/LocalRemoval")]
    public Task<IActionResult> State(string handle, CancellationToken cancellationToken) => ShoalPaths.IsValidPeerKey(handle)
        ? StateAsync(handle, cancellationToken) : Task.FromResult<IActionResult>(AdminReply.Error(404, "notFound"));

    /// <summary>Route 2: the panel state without a friend (held originals stay restorable after the last friend is removed).</summary>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>The panel state with no candidates.</returns>
    [HttpGet("LocalRemoval/Status")]
    public Task<IActionResult> Status(CancellationToken cancellationToken) => StateAsync(null, cancellationToken);

    /// <summary>Route 3: the one switch. Off stops a running batch after its current item.</summary>
    /// <param name="body">The explicit value.</param>
    /// <returns>The saved value.</returns>
    [HttpPut("LocalRemoval/Enabled")]
    public IActionResult Enabled([FromBody] RemovalEnabledRequest body) => Run(() =>
    {
        if (body?.Enabled is not { } enabled)
        {
            throw new InvalidOperationException("invalidRequest");
        }

        _ = Admin();
        var settings = _settings.Current;
        settings.LocalDeleteEnabled = enabled;
        _settings.Save(settings);
        if (!enabled)
        {
            _service.StopRunning();
            _service.ForgetCandidates();
        }

        return (200, new { enabled });
    });

    /// <summary>Route 4: starts a batch from candidate handles.</summary>
    /// <param name="body">1 to 50 distinct 32 hex handles.</param>
    /// <returns>202 with every item queued.</returns>
    [HttpPost("LocalRemoval/Deletions")]
    [RequestSizeLimit(4096)] // W23-7: 50 handles are about 1.8 KB; the class limit of 1 KB stopped batches above about 28
    public IActionResult Start([FromBody] LocalDeleteStartRequest body) => Run(() =>
    {
        var ids = body?.CandidateIds;
        if (ids is null || ids.Count is < 1 or > LocalDeleteService.MaxItems || !ids.All(Opaque) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new InvalidOperationException("invalidRequest");
        }

        var admin = Admin();
        return (202, _service.StartFromCandidates(admin, ids));
    });

    /// <summary>Route 6: the running batch, else the latest ended one.</summary>
    /// <returns>The batch or 404.</returns>
    [HttpGet("LocalRemoval/Deletions/Current")]
    public IActionResult Current() => Run(() => (200, (object?)_service.Current() ?? throw new InvalidOperationException("notFound")));

    /// <summary>Route 5: one batch.</summary>
    /// <param name="batchId">32 hex.</param>
    /// <returns>The batch or 404.</returns>
    [HttpGet("LocalRemoval/Deletions/{batchId}")]
    public IActionResult Get(string batchId) => Run(() => (200, (object?)(Opaque(batchId) ? _service.Get(batchId) : throw new InvalidOperationException("invalidRequest")) ?? throw new InvalidOperationException("notFound")));

    /// <summary>Route 7: stop after the current item (idempotent).</summary>
    /// <param name="batchId">32 hex.</param>
    /// <returns>The batch with <c>stopping</c>.</returns>
    [HttpPost("LocalRemoval/Deletions/{batchId}/Stop")]
    public IActionResult Stop(string batchId) => Run(() =>
    {
        if (!Opaque(batchId))
        {
            throw new InvalidOperationException("invalidRequest");
        }

        _ = Admin();
        return (200, (object?)_service.Stop(batchId) ?? throw new InvalidOperationException("notFound"));
    });

    private static bool Opaque(string? value) => value is { Length: 32 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

    private static ShoalResult Run(Func<(int Status, object Body)> action)
    {
        try
        {
            var (status, body) = action();
            return AdminReply.Json(status, body);
        }
        catch (InvalidOperationException ex)
        {
            var code = Codes.Contains(ex.Message) ? ex.Message : "removalUnavailable";
            return code switch
            {
                "invalidRequest" => AdminReply.Error(400, code),
                "administratorRequired" => AdminReply.Error(403, code),
                "notFound" => AdminReply.Error(404, code),
                _ => AdminReply.Error(409, code),
            };
        }
    }

    private async Task<IActionResult> StateAsync(string? peer, CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalDeleteLegacyJobDto> legacy;
        try
        {
            legacy = await _service.LegacyJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return AdminReply.Error(409, "recoveryRequired");
        }

        var enabled = _settings.Current.LocalDeleteEnabled;
        var candidates = enabled && peer is not null ? _service.ListCandidates(peer) : [];
        return AdminReply.Json(200, new LocalDeleteStateDto(enabled, _state.Status == StateStatus.Ready, candidates, legacy, _service.ActiveBatchId));
    }

    private Guid Admin()
    {
        if (HttpContext?.User.Identity?.IsAuthenticated != true || string.Equals(HttpContext.User.FindFirst(CallerContext.IsApiKeyClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(HttpContext.User.FindFirst(CallerContext.UserIdClaim)?.Value, out var id) || id == Guid.Empty)
        {
            throw new InvalidOperationException("administratorRequired");
        }

        return id;
    }
}

/// <summary>The body of <c>POST LocalRemoval/Deletions</c>: candidate handles only, never a path or item id.</summary>
public sealed class LocalDeleteStartRequest
{
    /// <summary>Gets the handles from the latest list read.</summary>
    public IReadOnlyList<string>? CandidateIds { get; init; }
}
