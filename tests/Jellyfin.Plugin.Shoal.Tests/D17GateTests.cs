using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Shoal.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// D17 gates. Exactly two actions answer without a sign-in (the invite redeem and the ping hint), and "Quick Connect" appears in the source only where a legacy
/// value, the old-state migration, the share-side token mint or the redaction of old secrets needs the word.
/// </summary>
public sealed class D17GateTests
{
    // Files that may name Quick Connect, with the reason.
    private static readonly string[] Allowed =
    [
        "Guard/PeerGuardFilter.cs",        // the ban on a share user calling QuickConnect/Authorize
        "Pairing/PairingEnums.cs",         // legacy error kinds that old health files hold
        "Pairing/PairingService.cs",       // the migration of an old state and the best-effort logout of the old token
        "Security/Redact.cs",              // old secrets and codes never appear in a log
        "Sharing/ShareUserSource.cs",      // a comment on how the share session is minted
        "State/ShoalState.cs",             // the legacy method value
        "Peers/TokenRotation.cs",          // a comment on why a Quick Connect pairing cannot rotate
    ];

    [Fact]
    public void T_D17_5_ExactlyTwoActionsAreAnonymous()
    {
        var anonymous = typeof(PairController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(static t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(static m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null).Select(m => t.Name + "." + m.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["PairController.Pair", "ShoalPingController.Ping"], anonymous);
    }

    [Fact]
    public void T_D17_6_QuickConnectIsNamedOnlyWhereItIsKept()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Jellyfin.Plugin.Shoal")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root); // the tests run from a checkout
        var source = Path.Combine(root, "src", "Jellyfin.Plugin.Shoal");
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => (File: Path.GetRelativePath(source, f).Replace('\\', '/'), Text: File.ReadAllText(f)))
            .Where(x => (x.Text.Contains("QuickConnect", StringComparison.OrdinalIgnoreCase) || x.Text.Contains("Quick Connect", StringComparison.OrdinalIgnoreCase)) && !Allowed.Contains(x.File))
            .Select(static x => x.File)
            .ToList();

        Assert.Empty(offenders);
    }
}
