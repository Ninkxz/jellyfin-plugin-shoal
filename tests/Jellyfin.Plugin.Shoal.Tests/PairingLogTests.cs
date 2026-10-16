using System;
using System.Linq;
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
/// Every pairing outcome leaves a line in the log on both sides, with a fixed reason word and redacted ids, and never the invite, the safety code,
/// the address or a token (owner feedback 01: only success was logged).
/// </summary>
public sealed class PairingLogTests
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
            Log = new CapturingLogger();
            State = new StateStore(new ShoalPaths(Dir.Path), NullLogger.Instance, Time, null);
            Users = new FakeShareUsers();
            Libraries = new FakeLibraries();
            Movies = Libraries.AddLocal("Movies");
            Book = new InviteBook(State, Users, new FakeHomeIdentity { ServerId = PeerId }, Time, Log);
            Service = new ShareService(State, Users, Libraries, Book, Time, NullLogger.Instance);
        }

        public TempDirectory Dir { get; }

        public ManualTimeProvider Time { get; }

        public CapturingLogger Log { get; }

        public StateStore State { get; }

        public FakeShareUsers Users { get; }

        public FakeLibraries Libraries { get; }

        public Jellyfin.Plugin.Shoal.Visibility.LibraryInfo Movies { get; }

        public InviteBook Book { get; }

        public ShareService Service { get; }

        public async Task<(string Invite, string Secret, string ShareId)> NewInviteAsync()
        {
            Assert.Equal(ShareOutcome.Ok, (await Service.CreateAsync(new ShareRequest { FriendName = "Bob", LibraryIds = [Movies.Id] }, default)).Outcome);
            var share = State.Current.PeerRole.Shares[0];
            var (_, body) = await Service.InviteAsync(share.ShareId, true, default);
            var invite = JsonSerializer.SerializeToElement(body).GetProperty("invite").GetString()!;
            Assert.True(InviteCrypto.TryParse(invite, out _, out var secret));
            return (invite, secret, share.ShareId);
        }

        public void Dispose()
        {
            State.Dispose();
            Dir.Dispose();
        }
    }

    private static PairRequestData Request(string secret, byte[] nonce, bool reveal) =>
        new(secret, InviteCrypto.Commit(nonce), reveal ? InviteCrypto.Base64Url(nonce) : null, HomeId, "Rich's Jellyfin", HomeDevice, "100.64.1.2");

    [Fact]
    public async Task APairingRequestWithAnUnknownInvite_IsLoggedWithItsReason_ButNotItsSecret()
    {
        using var rig = new Rig();
        var (_, _, _) = await rig.NewInviteAsync();
        var wrong = InviteCrypto.NewSecret();

        Assert.True(rig.Book.Redeem(Request(wrong, new byte[16], false)).Failed);

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("Information", StringComparison.Ordinal) && l.Contains("unknownInvite", StringComparison.Ordinal));
        Assert.DoesNotContain(wrong, rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("100.64.1.2", rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedeem_ADenial_AndAnApproval_AreLogged_WithOnlyARedactedHomeId()
    {
        using var rig = new Rig();
        var (invite, secret, shareId) = await rig.NewInviteAsync();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        var first = rig.Book.Redeem(Request(secret, nonce, false));
        Assert.Equal("pending", first.State);
        rig.Book.Redeem(Request(secret, nonce, true));
        Assert.Contains(rig.Log.Lines, l => l.Contains("redeemed by server", StringComparison.Ordinal) && l.Contains("tailnet", StringComparison.Ordinal));

        await rig.Service.DecideAsync(shareId, new ApprovalRequest { Approve = false }, default);

        Assert.Contains(rig.Log.Lines, l => l.Contains("denied", StringComparison.Ordinal));
        Assert.DoesNotContain(HomeId, rig.Log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(invite, rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeWrongCodes_AreLoggedAsCodeMismatch_WithoutTheCodes()
    {
        using var rig = new Rig();
        var (_, secret, shareId) = await rig.NewInviteAsync();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        rig.Book.Redeem(Request(secret, nonce, false));
        rig.Book.Redeem(Request(secret, nonce, true));

        for (var i = 0; i < 3; i++)
        {
            await rig.Service.DecideAsync(shareId, new ApprovalRequest { Approve = true, Sas = "AAAAAAAA" }, default);
        }

        Assert.Contains(rig.Log.Lines, l => l.Contains("codeMismatch", StringComparison.Ordinal));
        Assert.DoesNotContain("AAAAAAAA", rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HomeLogsARefusedCheck_WithTheReasonWord_ButNeverTheInviteOrTheAddress()
    {
        using var rig = new PairingRig();
        var secret = InviteCrypto.NewSecret();
        var invite = InviteCrypto.Format(PeerId, secret);

        var lan = await rig.Service.ProbeAsync(new ProbeRequest { Url = "http://192.168.1.50:8096", Network = PeerNetwork.Lan, Invite = invite, LanAck = true });
        var broken = await rig.Service.ProbeAsync(new ProbeRequest { Url = "http://hops.tail1234.ts.net:8096", Network = PeerNetwork.Tailnet, Invite = "shoal1-nonsense" });

        Assert.False(lan.Ok);
        Assert.False(broken.Ok);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("Information", StringComparison.Ordinal) && l.Contains("inviteLanHttp", StringComparison.Ordinal));
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("Information", StringComparison.Ordinal) && l.Contains("inviteInvalid", StringComparison.Ordinal));
        Assert.DoesNotContain(secret, rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.50", rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("hops.tail1234", rig.Log.All, StringComparison.Ordinal);
    }
}
