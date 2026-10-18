using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K50 (PLAN v2.9): the per-connection quality limit and dismissed recommendations in <c>state.json</c>, their validation and their DTOs.
/// </summary>
public sealed class QualityLimitTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ShoalState Filled()
    {
        var state = new ShoalState();
        StateSamples.Fill(state);
        return state;
    }

    [Fact]
    public void ANewConnection_HasNoLimit_AndNoDismissals()
    {
        var peer = new PeerState();
        Assert.Equal(QualityClass.Off, peer.QualityLimit.MaxClass);
        Assert.Null(peer.QualityLimit.MaxMbps);
        Assert.Null(peer.QualityLimit.SetUtc);
        Assert.Null(peer.QualityLimit.SetByUserId);
        Assert.Equal(QualityLimitOrigin.Manual, peer.QualityLimit.Origin);
        Assert.Empty(peer.Dismissed);
    }

    [Theory]
    [InlineData(QualityClass.Off, "\"Off\"")]
    [InlineData(QualityClass.Sd480, "\"480\"")]
    [InlineData(QualityClass.Hd720, "\"720\"")]
    [InlineData(QualityClass.Hd1080, "\"1080\"")]
    [InlineData(QualityClass.Uhd2160, "\"2160\"")]
    public void TheClass_IsWrittenAsItsPlainName(QualityClass cls, string json)
    {
        var state = Filled();
        state.Home.Peers[0].QualityLimit.MaxClass = cls;
        var text = Encoding.UTF8.GetString(StateJson.Serialize(state));

        Assert.Contains("\"maxClass\": " + json, text, StringComparison.Ordinal);
        Assert.Equal(cls, StateJson.Deserialize(Encoding.UTF8.GetBytes(text)).Home.Peers[0].QualityLimit.MaxClass);
    }

    [Theory]
    [InlineData("\"4320\"")]
    [InlineData("\"1080p\"")]
    [InlineData("3")]
    public void AnUnknownClass_IsNotReadAsSomethingElse(string json)
    {
        var text = Encoding.UTF8.GetString(StateJson.Serialize(Filled())).Replace("\"maxClass\": \"Off\"", "\"maxClass\": " + json, StringComparison.Ordinal);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => StateJson.Deserialize(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(int.MaxValue, 1000)]
    public void TheMbps_IsClampedTo1Through1000(int mbps, int expected)
    {
        var state = Filled();
        state.Home.Peers[0].QualityLimit.MaxMbps = mbps;
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Equal(expected, state.Home.Peers[0].QualityLimit.MaxMbps);
        Assert.Equal(mbps != expected, notes.Any(static n => n.Contains("quality limit Mbps", StringComparison.Ordinal)));
    }

    [Fact]
    public void AValidLimit_IsKeptAsIs_WithoutNotes()
    {
        var state = Filled();
        var peer = state.Home.Peers[0];
        peer.QualityLimit = new QualityLimit { MaxClass = QualityClass.Hd1080, MaxMbps = 20, SetUtc = Now, SetByUserId = StateSamples.AdminUser, Origin = QualityLimitOrigin.Recommendation };
        peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Hd720, ThroughputMbps = 12.5, DismissedUtc = Now, DismissedByUserId = StateSamples.AdminUser });
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Empty(notes);
        Assert.Equal(QualityClass.Hd1080, peer.QualityLimit.MaxClass);
        Assert.Equal(20, peer.QualityLimit.MaxMbps);
        Assert.Single(peer.Dismissed);
    }

    [Fact]
    public void ImpossibleDismissals_AreDropped_AndOnlyTheNewest20AreKept()
    {
        var state = Filled();
        var peer = state.Home.Peers[0];
        for (var i = 0; i < 30; i++)
        {
            peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Hd1080, ThroughputMbps = 10, DismissedUtc = Now.AddDays(-i) });
        }

        peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Off, ThroughputMbps = 10, DismissedUtc = Now });
        peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Hd720, ThroughputMbps = double.NaN, DismissedUtc = Now });
        peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Hd720, ThroughputMbps = -1, DismissedUtc = Now });
        peer.Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Hd720, ThroughputMbps = double.PositiveInfinity, DismissedUtc = Now });
        var notes = new List<string>();

        Assert.Null(StateValidator.Validate(state, notes));

        Assert.Equal(StateValidator.MaxDismissed, peer.Dismissed.Count);
        Assert.All(peer.Dismissed, static d => Assert.Equal(QualityClass.Hd1080, d.SuggestedClass));
        Assert.Equal(Now, peer.Dismissed[^1].DismissedUtc); // newest last
        Assert.Contains(notes, static n => n.Contains("dismissed recommendations", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(QualityClass.Off, 7680, 4320, true)]
    [InlineData(QualityClass.Hd1080, 1920, 1080, true)]
    [InlineData(QualityClass.Hd1080, 1920, 1088, false)]
    [InlineData(QualityClass.Hd1080, 2048, 858, false)] // a wide 2K scope: width AND height must fit
    [InlineData(QualityClass.Hd720, 1280, 534, true)]
    [InlineData(QualityClass.Sd480, 854, 480, true)]
    [InlineData(QualityClass.Sd480, 720, 576, false)]
    [InlineData(QualityClass.Uhd2160, 3840, 2160, true)]
    [InlineData(QualityClass.Hd1080, 0, 0, false)] // an unknown size is never assumed small
    public void Fits_UsesTheClassBox(QualityClass cls, int width, int height, bool fits)
    {
        Assert.Equal(fits, QualityLimit.Fits(cls, width, height));
    }

    [Fact]
    public async Task TheLimit_SurvivesASaveAndAReload()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await store.UpdateAsync(static s =>
            {
                StateSamples.Fill(s);
                s.Home.Peers[0].QualityLimit = new QualityLimit { MaxClass = QualityClass.Hd720, MaxMbps = 5000, SetUtc = Now, SetByUserId = StateSamples.AdminUser };
                s.Home.Peers[0].Dismissed.Add(new DismissedRecommendation { SuggestedClass = QualityClass.Sd480, ThroughputMbps = 3.2, DismissedUtc = Now });
            });
        }

        using var reopened = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);
        var peer = reopened.Current.Home.Peers[0];

        Assert.Equal(StateStatus.Ready, reopened.Status);
        Assert.Equal(QualityClass.Hd720, peer.QualityLimit.MaxClass);
        Assert.Equal(1000, peer.QualityLimit.MaxMbps); // clamped on the way in
        Assert.Equal(StateSamples.AdminUser, peer.QualityLimit.SetByUserId);
        Assert.Equal(3.2, Assert.Single(peer.Dismissed).ThroughputMbps);
    }

    // --- DTOs

    [Fact]
    public void TheDto_CarriesNoUserId()
    {
        var dto = QualityLimitDto.From(new QualityLimit { MaxClass = QualityClass.Hd1080, MaxMbps = 15, SetUtc = Now, SetByUserId = StateSamples.AdminUser });
        Assert.Equal(new QualityLimitDto(QualityClass.Hd1080, 15, Now, QualityLimitOrigin.Manual), dto);
        Assert.DoesNotContain(typeof(QualityLimitDto).GetProperties(), static p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?));
        Assert.DoesNotContain(typeof(DismissedRecommendationDto).GetProperties(), static p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?));
    }

    [Fact]
    public void ARequest_IsBuilt_WithTheMbpsClamped_AndTheOrigin()
    {
        var request = new QualityLimitRequest { MaxClass = QualityClass.Hd1080, MaxMbps = 9000, FromRecommendation = true, ExpectedCurrent = new QualityLimitExpected { MaxClass = QualityClass.Off } };

        Assert.True(request.TryBuild(StateSamples.AdminUser, Now, out var limit, out var problem));

        Assert.Null(problem);
        Assert.Equal(QualityClass.Hd1080, limit.MaxClass);
        Assert.Equal(1000, limit.MaxMbps);
        Assert.Equal(Now, limit.SetUtc);
        Assert.Equal(StateSamples.AdminUser, limit.SetByUserId);
        Assert.Equal(QualityLimitOrigin.Recommendation, limit.Origin);
    }

    [Fact]
    public void ARequest_WithoutAClassOrTheExpectedLimit_IsRefused()
    {
        Assert.False(new QualityLimitRequest { ExpectedCurrent = new QualityLimitExpected { MaxClass = QualityClass.Off } }.TryBuild(StateSamples.AdminUser, Now, out _, out var noClass));
        Assert.NotNull(noClass);
        Assert.False(new QualityLimitRequest { MaxClass = QualityClass.Hd720 }.TryBuild(StateSamples.AdminUser, Now, out _, out var noExpected));
        Assert.NotNull(noExpected);
        Assert.False(new QualityLimitRequest { MaxClass = (QualityClass)42, ExpectedCurrent = new QualityLimitExpected { MaxClass = QualityClass.Off } }.TryBuild(StateSamples.AdminUser, Now, out _, out _));
    }

    [Fact]
    public void ExpectedCurrent_MatchesOnlyTheSameClassAndMbps()
    {
        var current = new QualityLimit { MaxClass = QualityClass.Hd1080, MaxMbps = 20 };
        Assert.True(new QualityLimitExpected { MaxClass = QualityClass.Hd1080, MaxMbps = 20 }.Matches(current));
        Assert.False(new QualityLimitExpected { MaxClass = QualityClass.Hd1080 }.Matches(current));
        Assert.False(new QualityLimitExpected { MaxClass = QualityClass.Hd720, MaxMbps = 20 }.Matches(current));
        Assert.False(new QualityLimitExpected { MaxMbps = 20 }.Matches(current));
    }

    [Fact]
    public void DismissalDtos_AreNewestFirst()
    {
        var list = DismissedRecommendationDto.From(
        [
            new DismissedRecommendation { SuggestedClass = QualityClass.Hd720, DismissedUtc = Now.AddDays(-2) },
            new DismissedRecommendation { SuggestedClass = QualityClass.Sd480, DismissedUtc = Now },
        ]);

        Assert.Equal([QualityClass.Sd480, QualityClass.Hd720], list.Select(static d => d.SuggestedClass));
    }
}
