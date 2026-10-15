using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// W6 M-2: the banned-API list is proved by compiling a probe project against the real <c>BannedSymbols.txt</c> and the real analyzer package.
/// A ban whose ID matches nothing (the old <c>M:...set_ContentType</c>) shows up here as a missing diagnostic. Grepping the ban file proves nothing.
/// </summary>
public sealed class BannedApiProbeTests
{
    private static readonly string[] Header =
    [
        "using System;",
        "using System.IO;",
        "using System.Net.Http;",
        "using System.Text.Encodings.Web;",
        "using MediaBrowser.Controller.Library;",
        "using MediaBrowser.Controller.Net;",
        "using Microsoft.AspNetCore.Http;",
        "using Microsoft.AspNetCore.Http.Extensions;",
        "using Microsoft.AspNetCore.Mvc;",
        string.Empty,
        "public sealed class Probe : ControllerBase",
        "{",
        "    public void Run(HttpContext http, HttpRequest request, HttpResponse response, IAuthorizationContext auth, ILibraryManager lib, HttpClient client, HttpContent content, Guid id, Stream stream)",
        "    {",
    ];

    // One call per line. BANNED lines must produce RS0030, OK lines must not.
    private static readonly string[] Body =
    [
        "response.ContentType = \"text/html\"; // BANNED",
        "response.Headers[\"X-A\"] = \"b\"; // BANNED",
        "_ = response.Cookies; // BANNED",
        "response.Redirect(\"/x\"); // BANNED",
        "_ = response.GetTypedHeaders(); // BANNED",
        "_ = response.WriteAsJsonAsync(1); // BANNED",
        "_ = JavaScriptEncoder.UnsafeRelaxedJsonEscaping; // BANNED",
        "_ = File(new byte[1], \"a/b\"); // BANNED",
        "_ = File(stream, \"a/b\"); // BANNED",
        "_ = Redirect(\"/x\"); // BANNED",
        "_ = Ok(); // BANNED",
        "_ = NotFound(); // BANNED",
        "_ = StatusCode(500); // BANNED",
        "_ = new JsonResult(1); // BANNED",
        "_ = new ObjectResult(1); // BANNED",
        "_ = new OkObjectResult(1); // BANNED",
        "_ = new FileStreamResult(stream, \"a/b\"); // BANNED",
        "_ = new ContentResult { ContentType = \"text/html\" }; // BANNED",
        "_ = auth.GetAuthorizationInfo(http); // BANNED",
        "_ = lib.GetItemById(id); // BANNED",
        "_ = lib.GetItemById<MediaBrowser.Controller.Entities.BaseItem>(id, id); // BANNED",
        "_ = request.QueryString; // BANNED",
        "_ = request.Path; // BANNED",
        "_ = request.GetDisplayUrl(); // BANNED",
        "_ = content.ReadAsStringAsync(); // BANNED",
        "_ = content.ReadAsStreamAsync(); // BANNED",
        "_ = client.GetStringAsync(\"http://x\"); // BANNED",
        "_ = new Random(); // BANNED",
        "_ = new MediaBrowser.Model.Users.UserPolicy().BlockedMediaFolders; // BANNED",
        "response.StatusCode = 200; // OK",
        "_ = request.Query.Count; // OK",
        "_ = request.Method; // OK",
        "_ = new ContentResult(); // OK",
    ];

    [Fact]
    public async Task EveryBannedCall_FailsTheBuild_AndTheControlsDoNot()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(Path.GetTempPath(), "shoal-ban-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(root, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt"), Path.Combine(dir, "BannedSymbols.txt"));
            File.WriteAllText(
                Path.Combine(dir, "Probe.csproj"),
                string.Join(
                    '\n',
                    "<Project Sdk=\"Microsoft.NET.Sdk\">",
                    "  <PropertyGroup>",
                    "    <TargetFramework>net10.0</TargetFramework>",
                    "    <Nullable>enable</Nullable>",
                    "    <ImplicitUsings>disable</ImplicitUsings>",
                    "  </PropertyGroup>",
                    "  <ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup>",
                    "  <ItemGroup>",
                    "    <PackageReference Include=\"Jellyfin.Controller\" Version=\"12.1.0\" />",
                    "    <PackageReference Include=\"Jellyfin.Model\" Version=\"12.1.0\" />",
                    "    <PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" Version=\"3.3.4\" PrivateAssets=\"All\" />",
                    "  </ItemGroup>",
                    "  <ItemGroup><AdditionalFiles Include=\"BannedSymbols.txt\" /></ItemGroup>",
                    "</Project>"));
            var source = new StringBuilder();
            foreach (var line in Header)
            {
                source.Append(line).Append('\n');
            }

            foreach (var line in Body)
            {
                source.Append("        ").Append(line).Append('\n');
            }

            source.Append("    }\n}\n");
            File.WriteAllText(Path.Combine(dir, "Probe.cs"), source.ToString());

            var output = await BuildAsync(dir);

            Assert.DoesNotContain("error CS", output, StringComparison.Ordinal); // the probe itself must compile
            var flagged = Regex.Matches(output, @"Probe\.cs\((\d+),\d+\): (?:warning|error) RS0030")
                .Select(static m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToHashSet();
            Assert.NotEmpty(flagged); // the build ran and the analyzer was loaded
            var missing = new List<string>();
            var wrong = new List<string>();
            for (var i = 0; i < Body.Length; i++)
            {
                var lineNumber = Header.Length + i + 1;
                var banned = Body[i].EndsWith("// BANNED", StringComparison.Ordinal);
                if (banned && !flagged.Contains(lineNumber))
                {
                    missing.Add(Body[i]);
                }
                else if (!banned && flagged.Contains(lineNumber))
                {
                    wrong.Add(Body[i]);
                }
            }

            Assert.True(missing.Count == 0, "These calls compiled although they are banned (the ID matches nothing):\n" + string.Join('\n', missing));
            Assert.True(wrong.Count == 0, "These control calls were rejected:\n" + string.Join('\n', wrong));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // a lingering build server may hold a file; the temp folder is harmless
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.Shoal", "BannedSymbols.txt")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private static async Task<string> BuildAsync(string dir)
    {
        var info = new ProcessStartInfo("dotnet", "build Probe.csproj -nologo -nodeReuse:false -p:UseSharedCompilation=false -p:TreatWarningsAsErrors=false -clp:NoSummary")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4));
        await process.WaitForExitAsync(cts.Token);
        return await stdout + await stderr;
    }
}
