using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot 04 blocker: choosing a friend's libraries (DESIGN 9.6, wizard step 5). RemoteLibraries lists the share user's views, Libraries adds a mapping
/// plus the local library below the stub root with the D3 default access, and DELETE takes all of it away again. A sync after the add imports titles.
/// </summary>
public sealed class AdminPeerLibrariesTests : IAsyncLifetime
{
    private SyncRig _rig = null!;
    private FakeLibraries _libraries = null!;
    private FakeAccounts _accounts = null!;
    private AccountInfo _admin = null!;
    private AdminPeerLibrariesController _controller = null!;
    private RecordingLive _live = null!;
    private string _key = string.Empty;

    public async Task InitializeAsync()
    {
        _rig = await SyncRig.CreateAsync(peer =>
        {
            peer.Add("The Matrix", 1999, tmdb: 603);
            peer.Add("Inception", 2010, tmdb: 27205);
        });
        await _rig.State.UpdateAsync(s => s.Home.Peers[0].Libraries.Clear());
        _libraries = new FakeLibraries();
        _libraries.AddLocal("Shows");
        _accounts = new FakeAccounts();
        _admin = _accounts.Add("admin", admin: true);
        _live = new RecordingLive();
        _key = AdminPeerLibrariesController.KeyOf(_rig.Peer.LibraryId);
        _controller = NewController(_rig.Endpoint);
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private sealed class Ratings : IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }

    private sealed class Endpoints(PeerEndpoint endpoint) : IPeerEndpoints
    {
        public PeerEndpoint? Find(string peerKey) => peerKey == endpoint.PeerKey ? endpoint : null;
    }

    private sealed class RecordingLive : IPeerLiveStatus
    {
        public List<string> Syncs { get; } = [];

        public PeerLiveInfo ForPeer(string peerKey) => new();

        public ShareLiveInfo ForShare(string shareId) => new();

        public int StartSyncAll() => 0;

        public bool StartSync(string peerKey)
        {
            Syncs.Add(peerKey);
            return true;
        }
    }

    private AdminPeerLibrariesController NewController(PeerEndpoint endpoint, Guid? user = null, Jellyfin.Plugin.Shoal.State.StubRootInitializer? stubRoots = null)
    {
        var trust = new PeerTrust(_rig.Client, _rig.Registry, _rig.Time);
        var reader = new PeerCatalogReader(_rig.Client, trust, _rig.Registry, new MetadataSanitizer(new Ratings()), _rig.Log);
        var visibility = new VisibilityService(_rig.State, _libraries, _accounts, _rig.Time, _rig.Log);
        var access = new AccessService(_rig.State, _accounts, visibility, _rig.Time);
        var service = new ShoalLibraryService(_rig.State, _libraries, visibility, _rig.Time, NullLogger<ShoalLibraryService>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", (user ?? _admin.Id).ToString("N"))], "test")),
        };
        return new AdminPeerLibrariesController(_rig.State, new Endpoints(endpoint), reader, service, access, _rig.Catalogs, _live, NullLogger<AdminPeerLibrariesController>.Instance, stubRoots)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static void Send(ControllerBase controller, string json)
    {
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private Task<IActionResult> AddAsync(string json, AdminPeerLibrariesController? controller = null)
    {
        var target = controller ?? _controller;
        Send(target, json);
        return target.AddLibraries(SyncRig.PeerKey, default);
    }

    private string Single(string? name = null, string? users = null) =>
        "[{\"mappingKey\":\"" + _key + "\",\"localLibraryName\":\"" + (name ?? "Hops Movies") + "\",\"selectedUserIds\":" + (users ?? "[]") + ",\"ratingsAcknowledged\":false}]";

    private PeerState Peer => _rig.State.Current.Home.Peers.Single();

    [Fact]
    public async Task RemoteLibraries_ListsTheShareUsersViews_WithCountsAndTheSelectableFlag()
    {
        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(200, rendered.Status);
        var library = Assert.Single(rendered.Json.EnumerateArray());
        Assert.Equal(_key, library.GetProperty("mappingKey").GetString());
        Assert.Equal("Movies", library.GetProperty("name").GetString());
        Assert.Equal("movies", library.GetProperty("type").GetString());
        Assert.Equal(2, library.GetProperty("count").GetInt32());
        Assert.True(library.GetProperty("selectable").GetBoolean());
        Assert.False(library.GetProperty("added").GetBoolean());
        Assert.DoesNotContain(_rig.Peer.LibraryId.ToString("N"), rendered.Text, StringComparison.OrdinalIgnoreCase); // no peer-side id leaves
        Assert.DoesNotContain("token", rendered.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Contract_RemoteLibraries_MatchesTheGoldenJson()
    {
        _rig.Peer.Tv = FakeTvTree.Generate(1, 1, 2); // a TV row too (Warden 14): seriesCount and episodeCount
        _rig.Peer.EnabledFolders = [_rig.Peer.LibraryId.ToString("N"), _rig.Peer.Tv.LibraryId.ToString("N"), "3fb79a0c5d2e4f6a8b9c0d1e2f3a4b5c"]; // G1: plus a stale id, and Playlists is listed but out of scope
        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(200, rendered.Status);
        ContractGolden.Match("peers.remote-libraries", rendered.Text);
    }

    // 2.9.2 K2, from the Pilot 21 G1 payload: EnabledFolders holds the Movies id in N form plus a stale id (a deleted library); /UserViews also lists
    // a TV library the share does not include and core's Playlists view (never in EnabledFolders). Only Movies is listed.
    [Fact]
    public async Task RemoteLibraries_ViewOutsideEnabledFolders_NotListed()
    {
        _rig.Peer.Tv = FakeTvTree.Generate(1, 1, 2);
        _rig.Peer.EnabledFolders = [_rig.Peer.LibraryId.ToString("N"), "3fb79a0c5d2e4f6a8b9c0d1e2f3a4b5c"];

        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(200, rendered.Status);
        var library = Assert.Single(rendered.Json.EnumerateArray());
        Assert.Equal(_key, library.GetProperty("mappingKey").GetString());
        Assert.DoesNotContain(_rig.Peer.Requests, r => r.Contains(_rig.Peer.Tv.LibraryId.ToString("N"), StringComparison.OrdinalIgnoreCase)); // never counted
    }

    [Fact]
    public async Task RemoteLibraries_AllFolders_409()
    {
        _rig.Peer.EnableAllFolders = true;

        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(409, rendered.Status);
        Assert.Equal("shareScopeUnknown", rendered.Json.GetProperty("error").GetString());
        ContractGolden.Match("peers.remote-libraries.scope-unknown", rendered.Text);
        Assert.DoesNotContain(_rig.Peer.Requests, static r => r.StartsWith("/Items?", StringComparison.Ordinal)); // nothing was counted either
    }

    [Theory]
    [InlineData(200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Name\":\"shoal-home\"}")]
    [InlineData(200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Policy\":{\"IsAdministrator\":false,\"EnableAllFolders\":false}}")]
    [InlineData(200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Policy\":{\"IsAdministrator\":false,\"EnabledFolders\":[]}}")]
    [InlineData(200, "{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Policy\":{\"EnableAllFolders\":false,\"EnabledFolders\":[42]}}")]
    [InlineData(200, "not json")]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    public async Task RemoteLibraries_UnreadableScope_409(int status, string body)
    {
        _rig.Peer.MeOverride = () => (status, body);

        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(409, rendered.Status);
        Assert.Equal("shareScopeUnknown", rendered.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RemoteLibraries_ScopeReadFailsWithA5xx_Is502()
    {
        _rig.Peer.MeOverride = static () => (503, "{}");

        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(502, rendered.Status);
        Assert.Equal("unreachable", rendered.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RemoteLibraries_AnEmptyShare_ListsNothing()
    {
        _rig.Peer.EnabledFolders = [];

        var rendered = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(200, rendered.Status);
        Assert.Empty(rendered.Json.EnumerateArray());
    }

    [Fact]
    public async Task AddLibraries_KeyOutsideScope_400UnknownLibrary()
    {
        _rig.Peer.Tv = FakeTvTree.Generate(1, 1, 2);
        _rig.Peer.EnabledFolders = [_rig.Peer.LibraryId.ToString("N")];
        var tvKey = AdminPeerLibrariesController.KeyOf(_rig.Peer.Tv.LibraryId);
        var playlistsKey = AdminPeerLibrariesController.KeyOf(_rig.Peer.PlaylistsViewId);

        var tv = AdminTestSupport.Render(await AddAsync("[{\"mappingKey\":\"" + tvKey + "\",\"localLibraryName\":\"Hops TV\"}]"));
        var playlists = AdminTestSupport.Render(await AddAsync("[{\"mappingKey\":\"" + playlistsKey + "\",\"localLibraryName\":\"Hops Lists\"}]"));

        Assert.Equal(400, tv.Status);
        Assert.Equal("unknownLibrary", tv.Json.GetProperty("error").GetString());
        Assert.Equal(400, playlists.Status);
        Assert.Empty(Peer.Libraries);
        Assert.Equal(200, AdminTestSupport.Render(await AddAsync(Single())).Status); // the library in scope still adds
    }

    [Fact]
    public async Task AddLibraries_AllFolders_409_AndAddsNothing()
    {
        _rig.Peer.EnableAllFolders = true;

        var rendered = AdminTestSupport.Render(await AddAsync(Single()));

        Assert.Equal(409, rendered.Status);
        Assert.Equal("shareScopeUnknown", rendered.Json.GetProperty("error").GetString());
        Assert.Empty(Peer.Libraries);
        Assert.DoesNotContain(_libraries.List(), static l => l.Name == "Hops Movies");
        Assert.Empty(_live.Syncs);
    }

    [Fact]
    public async Task RemoteLibraries_AnUnknownServer_Is404_AndAnUnreachableOne_Is502()
    {
        var unknown = AdminTestSupport.Render(await _controller.RemoteLibraries("nobody", default));
        var dead = new PeerEndpoint(SyncRig.PeerKey, "Hops", new Uri("http://127.0.0.1:1"), "d", "token-for-tests-0123456789abcdef").Configured(PeerNetwork.Tailnet, null, false, _rig.Peer.ServerId.ToString("N"));
        var unreachable = AdminTestSupport.Render(await NewController(dead).RemoteLibraries(SyncRig.PeerKey, default));

        Assert.Equal(404, unknown.Status);
        Assert.Equal(502, unreachable.Status);
        Assert.Equal("unreachable", unreachable.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Add_CreatesTheMapping_TheLocalLibraryBelowTheStubRoot_AndTheDefaultAccess()
    {
        var rendered = AdminTestSupport.Render(await AddAsync(Single()));

        Assert.Equal(200, rendered.Status);
        var mapping = Assert.Single(Peer.Libraries);
        Assert.Equal(_key, mapping.MappingKey);
        Assert.Equal("Movies", mapping.RemoteName);
        Assert.Equal("movies", mapping.CollectionType);
        Assert.Equal("Hops Movies", mapping.LocalLibraryName);
        Assert.Equal(_key, mapping.Subfolder);
        Assert.Equal(AccessMode.Selected, mapping.Access.Mode);
        Assert.Equal([_admin.Id], mapping.Access.SelectedUserIds); // D3: the pairing admin only
        var created = _libraries.List().Single(static l => l.Name == "Hops Movies");
        Assert.Equal(Path.Combine(_rig.StubRoot, SyncRig.PeerKey, _key), Path.GetFullPath(created.Locations.Single()));
        Assert.True(Directory.Exists(created.Locations.Single()));
        Assert.Equal("Hops Movies", Assert.Single(rendered.Json.GetProperty("added").EnumerateArray()).GetProperty("localLibraryName").GetString());
        Assert.Empty(rendered.Json.GetProperty("hiddenLibraries").EnumerateArray());
        Assert.Equal([SyncRig.PeerKey], _live.Syncs); // the first sync starts
        Assert.Equal(_rig.Peer.LibraryId.ToString("N"), _rig.Catalog().Libraries.Single(l => l.MappingKey == _key).PeerLibraryId); // 2.9.2 K1: the picked id is stored at Add
    }

    [Fact]
    public async Task Add_ThenASync_ImportsTheTitles_ThisWasTheBlocker()
    {
        await AddAsync(Single());

        var report = await _rig.SyncAsync();

        Assert.Equal(SyncResultKind.Ok, report.Result);
        Assert.Equal(2, report.Added);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_rig.StubRoot, SyncRig.PeerKey, _key)).Length);
        var listed = AdminTestSupport.Render(await _controller.RemoteLibraries(SyncRig.PeerKey, default));
        Assert.True(listed.Json[0].GetProperty("added").GetBoolean());
    }

    [Fact]
    public async Task Add_WithPickedPeople_OpensTheLibraryToThem_AtOnce()
    {
        var alex = _accounts.Add("Alex");

        var rendered = AdminTestSupport.Render(await AddAsync(Single(users: "[\"" + alex.Id + "\"," + "\"" + _admin.Id + "\"]")));

        Assert.Equal(200, rendered.Status);
        Assert.Equal(AccessMode.Selected, Peer.Libraries.Single().Access.Mode);
        Assert.Equal(new[] { alex.Id, _admin.Id }.Order(), Peer.Libraries.Single().Access.SelectedUserIds.Order());
    }

    [Fact]
    public async Task Add_WithAnUnknownOrSharingUser_Is400_AndLeavesNothingBehind()
    {
        var ghost = Guid.NewGuid();

        var rendered = AdminTestSupport.Render(await AddAsync(Single(users: "[\"" + ghost + "\"]")));

        Assert.Equal(400, rendered.Status);
        Assert.Empty(Peer.Libraries);
        Assert.DoesNotContain(_libraries.List(), static l => l.Name == "Hops Movies");
    }

    [Fact]
    public async Task Add_TheSameLibraryTwice_Is409()
    {
        await AddAsync(Single());

        var second = AdminTestSupport.Render(await AddAsync(Single("Another name")));

        Assert.Equal(409, second.Status);
        Assert.Equal("alreadyAdded", second.Json.GetProperty("error").GetString());
        Assert.Single(Peer.Libraries);
    }

    [Fact]
    public async Task Add_ANameAlreadyInUse_Is409_AndRollsTheMappingBack()
    {
        var rendered = AdminTestSupport.Render(await AddAsync(Single("Shows")));

        Assert.Equal(409, rendered.Status);
        Assert.Equal("nameInUse", rendered.Json.GetProperty("error").GetString());
        Assert.Empty(Peer.Libraries);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[{\"mappingKey\":\"../x\"}]")]
    [InlineData("[{\"mappingKey\":\"a b\"}]")]
    [InlineData("[{\"mappingKey\":null}]")]
    [InlineData("[{\"mappingKey\":\"x\"}]")]
    public async Task Add_InvalidBodies_Are400(string json)
    {
        var rendered = AdminTestSupport.Render(await AddAsync(json));

        Assert.Equal(400, rendered.Status);
        Assert.Empty(Peer.Libraries);
    }

    [Fact]
    public async Task Add_ALibraryTheFriendDoesNotHave_Is400_AndDuplicatesInOneRequestToo()
    {
        var unknown = AdminTestSupport.Render(await AddAsync("[{\"mappingKey\":\"r0123456789abcdef\",\"localLibraryName\":\"X\"}]"));
        var twice = AdminTestSupport.Render(await AddAsync("[{\"mappingKey\":\"" + _key + "\"},{\"mappingKey\":\"" + _key + "\"}]"));

        Assert.Equal(400, unknown.Status);
        Assert.Equal("unknownLibrary", unknown.Json.GetProperty("error").GetString());
        Assert.Equal(400, twice.Status);
        Assert.Empty(Peer.Libraries);
    }

    [Fact]
    public async Task Add_WithoutAStubRoot_Is409()
    {
        await _rig.State.UpdateAsync(s => s.StubRoot = string.Empty);

        var rendered = AdminTestSupport.Render(await AddAsync(Single()));

        Assert.Equal(409, rendered.Status);
        Assert.Empty(Peer.Libraries);
    }

    [Fact]
    public async Task Add_OnAFreshInstall_SetsTheDefaultStubRoot_AndSucceeds()
    {
        await _rig.State.UpdateAsync(s => s.StubRoot = string.Empty);
        var initializer = new Jellyfin.Plugin.Shoal.State.StubRootInitializer(_rig.State, new StubRootChecker(static () => []), _rig.Paths, NullLogger<Jellyfin.Plugin.Shoal.State.StubRootInitializer>.Instance);
        var controller = NewController(_rig.Endpoint, stubRoots: initializer);

        var rendered = AdminTestSupport.Render(await AddAsync(Single(), controller));

        Assert.Equal(200, rendered.Status);
        Assert.Equal(_rig.Paths.DefaultStubRoot, _rig.State.Current.StubRoot);
        Assert.NotEmpty(Peer.Libraries);
    }

    [Fact]
    public async Task Remove_TakesTheLibraryTheMappingTheEntriesAndTheStubsAway()
    {
        await AddAsync(Single());
        await _rig.SyncAsync();
        var folder = Path.Combine(_rig.StubRoot, SyncRig.PeerKey, _key);
        Assert.NotEmpty(_rig.Entries);
        Assert.True(Directory.Exists(folder));

        var rendered = AdminTestSupport.Render(await _controller.RemoveLibrary(SyncRig.PeerKey, _key, default));

        Assert.Equal(200, rendered.Status);
        Assert.Empty(Peer.Libraries);
        Assert.DoesNotContain(_libraries.List(), static l => l.Name == "Hops Movies");
        Assert.Empty(_rig.Entries);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(404, AdminTestSupport.Render(await _controller.RemoveLibrary(SyncRig.PeerKey, _key, default)).Status);
    }

    [Fact]
    public async Task Pilot19D1_Remove_WithTheCompositeKeyThePageSends_RemovesTheLibrary_AnotherServersPrefixIs404()
    {
        await AddAsync(Single());
        var composite = AdminLibraryView.Compose(SyncRig.PeerKey, _key); // what GET Peers/{h} lists as mappingKey, and what shoal.js sent

        Assert.Equal(404, AdminTestSupport.Render(await _controller.RemoveLibrary(SyncRig.PeerKey, "0123456789abcdef0123456789abcdef." + _key, default)).Status);
        Assert.NotEmpty(Peer.Libraries);

        var rendered = AdminTestSupport.Render(await _controller.RemoveLibrary(SyncRig.PeerKey, composite, default));

        Assert.Equal(200, rendered.Status);
        Assert.Empty(Peer.Libraries);
        Assert.DoesNotContain(_libraries.List(), static l => l.Name == "Hops Movies");
    }

    [Fact]
    public async Task Remove_AnUnknownMapping_OrServer_Is404()
    {
        Assert.Equal(404, AdminTestSupport.Render(await _controller.RemoveLibrary(SyncRig.PeerKey, "nothing", default)).Status);
        Assert.Equal(404, AdminTestSupport.Render(await _controller.RemoveLibrary("nobody", _key, default)).Status);
    }


    [Fact]
    public async Task Remove_DeletesOnlyThatMappingsFolder_NotItsSiblings_NorAnythingOutsideTheStubRoot()
    {
        await AddAsync(Single());
        var sibling = Path.Combine(_rig.StubRoot, SyncRig.PeerKey, "another-mapping");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "x");
        var outside = Path.Combine(_rig.Dir.Path, "precious");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");

        await _controller.RemoveLibrary(SyncRig.PeerKey, _key, default);

        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(_rig.StubRoot, SyncRig.PeerKey, _key)));
    }

    [Fact]
    public void TheEndpoints_AreAdminOnly_AndNothingIsAnonymous()
    {
        var type = typeof(AdminPeerLibrariesController);

        var authorize = type.GetCustomAttributes<AuthorizeAttribute>().Single();

        Assert.Equal("RequiresElevation", authorize.Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.All(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), static m => Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>()));
    }
}
