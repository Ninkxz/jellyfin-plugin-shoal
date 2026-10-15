using System;
using System.IO;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// A throw-away folder that is deleted when the test ends.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shoal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A clock the test moves by hand.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A test that only runs on Linux (Unix file modes, directory fsync).
/// </summary>
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only (Unix file modes).";
        }
    }
}

/// <summary>
/// A test that only runs on Windows (DPAPI).
/// </summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only (DPAPI).";
        }
    }
}

/// <summary>
/// Ready-made states for tests.
/// </summary>
internal static class StateSamples
{
    public const string PeerKeyA = "4b0c1d2e3f405162738495a6b7c8d9e0";
    public const string PeerKeyB = "00112233445566778899aabbccddeeff";
    public static readonly Guid AdminUser = Guid.Parse("11111111-2222-3333-4444-555555555555");
    public static readonly Guid OtherUser = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>One paired peer with one library in "Everyone" mode and a hardening mode that is not the default.</summary>
    public static void Fill(ShoalState state)
    {
        state.StubRoot = "/config/data/shoal/stubs";
        state.Home.Peers.Add(new PeerState
        {
            PeerKey = PeerKeyA,
            DisplayName = "Hops",
            BaseUrl = "http://hops.tail1234.ts.net:8096",
            Network = PeerNetwork.Tailnet,
            Enabled = true,
            PinnedServerId = "0123456789abcdef0123456789abcdef",
            Pairing = new PairingInfo { Method = PairingMethod.Qc, CheckString = "K7QM", PairedUtc = new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc), PairedByUserId = AdminUser },
            Libraries =
            {
                new LibraryMapping
                {
                    MappingKey = "movies-1",
                    RemoteName = "Movies",
                    LocalLibraryName = "Hops Movies",
                    Subfolder = "movies",
                    Access = new LibraryAccess
                    {
                        Mode = AccessMode.Everyone,
                        SelectedUserIds = { AdminUser, OtherUser },
                        IncludeRestricted = true,
                        ChangedUtc = new DateTime(2026, 10, 14, 20, 5, 0, DateTimeKind.Utc),
                        ChangedByUserId = AdminUser
                    }
                }
            }
        });
        state.Home.Visibility.ManagedUsers[OtherUser] = new ManagedUser { Since = new DateTime(2026, 10, 14, 20, 6, 0, DateTimeKind.Utc), LastWrittenFoldersHash = "abc123" };
        state.PeerRole.HardeningMode = HardeningMode.Enforce;
        state.PeerRole.HardeningSinceUtc = new DateTime(2026, 10, 14, 20, 7, 0, DateTimeKind.Utc);
        state.PeerRole.Shares.Add(new ShareState
        {
            ShareId = "share-bob",
            FriendName = "Bob",
            ShareUserId = Guid.Parse("99999999-8888-7777-6666-555555555555"),
            ExpectedDeviceIds = { "shoal-1a2b3c4d5e6f-9a8b7c6d" },
            MaxConcurrentTranscodes = 4
        });
    }
}
