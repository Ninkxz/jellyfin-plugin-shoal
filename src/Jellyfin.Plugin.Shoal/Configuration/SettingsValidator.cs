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

    private const int MaxSocketPathChars = 260;

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
        configuration.SyncIntervalHours = Fix(configuration.SyncIntervalHours, 1, 168, nameof(configuration.SyncIntervalHours), notes);
        configuration.SpeedTestIntervalHours = Fix(configuration.SpeedTestIntervalHours, 1, 168, nameof(configuration.SpeedTestIntervalHours), notes);
        configuration.MassDeletionMinCount = Fix(configuration.MassDeletionMinCount, 1, 100_000, nameof(configuration.MassDeletionMinCount), notes);
        configuration.MassDeletionPercent = Fix(configuration.MassDeletionPercent, 1, 100, nameof(configuration.MassDeletionPercent), notes);
        configuration.NewItemsMinCount = Fix(configuration.NewItemsMinCount, 1, 100_000, nameof(configuration.NewItemsMinCount), notes);
        configuration.NewItemsPercent = Fix(configuration.NewItemsPercent, 1, 100, nameof(configuration.NewItemsPercent), notes);
        configuration.DiskQuotaGbPerPeer = Fix(configuration.DiskQuotaGbPerPeer, 1, 50, nameof(configuration.DiskQuotaGbPerPeer), notes);
        configuration.ParkAfterHours = Fix(configuration.ParkAfterHours, 1, 8760, nameof(configuration.ParkAfterHours), notes);
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

        return notes;
    }

    private static bool IsValidSocket(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSocketPathChars || value.Any(char.IsControl))
        {
            return false;
        }

        return string.Equals(value, AutoSocket, StringComparison.Ordinal) || string.Equals(value, OffSocket, StringComparison.Ordinal) || System.IO.Path.IsPathFullyQualified(value);
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
