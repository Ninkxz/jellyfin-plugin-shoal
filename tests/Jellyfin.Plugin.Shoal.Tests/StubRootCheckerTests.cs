using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>DESIGN 3.1 (Warden W5-6): a root inside or containing a library, a link, a network or FUSE file system are all refused; a read creates nothing.</summary>
public sealed class StubRootCheckerTests
{
    private static StubRootChecker With(params string[] libraries) => new(() => libraries);

    [Fact]
    public void AFolderInsideALibrary_IsRefused()
    {
        using var dir = new TempDirectory();
        var library = Path.Combine(dir.Path, "media");
        Directory.CreateDirectory(library);

        Assert.Equal(StubRootCheck.InsideLibrary, With(library).Check(Path.Combine(library, "shoal")));
    }

    [Fact]
    public void AFolderThatContainsALibrary_IsRefused()
    {
        using var dir = new TempDirectory();
        var library = Path.Combine(dir.Path, "all", "movies");
        Directory.CreateDirectory(library);

        Assert.Equal(StubRootCheck.InsideLibrary, With(library).Check(Path.Combine(dir.Path, "all")));
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefix_IsFine()
    {
        using var dir = new TempDirectory();
        var library = Path.Combine(dir.Path, "media");
        Directory.CreateDirectory(library);

        Assert.Equal(StubRootCheck.Ok, With(library).Check(Path.Combine(dir.Path, "media-shoal")));
    }

    [Fact]
    public void ASymlinkIsRefused_AndSoIsAFolderUnderOneThatPointsIntoALibrary()
    {
        using var dir = new TempDirectory();
        var library = Path.Combine(dir.Path, "media");
        Directory.CreateDirectory(library);
        var link = Path.Combine(dir.Path, "shoal-link");
        try
        {
            Directory.CreateSymbolicLink(link, library);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // no permission to create links on this machine
        }

        Assert.NotEqual(StubRootCheck.Ok, With(library).Check(link));
        Assert.Equal(StubRootCheck.InsideLibrary, With(library).Check(Path.Combine(link, "sub")));
    }

    [Theory]
    [InlineData("nfs", true)]
    [InlineData("cifs", true)]
    [InlineData("fuse.sshfs", true)]
    [InlineData("fuse.rclone", true)]
    [InlineData("fuse.mergerfs", true)]
    [InlineData("ext4", false)]
    [InlineData("xfs", false)]
    [InlineData("overlay", false)]
    public void NetworkAndFuseFileSystems_AreRefused(string type, bool refused) =>
        Assert.Equal(refused, StubRootChecker.IsNetworkFileSystem(type));

    [Fact]
    public void TheReadPath_CreatesNothing()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "not", "yet");

        var result = With().Check(target, probeWrite: false);

        Assert.Equal(StubRootCheck.Ok, result);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "not")));
    }

    [Fact]
    public void ThePutPath_CreatesAndProvesTheFolder()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "made");

        Assert.Equal(StubRootCheck.Ok, With().Check(target));
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
}
