using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>2.9.3 K6: the friend link label ("Also your server: {name}") is the admin's alias, else the friend's name, without links.</summary>
public sealed class AdminStatusBuilderTests
{
    private const string ServerId = "0123456789ab0123456789abcdef0123";

    private static PeerState Peer(string alias) => new()
    {
        PeerKey = StateSamples.PeerKeyA,
        DisplayName = "BTH0MP's Server www.evil.example",
        Alias = alias,
        BaseUrl = "http://bth.tail1234.ts.net:8096",
        PinnedServerId = ServerId,
    };

    private static string LinkName(PeerState peer)
    {
        var share = new ShareState { ShareId = "share-friendd", FriendName = "FriendD", HomeServerIdPrefix = ServerId[..12] };
        var links = AdminStatusBuilder.Links([peer], [AdminStatusBuilder.Server(peer, new PeerLiveInfo())], [share], [AdminStatusBuilder.Friend(share, new ShareLiveInfo())]);
        var link = Assert.Single(links);
        Assert.True(link.Linked);
        return link.Name;
    }

    [Fact]
    public void LinkName_IsAliasWithoutUrl()
    {
        Assert.Equal("FriendC", LinkName(Peer("FriendC https://evil.example/x")));
        Assert.Equal("BTH0MP's Server", LinkName(Peer(string.Empty))); // no alias: the friend's own name, cleaned
        Assert.Equal("FriendD", LinkName(Peer("evil.example"))); // nothing left: the name the owner typed for the share
    }
}
