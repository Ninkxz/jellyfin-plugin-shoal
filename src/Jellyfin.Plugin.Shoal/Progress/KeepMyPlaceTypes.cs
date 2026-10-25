using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Shoal.Progress;

/// <summary>The two transferable playable kinds.</summary>
internal enum KeepMyPlaceKind
{
    /// <summary>A movie.</summary>
    Movie,

    /// <summary>An exactly numbered episode or episode range.</summary>
    Episode
}

/// <summary>IDs describe the movie itself, or the parent series for an episode. No titles are used.</summary>
internal sealed record KeepMyPlaceIdentity(
    KeepMyPlaceKind Kind,
    IReadOnlyDictionary<string, string> Ids,
    int? Season = null,
    int? Episode = null,
    int? End = null,
    IReadOnlyDictionary<string, string>? EpisodeIds = null);

/// <summary>Immutable media facts needed to assess identity and resume compatibility.</summary>
internal sealed record KeepMyPlaceMedia(
    Guid ItemId,
    KeepMyPlaceIdentity Identity,
    long? RuntimeTicks,
    string? Edition = null);

/// <summary>A detached value: never keep core's mutable UserItemData instance in a work queue.</summary>
internal sealed record KeepMyPlaceHistory(
    Guid UserId,
    bool Played = false,
    long PositionTicks = 0,
    int PlayCount = 0,
    DateTime? LastPlayedUtc = null,
    bool HasPersonalHistory = false);

/// <summary>A same-server source and its locally observed progress.</summary>
internal sealed record KeepMyPlaceSource(KeepMyPlaceMedia Media, KeepMyPlaceHistory History);

/// <summary>Fresh authorization and concurrency facts required at the write boundary.</summary>
internal sealed record KeepMyPlaceContext(
    Guid UserId,
    bool Enabled,
    bool UserEligible,
    bool SourceAdmitted,
    bool DestinationAdmitted,
    bool DestinationLocal,
    bool ActivePlayback,
    bool SameHistoryRecord = false,
    bool DestinationHistoryObserved = false);

/// <summary>Only progress fields may be written. Favorites, ratings and stream choices are absent by design.</summary>
internal sealed record KeepMyPlaceDecision(string Reason, bool Played = false, long PositionTicks = 0, int PlayCount = 0, DateTime? LastPlayedUtc = null)
{
    /// <summary>Gets a value indicating whether the decision permits a progress write.</summary>
    public bool Apply => Reason == "watched" || Reason == "resume";
}
