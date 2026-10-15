using System;
using System.Linq;
using Jellyfin.Plugin.Shoal.Authorization;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Playback;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The <see cref="IVersionResolver"/> of alternate versions (DESIGN v2.6.4, K28): a title's stubs share one folder, and each version is its own local
/// item with its own <c>.strm</c>. A request names a version by its local item id; the answer is the version's stub and the Peer ids behind it, or
/// null (a 404 before any Peer call) when the id is unknown, does not parse, or belongs to another title.
/// </summary>
public sealed class ShoalVersionResolver : IVersionResolver
{
    private readonly ShoalIndex _index;
    private readonly StubCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoalVersionResolver"/> class.
    /// </summary>
    /// <param name="index">The federated items.</param>
    /// <param name="catalog">The catalogs.</param>
    public ShoalVersionResolver(ShoalIndex index, StubCatalog catalog)
    {
        _index = index;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public ResolvedVersion? Resolve(Guid routeItemId, string? requestedMediaSourceId)
    {
        if (_index.Get(routeItemId) is not { } route)
        {
            return null;
        }

        var versionId = routeItemId;
        if (requestedMediaSourceId is not null)
        {
            if (!ShoalRequest.TryParseGuid(requestedMediaSourceId, out versionId))
            {
                return null;
            }
        }

        var stub = versionId == routeItemId ? route : _index.Get(versionId);
        if (stub is null || !string.Equals(stub.PeerKey, route.PeerKey, StringComparison.Ordinal) || !string.Equals(stub.FolderName, route.FolderName, StringComparison.Ordinal))
        {
            return null; // another title (or not a stub at all): outside the set
        }

        if (_catalog.Find(stub.PeerKey, stub.FolderName) is not { } entry)
        {
            return null;
        }

        var source = stub.FileName.Length == 0 || entry.Sources.Count == 0
            ? entry.Sources.FirstOrDefault()
            : entry.Sources.FirstOrDefault(s => s.StubRelPath is not null && string.Equals(s.StubRelPath.Split('/')[^1], stub.FileName, StringComparison.Ordinal));
        if (source is null || !MetadataSanitizer.TryParseId(entry.PeerItemId, out var peerItem))
        {
            return null;
        }

        var peerSourceText = source.PeerMediaSourceId.Length > 0 ? source.PeerMediaSourceId : entry.PeerMediaSourceId;
        return MetadataSanitizer.TryParseId(peerSourceText, out var peerSource)
            ? new ResolvedVersion(versionId, source.StubKey.Length > 0 ? source.StubKey : entry.StubKey, stub.PeerKey, peerItem, peerSource, entry.StubKey)
            : null;
    }
}
