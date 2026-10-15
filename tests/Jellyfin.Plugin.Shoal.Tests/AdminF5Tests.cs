using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// ui-spec F5 / Warden W5-5: no admin answer carries a secret or a peer-side id. This runs the REAL controllers over EVERY admin route
/// (GET, POST, PUT and DELETE), against a state and a secret store that hold known values, and looks for those values by value (any
/// case, with or without dashes), not for a shape. A route that is added without a step here fails the coverage check at the end.
/// </summary>
public sealed class AdminF5Tests
{
    // Property names that must never appear in an admin answer. The pairing session's own one-time `code` and `safetyCode` are the two
    // intentional exceptions (the admin reads them out); they are not on this list.
    private static readonly string[] BannedNames =
        ["token", "accesstoken", "apikey", "secret", "password", "sas", "nonce", "homenonce", "commit", "deviceid", "accesskey", "invite", "privatekey"];

    private sealed class Settings : ISettingsAccessor
    {
        public PluginConfiguration Stored { get; private set; } = new();

        public PluginConfiguration Current => PluginSettingsAccessor.Copy(Stored);

        public void Save(PluginConfiguration configuration) => Stored = PluginSettingsAccessor.Copy(configuration);
    }

    private sealed class Checker : IStubRootChecker
    {
        public StubRootCheck Check(string path) => StubRootCheck.Ok;
    }

    private sealed class Findings : IFindingsSource
    {
        public IReadOnlyList<ReconcileFinding> Compute() =>
        [
            new ReconcileFinding(ReconcileKind.PeerWithoutSecret, StateSamples.PeerKeyA, null),
            new ReconcileFinding(ReconcileKind.LibraryWithoutPeer, null, "Old Friend Movies")
        ];
    }

    private sealed class HomeName : IHomeNameSource
    {
        public string? Name => "Rich's Jellyfin";
    }

    private static string Canon(string text) => text.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task NoAnswerOfAnyAdminRoute_ContainsAKnownSecretOrPeerSideId()
    {
        await using var peer = await FakeJellyfinPeer.StartAsync();
        peer.ApproveFromPoll = 2;
        using var rig = new PairingRig();
        await rig.State.UpdateAsync(StateSamples.Fill);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", PairingRig.Admin.ToString("N"))], "test"))
        };
        var context = new ControllerContext { HttpContext = http };
        var live = new StateBackedLiveStatus(rig.Health, rig.Service);

        // Libraries and accounts: a real visibility service over fake Jellyfin libraries and accounts. One account is a friend's sharing account.
        var fakeLibraries = new FakeLibraries();
        var fakeAccounts = new FakeAccounts();
        var alex = fakeAccounts.Add("Alex");
        var shareAccount = fakeAccounts.Add("shoal-bob");
        await rig.State.UpdateAsync(s => s.PeerRole.Shares[0].ShareUserId = shareAccount.Id);
        var visibility = new Jellyfin.Plugin.Shoal.Visibility.VisibilityService(rig.State, fakeLibraries, fakeAccounts, rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.VisibilityService>.Instance);
        var accessService = new Jellyfin.Plugin.Shoal.Visibility.AccessService(rig.State, fakeAccounts, visibility, rig.Time);
        var catalogs = new Jellyfin.Plugin.Shoal.Sync.CatalogStore(rig.Paths, NullLogger<Jellyfin.Plugin.Shoal.Sync.CatalogStore>.Instance);
        var view = new AdminLibraryView(rig.State, catalogs, fakeLibraries, fakeAccounts, visibility);
        var settings = new Settings();
        var home = new AdminController(rig.State, rig.Secrets, live, new HomeName(), new AdminCooldowns(rig.Time), new NoGuardCounters(), view, NullLogger<AdminController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var prefs = new AdminSettingsController(rig.State, rig.Secrets, settings, new Checker(), new NoGuardCounters(), new Findings(), rig.Time, NullLogger<AdminSettingsController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var pairing = new AdminPairingController(rig.Service, rig.State) { ControllerContext = context };
        var shares = new AdminSharesController(new PendingShareService(rig.State), rig.State) { ControllerContext = context };
        var libs = new AdminLibrariesController(rig.State, view, accessService, visibility) { ControllerContext = context };

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answers = new List<(string Route, string Text)>();
        void Record(string verb, string route, IActionResult result)
        {
            covered.Add(verb + " " + Normalize(route));
            answers.Add((verb + " " + route, AdminTestSupport.Text(result)));
        }

        // Pair a real server through the fake peer, so the secret store holds a real token for it.
        Record("GET", "Shoal/Admin/Pairing", pairing.Get());
        var probe = await rig.ProbeAsync(peer);
        Record("POST", "Shoal/Admin/Peers/Probe", await pairing.Probe(new ProbeRequest { Url = peer.BaseUrl, Network = Jellyfin.Plugin.Shoal.State.PeerNetwork.Tailnet }, default));
        var fresh = await rig.ProbeAsync(peer);
        Record("POST", "Shoal/Admin/Pairing", await pairing.Start(new StartPairingRequest { ProbeHandle = fresh.ProbeHandle, DisplayName = "Hops", Method = "qc" }, default));
        await rig.UntilStepAsync(PairingStep.AwaitingConfirm);
        Record("GET", "Shoal/Admin/Pairing", pairing.Get());
        Record("POST", "Shoal/Admin/Pairing/Confirm", await pairing.Confirm(new ConfirmPairingRequest { Match = true }, default));
        await rig.UntilFinishedAsync();
        Assert.NotNull(probe);
        var pairedKey = rig.State.Current.Home.Peers.Single(p => p.DisplayName == "Hops" && p.PeerKey != StateSamples.PeerKeyA).PeerKey;

        Record("DELETE", "Shoal/Admin/Pairing", await pairing.Cancel(default));
        Record("POST", "Shoal/Admin/Peers/{handle}/DismissPairing", await pairing.DismissPairing(pairedKey, default));
        Record("POST", "Shoal/Admin/State/ConfirmRestore", pairing.ConfirmRestore());

        // The Home and settings controllers, every verb.
        Record("GET", "Shoal/Admin/Status", home.GetStatus());
        Record("POST", "Shoal/Admin/Sync", home.SyncAll());
        Record("POST", "Shoal/Admin/Sync", home.SyncAll());
        Record("GET", "Shoal/Admin/Peers/{handle}", home.GetPeer(pairedKey));
        Record("GET", "Shoal/Admin/Peers/{handle}", home.GetPeer(StateSamples.PeerKeyA));
        Record("GET", "Shoal/Admin/Peers/{handle}", home.GetPeer("nope"));
        Record("POST", "Shoal/Admin/Peers/{handle}/Sync", home.SyncPeer(StateSamples.PeerKeyA));
        Record("POST", "Shoal/Admin/Peers/{handle}/Sync", home.SyncPeer(StateSamples.PeerKeyA));
        Record("POST", "Shoal/Admin/Peers/{handle}/Holds/{kind}/{action}", home.Hold(StateSamples.PeerKeyA, "removals", "Release"));
        Record("POST", "Shoal/Admin/Peers/{handle}/Holds/{kind}/{action}", home.Hold(StateSamples.PeerKeyA, "additions", "Keep"));
        Record("POST", "Shoal/Admin/Peers/{handle}/Holds/{kind}/{action}", home.Hold(StateSamples.PeerKeyA, "nope", "Release"));
        Record("POST", "Shoal/Admin/Peers/{handle}/Pause", await home.Pause(StateSamples.PeerKeyA, default));
        Record("POST", "Shoal/Admin/Peers/{handle}/Resume", await home.Resume(StateSamples.PeerKeyA, default));
        // Friends who watch from here (the share service answers "not built" until K21 and K22; the reads come from the state).
        Record("GET", "Shoal/Admin/Shares", shares.List());
        Send(shares, "{\"friendName\":\"Riley\",\"libraryIds\":[],\"maxConcurrentTranscodes\":4}");
        Record("POST", "Shoal/Admin/Shares", await shares.Create(default));
        Record("GET", "Shoal/Admin/Shares/{id}", shares.Get("share-bob"));
        Record("GET", "Shoal/Admin/Shares/{id}", shares.Get("nope"));
        Send(shares, "{\"friendName\":\"Bob\",\"libraryIds\":[],\"maxConcurrentTranscodes\":3}");
        Record("PUT", "Shoal/Admin/Shares/{id}", await shares.Update("share-bob", default));
        Send(shares, "{\"confirmName\":\"Bob\"}");
        Record("DELETE", "Shoal/Admin/Shares/{id}", await shares.Delete("share-bob", default));
        Record("POST", "Shoal/Admin/Shares/{id}/Pause", await shares.Pause("share-bob", default));
        Record("POST", "Shoal/Admin/Shares/{id}/Resume", await shares.Resume("share-bob", default));
        Record("POST", "Shoal/Admin/Shares/{id}/Invites", await shares.CreateInvite("share-bob", default));
        Record("DELETE", "Shoal/Admin/Shares/{id}/Invites/current", await shares.CancelInvite("share-bob", default));
        Record("GET", "Shoal/Admin/Shares/{id}/Approval", shares.GetApproval("share-bob"));
        Send(shares, "{\"approve\":true,\"sas\":\"K7QM2XA4\"}");
        Record("POST", "Shoal/Admin/Shares/{id}/Approval", await shares.Decide("share-bob", default));
        Record("DELETE", "Shoal/Admin/Shares/{id}/Devices/{deviceHandle}", await shares.RemoveDevice("share-bob", "d-1", default));

        // Libraries and access (the answers name local accounts on purpose; the sharing account must never be among them).
        var libraryId = AdminLibraryView.Compose(StateSamples.PeerKeyA, "movies-1");
        Record("GET", "Shoal/Admin/Libraries", libs.GetLibraries());
        Send(libs, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + alex.Id + "\"]}");
        Record("PUT", "Shoal/Admin/Libraries/{id}/Access", await libs.PutAccess(libraryId, default));
        Send(libs, "{\"mode\":\"Selected\",\"selectedUserIds\":[\"" + shareAccount.Id + "\"]}");
        Record("PUT", "Shoal/Admin/Libraries/{id}/Access", await libs.PutAccess(libraryId, default));
        Send(libs, "not json");
        Record("PUT", "Shoal/Admin/Libraries/{id}/Access", await libs.PutAccess(libraryId, default));
        Record("PUT", "Shoal/Admin/Libraries/{id}/Access", await libs.PutAccess("nope", default));
        Record("POST", "Shoal/Admin/Libraries/{id}/Reconcile", await libs.Reconcile(libraryId, default));
        Record("POST", "Shoal/Admin/Libraries/{id}/Reconcile", await libs.Reconcile("nope", default));
        Assert.DoesNotContain(shareAccount.Id.ToString(), AdminTestSupport.Text(libs.GetLibraries()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shoal-bob", AdminTestSupport.Text(libs.GetLibraries()), StringComparison.Ordinal);
        Assert.Contains("Alex", AdminTestSupport.Text(libs.GetLibraries()), StringComparison.Ordinal);
        Record("GET", "Shoal/Admin/Settings", prefs.GetSettings());
        Send(prefs, """{"syncIntervalHours":12}""");
        Record("PUT", "Shoal/Admin/Settings", await prefs.PutSettings(default));
        Send(prefs, "{ not json");
        Record("PUT", "Shoal/Admin/Settings", await prefs.PutSettings(default));
        Record("GET", "Shoal/Admin/Hardening", prefs.GetHardening());
        Send(prefs, """{"mode":"Enforce"}""");
        Record("PUT", "Shoal/Admin/Hardening", await prefs.PutHardening(default));
        Record("GET", "Shoal/Admin/State", prefs.GetState());
        Send(prefs, """{"file":"../../etc/passwd"}""");
        Record("POST", "Shoal/Admin/State/Restore", await prefs.Restore(default));
        Send(prefs, """{"confirmName":"start over"}""");
        Record("POST", "Shoal/Admin/State/StartOver", await prefs.StartOver(default));
        Send(prefs, """{"confirmName":"delete everything"}""");
        Record("DELETE", "Shoal/Admin/All", await prefs.DeleteAll(default));

        // Choosing a friend's libraries (Pilot 04): the answers carry no token, no peer-side id and no secret.
        var libraryReader = new Jellyfin.Plugin.Shoal.Sync.PeerCatalogReader(rig.Client, new Jellyfin.Plugin.Shoal.Peers.PeerTrust(rig.Client, rig.Registry, rig.Time), rig.Registry, new Jellyfin.Plugin.Shoal.Sync.MetadataSanitizer(new AllRatings()), NullLogger<Jellyfin.Plugin.Shoal.Sync.PeerCatalogReader>.Instance);
        var libraryService = new Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService(rig.State, fakeLibraries, visibility, rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Visibility.ShoalLibraryService>.Instance);
        var peerLibraries = new AdminPeerLibrariesController(rig.State, new Jellyfin.Plugin.Shoal.Peers.PeerDirectory(rig.State, rig.Secrets), libraryReader, libraryService, accessService, catalogs, live, NullLogger<AdminPeerLibrariesController>.Instance) { ControllerContext = context };
        Record("GET", "Shoal/Admin/Peers/{handle}/RemoteLibraries", await peerLibraries.RemoteLibraries(StateSamples.PeerKeyA, default));
        Send(peerLibraries, """[{"mappingKey":"r0123456789abcdef","localLibraryName":"X"}]""");
        Record("POST", "Shoal/Admin/Peers/{handle}/Libraries", await peerLibraries.AddLibraries(StateSamples.PeerKeyA, default));
        Record("DELETE", "Shoal/Admin/Peers/{handle}/Libraries/{mappingKey}", await peerLibraries.RemoveLibrary(StateSamples.PeerKeyA, "nothing", default));

        // Leftover sharing accounts (Warden 08 M7): the answer carries no token and no secret.
        var orphans = new AdminOrphanAccountsController(rig.State, fakeAccounts, new FakeShareUsers(), NullLogger<AdminOrphanAccountsController>.Instance) { ControllerContext = context };
        Record("DELETE", "Shoal/Admin/OrphanAccounts/{userId}", await orphans.Delete("nothing", default));

        // Token rotation (K26): handles and words only, no token.
        var rotation = new Jellyfin.Plugin.Shoal.Peers.TokenRotation(rig.State, rig.Secrets, new Jellyfin.Plugin.Shoal.Peers.PeerDirectory(rig.State, rig.Secrets), rig.Client, rig.Registry, new Jellyfin.Plugin.Shoal.Playback.StreamSlots(rig.Time, () => 4), rig.Service, rig.Health, rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Peers.TokenRotation>.Instance);
        var rotate = new AdminRotateController(rig.State, rotation) { ControllerContext = context };
        Record("POST", "Shoal/Admin/Peers/{handle}/Rotate", await rotate.Rotate(StateSamples.PeerKeyA, default));
        Record("GET", "Shoal/Admin/Peers/{handle}/Rotation", rotate.GetRotation(StateSamples.PeerKeyA));
        Send(rotate, """{"days":30}""");
        Record("PUT", "Shoal/Admin/Peers/{handle}/Rotation", await rotate.SetRotation(StateSamples.PeerKeyA, default));

        // Speed (U8): the history and "Test now"; the answers carry numbers and words only.
        var speedHistory = new Jellyfin.Plugin.Shoal.Diagnostics.SpeedHistoryStore(rig.Paths, NullLogger.Instance);
        speedHistory.Append(StateSamples.PeerKeyA, new Jellyfin.Plugin.Shoal.Diagnostics.SpeedSample { T = DateTimeOffset.UtcNow, Kind = "manual", Mbps = 42, RttMedianMs = 38, Path = "unknown" });
        var speedTests = new Jellyfin.Plugin.Shoal.Diagnostics.SpeedTestService(rig.State, new Jellyfin.Plugin.Shoal.Peers.PeerDirectory(rig.State, rig.Secrets), rig.Client, speedHistory, null, rig.Time, NullLogger.Instance);
        var speed = new Jellyfin.Plugin.Shoal.Diagnostics.AdminSpeedController(rig.State, speedHistory, speedTests, new Jellyfin.Plugin.Shoal.Diagnostics.SpeedCooldowns(rig.Time), settings, rig.Time, NullLogger<Jellyfin.Plugin.Shoal.Diagnostics.AdminSpeedController>.Instance) { ControllerContext = context };
        Record("GET", "Shoal/Admin/Peers/{handle}/Speed", speed.Speed(StateSamples.PeerKeyA, "24h"));
        Record("GET", "Shoal/Admin/Peers/{handle}/Speed", speed.Speed("nope", "24h"));
        Send(speed, """{"deep":false}""");
        Record("POST", "Shoal/Admin/Peers/{handle}/SpeedTest", await speed.Start("nope", default));

        // Settings (K33): the cache clear answers a number of bytes and nothing else.
        Record("DELETE", "Shoal/Admin/TrickplayCache", prefs.ClearTrickplayCache());

        // Overlap: numbers and words only.
        var overlap = new AdminOverlapController(rig.State, catalogs) { ControllerContext = context };
        Record("GET", "Shoal/Admin/Peers/{handle}/Overlap", overlap.Overlap(StateSamples.PeerKeyA));
        Record("GET", "Shoal/Admin/Peers/{handle}/Overlap", overlap.Overlap("nope"));

        // Server detail settings: words only.
        var peerSettings = new AdminPeerSettingsController(rig.State, new Jellyfin.Plugin.Shoal.Peers.PeerDirectory(rig.State, rig.Secrets), rig.Client, rig.Registry, new Jellyfin.Plugin.Shoal.Peers.PeerAddressService(rig.State, rig.Secrets, rig.Registry, rig.Service), rig.Secrets, libraryService, rig.Paths, NullLogger<AdminPeerSettingsController>.Instance, rig.Service) { ControllerContext = context };
        Record("GET", "Shoal/Admin/Peers/{handle}/Certificate", await peerSettings.Certificate(StateSamples.PeerKeyA, default));
        Record("GET", "Shoal/Admin/Peers/{handle}/Certificate", await peerSettings.Certificate("nope", default));
        Send(peerSettings, """{"expectedPin":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""");
        Record("POST", "Shoal/Admin/Peers/{handle}/TrustCertificate", await peerSettings.TrustCertificate(StateSamples.PeerKeyA, default));
        Record("POST", "Shoal/Admin/Peers/{handle}/TrustCertificate", await peerSettings.TrustCertificate("nope", default));
        Send(peerSettings, """{"maxConcurrentStreams":5,"priority":50}""");
        Record("PUT", "Shoal/Admin/Peers/{handle}", await peerSettings.Update(StateSamples.PeerKeyA, default));
        Send(peerSettings, """{"url":"http://elsewhere.tail1234.ts.net:8096","confirmName":"wrong"}""");
        Record("PUT", "Shoal/Admin/Peers/{handle}", await peerSettings.Update(StateSamples.PeerKeyA, default));
        Send(peerSettings, """{"confirmName":"wrong"}""");
        Record("DELETE", "Shoal/Admin/Peers/{handle}", await peerSettings.Remove(StateSamples.PeerKeyA, true, default));
        Send(peerSettings, """{"confirmName":"Hops"}""");
        Record("DELETE", "Shoal/Admin/Peers/{handle}", await peerSettings.Remove(pairedKey, false, default));

        // Every admin route of the assembly has a step above (a new route fails here until it has one).
        var all = AdminRouteTests.AllRoutes()
            .Where(r => r.Route.StartsWith("Shoal/Admin", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Verb + " " + Normalize(r.Route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(all.Except(covered));

        // The sentinels: everything the state and the stores hold that must never come out.
        var sentinels = new List<string> { peer.Token, peer.Secret, rig.Key, PairingRig.Admin.ToString("N"), PairingRig.Admin.ToString("D") };
        foreach (var p in rig.State.Current.Home.Peers)
        {
            AddIfPresent(sentinels, p.PinnedServerId);
            AddIfPresent(sentinels, p.TlsSpkiPin);
            if (p.Pairing is { } pairingInfo && pairingInfo.PairedByUserId != Guid.Empty)
            {
                sentinels.Add(pairingInfo.PairedByUserId.ToString("N"));
            }

            if (rig.Secrets.TryGet(p.PeerKey) is { } credential)
            {
                sentinels.Add(credential.Token);
                sentinels.Add(credential.DeviceId);
            }
        }

        foreach (var share in rig.State.Current.PeerRole.Shares)
        {
            sentinels.Add(share.ShareUserId.ToString("N"));
            sentinels.AddRange(share.ExpectedDeviceIds);
        }

        // Local account ids are what the picker is made of: only the routes that list accounts may carry them.
        var localIds = new List<string> { PairingRig.Admin.ToString("N"), PairingRig.Admin.ToString("D"), StateSamples.OtherUser.ToString("N"), alex.Id.ToString("N") };
        sentinels.RemoveAll(s => localIds.Contains(s, StringComparer.OrdinalIgnoreCase));
        sentinels.Add(shareAccount.Id.ToString("N"));
        Assert.All(sentinels, s => Assert.False(string.IsNullOrWhiteSpace(s)));

        foreach (var (route, text) in answers)
        {
            var canon = Canon(text);
            var listsAccounts = route.Contains("/Libraries", StringComparison.Ordinal) || route.Contains("Peers/{handle}", StringComparison.Ordinal) && route.StartsWith("GET", StringComparison.Ordinal);
            if (!listsAccounts)
            {
                foreach (var local in localIds)
                {
                    Assert.False(canon.Contains(Canon(local), StringComparison.Ordinal), route + " leaked a local account id");
                }
            }

            foreach (var sentinel in sentinels)
            {
                Assert.False(canon.Contains(Canon(sentinel), StringComparison.Ordinal), route + " leaked a known value");
            }

            if (text.Length > 0)
            {
                AssertNoBannedNames(JsonDocument.Parse(text).RootElement, route);
            }
        }

        // The peer's 8-character prefix of the pinned id is the one thing the spec allows (Server ID, 9.3): never the whole id.
        Assert.All(answers, a => Assert.DoesNotContain("0123456789abcdef0123456789abcdef", a.Text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AllRatings : Jellyfin.Plugin.Shoal.Sync.IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }

    private static void AddIfPresent(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.Add(value);
        }
    }

    private static string Normalize(string route) => Regex.Replace(route.ToLowerInvariant(), @"\{[^}]+\}", "{}");

    private static void AssertNoBannedNames(JsonElement element, string route)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // `invite` as an object is the invite state ({state, expiresUtc}); an invite string would be the secret itself.
                    if (!(property.Name.Equals("invite", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object))
                    {
                        Assert.DoesNotContain(property.Name.ToLowerInvariant(), BannedNames);
                    }

                    AssertNoBannedNames(property.Value, route);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoBannedNames(item, route);
                }

                break;
            default:
                break;
        }
    }
}
