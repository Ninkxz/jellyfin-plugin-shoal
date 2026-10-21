using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Golden JSON for the admin routes shoal.js depends on (tests/contracts/). A rendered answer is compared by shape with its checked-in file: the same camelCase
/// field names at every level, the same JSON kind for every value (string, number, bool, null, object, array) and the same array lengths. Scalar values are not
/// compared (ids, hashes and counts differ per run). So a backend rename, a dropped field or a new field fails the test instead of silently breaking the page.
/// <para>
/// To change a shape on purpose: run the tests with <c>SHOAL_UPDATE_GOLDENS=1</c> (PowerShell <c>$env:SHOAL_UPDATE_GOLDENS=1; dotnet test</c>), review the diff
/// of tests/contracts/ in git, and commit it with the change (and the matching shoal.js / ui-dev mock edit). The saved file has ids, hashes, times and temp
/// paths replaced by placeholders.
/// </para>
/// </summary>
internal static partial class ContractGolden
{
    /// <summary>The environment variable that rewrites the golden files instead of comparing.</summary>
    public const string UpdateVariable = "SHOAL_UPDATE_GOLDENS";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // test files only: keeps "<id>" readable

    /// <summary>Gets the folder of the golden files (tests/contracts in the source tree, so an update lands where git sees it).</summary>
    public static string Folder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Shoal.sln")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir is not null, "Jellyfin.Shoal.sln not found above " + AppContext.BaseDirectory);
            return Path.Combine(dir!.FullName, "tests", "contracts");
        }
    }

    /// <summary>Compares a rendered answer with tests/contracts/<paramref name="name"/>.json (or rewrites it when <see cref="UpdateVariable"/> is 1).</summary>
    /// <param name="name">The golden file name without extension, e.g. <c>settings.get</c>.</param>
    /// <param name="json">The body as the client receives it.</param>
    public static void Match(string name, string json)
    {
        var actual = Normalize(JsonNode.Parse(json));
        var path = Path.Combine(Folder, name + ".json");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, (actual?.ToJsonString(Indented) ?? "null") + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"No golden file {path}. Create it on purpose with {UpdateVariable}=1, review it, and commit it.");
        var expected = JsonNode.Parse(File.ReadAllText(path));
        var diffs = new List<string>();
        Compare("$", expected, actual, diffs);
        Assert.True(diffs.Count == 0, $"{name}: the answer no longer matches tests/contracts/{name}.json (shoal.js reads these fields). If the change is deliberate, rerun with {UpdateVariable}=1 and update shoal.js and the mock.\n" + string.Join('\n', diffs));
    }

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            var k => k.ToString(),
        },
        _ => "unknown",
    };

    private static void Compare(string at, JsonNode? expected, JsonNode? actual, List<string> diffs)
    {
        var (ek, ak) = (Kind(expected), Kind(actual));
        if (ek != ak)
        {
            diffs.Add($"{at}: expected {ek}, got {ak}");
            return;
        }

        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var missing in eo.Select(static p => p.Key).Except(ao.Select(static p => p.Key), StringComparer.Ordinal))
            {
                diffs.Add($"{at}.{missing}: missing");
            }

            foreach (var extra in ao.Select(static p => p.Key).Except(eo.Select(static p => p.Key), StringComparer.Ordinal))
            {
                diffs.Add($"{at}.{extra}: not in the golden file");
            }

            foreach (var (key, value) in eo)
            {
                if (ao.TryGetPropertyValue(key, out var other))
                {
                    Compare(at + "." + key, value, other, diffs);
                }
            }
        }
        else if (expected is JsonArray ea && actual is JsonArray aa)
        {
            if (ea.Count != aa.Count)
            {
                diffs.Add($"{at}: expected {ea.Count} items, got {aa.Count}");
            }

            for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++)
            {
                Compare($"{at}[{i}]", ea[i], aa[i], diffs);
            }
        }
    }

    // Placeholders for what differs per run, so the saved file is stable and readable (and usable by the UI mock).
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    copy[key] = Normalize(value);
                }

                return copy;
            case JsonArray a:
                return new JsonArray([.. a.Select(Normalize)]);
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                var text = v.GetValue<string>();
                if (Guid.TryParse(text, out _))
                {
                    return JsonValue.Create("<id>");
                }

                if (HexRegex().IsMatch(text))
                {
                    return JsonValue.Create("<hex>");
                }

                if (TimeRegex().IsMatch(text))
                {
                    return JsonValue.Create("<time>");
                }

                var temp = Path.GetTempPath().TrimEnd('\\', '/');
                return text.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || text.StartsWith(temp.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ? JsonValue.Create("<tmp>") : JsonValue.Create(text);
            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex("^[0-9a-f]{16,64}$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")]
    private static partial Regex TimeRegex();
}
