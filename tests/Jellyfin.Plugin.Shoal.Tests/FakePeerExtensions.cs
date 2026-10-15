using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// An extra set of endpoints for the fake peers (DESIGN v2.6.9). A phase 5 or 6 task adds its endpoints (Rotate, Trickplay, Attachments, a peer
/// with several media sources) as a class in its own new file; the fake peers discover every implementation by reflection, so no task edits
/// <c>FakeJellyfinPeer.cs</c> or <c>PeerServerFixture.cs</c> again.
/// </summary>
internal interface IFakePeerExtension
{
    /// <summary>Maps the endpoints on the peer's app, after the built-in ones.</summary>
    /// <param name="peer">The fake peer (its recorded requests and settings), or null for the plain fixture.</param>
    /// <param name="app">The app.</param>
    void Map(FakeJellyfinPeer? peer, WebApplication app);
}

/// <summary>
/// Finds the <see cref="IFakePeerExtension"/>s of the test assembly.
/// </summary>
internal static class FakePeerExtensions
{
    private static readonly Lazy<IReadOnlyList<IFakePeerExtension>> Found = new(() =>
        [.. typeof(FakePeerExtensions).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(IFakePeerExtension).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)
            .Select(static t => (IFakePeerExtension)Activator.CreateInstance(t)!)]);

    /// <summary>
    /// Maps every extension.
    /// </summary>
    /// <param name="peer">The fake peer, or null.</param>
    /// <param name="app">The app.</param>
    public static void MapAll(FakeJellyfinPeer? peer, WebApplication app)
    {
        foreach (var extension in Found.Value)
        {
            extension.Map(peer, app);
        }
    }
}
