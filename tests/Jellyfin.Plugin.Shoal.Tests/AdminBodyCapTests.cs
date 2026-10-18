using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 10 L4: an admin body over 64 KB (declared or streamed) reads as no body (400 at every route); an account list longer than any server has is refused.
/// </summary>
public sealed class AdminBodyCapTests
{
    private sealed record Probe(string? Name);

    private static HttpRequest Request(string body, bool declareLength)
    {
        var http = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(bytes);
        if (declareLength)
        {
            http.Request.ContentLength = bytes.Length;
        }

        return http.Request;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOver64KB_ReadsAsNothing_AndOneUnderItReads(bool declareLength)
    {
        var big = "{\"name\":\"" + new string('x', AdminReply.MaxBodyBytes) + "\"}";

        Assert.Null(await AdminReply.ReadAsync<Probe>(Request(big, declareLength), default));
        Assert.Equal("ok", (await AdminReply.ReadAsync<Probe>(Request("{\"name\":\"ok\"}", declareLength), default))!.Name);
        Assert.Null(await AdminReply.ReadAsync<Probe>(Request("not json", declareLength), default));
    }

    [Fact]
    public void ASelectionLongerThanAnyServerHas_IsRefused()
    {
        using var rig = new LayoutRig();
        var ids = Enumerable.Repeat(rig.Admin.Id, AccessService.MaxSelectedUsers + 1).ToList(); // a known account, repeated: only the length is wrong
        var access = new AccessService(rig.State, rig.Accounts, rig.Visibility, TimeProvider.System);

        var result = access.Preview(LayoutRig.PeerA, "movies-1", new AccessChange(AccessMode.Selected, ids, false, true));

        Assert.Equal(AccessError.UnknownUser, result.Error);
    }
}
