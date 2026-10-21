using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Shoal.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// v2.9.1 owner decision: TV shows from friends are a key feature, never a toggle. An old config with TvShowsEnabled false still gives TV, the file is written with
/// true, and no code reads the old switch.
/// </summary>
public sealed class TvAlwaysOnTests
{
    [Fact]
    public void AnOldConfigWithTvShowsEnabledFalse_StillHasTvOn_AndIsWrittenBackAsTrue()
    {
        var old = new PluginConfiguration();
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        xml.Serialize(writer, old);
        var text = writer.ToString().Replace("<TvShowsEnabled>true</TvShowsEnabled>", "<TvShowsEnabled>false</TvShowsEnabled>", StringComparison.Ordinal);
        Assert.Contains("<TvShowsEnabled>false</TvShowsEnabled>", text, StringComparison.Ordinal); // what a 2.9.0 install that had TV off has on disk

        var loaded = (PluginConfiguration)xml.Deserialize(new StringReader(text))!;
        Assert.True(loaded.TvShowsEnabled);

        loaded.TvShowsEnabled = false; // a setter call (an old page) is ignored too
        Assert.True(loaded.TvShowsEnabled);
        using var again = new StringWriter();
        xml.Serialize(again, loaded);
        Assert.Contains("<TvShowsEnabled>true</TvShowsEnabled>", again.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoCodePath_ReadsTheOldSwitch()
    {
        var src = Path.Combine(FindRepoRoot(), "src", "Jellyfin.Plugin.Shoal");
        var reads = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(static f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line)))
            .Where(static l => Regex.IsMatch(l.Line, @"\.TvShowsEnabled\b") && !l.Line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(reads); // only the declaration (PluginConfiguration) and the answer's constant `TvShowsEnabled = true` remain
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
