using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Api.Admin;

/// <summary>The explainer card on the admin page.</summary>
/// <param name="Installed">Whether Moonbase answers (false: show <paramref name="FallbackNote"/>).</param>
/// <param name="Posted">Whether the explainer is in Moonbase's list now (by the stored id, or by its exact title).</param>
/// <param name="PostedUtc">When Shoal last posted it, or null.</param>
/// <param name="Recipients">How many accounts the posted message reaches.</param>
/// <param name="MissingRecipients">How many accounts it does not reach yet (created since the post): post again to include them.</param>
/// <param name="Title">The message title.</param>
/// <param name="Body">The message body (markdown).</param>
/// <param name="ButtonNote">The note that Moonfin's inbox button is off by default.</param>
/// <param name="FallbackNote">The note shown when Moonbase is not installed, or null.</param>
/// <param name="Error">refused or failed when Moonbase could not be asked, or null.</param>
public sealed record InboxExplainerDto(bool Installed, bool Posted, DateTime? PostedUtc, int Recipients, int MissingRecipients, string Title, string Body, string ButtonNote, string? FallbackNote, string? Error);

/// <summary>
/// The Moonfin inbox explainer (PLAN v2.9 K49, Pilot 13 S1-S4): one message "Why might a title not work?" posted on the admin's click, through Moonbase's own admin
/// route with the admin's own session (no API key). Moonbase's "users" audience reaches only the accounts listed, so every account (not the friends' sharing
/// accounts) is listed, and a newer account needs a re-post; a post with the stored id replaces the message in place, so a second click never duplicates it.
/// </summary>
public sealed class InboxExplainerService
{
    /// <summary>The title.</summary>
    public const string Title = "Why might a title not work?";

    /// <summary>The body (markdown bold is rendered by Moonfin).</summary>
    public const string Body = "**Why might a title not work?** Some titles come from a friend's server. If that server is turned off or its internet is slow, the title may buffer or not play. Quality may be lowered to keep it smooth. Subtitles may be different from what you expect. If something won't play, try again later.";

    /// <summary>The note about Moonfin's inbox button.</summary>
    public const string ButtonNote = "Moonfin hides the inbox button until each person turns on Settings, Personalization, Navigation, \"Show messages button\". You can make it the default for everyone in Moonbase's default user settings.";

    /// <summary>The note when Moonbase is not installed.</summary>
    public const string FallbackNote = "Moonbase (Moonfin's server plugin) is not installed, so there is no inbox to post to. Each friend title already says it comes from a friend's server.";

    private readonly IStateStore _state;
    private readonly IAccountSource _accounts;
    private readonly IMoonbaseClient _moonbase;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxExplainerService"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="accounts">The accounts.</param>
    /// <param name="moonbase">Moonbase.</param>
    /// <param name="time">The clock.</param>
    public InboxExplainerService(IStateStore state, IAccountSource accounts, IMoonbaseClient moonbase, TimeProvider time)
    {
        _state = state;
        _accounts = accounts;
        _moonbase = moonbase;
        _time = time;
    }

    /// <summary>
    /// The card.
    /// </summary>
    /// <param name="callerToken">The admin's own token.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The explainer card as the page shows it.</returns>
    public async Task<InboxExplainerDto> GetAsync(string callerToken, CancellationToken cancellationToken)
    {
        var (outcome, items) = await _moonbase.ListAsync(callerToken, cancellationToken).ConfigureAwait(false);
        return Card(outcome, Existing(items), Recipients());
    }

    /// <summary>
    /// Posts the explainer, or replaces the one posted before. A click when the message is there and already reaches every account changes nothing.
    /// </summary>
    /// <param name="callerToken">The admin's own token.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The card after it, and whether a post was made.</returns>
    public async Task<(InboxExplainerDto Card, bool Sent)> PostAsync(string callerToken, CancellationToken cancellationToken)
    {
        var (outcome, items) = await _moonbase.ListAsync(callerToken, cancellationToken).ConfigureAwait(false);
        var recipients = Recipients();
        var existing = Existing(items);
        if (outcome != MoonbaseOutcome.Ok)
        {
            return (Card(outcome, existing, recipients), false);
        }

        if (existing is not null && recipients.All(id => existing.TargetUserIds.Contains(id)))
        {
            if (_state.Current.Home.Inbox.ExplainerMessageId != existing.Id)
            {
                await RecordAsync(existing.Id, existing.TargetUserIds, keepTime: true, cancellationToken).ConfigureAwait(false);
            }

            return (Card(outcome, existing, recipients), false); // already there for everyone: a no-op
        }

        var (posted, id) = await _moonbase.PostAsync(callerToken, new MoonbasePost(existing?.Id, Title, Body, recipients), cancellationToken).ConfigureAwait(false);
        if (posted != MoonbaseOutcome.Ok || id is not { } messageId)
        {
            return (Card(posted == MoonbaseOutcome.Ok ? MoonbaseOutcome.Failed : posted, existing, recipients), false);
        }

        await RecordAsync(messageId, recipients, keepTime: false, cancellationToken).ConfigureAwait(false);
        return (Card(MoonbaseOutcome.Ok, new MoonbaseMessage(messageId, Title, recipients), recipients), true);
    }

    private static InboxExplainerDto Card(MoonbaseOutcome outcome, MoonbaseMessage? existing, IReadOnlyList<Guid> recipients, DateTime? postedUtc = null) => new(
        outcome != MoonbaseOutcome.NotInstalled,
        outcome == MoonbaseOutcome.Ok && existing is not null,
        postedUtc,
        existing?.TargetUserIds.Count(recipients.Contains) ?? 0,
        existing is null ? recipients.Count : recipients.Count(id => !existing.TargetUserIds.Contains(id)),
        Title,
        Body,
        ButtonNote,
        outcome == MoonbaseOutcome.NotInstalled ? FallbackNote : null,
        outcome switch { MoonbaseOutcome.Refused => "refused", MoonbaseOutcome.Failed => "failed", _ => null });

    private InboxExplainerDto Card(MoonbaseOutcome outcome, MoonbaseMessage? existing, IReadOnlyList<Guid> recipients) =>
        Card(outcome, existing, recipients, _state.Status == StateStatus.Ready ? _state.Current.Home.Inbox.ExplainerPostedUtc : null);

    // The stored id first; else the exact title (a message posted before the id was stored, or by hand).
    private MoonbaseMessage? Existing(IReadOnlyList<MoonbaseMessage> items)
    {
        var stored = _state.Status == StateStatus.Ready ? _state.Current.Home.Inbox.ExplainerMessageId : null;
        return items.FirstOrDefault(i => stored is { } id && i.Id == id) ?? items.FirstOrDefault(static i => string.Equals(i.Title, Title, StringComparison.Ordinal));
    }

    // Every account but the friends' sharing accounts (they are not people of this server).
    private List<Guid> Recipients()
    {
        var shareUsers = _state.Status == StateStatus.Ready ? VisibilityService.ShareUserIds(_state.Current) : new HashSet<Guid>();
        return [.. _accounts.List().Where(a => !shareUsers.Contains(a.Id)).Select(static a => a.Id).Order()];
    }

    private async Task RecordAsync(Guid id, IReadOnlyList<Guid> targets, bool keepTime, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _state.UpdateAsync(
            s =>
            {
                s.Home.Inbox.ExplainerMessageId = id;
                s.Home.Inbox.TargetUserIds = [.. targets];
                if (!keepTime || s.Home.Inbox.ExplainerPostedUtc is null)
                {
                    s.Home.Inbox.ExplainerPostedUtc = now;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>GET/POST Shoal/Admin/Inbox/Explainer</c> (PLAN v2.9 K49). Admin only; Moonbase is called with the admin's own session.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Shoal/Admin")]
public class AdminInboxController : ControllerBase
{
    private readonly IStateStore _state;
    private readonly InboxExplainerService _inbox;
    private readonly ILogger<AdminInboxController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminInboxController"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="inbox">The explainer.</param>
    /// <param name="logger">The logger.</param>
    public AdminInboxController(IStateStore state, InboxExplainerService inbox, ILogger<AdminInboxController> logger)
    {
        _state = state;
        _inbox = inbox;
        _logger = logger;
    }

    /// <summary>
    /// The explainer card: <c>{ installed, posted, postedUtc?, recipients, missingRecipients, title, body, buttonNote, fallbackNote?, error? }</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200, 400 (<c>adminUserRequired</c>) or 409 (<c>recovery</c>).</returns>
    [HttpGet("Inbox/Explainer")]
    [Produces("application/json")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        return Token() is { } token
            ? AdminReply.Json(StatusCodes.Status200OK, await _inbox.GetAsync(token, cancellationToken).ConfigureAwait(false))
            : AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired");
    }

    /// <summary>
    /// "Post to users' inbox": posts the explainer, or replaces it to reach accounts created since; a no-op when it already reaches everyone.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>200 <c>{ sent, explainer }</c>, 400, 409 (<c>recovery</c>).</returns>
    [HttpPost("Inbox/Explainer")]
    [Produces("application/json")]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        if (_state.Status != StateStatus.Ready)
        {
            return AdminReply.Error(StatusCodes.Status409Conflict, "recovery");
        }

        if (Token() is not { } token)
        {
            return AdminReply.Error(StatusCodes.Status400BadRequest, "adminUserRequired");
        }

        var (card, sent) = await _inbox.PostAsync(token, cancellationToken).ConfigureAwait(false);
        if (sent)
        {
            _logger.LogInformation("Admin posted the inbox explainer to {Count} accounts", card.Recipients);
        }

        return AdminReply.Json(StatusCodes.Status200OK, new { sent, explainer = card });
    }

    // Warden 11 I-a: the admin's own session only (an admin user id, as every other admin route), never an API key.
    private string? Token() =>
        User.FindFirst("Jellyfin-UserId")?.Value is { } id && Guid.TryParse(id, out var admin) && admin != Guid.Empty && User.FindFirst(CallerContext.TokenClaim)?.Value is { Length: > 0 } token ? token : null;
}
