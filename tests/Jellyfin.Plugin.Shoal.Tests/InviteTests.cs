using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>K22 (DESIGN 4.1b, T-PAIR-8, -9, -10), Peer side: the invite, the commit-reveal, the safety code, the approval and the token delivery.</summary>
public sealed class InviteTests
{
    private const string HomeId = "1a2b3c4d5e6f70819a0b1c2d3e4f5a6b";
    private const string HomeDevice = "shoal-1a2b3c4d5e6f-9a8b7c6d";

    private sealed class Rig : IDisposable
    {
        public Rig(ShareLocks? locks = null)
        {
            Dir = new TempDirectory();
            Time = new ManualTimeProvider();
            State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, Time, null);
            Users = new FakeShareUsers();
            Libraries = new FakeLibraries();
            Movies = Libraries.AddLocal("Movies");
            Book = new InviteBook(State, Users, new FakeHomeIdentity { ServerId = PeerId }, Time, NullLogger.Instance, locks);
            Service = new ShareService(State, Users, Libraries, Book, Time, NullLogger.Instance, locks);
        }

        public const string PeerId = "ffeeddccbbaa99887766554433221100";

        public TempDirectory Dir { get; }

        public ManualTimeProvider Time { get; }

        public StateStore State { get; }

        public FakeShareUsers Users { get; }

        public FakeLibraries Libraries { get; }

        public Jellyfin.Plugin.Shoal.Visibility.LibraryInfo Movies { get; }

        public InviteBook Book { get; }

        public ShareService Service { get; }

        public ShareState Share => State.Current.PeerRole.Shares[0];

        public async Task<string> NewShareAndInviteAsync(string name = "Bob")
        {
            Assert.Equal(ShareOutcome.Ok, (await Service.CreateAsync(new ShareRequest { FriendName = name, LibraryIds = [Movies.Id] }, default)).Outcome);
            var share = State.Current.PeerRole.Shares.Single(s => s.FriendName == name);
            return await InviteAsync(share);
        }

        public async Task<string> InviteAsync(ShareState share)
        {
            var (outcome, body) = await Service.InviteAsync(share.ShareId, true, default);
            Assert.Equal(ShareOutcome.Ok, outcome);
            return JsonSerializer.SerializeToElement(body).GetProperty("invite").GetString()!;
        }

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }
    }

    private sealed class Home
    {
        public Home(string invite)
        {
            Assert.True(InviteCrypto.TryParse(invite, out _, out var secret));
            Secret = secret;
            Nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        }

        public string Secret { get; }

        public byte[] Nonce { get; }

        public string? PeerNonce { get; private set; }

        public PairRequestData Request(bool reveal, string secret = "", string ip = "100.64.1.2") =>
            new(secret.Length > 0 ? secret : Secret, InviteCrypto.Commit(Nonce), reveal ? InviteCrypto.Base64Url(Nonce) : null, HomeId, "Rich's Jellyfin", HomeDevice, ip);

        public string Sas(string peerServerId = Rig.PeerId)
        {
            Assert.True(InviteCrypto.TryBase64Url(PeerNonce, out var peer));
            return InviteCrypto.Sas(Secret, Nonce, peer, HomeId, peerServerId);
        }

        public PairAnswer Poll(InviteBook book, bool reveal = true, string ip = "100.64.1.2")
        {
            var answer = book.Redeem(Request(reveal, ip: ip));
            PeerNonce = answer.PeerNonce ?? PeerNonce;
            return answer;
        }
    }

    private static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void TheInvite_HasTheDocumentedShape_AndParsesAnyWayItWasTyped()
    {
        var text = InviteCrypto.Format(Rig.PeerId, InviteCrypto.NewSecret());

        Assert.Matches("^shoal1-ffeeddcc(-[0-9A-HJKMNP-TV-Z]{4}){6}-[0-9A-HJKMNP-TV-Z]{2}$", text);
        Assert.True(InviteCrypto.TryParse(text.ToLowerInvariant().Replace("-", " "), out var prefix, out var secret));
        Assert.Equal("ffeeddcc", prefix);
        Assert.Equal(26, secret.Length);
        Assert.False(InviteCrypto.TryParse(text[..^3], out _, out _));
    }

    [Fact]
    public async Task T_PAIR_8_TheFlow_CommitFirst_ThenReveal_ThenApprovalWithTheTypedCode()
    {
        using var rig = new Rig();
        var invite = await rig.NewShareAndInviteAsync();
        var home = new Home(invite);

        var first = home.Poll(rig.Book, reveal: false);
        Assert.False(first.Failed);
        Assert.Equal("pending", first.State);
        Assert.Equal(Rig.PeerId, first.PeerServerId);
        Assert.Equal("none", Json(rig.Service.Approval(rig.Share.ShareId)).GetProperty("state").GetString());

        var second = home.Poll(rig.Book);
        Assert.Equal("pending", second.State);
        var approval = Json(rig.Service.Approval(rig.Share.ShareId));
        Assert.Equal("pending", approval.GetProperty("state").GetString());
        Assert.Equal("Rich's Jellyfin", approval.GetProperty("homeName").GetString());
        Assert.Equal("1a2b3c4d", approval.GetProperty("homeServerIdPrefix").GetString());
        Assert.Equal("tailnet", approval.GetProperty("addressClass").GetString());
        Assert.Equal(3, approval.GetProperty("triesLeft").GetInt32());
        Assert.DoesNotContain(home.Sas(), approval.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var (outcome, answer) = await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas().ToLowerInvariant().Insert(4, " ") }, default);

        Assert.Equal(ShareOutcome.Ok, outcome);
        Assert.True(Json(answer).GetProperty("match").GetBoolean());
        Assert.Equal([HomeDevice], rig.Share.ExpectedDeviceIds);
        Assert.Equal([(rig.Share.ShareUserId, HomeDevice)], rig.Users.Minted);
        var delivered = home.Poll(rig.Book);
        Assert.Equal("approved", delivered.State);
        Assert.Equal("0123456789abcdef0123456789abcdef", delivered.Token);
        Assert.True(home.Poll(rig.Book).Failed, "the token is delivered once");
        Assert.Equal("collected", Json(rig.Service.Approval(rig.Share.ShareId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task M2_AShareThatIsPausedWhileTheApprovalRuns_GetsNoToken_AndHomeStaysPending()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        var shareId = rig.Share.ShareId;
        rig.Users.OnPolicyApplied = () =>
        {
            rig.Users.OnPolicyApplied = null;
            rig.State.UpdateAsync(s => s.PeerRole.Shares.Single(x => x.ShareId == shareId).Enabled = false).GetAwaiter().GetResult(); // the admin pauses right now
        };

        var (outcome, _) = await rig.Service.DecideAsync(shareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);

        Assert.Equal(ShareOutcome.Invalid, outcome); // the approval is rolled back, as for any failed approval
        Assert.Empty(rig.Users.Minted); // nothing was minted for a paused share
        Assert.DoesNotContain(rig.Users.Tokens, t => t.Value.User == rig.Share.ShareUserId);
        Assert.False(rig.Share.Enabled);
        var poll = home.Poll(rig.Book);
        Assert.Equal("pending", poll.State);
        Assert.Null(poll.Token);
    }

    [Fact]
    public async Task M2_APauseWaitsForTheApprovalInFlight_AndTheShareEndsPaused()
    {
        var locks = new ShareLocks();
        using var rig = new Rig(locks);
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        var shareId = rig.Share.ShareId;
        rig.Users.Mint(rig.Share.ShareUserId, "stray-device"); // approval signs it out first, which is where the gate holds it
        rig.Users.RemoveGate = new TaskCompletionSource();
        var approving = rig.Service.DecideAsync(shareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        for (var i = 0; i < 200 && rig.Users.RemovalsEntered == 0; i++)
        {
            await Task.Delay(10);
        }

        var pausing = rig.Service.SetEnabledAsync(shareId, false, default);
        await Task.Delay(150);
        Assert.False(pausing.IsCompleted); // not while the approval holds the share

        rig.Users.RemoveGate.SetResult();
        await approving;
        Assert.Equal(ShareOutcome.Ok, await pausing);

        Assert.False(rig.Share.Enabled);
        Assert.True(rig.Users.Policies[rig.Share.ShareUserId].Disabled); // the pause came last and stuck
    }

    [Fact]
    public async Task TheSafetyCode_IsTheSameOnBothSides_AndChangesWithAnyInput()

    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);

        var peerNonce = Convert.FromBase64String(home.PeerNonce!.Replace('-', '+').Replace('_', '/') + "==");
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", home.Sas());
        Assert.NotEqual(home.Sas(), InviteCrypto.Sas(home.Secret, home.Nonce, peerNonce, HomeId, "00000000000000000000000000000000"));
        Assert.NotEqual(home.Sas(), InviteCrypto.Sas(home.Secret, new byte[16], peerNonce, HomeId, Rig.PeerId));
        Assert.Equal(home.Sas(), InviteCrypto.Sas(home.Secret, home.Nonce, peerNonce, HomeId, Rig.PeerId));
    }

    [Fact]
    public async Task T_PAIR_8_Extended_ThreeWrongCodes_EndThatRequest_HomeReadsCodeMismatch_OtherInvitesAreUntouched()
    {
        using var rig = new Rig();
        var invite = await rig.NewShareAndInviteAsync("Bob");
        await rig.Service.CreateAsync(new ShareRequest { FriendName = "Cy", LibraryIds = [rig.Movies.Id] }, default);
        var cy = rig.State.Current.PeerRole.Shares.Single(s => s.FriendName == "Cy");
        var otherHome = new Home(await rig.InviteAsync(cy));
        otherHome.Poll(rig.Book, reveal: false);
        var home = new Home(invite);
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        var bob = rig.State.Current.PeerRole.Shares.Single(s => s.FriendName == "Bob");

        var first = Json((await rig.Service.DecideAsync(bob.ShareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default)).Body);
        Assert.False(first.GetProperty("match").GetBoolean());
        Assert.Equal(2, first.GetProperty("triesLeft").GetInt32());
        await rig.Service.DecideAsync(bob.ShareId, new ApprovalRequest { Approve = true, Sas = "not a code" }, default);
        var third = Json((await rig.Service.DecideAsync(bob.ShareId, new ApprovalRequest { Approve = true, Sas = "BBBBBBBB" }, default)).Body);

        Assert.Equal("cancelledWrongCode", third.GetProperty("state").GetString());
        Assert.Equal("codeMismatch", home.Poll(rig.Book).State);
        Assert.Equal("cancelledWrongCode", Json(rig.Service.Approval(bob.ShareId)).GetProperty("state").GetString());
        Assert.Equal(ShareOutcome.Conflict, (await rig.Service.DecideAsync(bob.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default)).Outcome);
        Assert.Empty(rig.Users.Minted);
        Assert.Equal("pending", otherHome.Poll(rig.Book).State);
    }

    [Fact]
    public async Task Deny_NeedsNoCode_AndHomeReadsDenied()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);

        var (outcome, body) = await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = false }, default);

        Assert.Equal(ShareOutcome.Ok, outcome);
        Assert.Equal("denied", Json(body).GetProperty("state").GetString());
        Assert.Equal("denied", home.Poll(rig.Book).State);
        Assert.Empty(rig.Users.Minted);
    }

    [Fact]
    public async Task OnApproval_ExistingDevicesAreSignedOutFirst_ThenTheLockIsSet_ThenTheTokenIsMinted()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        rig.Users.Mint(rig.Share.ShareUserId, "an-old-device");
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        rig.Users.Calls.Clear();

        await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);

        Assert.Equal(["remove-device", "policy"], rig.Users.Calls);
        Assert.Equal([HomeDevice], rig.Users.DevicesOf[rig.Share.ShareUserId].Select(d => d.DeviceId));
        Assert.Equal([HomeDevice], rig.Users.Policies[rig.Share.ShareUserId].AllowedDevices);
    }

    [Fact]
    public async Task T_PAIR_9_ATokenNobodyCollects_IsSignedOutAfterTwoMinutes()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        Assert.Single(rig.Users.DevicesOf[rig.Share.ShareUserId]);

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Book.SweepAsync(default);
        Assert.Single(rig.Users.DevicesOf[rig.Share.ShareUserId]);
        rig.Time.Advance(TimeSpan.FromMinutes(1.5));
        await rig.Book.SweepAsync(default);

        Assert.Empty(rig.Users.DevicesOf[rig.Share.ShareUserId]);
        Assert.Equal("uncollected", Json(rig.Service.Approval(rig.Share.ShareId)).GetProperty("state").GetString());
        Assert.True(home.Poll(rig.Book).State != "approved" || home.Poll(rig.Book).Token is null);
    }

    [Fact]
    public async Task AnInvite_LivesFifteenMinutes_IsSingleUse_AndANewOneReplacesTheOld()
    {
        using var rig = new Rig();
        var invite = await rig.NewShareAndInviteAsync();
        var home = new Home(invite);
        Assert.Equal("active", Json(rig.Service.Read(rig.Share.ShareId)!).GetProperty("invite").GetProperty("state").GetString());

        var replaced = await rig.InviteAsync(rig.Share);
        Assert.NotEqual(invite, replaced);
        Assert.True(home.Poll(rig.Book, reveal: false).Failed, "the old invite no longer works");

        var fresh = new Home(replaced);
        rig.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.True(fresh.Poll(rig.Book, reveal: false).Failed);
        Assert.Equal("expired", Json(rig.Service.Read(rig.Share.ShareId)!).GetProperty("invite").GetProperty("state").GetString());

        var cancelled = new Home(await rig.InviteAsync(rig.Share));
        await rig.Service.InviteAsync(rig.Share.ShareId, false, default);
        Assert.True(cancelled.Poll(rig.Book, reveal: false).Failed);
    }

    [Fact]
    public async Task EveryFailure_IsTheSameAnswer_AMismatchedCommitmentOrNonceIsAFailure()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        Assert.True(rig.Book.Redeem(home.Request(false, secret: "0000000000000000000000000A")).Failed);
        Assert.True(home.Poll(rig.Book, reveal: true).Failed, "the first request must be the commitment only");
        home.Poll(rig.Book, reveal: false);
        var other = new Home(await rig.InviteAsync(rig.Share));
        Assert.True(rig.Book.Redeem(home.Request(true) with { HomeNonceCommit = InviteCrypto.Commit(new byte[16]) }).Failed);
        Assert.True(rig.Book.Redeem(home.Request(true) with { HomeNonce = InviteCrypto.Base64Url(new byte[16]) }).Failed);
        Assert.NotNull(other);
    }

    private static byte[] PairBody(string homeName = "Rich", string deviceId = HomeDevice, string serverId = HomeId)
    {
        var secret = InviteCrypto.NewSecret();
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = 1,
            ["invite"] = secret,
            ["homeNonceCommit"] = InviteCrypto.Commit(new byte[16]),
            ["homeServerId"] = serverId,
            ["homeName"] = homeName,
            ["homeDeviceId"] = deviceId
        });
    }

    [Fact]
    public void L2_ThePairBody_IsParsedStrictly()
    {
        Assert.NotNull(Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody()));
        Assert.Null(Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody(deviceId: HomeDevice + "\n")));
        Assert.Null(Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody(serverId: HomeId + "\n")));
        Assert.Null(Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody(deviceId: "shoal-ffffffffffff-9a8b7c6d")));
        Assert.Null(Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody(homeName: "bad\u0001name")));
        Assert.Equal("Rich", Jellyfin.Plugin.Shoal.Api.PairController.Parse(PairBody(homeName: "  Rich  "))!.HomeName);
    }

    [Fact]
    public async Task M1_ASlowMint_KeepsHomeAtPending_NeverHandsOutANullToken_AndBlocksANewInvite()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        rig.Users.MintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deciding = rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        while (rig.Users.Minted.Count == 0)
        {
            await Task.Delay(5);
        }

        var during = home.Poll(rig.Book);
        Assert.Equal("pending", during.State);
        Assert.Null(during.Token);
        Assert.Equal(ShareOutcome.Conflict, (await rig.Service.InviteAsync(rig.Share.ShareId, true, default)).Outcome);
        Assert.Equal(ShareOutcome.Conflict, (await rig.Service.InviteAsync(rig.Share.ShareId, false, default)).Outcome);
        Assert.Equal("pending", Json(rig.Service.Approval(rig.Share.ShareId)).GetProperty("state").GetString());

        rig.Users.MintGate.SetResult();
        Assert.Equal(ShareOutcome.Ok, (await deciding).Outcome);
        var after = home.Poll(rig.Book);
        Assert.Equal("approved", after.State);
        Assert.Equal("0123456789abcdef0123456789abcdef", after.Token);
        Assert.Null(home.Poll(rig.Book).Token);
    }

    [Fact]
    public async Task M4_RevokingAShare_ClearsItsInvite_AndDropsAnApprovalThatIsStillMinting()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        var shareId = rig.Share.ShareId;
        var userId = rig.Share.ShareUserId;
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        rig.Users.MintGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deciding = rig.Service.DecideAsync(shareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        while (rig.Users.Minted.Count == 0)
        {
            await Task.Delay(5);
        }

        var revoke = await rig.Service.RevokeAsync(shareId, "Bob", default);
        Assert.Equal(ShareOutcome.Ok, revoke.Outcome);
        rig.Users.MintGate.SetResult();

        Assert.Equal(ShareOutcome.Invalid, (await deciding).Outcome);
        Assert.True(home.Poll(rig.Book).Failed, "the invite is gone");
        Assert.False(rig.Users.Exists(userId));
    }

    [Fact]
    public async Task M1_AFailedMint_SignsEverythingOut_AndLetsTheOwnerTryAgain()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        rig.Users.FailMint = true;

        Assert.Equal(ShareOutcome.Invalid, (await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default)).Outcome);

        Assert.Empty(rig.Users.DevicesOf[rig.Share.ShareUserId]);
        Assert.Equal("pending", Json(rig.Service.Approval(rig.Share.ShareId)).GetProperty("state").GetString());
        Assert.Null(home.Poll(rig.Book).Token);
        rig.Users.FailMint = false;
        Assert.Equal(ShareOutcome.Ok, (await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default)).Outcome);
        Assert.NotNull(home.Poll(rig.Book).Token);
    }

    [Fact]
    public async Task M1_APausedShare_GetsNoNewInvite_AndCannotBeApproved_ButCanBeDenied()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);
        await rig.Service.SetEnabledAsync(rig.Share.ShareId, false, default);

        Assert.Equal(ShareOutcome.Conflict, (await rig.Service.InviteAsync(rig.Share.ShareId, true, default)).Outcome);
        Assert.Equal(ShareOutcome.Conflict, (await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default)).Outcome);
        Assert.Empty(rig.Users.Minted);
        Assert.Equal(ShareOutcome.Ok, (await rig.Service.DecideAsync(rig.Share.ShareId, new ApprovalRequest { Approve = false }, default)).Outcome);
    }

    [Fact]
    public async Task T_PAIR_10_WrongInvitesAreLimited_ButAValidSecretNever_AndTheyOnlyRaiseANotice()
    {
        using var rig = new Rig();
        var home = new Home(await rig.NewShareAndInviteAsync());
        var wrong = "0000000000000000000000000A";
        for (var i = 0; i < 10; i++)
        {
            Assert.True(rig.Book.Redeem(home.Request(false, secret: wrong, ip: "203.0.113.9")).Failed);
        }

        Assert.True(rig.Book.Throttled("203.0.113.9"));
        Assert.False(rig.Book.Throttled("203.0.113.10"));
        Assert.False(home.Poll(rig.Book, reveal: false, ip: "203.0.113.9").Failed, "a valid secret is never refused");
        Assert.Equal(10, rig.Book.FailuresLastHour);
        rig.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(rig.Book.Throttled("203.0.113.9"));
        Assert.Equal("pending", home.Poll(rig.Book).State);
    }

    [Fact]
    public async Task TheApprovalAndTheShare_NeverCarryTheCodeOrTheSecret()
    {
        using var rig = new Rig();
        var invite = await rig.NewShareAndInviteAsync();
        var home = new Home(invite);
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book);

        var text = Json(rig.Service.Read(rig.Share.ShareId)).GetRawText();

        Assert.DoesNotContain(home.Sas(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(home.Secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HomeDevice, text, StringComparison.Ordinal);
    }
}
