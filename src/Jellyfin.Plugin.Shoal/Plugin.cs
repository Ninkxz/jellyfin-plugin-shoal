using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Shoal.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Shoal;

/// <summary>
/// Shoal for Jellyfin: share libraries between friends' Jellyfin servers.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The stable plugin identifier.
    /// </summary>
    public const string PluginId = "b3d48170-08c3-4272-8042-efb38df9bb24";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Core resets the XML to defaults when it cannot read it; whatever it loaded is brought into range here (never throws).
        SettingsNotes = SettingsValidator.Apply(Configuration);
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the adjustments <see cref="SettingsValidator"/> made to the loaded XML toggles at startup (empty when none).
    /// </summary>
    public IReadOnlyList<string> SettingsNotes { get; }

    /// <inheritdoc />
    public override string Name => "Shoal";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Share libraries between friends' Jellyfin servers. Nothing copied, nothing exposed.";

    /// <summary>
    /// Gets or sets the work to do just before the plugin is uninstalled: give "all libraries" back to the accounts Shoal manages
    /// (DESIGN 5.6, W2 N-13). Set by the visibility host while it runs.
    /// </summary>
    internal static Action? UninstallHook { get; set; }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        try
        {
            UninstallHook?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: the uninstall must go on.
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Every admin write is clamped before it is saved.
        SettingsValidator.Apply(configuration as PluginConfiguration);
        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // One dashboard page (markup), plus its controller and stylesheet served the same way (docs/ui-spec.md 1.1).
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                MenuIcon = "share",
                EmbeddedResourcePath = ResourcePath("shoal.html")
            },
            new PluginPageInfo
            {
                Name = Name + ".js",
                EmbeddedResourcePath = ResourcePath("shoal.js")
            },
            new PluginPageInfo
            {
                Name = Name + ".css",
                EmbeddedResourcePath = ResourcePath("shoal.css")
            }
        ];
    }

    private string ResourcePath(string file) => string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.{1}", GetType().Namespace, file);
}
