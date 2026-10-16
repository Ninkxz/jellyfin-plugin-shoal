using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Shoal.Configuration;

/// <summary>
/// Clamps the XML toggles into their valid ranges (DESIGN 3.2, component 3).
/// <para>
/// It <b>never throws</b>: core replaces the whole plugin XML with defaults when loading it fails, and a validator that threw
/// during load would trigger exactly that. It repairs values and reports what it did.
/// </para>
/// </summary>
public static class SettingsValidator
{
    /// <summary>The value of <see cref="PluginConfiguration.TailscaleSocket"/> that means "look in the usual places".</summary>
    public const string AutoSocket = "auto";

    /// <summary>The value of <see cref="PluginConfiguration.TailscaleSocket"/> that means "do not read Tailscale's status" (docs/ui-spec.md 8.5.1).</summary>
    public const string OffSocket = "off";

    /// <summary>The default request service name.</summary>
    public const string DefaultServiceName = "Seerr";

    private const int MaxSocketPathChars = 260;
    private const int MaxServiceUrlChars = 200;

    /// <summary>
    /// Brings <paramref name="configuration"/> into range, in place.
    /// </summary>
    /// <param name="configuration">The configuration; null is ignored.</param>
    /// <returns>One short note per adjustment (setting names and ranges, never values), empty when nothing changed.</returns>
    public static IReadOnlyList<string> Apply(PluginConfiguration? configuration)
    {
        var notes = new List<string>();
        if (configuration is null)
        {
            return notes;
        }

        configuration.SchemaVersion = Fix(configuration.SchemaVersion, PluginConfiguration.CurrentSchemaVersion, PluginConfiguration.CurrentSchemaVersion, nameof(configuration.SchemaVersion), notes);
        configuration.MaxStreamsPerUser = Fix(configuration.MaxStreamsPerUser, 1, 4, nameof(configuration.MaxStreamsPerUser), notes);
        configuration.HlsBufferAhead = Fix(configuration.HlsBufferAhead, 2, 20, nameof(configuration.HlsBufferAhead), notes);
        configuration.SyncIntervalHours = Fix(configuration.SyncIntervalHours, 1, 168, nameof(configuration.SyncIntervalHours), notes);
        configuration.SpeedTestIntervalHours = Fix(configuration.SpeedTestIntervalHours, 1, 168, nameof(configuration.SpeedTestIntervalHours), notes);
        configuration.MassDeletionMinCount = Fix(configuration.MassDeletionMinCount, 1, 100_000, nameof(configuration.MassDeletionMinCount), notes);
        configuration.MassDeletionPercent = Fix(configuration.MassDeletionPercent, 1, 100, nameof(configuration.MassDeletionPercent), notes);
        configuration.NewItemsMinCount = Fix(configuration.NewItemsMinCount, 1, 100_000, nameof(configuration.NewItemsMinCount), notes);
        configuration.NewItemsPercent = Fix(configuration.NewItemsPercent, 1, 100, nameof(configuration.NewItemsPercent), notes);
        configuration.DiskQuotaGbPerPeer = Fix(configuration.DiskQuotaGbPerPeer, 1, 50, nameof(configuration.DiskQuotaGbPerPeer), notes);
        configuration.ParkAfterHours = FixHours(configuration.ParkAfterHours, 0.1, 8760, nameof(configuration.ParkAfterHours), notes);
        configuration.TrickplayCacheGb = Fix(configuration.TrickplayCacheGb, 1, 20, nameof(configuration.TrickplayCacheGb), notes);

        if (!Enum.IsDefined(configuration.ArtQuality))
        {
            configuration.ArtQuality = ArtQuality.Standard;
            notes.Add(nameof(configuration.ArtQuality) + " was not a known quality and was reset.");
        }

        if (!IsValidSocket(configuration.TailscaleSocket))
        {
            configuration.TailscaleSocket = AutoSocket;
            notes.Add(nameof(configuration.TailscaleSocket) + " was not 'auto', 'off' or an absolute path and was reset.");
        }

        if (!IsValidServiceName(configuration.RequestServiceName))
        {
            configuration.RequestServiceName = DefaultServiceName;
            notes.Add(nameof(configuration.RequestServiceName) + " was not 1 to 30 letters, digits, spaces or dots and was reset.");
        }

        if (!IsValidServiceUrl(configuration.RequestServiceUrl))
        {
            configuration.RequestServiceUrl = string.Empty;
            notes.Add(nameof(configuration.RequestServiceUrl) + " was not an http or https address and was cleared.");
        }

        return notes;
    }

    /// <summary>
    /// Whether a request service name is plain words: 1 to 30 letters, digits, spaces or dots (it is drawn into a video and shown to users).
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>True when it is acceptable.</returns>
    public static bool IsValidServiceName(string? value) =>
        value is { Length: >= 1 and <= 30 } && value.Trim().Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.');

    /// <summary>
    /// Whether a request service address is empty or an absolute http or https address of at most 200 characters, with no login, spaces or control characters.
    /// </summary>
    /// <param name="value">The address.</param>
    /// <returns>True when it is acceptable.</returns>
    public static bool IsValidServiceUrl(string? value) =>
        string.IsNullOrEmpty(value)
        || (value.Length <= MaxServiceUrlChars && value.All(static c => c > ' ' && c < '\u007f')
            && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0);

    private static bool IsValidSocket(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSocketPathChars || value.Any(char.IsControl))
        {
            return false;
        }

        return string.Equals(value, AutoSocket, StringComparison.Ordinal) || string.Equals(value, OffSocket, StringComparison.Ordinal) || System.IO.Path.IsPathFullyQualified(value);
    }

    // Hours may be fractional; a value that is not a number at all (NaN) is reset to the minimum.
    private static double FixHours(double value, double min, double max, string name, List<string> notes)
    {
        var fixedValue = double.IsNaN(value) ? min : Math.Clamp(value, min, max);
        if (fixedValue != value)
        {
            notes.Add(name + " was outside " + min.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." + max.ToString(System.Globalization.CultureInfo.InvariantCulture) + " and was adjusted.");
        }

        return fixedValue;
    }

    private static int Fix(int value, int min, int max, string name, List<string> notes)
    {
        var fixedValue = Math.Clamp(value, min, max);
        if (fixedValue != value)
        {
            notes.Add($"{name} was outside {min} to {max} and was adjusted.");
        }

        return fixedValue;
    }
}
