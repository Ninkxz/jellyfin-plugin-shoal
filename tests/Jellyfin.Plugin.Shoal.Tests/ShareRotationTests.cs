using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K25 (DESIGN v2.6.1): <c>POST /Shoal/Share/Rotate</c> on a Shoal Peer. T-ROT-1 (Peer part) to T-ROT-4 and T-ROT-8.
/// </summary>
public sealed class ShareRotationTests : IDisposable
{
    private const string Device = "shoal-1a2b3c4d5e6f-9a8b7c6d";
    private const string OldToken = "00000000000000000000000000000001";

    private static readonly Guid ShareUser = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid OtherShareUser = Guid.Parse("99999999-8888-7777-6666-555555555556");

    private readonly TempDirectory _dir = new();
    private readonly StateStore _state;
    private readonly FakeShareUsers _users = new() { UniqueTokens = true };
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLogger _log = new();
    private readonly ShareRotation _rotation;

    public ShareRotationTests()
    {
        _state = new StateStore(new ShoalPaths(_dir.Path), NullLogger.Instance, _time, null);
        _state.UpdateAsync(s =>
        {
            s.PeerRole.Shares.Add(new ShareState { ShareId = "share-bob", FriendName = "Bob", ShareUserId = ShareUser, ExpectedDeviceIds = [Device], Enabled = true, LibraryIds = [Guid.Parse("11111111-1111-1111-1111-111111111111")] });
            s.PeerRole.Shares.Add(new ShareState { ShareId = "share-cy", FriendName = "Cy", ShareUserId = OtherShareUser, ExpectedDeviceIds = ["shoal-ffffffffffff-aaaaaaaa"], Enabled = true });
        }).GetAwaiter().GetResult();
        Seed(ShareUser, Device, OldToken);
        Seed(OtherShareUser, "shoal-ffffffffffff-aaaaaaaa", "00000000000000000000000000000002");
        _users.Calls.Clear();
        _users.Minted.Clear();
        _rotation = new ShareRotation(_state, _users, _time, _log);
    }

    public void Dispose()
    {
        _state.Dispose();
        _dir.Dispose();
    }

    // --- T-ROT-1 (Peer part)

    [Fact]
    public async Task T_ROT_1_ARotation_KillsTheOldToken_IssuesANewOne_AndKeepsTheDeviceAndTheDeviceList()
    {
        var result = await _rotation.RotateAsync(ShareUser, OldToken, default);

        Assert.Equal(RotationOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Token);
        Assert.NotEqual(OldToken, result.Token);
        Assert.Equal(Device, result.DeviceId);
        Assert.Null(_users.DeviceIdOfToken(ShareUser, OldToken)); // the old token is dead
        Assert.Equal(Device, _users.DeviceIdOfToken(ShareUser, result.Token!));
        var devices = _users.Devices(ShareUser);
        Assert.Equal(Device, Assert.Single(devices).DeviceId); // one device, the same DeviceId
        var share = _state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob");
        Assert.Equal([Device], share.ExpectedDeviceIds);
        Assert.Equal(1, share.RotationCount);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, share.LastRotatedUtc);
        Assert.Null(share.RotationFailedUtc);
        Assert.True(_users.PolicyMatches(ShareUser, ShareService.PolicyOf(share))); // locked to the one device
        Assert.Contains("Rotated the token of share Bob", _log.All, StringComparison.Ordinal);

        // The new token rotates again once the interval has passed.
        _time.Advance(TimeSpan.FromMinutes(11));
        var again = await _rotation.RotateAsync(ShareUser, result.Token!, default);
        Assert.Equal(RotationOutcome.Ok, again.Outcome);
        Assert.Equal(2, _state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob").RotationCount);
    }

    [Fact]
    public async Task TheApprovalAndTheRotation_UseTheSameSequence_AndApprovalStoresThePrefixOfHomesServerId()
    {
        var token = await ShareTokenIssuer.IssueAsync(_state, _users, "share-bob", "shoal-1a2b3c4d5e6f-12345678", "Shoal: Home", replaceExpectedDevices: true, "1a2b3c4d5e6f", 1, default);

        var share = _state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob");
        Assert.Equal(["shoal-1a2b3c4d5e6f-12345678"], share.ExpectedDeviceIds);
        Assert.Equal("1a2b3c4d5e6f", share.HomeServerIdPrefix);
        Assert.Equal("shoal-1a2b3c4d5e6f-12345678", _users.DeviceIdOfToken(ShareUser, token!));
        Assert.Null(_users.DeviceIdOfToken(ShareUser, OldToken)); // approval signed the old device out
        Assert.Contains("remove-device", _users.Calls);
    }

    // --- T-ROT-2: the negative matrix, one answer for every stranger

    [Fact]
    public async Task T_ROT_2_EveryCallerThatIsNotTheLiveTokenOfAnExpectedDevice_GetsTheSameAnswer_AndNothingChanges()
    {
        _users.DevicesOf[ShareUser].Add(new ShareDevice("stray-device", "Stray", "App", "1", DateTime.UtcNow, DateTime.UtcNow));
        _users.Tokens["00000000000000000000000000000003"] = (ShareUser, "stray-device"); // a leftover token of a device that is not expected
        var admin = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var before = _state.Current.PeerRole.Shares.Select(s => (s.ShareId, s.RotationCount, s.LastRotatedUtc)).ToList();
        _users.Calls.Clear();

        var strangers = new (string What, Guid User, string Token)[]
        {
            ("no token", ShareUser, string.Empty),
            ("garbage token", ShareUser, "garbage"),
            ("an admin", admin, "00000000000000000000000000000009"),
            ("another share's token", ShareUser, "00000000000000000000000000000002"),
            ("a leftover token of a device that is not expected", ShareUser, "00000000000000000000000000000003"),
            ("a share user that no share lists", Guid.NewGuid(), OldToken),
        };
        foreach (var (what, user, token) in strangers)
        {
            var result = await _rotation.RotateAsync(user, token, default);

            Assert.True(result.Outcome == RotationOutcome.NotFound, what);
            Assert.Null(result.Token);
        }

        await _state.UpdateAsync(static s => s.PeerRole.Shares.Single(x => x.ShareId == "share-bob").Enabled = false);
        Assert.Equal(RotationOutcome.NotFound, (await _rotation.RotateAsync(ShareUser, OldToken, default)).Outcome); // a paused share
        Assert.Empty(_users.Calls);
        Assert.Empty(_users.Minted);
        Assert.Equal(Device, _users.DeviceIdOfToken(ShareUser, OldToken)); // still alive
        Assert.Equal(before, _state.Current.PeerRole.Shares.Select(s => (s.ShareId, s.RotationCount, s.LastRotatedUtc)).ToList());
    }

    // --- T-ROT-3: rate limits and concurrency

    [Fact]
    public async Task T_ROT_3_OnePerTenMinutes_SixPerDay_AndTheCountsAreCounted()
    {
        var token = OldToken;
        var first = await _rotation.RotateAsync(ShareUser, token, default);
        Assert.Equal(RotationOutcome.Ok, first.Outcome);
        token = first.Token!;

        _time.Advance(TimeSpan.FromMinutes(4));
        var tooSoon = await _rotation.RotateAsync(ShareUser, token, default);
        Assert.Equal(RotationOutcome.TooMany, tooSoon.Outcome);
        Assert.InRange(tooSoon.RetryAfterSeconds, 355, 365); // six more minutes
        Assert.Equal(token, _users.Tokens.Single(t => t.Value.User == ShareUser).Key); // nothing was issued

        for (var i = 2; i <= 6; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            var next = await _rotation.RotateAsync(ShareUser, token, default);
            Assert.Equal(RotationOutcome.Ok, next.Outcome);
            token = next.Token!;
        }

        _time.Advance(TimeSpan.FromMinutes(11));
        var seventh = await _rotation.RotateAsync(ShareUser, token, default);
        Assert.Equal(RotationOutcome.TooMany, seventh.Outcome);
        Assert.True(seventh.RetryAfterSeconds > 3600);

        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(RotationOutcome.Ok, (await _rotation.RotateAsync(ShareUser, token, default)).Outcome);
        Assert.Equal(7, _state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob").RotationCount);
    }

    [Fact]
    public async Task T_ROT_3_TwoAtOnce_OneIsRefusedAsBusy_AndTheRouteHasAGlobalLimit()
    {
        _users.RemoveGate = new TaskCompletionSource();
        var running = _rotation.RotateAsync(ShareUser, OldToken, default);
        await WaitForRemovalAsync();

        var second = await _rotation.RotateAsync(ShareUser, OldToken, default);
        Assert.Equal(RotationOutcome.Busy, second.Outcome);

        _users.RemoveGate.SetResult();
        Assert.Equal(RotationOutcome.Ok, (await running).Outcome);

        // A stranger who hammers the route only ever sees the uniform 404, and a stranger's calls do not use up the route-wide limit (Warden 10 M1).
        var stranger = Guid.NewGuid();
        var outcomes = new List<RotationOutcome>();
        for (var i = 0; i < 60; i++)
        {
            outcomes.Add((await _rotation.RotateAsync(stranger, "garbage", default)).Outcome);
        }

        Assert.All(outcomes, static o => Assert.Equal(RotationOutcome.NotFound, o));
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(RotationOutcome.NotFound, (await _rotation.RotateAsync(Guid.NewGuid(), "garbage", default)).Outcome);
        }

        _time.Advance(TimeSpan.FromMinutes(11));
        var current = _users.Tokens.Single(t => t.Value.User == ShareUser).Key;
        Assert.Equal(RotationOutcome.Ok, (await _rotation.RotateAsync(ShareUser, current, default)).Outcome); // the real share is not starved
    }

    [Fact]
    public async Task T_ROT_3_TheRouteWideLimit_CountsOnlyKnownShareUsers_ThirtyAMinuteTogether()
    {
        var shareUsers = Enumerable.Range(0, 32).Select(i => (User: Guid.Parse("aaaaaaaa-0000-0000-0000-" + i.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), Device: "shoal-0123456789ab-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), Token: i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await _state.UpdateAsync(s =>
        {
            foreach (var (user, device, _) in shareUsers)
            {
                s.PeerRole.Shares.Add(new ShareState { ShareId = "s-" + device[^8..], FriendName = "F" + device[^8..], ShareUserId = user, ExpectedDeviceIds = [device], Enabled = true });
            }
        });
        foreach (var (user, device, token) in shareUsers)
        {
            Seed(user, device, token);
        }

        var outcomes = new List<RotationOutcome>();
        foreach (var (user, _, token) in shareUsers)
        {
            outcomes.Add((await _rotation.RotateAsync(user, token, default)).Outcome);
        }

        Assert.Equal(30, outcomes.Count(static o => o == RotationOutcome.Ok));
        Assert.Equal(2, outcomes.Count(static o => o == RotationOutcome.TooMany));
    }

    // --- T-ROT-4: fault injection

    [Fact]
    public async Task T_ROT_4_AMintThatFailsOnce_IsRetriedInside_AndTheCallerNeverNoticesIt()
    {
        _users.FailMintTimes = 1;

        var result = await _rotation.RotateAsync(ShareUser, OldToken, default);

        Assert.Equal(RotationOutcome.Ok, result.Outcome);
        Assert.Equal(Device, _users.DeviceIdOfToken(ShareUser, result.Token!));
        Assert.Equal(2, _users.Minted.Count);
    }

    [Fact]
    public async Task T_ROT_4_AMintThatFailsTwice_LeavesTheShareWithoutADevice_MarksIt_AndTheCallerGetsOneFailure()
    {
        _users.FailMintTimes = 2;

        var result = await _rotation.RotateAsync(ShareUser, OldToken, default);

        Assert.Equal(RotationOutcome.Failed, result.Outcome);
        Assert.Null(result.Token);
        Assert.DoesNotContain(_users.Tokens, t => t.Value.User == ShareUser); // the old token is already dead and no new one exists
        var share = _state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob");
        Assert.NotNull(share.RotationFailedUtc);
        Assert.Equal(0, share.RotationCount);
        Assert.Equal([Device], share.ExpectedDeviceIds);
    }

    // --- T-ROT-8: nothing secret in the logs

    [Fact]
    public async Task T_ROT_8_NoTokenIsEverLogged_OnSuccessOrFailure()
    {
        var ok = await _rotation.RotateAsync(ShareUser, OldToken, default);
        _time.Advance(TimeSpan.FromMinutes(11));
        _users.FailMintTimes = 2;
        var failed = await _rotation.RotateAsync(ShareUser, ok.Token!, default);

        Assert.Equal(RotationOutcome.Failed, failed.Outcome);
        Assert.DoesNotContain(OldToken, _log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ok.Token!, _log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("[0-9a-f]{32}", _log.All);
    }

    // --- Warden 10 M2: one lock per share

    private (ShareRotation Rotation, ShareService Service) WithSharedLocks()
    {
        var locks = new ShareLocks();
        return (new ShareRotation(_state, _users, _time, _log, locks), new ShareService(_state, _users, new FakeLibraries(), new NoShareInvites(), _time, NullLogger.Instance, locks));
    }

    [Fact]
    public async Task M2_APauseWaitsForARotationInFlight_AndTheShareEndsPaused_WithItsAccountDisabled()
    {
        var (rotation, service) = WithSharedLocks();
        _users.RemoveGate = new TaskCompletionSource();
        var rotating = rotation.RotateAsync(ShareUser, OldToken, default);
        await WaitForRemovalAsync();

        var pausing = service.SetEnabledAsync("share-bob", false, default);
        await Task.Delay(150);
        Assert.False(pausing.IsCompleted); // not while the rotation holds the share

        _users.RemoveGate.SetResult();
        await rotating;
        Assert.Equal(ShareOutcome.Ok, await pausing);

        Assert.False(_state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob").Enabled);
        Assert.True(_users.Policies[ShareUser].Disabled); // the pause was applied last: a stale policy did not undo it
    }

    [Fact]
    public async Task M2_ARevokeWaitsForARotationInFlight_Too()
    {
        var (rotation, service) = WithSharedLocks();
        _users.RemoveGate = new TaskCompletionSource();
        var rotating = rotation.RotateAsync(ShareUser, OldToken, default);
        await WaitForRemovalAsync();

        var revoking = service.RevokeAsync("share-bob", "Bob", default);
        await Task.Delay(150);
        Assert.False(revoking.IsCompleted);

        _users.RemoveGate.SetResult();
        await rotating;
        var (outcome, _) = await revoking;

        Assert.Equal(ShareOutcome.Ok, outcome);
        Assert.DoesNotContain(_users.Tokens, t => t.Value.User == ShareUser); // no token survives the revoke
    }

    [Fact]
    public async Task M2_AShareThatIsPausedAfterTheRead_GetsNoToken_AndAnswersNotFound()
    {
        _users.OnPolicyApplied = () =>
        {
            _users.OnPolicyApplied = null;
            _state.UpdateAsync(static s => s.PeerRole.Shares.Single(x => x.ShareId == "share-bob").Enabled = false).GetAwaiter().GetResult();
        };

        var result = await _rotation.RotateAsync(ShareUser, OldToken, default);

        Assert.Equal(RotationOutcome.NotFound, result.Outcome);
        Assert.Null(result.Token);
        Assert.Empty(_users.Minted); // nothing was minted for a paused share
        Assert.DoesNotContain(_users.Tokens, t => t.Value.User == ShareUser);
        Assert.Null(_state.Current.PeerRole.Shares.Single(s => s.ShareId == "share-bob").RotationFailedUtc); // a pause is not a failed rotation
    }

    // --- Warden 10 L7

    [Fact]
    public async Task L7_ARotationResult_NeverPrintsItsToken()
    {
        var result = await _rotation.RotateAsync(ShareUser, OldToken, default);

        Assert.Equal(RotationOutcome.Ok, result.Outcome);
        var printed = result.ToString() + $"{result}" + System.Text.Json.JsonSerializer.Serialize(result.Outcome);
        Assert.DoesNotContain(result.Token!, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(result.DeviceId!, printed, StringComparison.Ordinal);
        Assert.Equal("RotationResult { Outcome = Ok }", result.ToString());
    }

    [Fact]
    public void L7_ThePairController_FailsClosed_ByAClassLevelAuthorize_WithTheOneAnonymousActionOnTheMethod()
    {
        var type = typeof(PairController);

        Assert.Single(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false));
        Assert.Empty(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: false));
        var anonymous = type.GetMethods().Where(static m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: false).Length > 0).ToList();
        Assert.Equal("Pair", Assert.Single(anonymous).Name);
    }

    // --- the route

    [Fact]
    public async Task TheRoute_AnswersTheDocumentedBodies_AndTheSameNotFoundForAnythingElse()
    {
        var ok = await Call(JsonBody("{\"v\":1}"), ShareUser, OldToken);
        var json = AdminTestSupport.Body(ok);
        Assert.Equal(1, json.GetProperty("v").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", json.GetProperty("token").GetString());
        Assert.Equal(Device, json.GetProperty("deviceId").GetString());
        Assert.True(json.TryGetProperty("rotatedUtc", out _));

        _time.Advance(TimeSpan.FromMinutes(11));
        var token = json.GetProperty("token").GetString()!;
        foreach (var body in new[] { JsonBody("{\"v\":2}"), JsonBody("{}"), JsonBody("not json"), JsonBody(string.Empty), JsonBody("{\"v\":1,\"x\":\"" + new string('a', 100) + "\"}"), (Bytes: Encoding.UTF8.GetBytes("{\"v\":1}"), Type: "text/plain") })
        {
            var rendered = AdminTestSupport.Render(await Call(body, ShareUser, token));
            Assert.Equal(404, rendered.Status);
            Assert.Equal("{}", rendered.Text);
        }

        Assert.Equal(404, AdminTestSupport.Render(await Call(JsonBody("{\"v\":1}"), ShareUser, token, apiKey: true)).Status);
        Assert.Equal(404, AdminTestSupport.Render(await Call(JsonBody("{\"v\":1}"), null, token)).Status);
        Assert.Equal(Device, _users.DeviceIdOfToken(ShareUser, token)); // none of the refused calls touched the live token
    }

    [Fact]
    public async Task TheRoute_AnswersTooManyWithRetryAfter_AndBusyWithTheBusyBody()
    {
        var first = AdminTestSupport.Render(await Call(JsonBody("{\"v\":1}"), ShareUser, OldToken));
        var token = first.Json.GetProperty("token").GetString()!;

        var tooMany = AdminTestSupport.Render(await Call(JsonBody("{\"v\":1}"), ShareUser, token));

        Assert.Equal(429, tooMany.Status);
        Assert.True(int.Parse(tooMany.Headers["Retry-After"].ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        _time.Advance(TimeSpan.FromMinutes(11));
        _users.RemoveGate = new TaskCompletionSource();
        var running = Call(JsonBody("{\"v\":1}"), ShareUser, token);
        await WaitForRemovalAsync();
        var busy = AdminTestSupport.Render(await Call(JsonBody("{\"v\":1}"), ShareUser, token));
        Assert.Equal(409, busy.Status);
        Assert.Equal("busy", busy.Json.GetProperty("error").GetString());
        _users.RemoveGate.SetResult();
        await running;
    }

    [Fact]
    public async Task ThePeerSideReadShowsTheRotation()
    {
        await _rotation.RotateAsync(ShareUser, OldToken, default);
        var service = new ShareService(_state, _users, new FakeLibraries(), new NoShareInvites(), _time, NullLogger.Instance);

        var json = System.Text.Json.JsonSerializer.SerializeToElement(service.Read("share-bob"));

        Assert.Equal(1, json.GetProperty("rotationCount").GetInt32());
        Assert.False(json.GetProperty("rotationFailed").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, json.GetProperty("lastRotatedUtc").ValueKind);
        Assert.DoesNotContain(OldToken, json.GetRawText(), StringComparison.Ordinal);
    }

    private static (byte[] Bytes, string Type) JsonBody(string text) => (Encoding.UTF8.GetBytes(text), "application/json");

    private void Seed(Guid user, string device, string token)
    {
        _users.Policies[user] = new SharePolicy(false, [], null, [device]);
        _users.DevicesOf[user] = [new ShareDevice(device, "Shoal: Home", "Shoal", "1", DateTime.UtcNow, DateTime.UtcNow)];
        _users.Tokens[token] = (user, device);
    }

    private async Task<IActionResult> Call((byte[] Bytes, string Type) body, Guid? user, string token, bool apiKey = false)
    {
        var claims = new List<Claim> { new(CallerContext.TokenClaim, token), new(CallerContext.DeviceIdClaim, "client-chosen-device") };
        if (user is { } id)
        {
            claims.Add(new Claim(CallerContext.UserIdClaim, id.ToString("N")));
        }

        if (apiKey)
        {
            claims.Add(new Claim(CallerContext.IsApiKeyClaim, "true"));
        }

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        http.Request.ContentType = body.Type;
        http.Request.Body = new MemoryStream(body.Bytes);
        http.Request.ContentLength = body.Bytes.Length;
        var controller = new ShareRotateController(_rotation) { ControllerContext = new ControllerContext { HttpContext = http } };
        return await controller.Rotate(CancellationToken.None);
    }

    private async Task WaitForRemovalAsync()
    {
        for (var i = 0; i < 200 && _users.RemovalsEntered == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_users.RemovalsEntered > 0);
    }
}
