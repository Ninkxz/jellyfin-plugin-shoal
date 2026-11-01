using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Shoal.LocalRemoval;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class LocalRemovalPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Digest = new('a', 64);

    private static RemovalMediaEvidence Movie() => new(Guid.NewGuid(), new Dictionary<string, string> { ["Tmdb"] = "42" }, 8000, Digest, true, true, true, true);

    private static RemovalContext Admitted() => new()
    {
        Enabled = true,
        StateAndCatalogsReady = true,
        PeerAdmitted = true,
        GrantCurrent = true,
        IndependentOriginVerified = true,
        AllUsersAdmitted = true,
        HistoryHandoffVerified = true,
        RecoverableStorageVerified = true,
        ReplacementPlaybackVerified = true,
        EvidenceUtc = Now,
    };

    private static RemovalPlan Plan() => new(Guid.NewGuid().ToString("N"), Admin, Guid.NewGuid(), Digest, Now, Now.AddMinutes(5));

    [Fact]
    public void SameBytesAndIdentityAreEligibleOnlyWithEveryFreshIndependentProof()
    {
        Assert.Null(RemovalPolicy.Refusal(Movie(), Movie(), Admitted(), Now));
        Assert.Equal("disabled", RemovalPolicy.Refusal(Movie(), Movie(), new RemovalContext(), Now));
        Assert.Equal("disabled", RemovalPolicy.Refusal(Movie(), Movie(), Admitted() with { Enabled = false }, Now));
    }

    [Theory]
    [InlineData("state", "recovery")]
    [InlineData("peer", "sourceNotAdmitted")]
    [InlineData("grant", "sourceNotAdmitted")]
    [InlineData("origin", "originUnverified")]
    [InlineData("users", "userAccessUnverified")]
    [InlineData("history", "historyUnverified")]
    [InlineData("storage", "recoveryStorageUnverified")]
    [InlineData("playback", "playbackUnverified")]
    [InlineData("busy", "busy")]
    public void ASeparateRequirementCannotBeReplacedByMatchingBytes(string missing, string reason)
    {
        var context = missing switch
        {
            "state" => Admitted() with { StateAndCatalogsReady = false },
            "peer" => Admitted() with { PeerAdmitted = false },
            "grant" => Admitted() with { GrantCurrent = false },
            "origin" => Admitted() with { IndependentOriginVerified = false },
            "users" => Admitted() with { AllUsersAdmitted = false },
            "history" => Admitted() with { HistoryHandoffVerified = false },
            "storage" => Admitted() with { RecoverableStorageVerified = false },
            "playback" => Admitted() with { ReplacementPlaybackVerified = false },
            _ => Admitted() with { Busy = true },
        };
        Assert.Equal(reason, RemovalPolicy.Refusal(Movie(), Movie(), context, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void FutureAndExpiredProofsAreRefused(int ageSeconds) =>
        Assert.Equal("staleEvidence", RemovalPolicy.Refusal(Movie(), Movie(), Admitted() with { EvidenceUtc = Now.AddSeconds(-ageSeconds) }, Now));

    [Fact]
    public void EvidenceAtItsLifetimeBoundaryIsStillUsable() =>
        Assert.Null(RemovalPolicy.Refusal(Movie(), Movie(), Admitted() with { EvidenceUtc = Now - RemovalPolicy.EvidenceLifetime }, Now));

    [Theory]
    [InlineData("movie", "moviesOnly")]
    [InlineData("file", "physicalOriginRequired")]
    [InlineData("version", "unsupportedVersions")]
    [InlineData("identity", "fileIdentityUnverified")]
    [InlineData("id", "invalidItem")]
    public void UnsupportedLocalOrRemoteMediaRemainBlocked(string missing, string reason)
    {
        var changed = missing switch
        {
            "movie" => Movie() with { IsMovie = false },
            "file" => Movie() with { IsPhysicalFile = false },
            "version" => Movie() with { IsSingleVersion = false },
            "identity" => Movie() with { FileIdentityVerified = false },
            _ => Movie() with { ItemId = Guid.Empty },
        };
        Assert.Equal(reason, RemovalPolicy.Refusal(changed, Movie(), Admitted(), Now));
        Assert.Equal(reason, RemovalPolicy.Refusal(Movie(), changed, Admitted(), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void UnknownOrMalformedFullFileHashIsNeverQualityEvidence(string? hash) =>
        Assert.Equal("contentNotEquivalent", RemovalPolicy.Refusal(Movie(), Movie() with { ContentSha256 = hash }, Admitted(), Now));

    [Fact]
    public void SameTitleIdsAndResolutionCannotOverrideDifferentBytesOrLengths()
    {
        Assert.Equal("contentNotEquivalent", RemovalPolicy.Refusal(Movie(), Movie() with { ContentSha256 = new string('b', 64) }, Admitted(), Now));
        Assert.Equal("contentNotEquivalent", RemovalPolicy.Refusal(Movie(), Movie() with { SizeBytes = 8001 }, Admitted(), Now));
        Assert.Equal("contentNotEquivalent", RemovalPolicy.Refusal(Movie() with { SizeBytes = 0 }, Movie() with { SizeBytes = 0 }, Admitted(), Now));
        Assert.Equal("contentNotEquivalent", RemovalPolicy.Refusal(Movie() with { SizeBytes = RemovalPolicy.MaxFileSizeBytes + 1 }, Movie() with { SizeBytes = RemovalPolicy.MaxFileSizeBytes + 1 }, Admitted(), Now));
        Assert.Null(RemovalPolicy.Refusal(Movie(), Movie() with { ContentSha256 = Digest.ToUpperInvariant() }, Admitted(), Now));
    }

    [Fact]
    public void StrongProviderConflictCannotBeBridgedByAnotherMatchingIdOrSameContent()
    {
        var local = Movie() with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "42", ["Imdb"] = "tt123" } };
        var remote = Movie() with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "42", ["Imdb"] = "tt456" } };
        Assert.Equal("identityUnverified", RemovalPolicy.Refusal(local, remote, Admitted(), Now));
        Assert.Equal("identityUnverified", RemovalPolicy.Refusal(Movie(), Movie() with { ProviderIds = new Dictionary<string, string>() }, Admitted(), Now));
        Assert.Equal("identityUnverified", RemovalPolicy.Refusal(Movie() with { ProviderIds = new Dictionary<string, string> { ["Unknown"] = "42" } }, Movie() with { ProviderIds = new Dictionary<string, string> { ["Unknown"] = "42" } }, Admitted(), Now));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-42")]
    [InlineData(" ")]
    [InlineData("bogus")]
    public void InvalidKnownIdentityDoesNotBecomeAMissingHarmlessNamespace(string invalid)
    {
        var local = Movie() with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = invalid, ["Imdb"] = "tt123" } };
        var remote = Movie() with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "42", ["Imdb"] = "tt123" } };
        Assert.Equal("identityUnverified", RemovalPolicy.Refusal(local, remote, Admitted(), Now));
    }

    [Fact]
    public void CaseDuplicateNamespacesAreAmbiguousButCanonicalValuesCanMatch()
    {
        var duplicate = Movie() with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "42", ["tmdb"] = "43" } };
        Assert.Equal("identityUnverified", RemovalPolicy.Refusal(duplicate, Movie(), Admitted(), Now));
        Assert.Null(RemovalPolicy.Refusal(Movie(), Movie() with { ProviderIds = new Dictionary<string, string> { ["tmdb"] = "0042" } }, Admitted(), Now));
    }

    [Fact]
    public void PlansAreBoundToOneAdminExpiryAndFreshFacts()
    {
        var plan = Plan();
        Assert.Null(RemovalPolicy.PlanRefusal(plan, Admin, Digest, Now));
        Assert.Equal("wrongAdministrator", RemovalPolicy.PlanRefusal(plan, Guid.NewGuid(), Digest, Now));
        Assert.Equal("planAlreadyUsed", RemovalPolicy.PlanRefusal(plan with { Consumed = true }, Admin, Digest, Now));
        Assert.Equal("expiredPlan", RemovalPolicy.PlanRefusal(plan, Admin, Digest, plan.ExpiresUtc));
        Assert.Equal("expiredPlan", RemovalPolicy.PlanRefusal(plan with { ExpiresUtc = Now.AddHours(1) }, Admin, Digest, Now));
        Assert.Equal("expiredPlan", RemovalPolicy.PlanRefusal(plan with { CreatedUtc = Now.AddSeconds(1) }, Admin, Digest, Now));
        Assert.Equal("planChanged", RemovalPolicy.PlanRefusal(plan, Admin, new string('b', 64), Now));
        Assert.Equal("invalidPlan", RemovalPolicy.PlanRefusal(plan with { PlanId = "../file" }, Admin, Digest, Now));
    }

    [Fact]
    public void JournalRequiresExplicitPurgeIntentAndCannotReuseTerminalOperations()
    {
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.Prepared, RemovalPhase.Quarantined));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.Quarantined, RemovalPhase.ReplacementReady));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.ReplacementReady, RemovalPhase.Restored));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.NeedsRecovery, RemovalPhase.Restored));
        Assert.False(RemovalPolicy.CanTransition(RemovalPhase.Prepared, RemovalPhase.ReplacementReady));
        Assert.All(Enum.GetValues<RemovalPhase>(), next => Assert.False(RemovalPolicy.CanTransition(RemovalPhase.Restored, next)));
        Assert.False(RemovalPolicy.CanTransition(RemovalPhase.ReplacementReady, RemovalPhase.Purged));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.ReplacementReady, RemovalPhase.PurgePrepared));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.PurgePrepared, RemovalPhase.PurgeCommitting));
        Assert.True(RemovalPolicy.CanTransition(RemovalPhase.PurgeCommitting, RemovalPhase.Purged));
        Assert.All(Enum.GetValues<RemovalPhase>(), next => Assert.False(RemovalPolicy.CanTransition(RemovalPhase.Purged, next)));
    }
}
