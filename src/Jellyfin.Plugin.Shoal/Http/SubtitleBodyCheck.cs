using System;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Http;

/// <summary>
/// The checks a subtitle body from a peer must pass before anything is relayed or rebuilt (DESIGN 8.6 "Subtitle body checks"): valid UTF-8
/// (a BOM is allowed), no NUL, and the opening of the format. A body that fails is a 502, never passed on.
/// </summary>
public static class SubtitleBodyCheck
{
    private const int AssHeaderWindow = 4096;
    private const int MaxSrtIndexDigits = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Checks a body.
    /// </summary>
    /// <param name="kind">One of the subtitle kinds of <see cref="ResponseKind"/>.</param>
    /// <param name="body">The bytes.</param>
    /// <returns>True when the body may be used.</returns>
    public static bool IsAcceptable(ResponseKind kind, ReadOnlySpan<byte> body)
    {
        if (kind is not (ResponseKind.SubtitleVtt or ResponseKind.SubtitleSrt or ResponseKind.SubtitleAss or ResponseKind.SubtitleJson))
        {
            return false;
        }

        if (body.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return kind switch
        {
            ResponseKind.SubtitleVtt => text.StartsWith("WEBVTT", StringComparison.Ordinal),
            ResponseKind.SubtitleSrt => FirstLineIsAnIndex(text),
            ResponseKind.SubtitleAss => text[..Math.Min(text.Length, AssHeaderWindow)].Contains("[Script Info]", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool FirstLineIsAnIndex(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > MaxSrtIndexDigits)
            {
                return false;
            }

            foreach (var c in line)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
