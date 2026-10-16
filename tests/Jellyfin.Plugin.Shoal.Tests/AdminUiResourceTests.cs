using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// U1 (docs/ui-spec.md 1, 1.3, 15 F1): the admin page ships as three embedded files and is CSP-ready, and nothing in
/// it can render server data as markup. The browser-side behaviour (T-SEC-11 and the copy checks) is covered by
/// ui-dev/tests, which runs the same files under jsdom against the mock admin API.
/// </summary>
public sealed class AdminUiResourceTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static string Read(string file)
    {
        var name = "Jellyfin.Plugin.Shoal.Configuration." + file;
        using var stream = PluginAssembly.GetManifestResourceStream(name);
        Assert.True(stream is not null, "embedded resource missing: " + name);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("shoal.html")]
    [InlineData("shoal.js")]
    [InlineData("shoal.css")]
    public void ThePageIsThreeEmbeddedFiles(string file)
    {
        Assert.False(string.IsNullOrWhiteSpace(Read(file)));
    }

    [Fact]
    public void TheSpikePageIsGone()
    {
        Assert.DoesNotContain(PluginAssembly.GetManifestResourceNames(), n => n.EndsWith("configPage.html", StringComparison.Ordinal));
        var all = string.Join('\n', PluginAssembly.GetManifestResourceNames());
        Assert.DoesNotContain("getPluginConfiguration", Read("shoal.js"), StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", Read("shoal.js"), StringComparison.Ordinal);
        Assert.NotEmpty(all);
    }

    // ---------------- F1: the HTML ----------------

    [Fact]
    public void Html_HasNoScriptNoHandlersNoStyleAttributeNoTemplateSyntax()
    {
        var html = Read("shoal.html");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${", html, StringComparison.Ordinal); // globalize.translateHtml rewrites every ${...}
        Assert.False(Regex.IsMatch(html, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase), "inline event handler");
        Assert.False(Regex.IsMatch(html, @"\sstyle\s*=", RegexOptions.IgnoreCase), "style attribute");
        Assert.False(Regex.IsMatch(html, @"<style", RegexOptions.IgnoreCase), "style element");
    }

    [Fact]
    public void Html_LoadsOnlyItsOwnStylesheet()
    {
        var html = Read("shoal.html");
        var links = Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase);
        Assert.Single(links);
        Assert.Contains("configurationpage?name=Shoal.css", links[0].Value, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(html, @"(src|href)\s*=\s*""https?:", RegexOptions.IgnoreCase), "external reference");
        Assert.Contains("data-controller=\"__plugin/Shoal.js\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_HasTheShellSlots()
    {
        var html = Read("shoal.html");
        Assert.Contains("class=\"shoal-banners\"", html, StringComparison.Ordinal); // ui-spec 1.2 / 6.16
        Assert.Contains("data-slot=\"live\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<main", html, StringComparison.OrdinalIgnoreCase); // the dashboard already has one
    }

    // ---------------- the CSS ----------------

    [Fact]
    public void Css_UsesOnlyTheTwoBundledFontWeights()
    {
        var css = Read("shoal.css");
        var weights = Regex.Matches(css, @"font-weight\s*:\s*([^;}\s]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(weights);
        Assert.All(weights, w => Assert.Contains(w, new[] { "400", "700", "normal", "bold" }));
        Assert.DoesNotContain("@font-face", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(css, @"url\(", RegexOptions.IgnoreCase), "no url(): no fonts, no images, no network");
    }

    [Fact]
    public void Css_PrimaryButtonIsTheContrastFixedBlue()
    {
        var css = Read("shoal.css");
        Assert.Contains("--shoal-fill: #0a7cab", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--shoal-on-fill: #ffffff", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--shoal-fill-hover: #086c95", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--shoal-fill-active: #075a7d", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Css_NeverFixesThePageHeightOrScrollsInside()
    {
        var css = Read("shoal.css");
        foreach (var rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selector = ((Match)rule).Groups[1].Value;
            var body = ((Match)rule).Groups[2].Value;
            if (selector.Contains(".shoal-root", StringComparison.Ordinal) || selector.Contains(".shoal-frame", StringComparison.Ordinal) || selector.Contains(".shoal-view", StringComparison.Ordinal))
            {
                Assert.False(Regex.IsMatch(body, @"(^|[;\s])(height|max-height)\s*:"), "fixed height on " + selector.Trim());
                Assert.False(Regex.IsMatch(body, @"overflow(-y)?\s*:\s*(auto|scroll)"), "inner scroll area on " + selector.Trim());
            }
        }
    }

    [Fact]
    public void Css_IsScopedToThePage()
    {
        var css = Regex.Replace(Read("shoal.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (Match rule in Regex.Matches(css, @"(^|\})\s*([^{}@]+)\{"))
        {
            foreach (var selector in Regex.Split(rule.Groups[2].Value, @",(?![^(]*\))"))
            {
                var s = selector.Trim();
                if (s.Length == 0 || s.StartsWith("from", StringComparison.Ordinal) || s.StartsWith("to", StringComparison.Ordinal) || s.EndsWith('%'))
                {
                    continue;
                }

                Assert.True(
                    s.StartsWith(".shoalPage", StringComparison.Ordinal) || s.StartsWith("html[data-theme", StringComparison.Ordinal),
                    "selector escapes the page scope: " + s);
            }
        }
    }

    [Fact]
    public void Css_HasASignalSetForLightThemesAndReducedMotion()
    {
        var css = Read("shoal.css");
        Assert.Contains("html[data-theme=\"light\"] .shoalPage", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", css, StringComparison.Ordinal);
        Assert.Contains("@container shoal", css, StringComparison.Ordinal);
    }

    // ---------------- the controller ----------------

    [Fact]
    public void Js_NeverBuildsMarkupFromStrings()
    {
        var js = Read("shoal.js");
        foreach (var banned in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "setAttribute('style'", "style.cssText", "createContextualFragment", "DOMParser" })
        {
            Assert.DoesNotContain(banned, js, StringComparison.Ordinal);
        }

        Assert.False(Regex.IsMatch(js, @"set(Timeout|Interval)\(\s*['""]"), "string timers");
        Assert.False(Regex.IsMatch(js, @"\.on[a-z]+\s*="), "DOM0 event handler assignment");
    }

    [Fact]
    public void Js_OnlySizesThingsThroughShoalCustomProperties()
    {
        var js = Read("shoal.js");
        // style is touched in exactly one place: setVar() -> style.setProperty('--shoal-...').
        var uses = Regex.Matches(js, @"\.style\b").Count;
        Assert.Equal(1, uses);
        Assert.Contains("node.style.setProperty(name, String(value))", js, StringComparison.Ordinal);
        Assert.Contains("if (!name.startsWith('--shoal-'))", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Js_TalksOnlyToTheShoalAdminApi()
    {
        var js = Read("shoal.js");
        Assert.Contains("const API_PREFIX = 'Shoal/Admin/'", js, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", js, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", js, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocket", js, StringComparison.Ordinal);
        Assert.DoesNotContain("sendBeacon", js, StringComparison.Ordinal);
        var addresses = Regex.Matches(js, @"['""]https?://[a-z0-9][^'""]*", RegexOptions.IgnoreCase).Select(m => m.Value.Trim('\'', '"')).ToList();
        Assert.Equal(new[] { "http://www.w3.org/2000/svg", "https://github.com/Ninkxz/jellyfin-plugin-shoal" }, addresses); // the SVG namespace, and the install link shown to a friend without Shoal (a link the admin may follow, never a request)
    }

    [Fact]
    public void Js_NeverLogsAndNeverStoresSecrets()
    {
        var js = Read("shoal.js");
        Assert.DoesNotContain("console.", js, StringComparison.Ordinal);
        Assert.DoesNotContain("debugger", js, StringComparison.Ordinal);
        // The only persisted browser state is two per-viewer conveniences, always through safeStorage().
        var keys = Regex.Matches(js, @"const [A-Z_]*KEY = '([^']+)'").Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "shoal.selected" }.OrderBy(x => x, StringComparer.Ordinal).ToList(), keys);
        Assert.Single(Regex.Matches(js, @"window\.localStorage\.setItem"));
        Assert.DoesNotContain("sessionStorage", js, StringComparison.Ordinal);
        Assert.DoesNotContain("indexedDB", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Js_IsAnEsModuleWithADefaultController()
    {
        var js = Read("shoal.js");
        Assert.Contains("export default function ShoalController(view, params)", js, StringComparison.Ordinal);
    }
}
