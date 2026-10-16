using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A fresh install has no stub root and nothing but the admin ever set one, so adding a library answered 409 noStubRoot. The initializer sets the default
/// folder once, with the check of the Settings page, and never touches a value the admin chose.
/// </summary>
public sealed class StubRootInitializerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ShoalPaths _paths;
    private readonly StateStore _state;

    public StubRootInitializerTests()
    {
        _paths = new ShoalPaths(_dir.Path);
        _state = new StateStore(_paths, NullLogger.Instance, TimeProvider.System, null);
    }

    public void Dispose()
    {
        _state.Dispose();
        _dir.Dispose();
    }

    private StubRootInitializer Initializer(IStubRootChecker? checker = null) =>
        new(_state, checker ?? new StubRootChecker(static () => []), _paths, NullLogger<StubRootInitializer>.Instance);

    [Fact]
    public async Task AFreshInstall_GetsTheDefaultStubRoot_CreatedAndPersisted()
    {
        Assert.True(string.IsNullOrEmpty(_state.Current.StubRoot));

        var ok = await Initializer().EnsureAsync();

        Assert.True(ok);
        Assert.Equal(_paths.DefaultStubRoot, _state.Current.StubRoot);
        Assert.True(Directory.Exists(_paths.DefaultStubRoot));
        using var reopened = new StateStore(_paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.Equal(_paths.DefaultStubRoot, reopened.Current.StubRoot); // it survived a restart
    }

    [Fact]
    public async Task AStubRootTheAdminSet_IsNeverOverridden_AndNoFolderIsMade()
    {
        var chosen = Path.Combine(_dir.Path, "my-own-stubs");
        await _state.UpdateAsync(s => s.StubRoot = chosen);

        var ok = await Initializer().EnsureAsync();

        Assert.True(ok);
        Assert.Equal(chosen, _state.Current.StubRoot);
        Assert.False(Directory.Exists(_paths.DefaultStubRoot));
    }

    [Fact]
    public async Task AValueSetBetweenTheCheckAndTheWrite_Wins()
    {
        var chosen = Path.Combine(_dir.Path, "set-meanwhile");
        var racing = new RacingChecker(() => _state.UpdateAsync(s => s.StubRoot = chosen).GetAwaiter().GetResult());

        await Initializer(racing).EnsureAsync();

        Assert.Equal(chosen, _state.Current.StubRoot);
    }

    [Theory]
    [InlineData(StubRootCheck.InsideLibrary)]
    [InlineData(StubRootCheck.Network)]
    [InlineData(StubRootCheck.NotWritable)]
    public async Task ADefaultThatFailsTheCheck_IsNotUsed_AndTheAdminIsLeftToChoose(StubRootCheck result)
    {
        var ok = await Initializer(new FixedChecker(result)).EnsureAsync();

        Assert.False(ok);
        Assert.True(string.IsNullOrEmpty(_state.Current.StubRoot));
    }

    [Fact]
    public async Task ItRunsAtStartup_AndInRecoveryModeWritesNothing()
    {
        await Initializer().StartAsync(default);
        Assert.Equal(_paths.DefaultStubRoot, _state.Current.StubRoot);

        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, "{ not json");
        File.WriteAllText(paths.StateFile + ".bak", "{ nor this");
        using var broken = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        Assert.NotEqual(StateStatus.Ready, broken.Status);

        var ok = await new StubRootInitializer(broken, new StubRootChecker(static () => []), paths, NullLogger<StubRootInitializer>.Instance).EnsureAsync();

        Assert.False(ok);
        Assert.False(Directory.Exists(paths.DefaultStubRoot));
    }

    [Fact]
    public void Pilot07_ShoalsOwnLibrariesInsideTheStubRoot_AreNotACollision_ButAForeignLibraryIs()
    {
        var root = Path.Combine(Path.GetTempPath(), "shoal-stubroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var own = Path.Combine(root, "4b0c1d2e3f405162738495a6b7c8d9e0", "Movies");
            Directory.CreateDirectory(own);
            Assert.Equal(StubRootCheck.Ok, new StubRootChecker(() => [own]).Check(root));

            var foreign = Path.Combine(root, "my media");
            Directory.CreateDirectory(foreign);
            Assert.Equal(StubRootCheck.InsideLibrary, new StubRootChecker(() => [foreign]).Check(root));
            Assert.Equal(StubRootCheck.InsideLibrary, new StubRootChecker(() => [Path.GetDirectoryName(root)!]).Check(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixedChecker : IStubRootChecker
    {
        private readonly StubRootCheck _result;

        public FixedChecker(StubRootCheck result) => _result = result;

        public StubRootCheck Check(string path) => _result;

        public StubRootCheck Check(string path, bool probeWrite) => _result;
    }

    private sealed class RacingChecker : IStubRootChecker
    {
        private readonly Action _during;

        public RacingChecker(Action during) => _during = during;

        public StubRootCheck Check(string path)
        {
            _during();
            return StubRootCheck.Ok;
        }

        public StubRootCheck Check(string path, bool probeWrite) => Check(path);
    }
}
