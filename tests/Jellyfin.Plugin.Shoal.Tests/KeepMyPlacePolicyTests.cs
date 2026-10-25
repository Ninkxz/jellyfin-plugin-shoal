using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Shoal.Progress;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class KeepMyPlacePolicyTests
{
    private static readonly Guid User = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly KeepMyPlaceIdentity Movie = new(KeepMyPlaceKind.Movie, Ids("Tmdb", "123"));
    private static readonly KeepMyPlaceMedia Source = new(Guid.NewGuid(), Movie, TimeSpan.FromMinutes(90).Ticks);
    private static readonly KeepMyPlaceMedia Destination = Source with { ItemId = Guid.NewGuid() };
    private static readonly KeepMyPlaceHistory Empty = new(User);
    private static readonly KeepMyPlaceHistory Resume = new(User, PositionTicks: TimeSpan.FromMinutes(10).Ticks);
    private static readonly KeepMyPlaceContext Allowed = new(User, true, true, true, true, true, false);

    [Fact]
    public void WatchedCopiesOnlyProgressAndClearsResume()
    {
        var date = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
        var decision = Decide(Resume with { Played = true, LastPlayedUtc = date, HasPersonalHistory = true });
        Assert.True(decision.Apply);
        Assert.True(decision.Played);
        Assert.Equal(0, decision.PositionTicks);
        Assert.Equal(1, decision.PlayCount);
        Assert.Equal(date, decision.LastPlayedUtc);
    }

    [Fact]
    public void ResumeCopiesAbsoluteTicks()
    {
        var decision = Decide(Resume, Destination with { RuntimeTicks = Source.RuntimeTicks + TimeSpan.TicksPerSecond * 30 });
        Assert.True(decision.Apply);
        Assert.False(decision.Played);
        Assert.Equal(Resume.PositionTicks, decision.PositionTicks);
    }

    [Fact]
    public void SourceResetProducesNoWrite() => Assert.Equal("noProgress", Decide(Empty).Reason);

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void InvalidPositionsNeverTransfer(long ticks) => Assert.False(Decide(Resume with { PositionTicks = ticks }).Apply);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void UnknownOrIncompatibleRuntimeDoesNotResume(long? runtime) => Assert.False(Decide(Resume, Destination with { RuntimeTicks = runtime }).Apply);

    [Fact]
    public void ShortTitlesUseTwoPercentTolerance()
    {
        var source = Source with { RuntimeTicks = TimeSpan.FromMinutes(10).Ticks };
        Assert.True(KeepMyPlacePolicy.Decide(new(source, Resume with { PositionTicks = 10 }), Destination with { RuntimeTicks = source.RuntimeTicks + TimeSpan.TicksPerSecond * 10 }, Empty, Allowed).Apply);
        Assert.False(KeepMyPlacePolicy.Decide(new(source, Resume with { PositionTicks = 10 }), Destination with { RuntimeTicks = source.RuntimeTicks + TimeSpan.TicksPerSecond * 20 }, Empty, Allowed).Apply);
    }

    [Fact]
    public void PositionAtEndAndEditionConflictDenyResume()
    {
        Assert.False(Decide(Resume with { PositionTicks = Source.RuntimeTicks!.Value }).Apply);
        Assert.False(KeepMyPlacePolicy.Decide(new(Source with { Edition = "Director's cut" }, Resume), Destination with { Edition = "Theatrical" }, Empty, Allowed).Apply);
        Assert.True(Decide(Resume with { Played = true }, Destination with { RuntimeTicks = null }).Apply);
    }

    [Fact]
    public void EveryDestinationHistorySignalIsAuthoritative()
    {
        KeepMyPlaceHistory[] histories = [Empty with { Played = true }, Empty with { PositionTicks = 1 }, Empty with { PlayCount = 1 }, Empty with { LastPlayedUtc = DateTime.UtcNow }, Empty with { HasPersonalHistory = true }];
        foreach (var history in histories)
        {
            Assert.Equal("destinationHistory", KeepMyPlacePolicy.Decide(new(Source, Resume), Destination, history, Allowed).Reason);
        }
    }

    [Fact]
    public void SameUserAndEveryAccessGateAreRequired()
    {
        KeepMyPlaceContext[] denied = [Allowed with { Enabled = false }, Allowed with { UserEligible = false }, Allowed with { SourceAdmitted = false }, Allowed with { DestinationAdmitted = false }, Allowed with { DestinationLocal = false }, Allowed with { ActivePlayback = true }, Allowed with { SameHistoryRecord = true }, Allowed with { DestinationHistoryObserved = true }, Allowed with { UserId = Guid.NewGuid() }];
        foreach (var context in denied)
        {
            Assert.False(KeepMyPlacePolicy.Decide(new(Source, Resume), Destination, Empty, context).Apply);
        }

        Assert.False(Decide(Resume with { UserId = Guid.NewGuid() }).Apply);
        Assert.False(KeepMyPlacePolicy.Decide(new(Source, Resume), Destination, Empty with { UserId = Guid.NewGuid() }, Allowed).Apply);
        Assert.False(Decide(Resume, Source).Apply);
    }

    [Fact]
    public void ProviderConflictCannotBeOverriddenByOneMatchingId()
    {
        var a = Movie with { Ids = Ids("Tmdb", "123", "Imdb", "tt123") };
        var b = Movie with { Ids = Ids("Tmdb", "123", "Imdb", "tt456") };
        Assert.False(KeepMyPlacePolicy.Matches(a, b));
        Assert.False(KeepMyPlacePolicy.Matches(Movie with { Ids = Ids() }, Movie));
        Assert.False(KeepMyPlacePolicy.Matches(Movie with { Ids = Ids("Tmdb", "nonsense") }, Movie));
        Assert.True(KeepMyPlacePolicy.Matches(Movie with { Ids = Ids("tmdb", "00123") }, Movie));
    }

    [Fact]
    public void EpisodesRequireStrongSeriesAndExactCompleteNumbering()
    {
        var episode = Movie with { Kind = KeepMyPlaceKind.Episode, Season = 0, Episode = 1 };
        Assert.True(KeepMyPlacePolicy.Matches(episode, episode with { End = 1 }));
        Assert.False(KeepMyPlacePolicy.Matches(episode, episode with { End = 2 }));
        Assert.False(KeepMyPlacePolicy.Matches(episode, episode with { Season = null }));
        Assert.False(KeepMyPlacePolicy.Matches(episode, episode with { Ids = Ids("Tmdb", "456") }));
        Assert.False(KeepMyPlacePolicy.Matches(episode with { EpisodeIds = Ids("Tvdb", "1") }, episode with { EpisodeIds = Ids("Tvdb", "2") }));
        Assert.False(KeepMyPlacePolicy.Matches(episode, Movie));
    }

    private static KeepMyPlaceDecision Decide(KeepMyPlaceHistory progress, KeepMyPlaceMedia? destination = null) =>
        KeepMyPlacePolicy.Decide(new(Source, progress), destination ?? Destination, Empty, Allowed);

    private static Dictionary<string, string> Ids(params string[] pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            result.Add(pairs[i], pairs[i + 1]);
        }

        return result;
    }
}
