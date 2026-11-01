using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.10.4 K2: the real adapter's delete rules (B, E, F, G) and outcome table against a fake core and real files.</summary>
public sealed class LocalDeleteLibraryTests
{
    private static readonly Func<bool> Allowed = static () => true;

    // W23-1: the switch is the last thing read under the hold before core deletes.
    [Fact]
    public async Task ASwitchTurnedOffBeforeTheCallRefusesAndNothingIsDeleted()
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        var reads = 0;
        Assert.Equal(LocalDeleteReason.Disabled, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, () => { reads++; return false; }, default)));
        Assert.Equal(1, reads);
        f.AssertUntouched();
    }

    [Fact]
    public void ResolvePicksByIdsWithoutTheHoldingFolderRule()
    {
        using var f = new Fixture();
        // The 2.10.3 holding folder (<parent of root>/.shoal-recovery) collides with the stub root: the legacy discovery refuses, delete discovery does not.
        f.State.StubRoot = Path.Combine(Path.GetDirectoryName(f.Root)!, ".shoal-recovery");
        Assert.Empty(f.Adapter.Discover(Fixture.Peer));
        var target = f.Adapter.Resolve(Fixture.Peer, f.Movie.Id, f.PeerItem);
        Assert.Equal(string.Empty, target.RecoveryRoot);
        Assert.Equal(Path.GetFullPath(f.Movie.Path), target.Path);
    }

    [Fact]
    public void ResolveRefusesAnUnknownPairAndAScan()
    {
        using var f = new Fixture();
        Assert.Equal(LocalDeleteReason.CandidateChanged, Refusal(() => f.Adapter.Resolve(Fixture.Peer, f.Movie.Id, Guid.NewGuid())));
        Assert.Equal(LocalDeleteReason.CandidateChanged, Refusal(() => f.Adapter.Resolve(Fixture.Peer, Guid.NewGuid(), f.PeerItem)));
        Assert.Equal(LocalDeleteReason.CandidateChanged, Refusal(() => f.Adapter.Resolve("22222222222222222222222222222222", f.Movie.Id, f.PeerItem)));
        f.Refresh.IsScanRunning = true;
        Assert.Equal(LocalDeleteReason.LibraryBusy, Refusal(() => f.Adapter.Resolve(Fixture.Peer, f.Movie.Id, f.PeerItem)));
    }

    [Fact]
    public async Task ADedicatedFolderWithItsOwnExtraIsDeletedTheWayTheButtonDoes()
    {
        using var f = new Fixture();
        var trailer = f.Add(Path.Combine("trailers", "trailer.mkv"), owner: f.Movie.Id);
        f.Add("Sintel.nfo");
        var (target, hashed) = await f.Ready();

        Assert.Equal([f.MovieFolder], f.Adapter.Inspect(target, f.Admin.Id, hashed)); // gate S2: the real GetDeletePaths gives the containing folder
        Assert.Equal(LocalDeleteOutcome.Done, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        Assert.False(Directory.Exists(f.MovieFolder));
        Assert.False(File.Exists(trailer));
        var call = Assert.Single(f.DeleteCalls);
        Assert.True(call.Options.DeleteFileLocation);
        Assert.True(call.Notify);
        Assert.Equal(1, f.Holds);
    }

    [Theory]
    [InlineData("secondMovie")]
    [InlineData("episode")]
    [InlineData("foreignExtra")]
    public async Task AFolderThatHoldsOtherMediaIsRefusedAndNothingIsDeleted(string other)
    {
        using var f = new Fixture();
        f.Add("Other.mkv", owner: other == "foreignExtra" ? Guid.NewGuid() : null, episode: other == "episode");
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.AssertUntouched();
    }

    // W23-4: core deletes the whole folder, so a file Jellyfin does not know is deleted with no proof. Only known sidecars may go along.
    [Theory]
    [InlineData("Sintel (Director's Cut).mkv")] // an unscanned second cut
    [InlineData("Sintel.part2.avi")]
    [InlineData("notes.zip")]
    [InlineData("download.part")]
    [InlineData("README")]
    public async Task AFileJellyfinDoesNotKnowAndIsNoSidecarIsRefused(string name)
    {
        using var f = new Fixture();
        f.Add(name, indexed: false);
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.AssertUntouched();
    }

    [Fact]
    public async Task KnownSidecarsGoWithTheMovie()
    {
        using var f = new Fixture();
        foreach (var name in new[] { "Sintel.nfo", "poster.jpg", "fanart.JPEG", "folder.png", "backdrop.webp", "Sintel.en.srt", "Sintel.ass", "Sintel.ssa", "Sintel.sub", "Sintel.idx", "Sintel.vtt", "Sintel.sup", Path.Combine("extrafanart", "fanart1.jpg") })
        {
            f.Add(name, indexed: false);
        }

        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Done, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
    }

    [Fact]
    public async Task AMixedFolderIsRefusedBeforeCoreIsAskedForPaths()
    {
        using var f = new Fixture();
        f.Movie.IsInMixedFolder = true;
        f.Adapter.DeletePathsRule = _ => throw new InvalidOperationException("must not be asked");
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Inspect(target, f.Admin.Id, hashed)));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parentOfRoot")]
    [InlineData("nestedRoot")]
    [InlineData("stubRootInside")]
    [InlineData("missesTheMovie")]
    [InlineData("relative")]
    public async Task ADeleteSetOutsideOneDedicatedFolderIsRefused(string shape)
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        switch (shape)
        {
            case "root": f.Adapter.DeletePathsRule = _ => [(f.Root, true)]; break;
            case "parentOfRoot": f.Adapter.DeletePathsRule = _ => [(Path.GetDirectoryName(f.Root)!, true)]; break;
            case "nestedRoot": f.Roots.Add(Directory.CreateDirectory(Path.Combine(f.MovieFolder, "nested")).FullName); break;
            case "stubRootInside": f.State.StubRoot = Directory.CreateDirectory(Path.Combine(f.MovieFolder, "stubs")).FullName; break;
            case "missesTheMovie": f.Adapter.DeletePathsRule = _ => [(Directory.CreateDirectory(Path.Combine(f.Root, "Elsewhere")).FullName, true)]; break;
            case "relative": f.Adapter.DeletePathsRule = _ => [("Sintel (2010)", true)]; break;
        }

        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.AssertUntouched();
    }

    [Fact]
    public async Task ALinkInsideTheFolderIsRefused()
    {
        using var f = new Fixture();
        var elsewhere = Directory.CreateDirectory(Path.Combine(f.Dir.Path, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        LinkDirectory(Path.Combine(f.MovieFolder, "linked"), elsewhere);
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
        f.AssertUntouched();
    }

    [Theory]
    [InlineData("federated")]
    [InlineData("strm")]
    [InlineData("inStubRoot")]
    [InlineData("gone")]
    public async Task AStubAFederatedItemOrAnUnindexedItemIsNeverDeleted(string kind)
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        switch (kind)
        {
            case "federated": f.Federated = true; break; // Pilot 2 S3: core says CanDelete true for stubs, so Shoal refuses them itself
            case "strm": f.Movie.Path = Path.ChangeExtension(f.Movie.Path, ".strm"); break;
            case "inStubRoot": f.State.StubRoot = f.Root; break;
            case "gone": f.Indexed = false; break;
        }

        Assert.Equal(LocalDeleteReason.UnsupportedItem, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        Assert.Empty(f.DeleteCalls);
        Assert.True(File.Exists(target.Path));
    }

    [Theory]
    [InlineData("playing")]
    [InlineData("rewritten")]
    [InlineData("grown")]
    [InlineData("nativeSnapshot")]
    [InlineData("otherIds")]
    public async Task AFileOrItemThatChangedSinceTheHashIsRefused(string change)
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        switch (change)
        {
            case "playing": f.Sessions.Add(new SessionInfo(null!, NullLogger.Instance) { NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = f.Movie.Id } }); break;
            case "rewritten": File.SetLastWriteTimeUtc(target.Path, File.GetLastWriteTimeUtc(target.Path).AddMinutes(1)); break;
            case "grown": File.AppendAllText(target.Path, "x"); break;
            case "nativeSnapshot": hashed = hashed with { Identity = "native-identity|ancestry" }; break;
            case "otherIds": f.Movie.ProviderIds = new() { ["Tmdb"] = "999" }; break;
        }

        Assert.Equal(LocalDeleteReason.RemovalChanged, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        Assert.Empty(f.DeleteCalls);
    }

    [Fact]
    public async Task JellyfinsOwnPermissionAndTheAdminAreCheckedEveryTime()
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        f.Adapter.CanDeleteRule = (_, _) => false;
        Assert.Equal(LocalDeleteReason.DeleteNotPermitted, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.Adapter.CanDeleteRule = (_, _) => true;
        f.Admin.Permissions.Single(p => p.Kind == PermissionKind.IsAdministrator).Value = false;
        Assert.Equal("administratorRequired", Assert.Throws<InvalidOperationException>(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)).Message);
        Assert.Equal("administratorRequired", Assert.Throws<InvalidOperationException>(() => f.Adapter.VerifyAdministrator(Guid.NewGuid())).Message);
        f.AssertUntouched();
    }

    [Fact]
    public async Task ARunningSyncOrScanStopsBeforeTheCall()
    {
        using var f = new Fixture();
        var (target, hashed) = await f.Ready();
        f.Adapter.HoldSync = () => null;
        Assert.Equal(LocalDeleteReason.Busy, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.Adapter.HoldSync = () => new Held(f);
        f.Refresh.IsScanRunning = true;
        Assert.Equal(LocalDeleteReason.LibraryBusy, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        f.Refresh.IsScanRunning = false;
        Assert.Throws<OperationCanceledException>(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, cancelled.Token));
        f.AssertUntouched();
    }

    [Fact]
    public async Task CoreRefusingWithTheFileIntactIsDeleteFailed()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.ThrowAccessDenied };
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Failed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        f.AssertUntouched(calls: 1);
    }

    [Fact]
    public async Task FileGoneButStillIndexedIsRetriedOnce()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.FilesOnlyThenRow };
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Done, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        Assert.Equal(2, f.DeleteCalls.Count);
    }

    [Fact]
    public async Task FileGoneAndStillIndexedAfterTheRetryIsNotConfirmed()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.FilesOnly };
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        Assert.Equal(2, f.DeleteCalls.Count); // never more than one retry
    }

    [Fact]
    public async Task RemovedFromTheIndexWithTheFileStillOnDiskIsNotConfirmed()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.RowOnly };
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        Assert.Single(f.DeleteCalls);
    }

    [Fact]
    public async Task AnUnexpectedErrorIsNeverReportedAsDoneOrFailed()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.DeleteThenThrowUnexpected };
        var (target, hashed) = await f.Ready();
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        using var g = new Fixture { Core = Fixture.CoreAction.ThrowUnexpected };
        var (t2, h2) = await g.Ready();
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, g.Adapter.Delete(t2, g.Admin.Id, h2, Allowed, default));
    }

    // W23-5: the errors a dropped share raises on a stat, in the shapes .NET gives them (Windows SMB, Linux CIFS, access denied).
    public static TheoryData<string> ShareErrors => ["netnameDeleted", "badNetName", "hostDown", "accessDenied"];

    // W23-5: core fails on a share blip, the stat after it fails too: this is not "file gone", so no retry may remove the row and nothing is Done.
    [Theory]
    [MemberData(nameof(ShareErrors))]
    public async Task AShareThatDropsAfterAFailedDeleteIsNotConfirmedAndTheRowIsNotRetried(string error)
    {
        using var f = new Fixture { Core = Fixture.CoreAction.ShareDropsThenRowOnly, ShareError = Fixture.Error(error) };
        var (target, hashed) = await f.Ready();
        f.Adapter.FileAttributesRule = f.Stat;
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
        Assert.Single(f.DeleteCalls); // no retry on an unknown file state
        Assert.True(f.Indexed);
        Assert.True(File.Exists(f.Movie.Path));
    }

    // W23-5: core deleted everything, but the stat after it errors: absence is not proven, so the item is notConfirmed, never Done.
    [Theory]
    [MemberData(nameof(ShareErrors))]
    public async Task ADeleteWhoseCheckErrorsIsNeverDone(string error)
    {
        using var f = new Fixture { Core = Fixture.CoreAction.DeleteThenShareDrops, ShareError = Fixture.Error(error) };
        var (target, hashed) = await f.Ready();
        f.Adapter.FileAttributesRule = f.Stat;
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
    }

    // W23-5: a library root that is gone (a mapped drive or UNC share that vanished) makes every path below it read "not found": not a proof.
    [Fact]
    public async Task NotFoundBelowARootThatIsGoneIsNotConfirmed()
    {
        using var f = new Fixture { Core = Fixture.CoreAction.DeleteAndRootGone };
        var (target, hashed) = await f.Ready();
        f.Adapter.FileAttributesRule = f.Stat;
        Assert.Equal(LocalDeleteOutcome.Unconfirmed, f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default));
    }

    // W23-5 siblings: a folder on the delete path that cannot be read is refused (its link check would be skipped), and the legacy deindex never removes a row on an unknown file.
    [Fact]
    public async Task AnUnreadableFolderOnTheDeletePathIsRefused()
    {
        using var f = new Fixture { ShareError = Fixture.Error("netnameDeleted") };
        var (target, hashed) = await f.Ready();
        f.Adapter.FileAttributesRule = p => SamePathForTest(p, f.MovieFolder) ? throw f.ShareError : File.GetAttributes(p);
        Assert.Equal(LocalDeleteReason.FolderNotDedicated, Refusal(() => f.Adapter.Delete(target, f.Admin.Id, hashed, Allowed, default)));
        f.AssertUntouched();
    }

    [Fact]
    public async Task TheLegacyDeindexRefusesWhenTheFileStateIsUnknown()
    {
        using var f = new Fixture { ShareError = Fixture.Error("hostDown") };
        var (target, _) = await f.Ready();
        File.Delete(f.Movie.Path);
        f.Adapter.FileAttributesRule = p => SamePathForTest(p, f.Movie.Path) ? throw f.ShareError : File.GetAttributes(p);
        Assert.ThrowsAny<InvalidOperationException>(() => f.Adapter.Deindex(target));
        Assert.Empty(f.DeleteCalls);
    }

    [Fact]
    public async Task NoSharedCopyYetIsNull()
    {
        using var f = new Fixture();
        var (target, _) = await f.Ready();
        Assert.Null(f.Adapter.FindSharedFor(target));
    }

    [Fact]
    public void FilesAbove32GibGetTheCappedDeadlineAndTheProofCallSparesTheBreaker()
    {
        Assert.Equal(TimeSpan.FromMinutes(34), RemovalPolicy.HashingDeadlineFor(RemovalPolicy.MaxFileSizeBytes + 1));
        Assert.Equal(TimeSpan.FromMinutes(34), RemovalPolicy.HashingDeadlineFor(long.MaxValue));
        Assert.Equal(TimeSpan.FromMinutes(2), RemovalPolicy.HashingDeadlineFor(0));
        Assert.False(RemovalOriginClient.HashProfileFor(20L << 30).CountsTowardBreaker);
    }

    private static LocalDeleteReason Refusal(Action action) => Assert.Throws<LocalDeleteRefusedException>(action).Reason;

    private static LocalDeleteReason Refusal<T>(Func<T> action) => Assert.Throws<LocalDeleteRefusedException>(() => action()).Reason;

    private static bool SamePathForTest(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }

    private sealed class Held(Fixture f) : IDisposable
    {
        private readonly Fixture _f = f.Count();

        public void Dispose() => _f.Released++;
    }

    private sealed class SingleFileMovie : Movie
    {
        public override bool HasLocalAlternateVersions => false;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Peer = "11111111111111111111111111111111";
        internal readonly TempDirectory Dir = new();
        internal readonly ShoalState State = new();
        internal readonly LibraryMapping Mapping = new() { MappingKey = "movies-1", Subfolder = "movies", CollectionType = "movies", Import = true };
        internal readonly ShoalLibrary Library = new() { LibKey = "aaaaaaaaaaaaaaaa", PathRel = "libs/aaaaaaaaaaaaaaaa", Type = "movies", Feeds = [Peer] };
        internal readonly SingleFileMovie Movie;
        internal readonly User Admin = new("admin", "test", "test") { Id = Guid.NewGuid() };
        internal readonly Guid PeerItem = Guid.NewGuid();
        internal readonly Guid LibraryId = Guid.NewGuid();
        internal readonly string Root;
        internal readonly string MovieFolder;
        internal readonly List<string> Roots = [];
        internal readonly List<SessionInfo> Sessions = [];
        internal readonly Dictionary<string, BaseItem> ByPath = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<(DeleteOptions Options, bool Notify)> DeleteCalls = [];
        internal readonly FakeRefresh Refresh = new();
        internal readonly JellyfinRemovalLibrary Adapter;
        private readonly List<string> _before = [];

        internal Fixture()
        {
            State.StubRoot = Dir.Combine("stubs");
            State.Home.Peers.Add(new() { PeerKey = Peer, Enabled = true, Pairing = new() { Method = PairingMethod.Invite }, Libraries = [Mapping] });
            State.Home.Libraries.Add(Library);
            Root = Directory.CreateDirectory(Dir.Combine("media", "Movies")).FullName;
            Roots.Add(Root);
            MovieFolder = Directory.CreateDirectory(Path.Combine(Root, "Sintel (2010)")).FullName;
            var path = Path.Combine(MovieFolder, "Sintel.mkv");
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            Movie = new() { Id = Guid.NewGuid(), Name = "Sintel", ProductionYear = 2010, Path = path, VideoType = VideoType.VideoFile, RunTimeTicks = 200_000_000, ProviderIds = new() { ["Tmdb"] = "45745" } };
            Admin.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
            Admin.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            var library = RemoveServerGhostTests.CoreProxy<ILibraryManager>.Create((m, args) => m.Name switch
            {
                "GetItemList" => Indexed ? new List<BaseItem> { Movie } : new List<BaseItem>(),
                "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = LibraryId.ToString("N"), Locations = [.. Roots] } },
                "FindByPath" => ByPath.GetValueOrDefault(Path.GetFullPath((string)args[0]!)),
                "DeleteItem" => Delete((DeleteOptions)args[1]!, args.Length > 2 && args[2] is true),
                _ => RemoveServerGhostTests.CoreProxy<ILibraryManager>.Empty(m.ReturnType),
            });
            var users = RemoveServerGhostTests.CoreProxy<IUserManager>.Create((m, args) => m.Name == "GetUserById" ? ((Guid)args[0]! == Admin.Id ? Admin : null) : RemoveServerGhostTests.CoreProxy<IUserManager>.Empty(m.ReturnType));
            var sessions = RemoveServerGhostTests.CoreProxy<ISessionManager>.Create((m, _) => m.Name == "get_Sessions" ? Sessions : RemoveServerGhostTests.CoreProxy<ISessionManager>.Empty(m.ReturnType));
            var federated = RemoveServerGhostTests.CoreProxy<IFederatedItems>.Create((m, _) => m.Name == "Find" && Federated ? new FederatedItem(Peer, Mapping.MappingKey) : null);
            var state = RemoveServerGhostTests.CoreProxy<IStateStore>.Create((m, _) => m.Name == "get_Status" ? StateStatus.Ready : m.Name == "get_Current" ? State : null);
            var catalogs = new CatalogStore(new ShoalPaths(Dir.Path), NullLogger<CatalogStore>.Instance);
            catalogs.Save(new() { PeerKey = Peer, Entries = [new() { Kind = EntryKind.Movie, State = EntryState.DuplicateLocal, MappingKey = Mapping.MappingKey, PeerItemId = PeerItem.ToString("N"), StubKey = "bbbbbbbbbbbbbbbb", Title = Movie.Name, ProviderIds = Movie.ProviderIds }] });
            Adapter = new(library, null!, users, RemoveServerGhostTests.CoreProxy<IUserDataManager>.Create(), sessions, federated, RemoveServerGhostTests.CoreProxy<IVisibleItems>.Create((_, _) => true), state, catalogs, null!, Refresh, new GroupBook(new MemoryGroupStore(), NullLogger.Instance))
            {
                CanDeleteRule = (_, _) => true, // the real rule reads core's static library manager (gate S3 verified it is true for admins)
                HoldSync = () => new Held(this),
            };
        }

        internal enum CoreAction
        {
            Delete,
            ThrowAccessDenied,
            FilesOnly,
            FilesOnlyThenRow,
            RowOnly,
            DeleteThenThrowUnexpected,
            ThrowUnexpected,
            ShareDropsThenRowOnly,
            DeleteThenShareDrops,
            DeleteAndRootGone,
        }

        internal bool ShareDown { get; set; }

        internal Exception ShareError { get; init; } = new IOException("unset");

        // The exception .NET raises for each case (message and HResult as the runtime builds them).
        internal static Exception Error(string kind) => kind switch
        {
            "netnameDeleted" => new IOException("The specified network name is no longer available.", unchecked((int)0x80070040)), // Windows ERROR_NETNAME_DELETED
            "badNetName" => new IOException("The network name cannot be found.", unchecked((int)0x80070043)), // Windows ERROR_BAD_NET_NAME
            "hostDown" => new IOException("Host is down", 112), // Linux CIFS EHOSTDOWN
            "accessDenied" => new UnauthorizedAccessException("Access to the path is denied."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // The stat as the share sees it: every path below the library root errors while the share is down.
        internal FileAttributes Stat(string path) => ShareDown && Path.GetFullPath(path).StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? throw ShareError : File.GetAttributes(path);

        internal CoreAction Core { get; init; } = CoreAction.Delete;

        internal bool Indexed { get; set; } = true;

        internal bool Federated { get; set; }

        internal int Holds { get; private set; }

        internal int Released { get; set; }

        internal Fixture Count()
        {
            Holds++;
            return this;
        }

        internal string Add(string relative, Guid? owner = null, bool episode = false, bool indexed = true)
        {
            var path = Path.Combine(MovieFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [9]);
            if (owner is { } id)
            {
                ByPath[path] = new Video { Id = Guid.NewGuid(), Path = path, OwnerId = id, ExtraType = ExtraType.Trailer };
            }
            else if (indexed && relative.EndsWith(".mkv", StringComparison.Ordinal))
            {
                ByPath[path] = episode ? new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Path = path } : new SingleFileMovie { Id = Guid.NewGuid(), Path = path };
            }

            return path;
        }

        internal async Task<(RemovalTarget Target, RemovalFileSnapshot Hashed)> Ready()
        {
            var target = Adapter.Resolve(Peer, Movie.Id, PeerItem);
            var hashed = await new PortableFileProbe().ProbeAsync(target.Root, target.Path, default);
            _before.AddRange(Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories));
            return (target, hashed);
        }

        internal void AssertUntouched(int calls = 0)
        {
            Assert.Equal(calls, DeleteCalls.Count);
            Assert.True(File.Exists(Movie.Path) || Movie.Path.EndsWith(".strm", StringComparison.Ordinal));
            foreach (var entry in _before)
            {
                Assert.True(File.Exists(entry) || Directory.Exists(entry), "deleted: " + entry);
            }

            Assert.Equal(Holds, Released);
        }

        public void Dispose()
        {
            Adapter.Dispose();
            Dir.Dispose();
        }

        private object? Delete(DeleteOptions options, bool notify)
        {
            DeleteCalls.Add((options, notify));
            var first = DeleteCalls.Count == 1;
            switch (Core)
            {
                case CoreAction.Delete:
                    Directory.Delete(MovieFolder, true);
                    Indexed = false;
                    break;
                case CoreAction.ThrowAccessDenied:
                    throw new UnauthorizedAccessException("Access to the path is denied");
                case CoreAction.FilesOnly:
                    if (Directory.Exists(MovieFolder)) Directory.Delete(MovieFolder, true);
                    break;
                case CoreAction.FilesOnlyThenRow:
                    if (first) Directory.Delete(MovieFolder, true);
                    else Indexed = false;
                    break;
                case CoreAction.RowOnly:
                    Indexed = false;
                    break;
                case CoreAction.DeleteThenThrowUnexpected:
                    Directory.Delete(MovieFolder, true);
                    Indexed = false;
                    throw new InvalidOperationException("core state");
                case CoreAction.ThrowUnexpected:
                    throw new InvalidOperationException("core state");
                case CoreAction.ShareDropsThenRowOnly:
                    if (first)
                    {
                        ShareDown = true;
                        throw ShareError is IOException io ? io : new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));
                    }

                    Indexed = false; // core swallows the not-found it reads on the dead share and drops only the row
                    break;
                case CoreAction.DeleteThenShareDrops:
                    Directory.Delete(MovieFolder, true);
                    Indexed = false;
                    ShareDown = true;
                    break;
                case CoreAction.DeleteAndRootGone:
                    Directory.Delete(Root, true);
                    Indexed = false;
                    break;
            }

            return null;
        }
    }
}
