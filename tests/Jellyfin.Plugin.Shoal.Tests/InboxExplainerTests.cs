using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN v2.9 K49 with the Pilot 13 Moonbase facts: "users" reaches only listed accounts (so every account but sharing accounts is listed), a post with the
/// stored id replaces in place, a second click is a no-op, a newer account needs a re-post, Moonbase absent = "not installed" with the fallback note, and the
/// admin's token is never stored.
/// </summary>
public sealed class InboxExplainerTests : IAsyncLifetime
{
    private const string Token = "admin-session-token-0123456789abcdef";

    private readonly TempDirectory _dir = new();
    private readonly FakeAccounts _accounts = new();
    private readonly FakeMoonbase _moonbase = new();
    private StateStore _state = null!;
    private InboxExplainerService _inbox = null!;
    private AccountInfo _share = null!;

    public async Task InitializeAsync()
    {
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, TimeProvider.System, null);
        _accounts.Add("admin", admin: true);
        _accounts.Add("alice");
        _share = _accounts.Add("shoal-bob");
        await _state.UpdateAsync(s => s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = _share.Id }));
        _inbox = new InboxExplainerService(_state, _accounts, _moonbase, TimeProvider.System);
    }

    public Task DisposeAsync()
    {
        _state.Dispose();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MoonbaseAbsent_SaysNotInstalled_WithTheFallbackNote_AndPostsNothing()
    {
        _moonbase.Installed = false;

        var card = await _inbox.GetAsync(Token, default);
        var (after, sent) = await _inbox.PostAsync(Token, default);

        Assert.False(card.Installed);
        Assert.Equal(InboxExplainerService.FallbackNote, card.FallbackNote);
        Assert.False(sent);
        Assert.False(after.Installed);
        Assert.Empty(_moonbase.Posts);
    }

    [Fact]
    public async Task TheFirstClickPosts_ToEveryAccountButTheSharingOnes_TheSecondIsANoOp_AndTheIdIsStored()
    {
        var (card, sent) = await _inbox.PostAsync(Token, default);

        Assert.True(sent);
        var post = Assert.Single(_moonbase.Posts);
        Assert.Null(post.Id);
        Assert.Equal(InboxExplainerService.Title, post.Title);
        Assert.StartsWith("**Why might a title not work?**", post.Body, StringComparison.Ordinal);
        Assert.Equal(2, post.TargetUserIds.Count);
        Assert.DoesNotContain(_share.Id, post.TargetUserIds);
        Assert.True(card.Posted);
        Assert.Equal(0, card.MissingRecipients);
        Assert.Equal(_moonbase.Items[0].Id, _state.Current.Home.Inbox.ExplainerMessageId);
        Assert.Equal(InboxExplainerService.ButtonNote, card.ButtonNote);

        var (_, again) = await _inbox.PostAsync(Token, default);
        Assert.False(again);
        Assert.Single(_moonbase.Posts);
        Assert.DoesNotContain(Token, File.ReadAllText(new ShoalPaths(_dir.Path).StateFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewAccount_IsMissingUntilARepost_WhichReplacesTheMessageInPlace()
    {
        await _inbox.PostAsync(Token, default);
        var first = _moonbase.Items[0].Id;
        _accounts.Add("carol");

        var card = await _inbox.GetAsync(Token, default);
        Assert.Equal(1, card.MissingRecipients);

        var (after, sent) = await _inbox.PostAsync(Token, default);
        Assert.True(sent);
        Assert.Equal(first, _moonbase.Posts[1].Id); // the stored id: replaced, not duplicated
        Assert.Single(_moonbase.Items);
        Assert.Equal(3, after.Recipients);
    }

    [Fact]
    public async Task AMessageWithTheSameTitle_IsFoundByTitle_WhenNoIdIsStored()
    {
        var existing = Guid.NewGuid();
        _moonbase.Items.Add(new MoonbaseMessage(existing, InboxExplainerService.Title, []));

        var (_, sent) = await _inbox.PostAsync(Token, default);

        Assert.True(sent);
        Assert.Equal(existing, _moonbase.Posts[0].Id);
        Assert.Equal(existing, _state.Current.Home.Inbox.ExplainerMessageId);
    }

    [Fact]
    public async Task ARefusedSession_SaysRefused_AndChangesNothing()
    {
        _moonbase.Refuse = true;

        var (card, sent) = await _inbox.PostAsync(Token, default);

        Assert.False(sent);
        Assert.Equal("refused", card.Error);
        Assert.Null(_state.Current.Home.Inbox.ExplainerMessageId);
    }

    [Fact]
    public async Task W11Ia_TheRoutes_NeedTheAdminUserId_NotOnlyAToken()
    {
        static Microsoft.AspNetCore.Mvc.ControllerContext Context(params System.Security.Claims.Claim[] claims) =>
            new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test")) } };

        var tokenOnly = new AdminInboxController(_state, _inbox, NullLogger<AdminInboxController>.Instance) { ControllerContext = Context(new System.Security.Claims.Claim(Jellyfin.Plugin.Shoal.Authorization.CallerContext.TokenClaim, Token)) };
        Assert.Equal("adminUserRequired", AdminTestSupport.Body(await tokenOnly.Get(default), 400).GetProperty("error").GetString());
        Assert.Equal("adminUserRequired", AdminTestSupport.Body(await tokenOnly.Post(default), 400).GetProperty("error").GetString());
        Assert.Empty(_moonbase.Posts);

        var admin = new AdminInboxController(_state, _inbox, NullLogger<AdminInboxController>.Instance)
        {
            ControllerContext = Context(new System.Security.Claims.Claim(Jellyfin.Plugin.Shoal.Authorization.CallerContext.TokenClaim, Token), new System.Security.Claims.Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N"))),
        };
        Assert.True(AdminTestSupport.Body(await admin.Get(default)).GetProperty("installed").GetBoolean());
    }

    private sealed class FakeMoonbase : IMoonbaseClient
    {
        public bool Installed { get; set; } = true;

        public bool Refuse { get; set; }

        public List<MoonbaseMessage> Items { get; } = [];

        public List<MoonbasePost> Posts { get; } = [];

        public Task<(MoonbaseOutcome Outcome, IReadOnlyList<MoonbaseMessage> Items)> ListAsync(string callerToken, CancellationToken cancellationToken)
        {
            Assert.Equal(Token, callerToken);
            return Task.FromResult<(MoonbaseOutcome, IReadOnlyList<MoonbaseMessage>)>(!Installed ? (MoonbaseOutcome.NotInstalled, []) : Refuse ? (MoonbaseOutcome.Refused, []) : (MoonbaseOutcome.Ok, Items.ToList()));
        }

        public Task<(MoonbaseOutcome Outcome, Guid? Id)> PostAsync(string callerToken, MoonbasePost message, CancellationToken cancellationToken)
        {
            Posts.Add(message);
            var id = message.Id ?? Guid.NewGuid();
            Items.RemoveAll(i => i.Id == id);
            Items.Add(new MoonbaseMessage(id, message.Title, message.TargetUserIds));
            return Task.FromResult<(MoonbaseOutcome, Guid?)>((MoonbaseOutcome.Ok, id));
        }
    }
}
