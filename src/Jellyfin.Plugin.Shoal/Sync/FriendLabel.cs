using System;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The plain-words label that opens the overview of every friend's title (PLAN v2.9, owner decision 4): the first line of the movie, series and
/// episode <c>&lt;plot&gt;</c>, never a season's. Plain text only (Moonfin strips markup); the apostrophe is the ASCII one, U+0027.
/// </summary>
public static class FriendLabel
{
    /// <summary>The exact label text.</summary>
    public const string Text = "From a friend's server. May buffer or be unavailable sometimes. If it won't play, try again later.";

    /// <summary>
    /// The version of the label. A catalog entry whose NFO was written with a lower version is rewritten once (a bump of this constant forces one
    /// NFO rewrite for every movie, series and episode).
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Puts the label in front of an overview. Leading copies of the exact label (and the whitespace around them) are removed first, so applying
    /// it twice gives the same text as applying it once.
    /// </summary>
    /// <param name="overview">The overview, already cleaned by <see cref="PeerText.Clean"/>; null or empty is allowed.</param>
    /// <returns>The label, a blank line and the rest; the label alone when nothing else is left.</returns>
    public static string Apply(string? overview)
    {
        var rest = (overview ?? string.Empty).TrimStart();
        while (rest.StartsWith(Text, StringComparison.Ordinal))
        {
            rest = rest[Text.Length..].TrimStart();
        }

        return rest.Length == 0 ? Text : Text + "\n\n" + rest;
    }
}
