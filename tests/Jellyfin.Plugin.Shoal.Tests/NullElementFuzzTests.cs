using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.Sync;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// W6 M-1: a hostile Peer puts <c>null</c> into every array and dictionary value of an item. The sanitizer must skip the element (or the item),
/// never throw.
/// </summary>
public sealed class NullElementFuzzTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    public static TheoryData<string> Shapes => new()
    {
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","Studios":[null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","Studios":[null,{"Name":"Real"},{"Name":null}]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","People":[null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","People":[null,{"Name":"N","Type":"Actor","Role":null},{"Name":null}]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","MediaSources":[null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","MediaSources":[null,{"Id":"0123456789abcdef0123456789abcdef","MediaStreams":[null]}]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","MediaSources":[{"Id":"0123456789abcdef0123456789abcdef","MediaStreams":[null,{"Type":"Video","Codec":null}]}]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","Genres":[null,"Drama",null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","Taglines":[null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","BackdropImageTags":[null]}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","ImageTags":{"Primary":null}}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","ProviderIds":{"Tmdb":null,"Imdb":null,"Tvdb":null}}""",
        """{"Id":"0123456789abcdef0123456789abcdef","Name":"A","Studios":null,"People":null,"MediaSources":null,"Genres":null,"ProviderIds":null,"ImageTags":null}""",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ANullElement_NeverThrows_AndTheRestOfTheItemSurvives(string json)
    {
        var dto = JsonSerializer.Deserialize<BaseItemDto>(json, JsonDefaults.Options)!;

        var movie = new MetadataSanitizer(new Rating()).Sanitize(dto);

        Assert.NotNull(movie);
        Assert.Equal("A", movie!.Title);
        Assert.DoesNotContain(null, movie.Studios);
        Assert.DoesNotContain(null, movie.Actors);
    }

    [Fact]
    public void NullsAreFuzzedIntoEveryArrayOfTheDto()
    {
        // every array-typed property of BaseItemDto gets [null]: reflection keeps this test honest when the DTO grows
        var arrays = typeof(BaseItemDto).GetProperties().Where(static p => p.PropertyType.IsArray || (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) || p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))).Select(static p => p.Name).ToList();
        Assert.NotEmpty(arrays);
        foreach (var name in arrays)
        {
            var json = "{\"Id\":\"" + Id + "\",\"Name\":\"A\",\"" + name + "\":[null,null]}";
            BaseItemDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<BaseItemDto>(json, JsonDefaults.Options);
            }
            catch (JsonException)
            {
                continue; // the reader skips an item that does not even deserialize
            }

            var ex = Record.Exception(() => new MetadataSanitizer(new Rating()).Sanitize(dto!));
            Assert.True(ex is null, name + ": " + ex);
        }
    }

    private sealed class Rating : IRatingTable
    {
        public bool IsRecognized(string rating) => true;
    }
}
