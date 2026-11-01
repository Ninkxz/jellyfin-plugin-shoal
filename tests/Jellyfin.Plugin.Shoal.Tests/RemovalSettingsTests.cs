using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

public sealed class RemovalSettingsTests
{
    [Fact]
    public void MissingSettingAndFreshInstallStayOff()
    {
        Assert.False(new PluginConfiguration().LocalRemovalEnabled);
        Assert.False(new PluginConfiguration().PermanentDeletionEnabled);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var input = new StringReader("<PluginConfiguration />");
        var defaults = (PluginConfiguration)serializer.Deserialize(input)!;
        Assert.False(defaults.LocalRemovalEnabled);
        Assert.False(defaults.PermanentDeletionEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyAndXmlRoundTripPreserveExplicitChoice(bool enabled)
    {
        var source = new PluginConfiguration { LocalRemovalEnabled = enabled, PermanentDeletionEnabled = enabled };
        var copy = PluginSettingsAccessor.Copy(source);
        Assert.Equal(enabled, copy.LocalRemovalEnabled);
        Assert.Equal(enabled, copy.PermanentDeletionEnabled);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var output = new StringWriter();
        serializer.Serialize(output, copy);
        using var input = new StringReader(output.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(input)!;
        Assert.Equal(enabled, restored.LocalRemovalEnabled);
        Assert.Equal(enabled, restored.PermanentDeletionEnabled);
    }
}
