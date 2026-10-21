using System.Globalization;

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// Which limit refused a play (PLAN 2.9.2 K11).
/// </summary>
public enum BusyReason
{
    /// <summary>The user asked to play too often in a short time.</summary>
    TooFast,

    /// <summary>The user already plays as many titles as allowed.</summary>
    UserStreams,

    /// <summary>The friend's server already sends as many streams as it allows (or this user holds half of them).</summary>
    PeerStreams,

    /// <summary>The friend's server already converts as many videos as it allows.</summary>
    Conversions,
}

/// <summary>
/// The words of the Busy message (PLAN 2.9.2 K11). No dashes.
/// </summary>
public static class BusyNotice
{
    /// <summary>
    /// The message for a refusal.
    /// </summary>
    /// <param name="reason">Which limit was reached.</param>
    /// <param name="friend">The friend's server name, already sanitized.</param>
    /// <param name="playing">How many titles the user plays now.</param>
    /// <returns>The text.</returns>
    public static string Text(BusyReason reason, string friend, int playing) => reason switch
    {
        BusyReason.TooFast => "Too many tries in a row. Wait a minute, then try again.",
        BusyReason.UserStreams => playing == 1
            ? "You're already playing 1 title. Stop it, then try again."
            : string.Create(CultureInfo.InvariantCulture, $"You're already playing {playing} titles. Stop one, then try again."),
        BusyReason.Conversions => Friend(friend) + " is converting as many videos as it can right now. Try again in a minute.",
        _ => Friend(friend) + " is sending as much as it can right now. Try again in a minute.",
    };

    private static string Friend(string friend) => friend.Length == 0 ? "Your friend's server" : friend + "'s server";
}
