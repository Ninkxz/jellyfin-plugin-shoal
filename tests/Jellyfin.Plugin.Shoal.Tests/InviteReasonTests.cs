using System;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Owner feedback 01: "invite expired" mid-flow with a fresh invite. The invite lives 15 minutes from when the friend made it (not from the first redeem),
/// an approved key can be collected for two minutes after the approval, and Home is told <i>why</i> an invite is over (expired, replaced, cancelled,
/// uncollected, used) instead of one 404 that it read as "invalid". Only a secret nobody knows stays the uniform 404.
/// </summary>
public sealed class InviteReasonTests
{
    private const string HomeId = "1a2b3c4d5e6f70819a0b1c2d3e4f5a6b";
    private const string HomeDevice = "shoal-1a2b3c4d5e6f-9a8b7c6d";
    private const string PeerId = "ffeeddccbbaa99887766554433221100";

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Dir = new TempDirectory();
            Time = new ManualTimeProvider();
            State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, Time, null);
            Users = new FakeShareUsers();
            Libraries = new FakeLibraries();
            Movies = Libraries.AddLocal("Movies");
            Book = new InviteBook(State, Users, new FakeHomeIdentity { ServerId = PeerId }, Time, NullLogger.Instance);
            Service = new ShareService(State, Users, Libraries, Book, Time, NullLogger.Instance);
        }

        public TempDirectory Dir { get; }

        public ManualTimeProvider Time { get; }

        public StateStore State { get; }

        public FakeShareUsers Users { get; }

        public FakeLibraries Libraries { get; }

        public Jellyfin.Plugin.Shoal.Visibility.LibraryInfo Movies { get; }

        public InviteBook Book { get; }

        public ShareService Service { get; }

        public string ShareId => State.Current.PeerRole.Shares[0].ShareId;

        public async Task<Home> NewHomeAsync()
        {
            if (State.Current.PeerRole.Shares.Count == 0)
            {
                Assert.Equal(ShareOutcome.Ok, (await Service.CreateAsync(new ShareRequest { FriendName = "Bob", LibraryIds = [Movies.Id] }, default)).Outcome);
            }

            var (outcome, body) = await Service.InviteAsync(ShareId, true, default);
            Assert.Equal(ShareOutcome.Ok, outcome);
            Assert.True(InviteCrypto.TryParse(JsonSerializer.SerializeToElement(body).GetProperty("invite").GetString()!, out _, out var secret));
            return new Home(secret);
        }

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }
    }

    private sealed class Home(string secret)
    {
        private readonly byte[] _nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        public string Secret { get; } = secret;

        public string? PeerNonce { get; private set; }

        public PairAnswer Poll(InviteBook book, bool reveal, string? homeId = null, string name = "Rich's Jellyfin")
        {
            var answer = book.Redeem(new PairRequestData(Secret, InviteCrypto.Commit(_nonce), reveal ? InviteCrypto.Base64Url(_nonce) : null, homeId ?? HomeId, name, HomeDevice, "100.64.1.2"));
            PeerNonce = answer.PeerNonce ?? PeerNonce;
            return answer;
        }

        public string Sas()
        {
            Assert.True(InviteCrypto.TryBase64Url(PeerNonce, out var peer));
            return InviteCrypto.Sas(Secret, _nonce, peer, HomeId, PeerId);
        }
    }

    // ---------------- Peer side ----------------

    [Fact]
    public async Task TheInvite_ExpiresFifteenMinutesAfterTheFriendMadeIt_AndHomeIsToldHowLongIsLeft()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(10)); // the friend sent it, the owner took ten minutes to paste it

        var first = home.Poll(rig.Book, reveal: false);
        Assert.Equal("pending", first.State);
        Assert.InRange(first.ExpiresInSeconds!.Value, 298, 300); // five minutes left, not fifteen

        rig.Time.Advance(TimeSpan.FromMinutes(6));
        var late = home.Poll(rig.Book, reveal: true);

        Assert.False(late.Failed);
        Assert.Equal("expired", late.State); // a reason, not the uniform 404 that Home read as "invalid"
        Assert.Null(late.Token);
    }

    [Fact]
    public async Task AnApprovedKey_CanBeCollectedForTwoMinutes_EvenPastTheInvitesOwnFifteen()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book, reveal: true);
        rig.Time.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(50));
        home.Poll(rig.Book, reveal: true); // Home is still polling, as it does every second
        var (outcome, _) = await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        Assert.Equal(ShareOutcome.Ok, outcome);

        rig.Time.Advance(TimeSpan.FromSeconds(40)); // 15:30 after the invite was made, 40 s after the approval
        var collected = home.Poll(rig.Book, reveal: true);

        Assert.Equal("approved", collected.State);
        Assert.Equal("0123456789abcdef0123456789abcdef", collected.Token);
    }

    [Fact]
    public async Task AKeyNobodyCollected_IsReportedAsUncollected_BeforeAndAfterTheSweep()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book, reveal: true);
        await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);

        rig.Time.Advance(InviteBook.CollectWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("uncollected", home.Poll(rig.Book, reveal: true).State);
        await rig.Book.SweepAsync(default);
        var after = home.Poll(rig.Book, reveal: true);

        Assert.Equal("uncollected", after.State);
        Assert.Null(after.Token);
    }

    [Fact]
    public async Task ANewInvite_ReplacesABoundOne_AndTheOldSecretSaysSo()
    {
        using var rig = new Rig();
        var old = await rig.NewHomeAsync();
        old.Poll(rig.Book, reveal: false);

        var current = await rig.NewHomeAsync();

        Assert.Equal("replaced", old.Poll(rig.Book, reveal: true).State);
        Assert.Equal("pending", current.Poll(rig.Book, reveal: false).State);
    }

    // ---------------- Pilot 05: an interrupted attempt does not burn the invite ----------------

    private static string StateOf(object? answer) => JsonSerializer.SerializeToElement(answer).GetProperty("state").GetString()!;

    [Fact]
    public async Task Pilot05_HomeThatBackedOut_CanStartAgainWithTheSameInvite_AndTheOldAttemptIsGone()
    {
        using var rig = new Rig();
        var first = await rig.NewHomeAsync();
        first.Poll(rig.Book, reveal: false);
        first.Poll(rig.Book, reveal: true); // the card is up for the first attempt
        var oldSas = first.Sas();

        var second = new Home(first.Secret); // Back out, reload: a new attempt with new nonces, the same secret
        Assert.Equal("pending", second.Poll(rig.Book, reveal: false).State);
        Assert.Equal("pending", second.Poll(rig.Book, reveal: true).State);

        Assert.True(first.Poll(rig.Book, reveal: true).Failed, "the old attempt's nonce no longer matches");
        Assert.NotEqual(oldSas, second.Sas());
        var (wrong, body) = await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = oldSas }, default);
        Assert.Equal(ShareOutcome.Ok, wrong);
        Assert.False(JsonSerializer.SerializeToElement(body).GetProperty("match").GetBoolean(), "the dead card's code is not accepted");
        var (ok, _) = await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = second.Sas() }, default);
        Assert.Equal(ShareOutcome.Ok, ok);
        Assert.Equal("approved", second.Poll(rig.Book, reveal: true).State);
    }

    [Fact]
    public async Task Pilot05_ARestart_NeedsTheSameHomeName_AndMarksTheCard()
    {
        using var rig = new Rig();
        var first = await rig.NewHomeAsync();
        first.Poll(rig.Book, reveal: false);
        first.Poll(rig.Book, reveal: true);
        Assert.False(JsonSerializer.SerializeToElement(rig.Service.Approval(rig.ShareId)).GetProperty("restarted").GetBoolean());

        Assert.True(new Home(first.Secret).Poll(rig.Book, reveal: false, name: "Someone else").Failed, "a restart under another name is refused");
        var second = new Home(first.Secret);
        second.Poll(rig.Book, reveal: false);
        second.Poll(rig.Book, reveal: true);

        Assert.True(JsonSerializer.SerializeToElement(rig.Service.Approval(rig.ShareId)).GetProperty("restarted").GetBoolean());
    }

    [Fact]
    public async Task Pilot05_StartingAgain_DoesNotGiveBackTheTriesAlreadyUsed()
    {
        using var rig = new Rig();
        var first = await rig.NewHomeAsync();
        first.Poll(rig.Book, reveal: false);
        first.Poll(rig.Book, reveal: true);
        await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default);
        await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default);

        var second = new Home(first.Secret);
        second.Poll(rig.Book, reveal: false);
        second.Poll(rig.Book, reveal: true);
        var (_, body) = await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default);

        Assert.Equal("cancelledWrongCode", JsonSerializer.SerializeToElement(body).GetProperty("state").GetString());
        Assert.Equal("codeMismatch", second.Poll(rig.Book, reveal: true).State);
    }

    [Fact]
    public async Task Pilot05_AnAttemptNobodyPollsAnyMore_ShowsNoCardAndCannotBeApproved_UntilHomeIsBack()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book, reveal: true);
        Assert.Equal("pending", StateOf(rig.Service.Approval(rig.ShareId)));

        rig.Time.Advance(InviteBook.AttemptStale + TimeSpan.FromSeconds(1));

        Assert.Equal("none", StateOf(rig.Service.Approval(rig.ShareId)));
        var (outcome, _) = await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        Assert.Equal(ShareOutcome.Conflict, outcome);
        Assert.Equal("pending", home.Poll(rig.Book, reveal: true).State); // Home is back: the card is back
        Assert.Equal("pending", StateOf(rig.Service.Approval(rig.ShareId)));
    }

    [Fact]
    public async Task Pilot05_AnApprovedAttempt_IsNotRestarted()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book, reveal: true);
        await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);
        Assert.True(new Home(home.Secret).Poll(rig.Book, reveal: false).Failed, "an approved key waits for its own Home");

    }

    [Fact]
    public async Task Pilot05_ADifferentHomeServer_CannotTakeOverABoundInvite()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);

        var stranger = new Home(home.Secret);
        Assert.True(stranger.Poll(rig.Book, reveal: false, homeId: "99999999999999999999999999999999").Failed);
        Assert.Equal("pending", home.Poll(rig.Book, reveal: true).State); // the real attempt is untouched
    }

    [Fact]
    public async Task Pilot05_AReplacedInvite_IsReportedAsReplaced_EvenToAHomeThatWasPolling()
    {
        using var rig = new Rig();
        var old = await rig.NewHomeAsync();
        old.Poll(rig.Book, reveal: false);
        old.Poll(rig.Book, reveal: true);

        await rig.NewHomeAsync();

        Assert.Equal("replaced", old.Poll(rig.Book, reveal: true).State);
    }

    [Fact]
    public async Task ANewInvite_NeverKillsAnApprovalInFlightOrAKeyWaitingToBeCollected()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        home.Poll(rig.Book, reveal: false);
        home.Poll(rig.Book, reveal: true);
        await rig.Service.DecideAsync(rig.ShareId, new ApprovalRequest { Approve = true, Sas = home.Sas() }, default);

        var (outcome, _) = await rig.Service.InviteAsync(rig.ShareId, true, default);

        Assert.Equal(ShareOutcome.Conflict, outcome);
        Assert.Equal("approved", home.Poll(rig.Book, reveal: true).State);
    }

    [Fact]
    public async Task ASecretNobodyKnows_StaysTheUniform404_AndALongOverInviteIsForgotten()
    {
        using var rig = new Rig();
        var home = await rig.NewHomeAsync();
        var stranger = new Home(InviteCrypto.NewSecret());

        Assert.True(stranger.Poll(rig.Book, reveal: false).Failed);
        rig.Time.Advance(TimeSpan.FromHours(7));
        await rig.Book.SweepAsync(default);
        Assert.True(home.Poll(rig.Book, reveal: false).Failed); // forgotten after six hours: back to the uniform answer
    }

    // ---------------- Home side: what Home makes of each answer ----------------

    private static PairingRig NewRig() => new(o =>
    {
        o.PollTotal = TimeSpan.FromSeconds(15);
        o.NetworkAbortAfter = TimeSpan.FromSeconds(30);
    });

    private static Reply Pending(FakeJellyfinPeer peer, int? expiresIn = null) =>
        Reply.Json(new { v = 1, state = "pending", peerServerId = peer.ServerId, peerNonce = InviteCrypto.Base64Url(new byte[16]), expiresInSeconds = expiresIn });

    private static async Task<PairingSessionInfo> RunAsync(FakeJellyfinPeer peer, PairingRig rig)
    {
        var invite = InviteCrypto.Format(peer.ServerId, InviteCrypto.NewSecret());
        var probe = await rig.Service.ProbeAsync(new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet, Invite = invite });
        Assert.True(probe.Ok, probe.Failure);
        await rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops");
        return await rig.UntilFinishedAsync(60_000);
    }

    [Theory]
    [InlineData("expired", PairingErrorKind.Expired)]
    [InlineData("replaced", PairingErrorKind.InviteReplaced)]
    [InlineData("cancelled", PairingErrorKind.InviteCancelled)]
    [InlineData("uncollected", PairingErrorKind.TokenNotCollected)]
    [InlineData("used", PairingErrorKind.InviteInvalid)]
    public async Task HomeStopsWithTheReasonThePeerGave_WhenTheInviteIsOverAtTheFirstCall(string word, PairingErrorKind expected)
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.PairReply = _ => Reply.Json(new { v = 1, state = word, peerServerId = peer.ServerId });
        using var rig = NewRig();

        var done = await RunAsync(peer, rig);

        Assert.Equal(expected, done.Error);
    }

    [Fact]
    public async Task HomeStopsWithTheReason_WhenTheInviteEndsWhileWaitingForTheApproval()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var calls = 0;
        peer.PairReply = _ => ++calls == 1 ? Pending(peer) : Reply.Json(new { v = 1, state = "replaced", peerServerId = peer.ServerId });
        using var rig = NewRig();

        var done = await RunAsync(peer, rig);

        Assert.Equal(PairingErrorKind.InviteReplaced, done.Error);
    }

    [Fact]
    public async Task HomesDeadline_IsWhatThePeerSaysIsLeft_NotAFreshFifteenMinutes()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.PairReply = _ => Pending(peer, expiresIn: 2); // the invite was made fourteen minutes ago
        using var rig = NewRig();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var done = await RunAsync(peer, rig);

        Assert.Equal(PairingErrorKind.Expired, done.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Home gave up when the invite ended, not 15 minutes later");
    }

    [Fact]
    public async Task APeerWithoutTheRemainingTime_KeepsHomesOwnDeadline_AndAnOldPeers404StaysInvalid()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var calls = 0;
        peer.PairReply = _ => ++calls == 1 ? Pending(peer) : new Reply(404, "{}"); // an older Peer: no reasons, no remaining time
        using var rig = NewRig();

        var done = await RunAsync(peer, rig);

        Assert.Equal(PairingErrorKind.InviteInvalid, done.Error);
    }

    [Fact]
    public async Task ASlowUser_WhoStartsAfterTheProbeLifetime_GetsProbeExpired_NotAnExpiredPairing()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        var time = new ManualTimeProvider();
        using var rig = new PairingRig(null, time);
        var probe = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)); // probe lifetime + 1 s

        var refused = await Assert.ThrowsAsync<PairingConflictException>(() => rig.Service.BeginAsync(PairingRig.Admin, probe.ProbeHandle!, "Hops"));

        Assert.Equal(PairingConflict.ProbeExpired, refused.Conflict);
        Assert.Null(rig.Service.GetSession()); // nothing was started, so there is no stale "expired" session to show when the page comes back
        var again = await rig.ProbeAsync(peer, invite: PairingRig.UnknownInvite(peer));
        Assert.True(again.Ok);
        Assert.NotNull(await rig.Service.BeginAsync(PairingRig.Admin, again.ProbeHandle!, "Hops"));
    }
}
