using System;
using System.IO;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Shared fixtures: dummy ids and tokens (none of these is a real credential) and access to the captured playlists.
/// </summary>
internal static class TestIds
{
    /// <summary>The item id on the peer (the Sintel lab item the captured fixtures belong to).</summary>
    public static readonly Guid PeerItem = Guid.Parse("d6b69020-0fc1-c631-3344-25f6f7db7d2e");

    /// <summary>The local stub item id.</summary>
    public static readonly Guid LocalItem = Guid.Parse("d0195e1e-7d62-c627-4713-097a2cef1f17");

    /// <summary>A dummy local (caller) token. The rewriter may only ever append this to validated URIs.</summary>
    public const string LocalToken = "LOCALTOKEN0123456789abcdefABCDEF";

    /// <summary>The dummy peer token that the captured fixtures carry (in place of the real one).</summary>
    public const string PeerToken = "0123456789abcdef0123456789abcdef";

    /// <summary>A dummy device id for the peer-side session.</summary>
    public const string PeerDeviceId = "shoal-test-device";

    public static string PeerN => PeerItem.ToString("N");

    public static string LocalN => LocalItem.ToString("N");

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Replace("\r\n", "\n", StringComparison.Ordinal);
}
