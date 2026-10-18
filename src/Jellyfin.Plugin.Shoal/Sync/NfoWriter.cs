using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// Writes the <c>.nfo</c> of a stub (DESIGN 3.4, 5.5): <c>XmlWriter</c> with <c>CheckCharacters</c>, UTF-8, no DTD, element text only, and
/// no URL-bearing element. Never written: thumb, fanart, trailer, fileinfo, set, lockdata, website, runtime.
/// </summary>
public static class NfoWriter
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

    /// <summary>
    /// Writes the NFO of one movie. The output is deterministic, so an unchanged item produces identical bytes.
    /// </summary>
    /// <param name="movie">The sanitized movie.</param>
    /// <param name="peerDisplayName">The peer's display name, for the <c>Shoal: &lt;Peer&gt;</c> tag; cleaned here to 64 characters.</param>
    /// <param name="extraTags">More tags (the library tag of a shared library folder, K44), or null.</param>
    /// <returns>The file content.</returns>
    public static byte[] Write(SanitizedMovie movie, string peerDisplayName, IReadOnlyList<string>? extraTags = null)
    {
        ArgumentNullException.ThrowIfNull(movie);
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, Settings))
        {
            xml.WriteStartDocument(standalone: true);
            xml.WriteStartElement("movie");
            Text(xml, "title", movie.Title);
            Text(xml, "originaltitle", movie.OriginalTitle);
            Text(xml, "sorttitle", movie.SortTitle);
            if (movie.Year is { } year)
            {
                Text(xml, "year", year.ToString(CultureInfo.InvariantCulture));
            }

            if (movie.Premiered is { } premiered)
            {
                Text(xml, "premiered", premiered.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            Text(xml, "plot", FriendLabel.Apply(movie.Overview));
            Text(xml, "tagline", movie.Tagline);
            Text(xml, "mpaa", movie.OfficialRating ?? string.Empty);
            if (movie.CommunityRating is { } rating)
            {
                Text(xml, "rating", rating.ToString("0.0##", CultureInfo.InvariantCulture));
            }

            foreach (var genre in movie.Genres)
            {
                Text(xml, "genre", genre);
            }

            foreach (var studio in movie.Studios)
            {
                Text(xml, "studio", studio);
            }

            UniqueIds(xml, movie.ProviderIds);
            foreach (var actor in movie.Actors)
            {
                xml.WriteStartElement("actor");
                Text(xml, "name", actor.Name);
                Text(xml, "role", actor.Role);
                xml.WriteEndElement();
            }

            Text(xml, "tag", "Shoal");
            var peer = MetadataSanitizer.DisplayName(peerDisplayName);
            if (peer.Length > 0)
            {
                Text(xml, "tag", "Shoal: " + peer);
            }

            foreach (var tag in extraTags ?? [])
            {
                Text(xml, "tag", tag);
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return stream.ToArray();
    }

    private static void Text(XmlWriter xml, string element, string value)
    {
        if (value.Length > 0)
        {
            xml.WriteElementString(element, value);
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

        if (ids.Tmdb is { } tmdb)
        {
            One("tmdb", tmdb.ToString(CultureInfo.InvariantCulture));
        }

        if (ids.Imdb is { } imdb)
        {
            One("imdb", imdb);
        }

        if (ids.Tvdb is { } tvdb)
        {
            One("tvdb", tvdb.ToString(CultureInfo.InvariantCulture));
        }
    }
}
