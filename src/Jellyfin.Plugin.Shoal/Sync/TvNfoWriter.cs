using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Writes <c>tvshow.nfo</c>, <c>season.nfo</c> and an episode's <c>.nfo</c> (DESIGN v2.7.1) with <c>XmlWriter</c>, <c>CheckCharacters</c> on, UTF-8, element text
/// only. Every value comes from a sanitized record, every number is written from its integer. Never written: <c>uniqueid</c> for an episode, <c>actor</c> for a
/// season or episode, <c>director</c>, <c>credits</c>, <c>runtime</c>, <c>thumb</c>, <c>fileinfo</c>, <c>lockdata</c>, or anything with a URL. The tags are written
/// at all three levels so stock <c>BlockedTags</c> works whatever inheritance does.
/// </summary>
public sealed class TvNfoWriter : ITvNfoWriter
{
    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        CheckCharacters = true,
        ConformanceLevel = ConformanceLevel.Document,
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        OmitXmlDeclaration = false,
    };

    /// <inheritdoc />
    public string Series(SanitizedSeries series, IReadOnlyList<string> tags, string? friendName = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Write("tvshow", xml =>
        {
            Text(xml, "title", series.Title);
            Text(xml, "originaltitle", series.OriginalTitle);
            Text(xml, "sorttitle", series.SortTitle);
            Text(xml, "year", series.Year?.ToString(CultureInfo.InvariantCulture));
            Text(xml, "premiered", Date(series.Premiered));
            Text(xml, "enddate", Date(series.EndDate));
            Text(xml, "status", series.Status);
            Text(xml, "plot", FriendLabel.Apply(series.Overview, friendName is null ? null : [friendName]));
            Text(xml, "mpaa", series.OfficialRating);
            Text(xml, "rating", series.CommunityRating?.ToString("0.0##", CultureInfo.InvariantCulture));
            foreach (var genre in series.Genres)
            {
                Text(xml, "genre", genre);
            }

            Studios(xml, friendName);
            UniqueIds(xml, series.ProviderIds);
            foreach (var actor in series.Actors)
            {
                xml.WriteStartElement("actor");
                Text(xml, "name", actor.Name);
                Text(xml, "role", actor.Role);
                xml.WriteEndElement();
            }

            Tags(xml, tags);
        });
    }

    /// <inheritdoc />
    public string Season(SanitizedSeason season, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(season);
        return Write("season", xml =>
        {
            Text(xml, "title", season.Name);
            xml.WriteElementString("seasonnumber", season.Number.ToString(CultureInfo.InvariantCulture));
            Text(xml, "plot", season.Overview);
            Tags(xml, tags);
        });
    }

    /// <inheritdoc />
    public string Episode(SanitizedEpisode episode, IReadOnlyList<string> tags, string? friendName = null)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return Write("episodedetails", xml =>
        {
            Text(xml, "title", episode.Title);
            xml.WriteElementString("season", episode.Season.ToString(CultureInfo.InvariantCulture));
            xml.WriteElementString("episode", episode.Number.ToString(CultureInfo.InvariantCulture));
            Text(xml, "episodenumberend", episode.NumberEnd?.ToString(CultureInfo.InvariantCulture));
            Text(xml, "aired", Date(episode.Aired));
            Text(xml, "plot", FriendLabel.Apply(episode.Overview, friendName is null ? null : [friendName]));
            Text(xml, "rating", episode.CommunityRating?.ToString("0.0##", CultureInfo.InvariantCulture));
            Studios(xml, friendName);
            Tags(xml, tags);
        });
    }

    private static string Write(string root, Action<XmlWriter> body)
    {
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, Settings))
        {
            xml.WriteStartDocument(standalone: true);
            xml.WriteStartElement(root);
            body(xml);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Text(XmlWriter xml, string element, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            xml.WriteElementString(element, value);
        }
    }

    // 2.9.2 K5: the friend is the only studio of a series or episode; the peer's studios (networks) are dropped. W1: written whatever OmitFacets says.
    private static void Studios(XmlWriter xml, string? friendName)
    {
        foreach (var studio in FriendLabel.Studios(friendName is null ? null : [friendName]))
        {
            Text(xml, "studio", studio);
        }
    }

    private static void Tags(XmlWriter xml, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            Text(xml, "tag", tag);
        }
    }

    private static void UniqueIds(XmlWriter xml, CanonicalIds ids)
    {
        var first = true;
        void One(string type, string value)
        {
            xml.WriteStartElement("uniqueid");
            xml.WriteAttributeString("type", type);
            if (first)
            {
                xml.WriteAttributeString("default", "true");
                first = false;
            }

            xml.WriteString(value);
            xml.WriteEndElement();
        }

        if (ids.Tvdb is { } tvdb)
        {
            One("tvdb", tvdb.ToString(CultureInfo.InvariantCulture));
        }

        if (ids.Tmdb is { } tmdb)
        {
            One("tmdb", tmdb.ToString(CultureInfo.InvariantCulture));
        }

        if (ids.Imdb is { } imdb)
        {
            One("imdb", imdb);
        }
    }
}
