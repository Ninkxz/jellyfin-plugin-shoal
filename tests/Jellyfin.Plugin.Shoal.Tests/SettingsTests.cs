using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.Shoal.Configuration;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// K1: the plugin XML holds toggles only, out-of-range values are clamped without throwing, and T-REG-2 (core replaces the XML
/// with defaults: peers, access modes and the hardening mode stay intact).
/// </summary>
public sealed class SettingsTests
{
    private static readonly XmlSerializer Xml = new(typeof(PluginConfiguration));

    private static PluginConfiguration FromXml(string xml)
    {
        using var reader = new StringReader(xml);
        return (PluginConfiguration)Xml.Deserialize(reader)!;
    }

    private static string ToXml(PluginConfiguration config)
    {
        using var writer = new StringWriter();
        Xml.Serialize(writer, config);
        return writer.ToString();
    }

    // What core does when it cannot read the file (BasePlugin<T>.LoadConfiguration): swallow the error and use defaults.
    private static PluginConfiguration LoadLikeCore(string xml)
    {
        try
        {
            return FromXml(xml);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            return new PluginConfiguration();
        }
    }

    [Fact]
    public void Defaults_MatchDesign_3_2()
    {
        var c = new PluginConfiguration();

        Assert.Equal(2, c.SchemaVersion);
        Assert.True(c.AutoFitQualityToLink);
        Assert.Equal(4, c.MaxStreamsPerUser);
        Assert.Equal(6, c.SyncIntervalHours);
        Assert.Equal(6, c.SpeedTestIntervalHours);
        Assert.Equal(25, c.MassDeletionMinCount);
        Assert.Equal(10, c.MassDeletionPercent);
        Assert.Equal(500, c.NewItemsMinCount);
        Assert.Equal(20, c.NewItemsPercent);
        Assert.Equal(2, c.DiskQuotaGbPerPeer);
        Assert.Equal(ArtQuality.Standard, c.ArtQuality);
        Assert.False(c.ParkOfflinePeers);
        Assert.Equal(0.5, c.ParkAfterHours);
        Assert.True(c.NotifyClientsWhenOffline);
        Assert.True(c.HlsSubtitlesEnabled);
        Assert.Equal("auto", c.TailscaleSocket);
        Assert.False(c.AlternateVersionsEnabled); // off until the lab gate T-ALT-0 has passed
        Assert.True(c.TrickplayEnabled);
        Assert.Equal(1, c.TrickplayCacheGb);
        Assert.True(c.AttachmentsEnabled);
        Assert.True(c.ReencodeImages);
        Assert.Empty(SettingsValidator.Apply(c));
    }

    [Fact]
    public void TheXmlHoldsToggles_AndNothingElse()
    {
        // Adding a member here is a decision: it must not be a secret, topology or a security choice (DESIGN 3.2).
        var members = typeof(PluginConfiguration).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AlternateVersionsEnabled", "ArtQuality", "AttachmentsEnabled", "AutoFitQualityToLink", "DetectableByFriends", "DiskQuotaGbPerPeer", "HlsBufferAhead", "HlsSubtitlesEnabled", "MassDeletionMinCount", "MassDeletionPercent",
                "MaxStreamsPerUser", "NewItemsMinCount", "NewItemsPercent", "NotifyClientsWhenOffline", "OfflineCardEnabled", "ParkAfterHours", "ParkOfflinePeers", "ReencodeImages",
                "RequestServiceName", "RequestServiceUrl", "SchemaVersion", "SpeedTestIntervalHours", "SyncIntervalHours", "TailscaleSocket", "TrickplayCacheGb", "TrickplayEnabled", "TvEpisodeThumbs", "TvShowsEnabled"
            }.Order(StringComparer.Ordinal),
            members);
        Assert.DoesNotContain(members, m => m.Contains("Token", StringComparison.OrdinalIgnoreCase) || m.Contains("Secret", StringComparison.OrdinalIgnoreCase) || m is "Peers" or "StubRoot" or "Url");
    }

    [Fact]
    public void ASpikeXml_WithPeersAndAToken_LoadsWithoutError_AndTheTokenIsGone()
    {
        const string spike = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Peers>
                <PeerConfiguration>
                  <Id>peer</Id><Name>jf-peer</Name><Url>http://jf-peer:8096</Url><Token>SPIKE-TOKEN-0123456789</Token>
                  <DeviceId>shoal-jfhome-lab</DeviceId><Enabled>true</Enabled>
                </PeerConfiguration>
              </Peers>
              <StubRoot>/config/shoal-stubs</StubRoot>
            </PluginConfiguration>
            """;

        var config = FromXml(spike);
        var rewritten = ToXml(config);

        Assert.DoesNotContain("SPIKE-TOKEN", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("<Peers", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("shoal-stubs", rewritten, StringComparison.Ordinal);
        Assert.Equal(2, config.SchemaVersion);
    }

    [Fact]
    public void TheSerializedXml_NeverContainsASecret()
    {
        var config = new PluginConfiguration { TailscaleSocket = "/var/run/tailscale/tailscaled.sock", MaxStreamsPerUser = 3 };

        var text = ToXml(config);

        Assert.DoesNotContain("oken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OutOfRangeXml_IsClampedWithoutThrowing()
    {
        var config = FromXml("""
            <PluginConfiguration>
              <SchemaVersion>9</SchemaVersion>
              <MaxStreamsPerUser>-4</MaxStreamsPerUser>
              <SyncIntervalHours>0</SyncIntervalHours>
              <SpeedTestIntervalHours>99999</SpeedTestIntervalHours>
              <MassDeletionMinCount>-1</MassDeletionMinCount>
              <MassDeletionPercent>9999</MassDeletionPercent>
              <NewItemsMinCount>2147483647</NewItemsMinCount>
              <NewItemsPercent>0</NewItemsPercent>
              <DiskQuotaGbPerPeer>-2147483648</DiskQuotaGbPerPeer>
              <ParkAfterHours>100000</ParkAfterHours>
              <TailscaleSocket>relative/socket</TailscaleSocket>
            </PluginConfiguration>
            """);

        var notes = SettingsValidator.Apply(config);

        Assert.Equal(2, config.SchemaVersion);
        Assert.Equal(1, config.MaxStreamsPerUser);
        Assert.Equal(1, config.SyncIntervalHours);
        Assert.Equal(168, config.SpeedTestIntervalHours);
        Assert.Equal(1, config.MassDeletionMinCount);
        Assert.Equal(100, config.MassDeletionPercent);
        Assert.Equal(100_000, config.NewItemsMinCount);
        Assert.Equal(1, config.NewItemsPercent);
        Assert.Equal(1, config.DiskQuotaGbPerPeer);
        Assert.Equal(8760, config.ParkAfterHours);
        Assert.Equal("auto", config.TailscaleSocket);
        Assert.Equal(11, notes.Count);
        Assert.All(notes, n => Assert.DoesNotContain("9999", n, StringComparison.Ordinal)); // names and ranges, not values
    }

    [Fact]
    public void AnUndefinedEnumValue_IsReset()
    {
        var config = new PluginConfiguration { ArtQuality = (ArtQuality)7 };

        var notes = SettingsValidator.Apply(config);

        Assert.Equal(ArtQuality.Standard, config.ArtQuality);
        Assert.Single(notes);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("off", "off")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData("rel/path.sock", "auto")]
    [InlineData("/ok/path.sock", "/ok/path.sock")]
    public void TailscaleSocket_IsAutoOrAnAbsolutePath(string given, string expected)
    {
        if (!OperatingSystem.IsWindows() || !expected.StartsWith('/'))
        {
            var config = new PluginConfiguration { TailscaleSocket = given };

            SettingsValidator.Apply(config);

            Assert.Equal(expected, config.TailscaleSocket);
        }
    }

    [Fact]
    public void ControlCharactersAndHugePaths_InTheSocketSetting_AreRejected()
    {
        var withControl = new PluginConfiguration { TailscaleSocket = "/tmp/a\nb.sock" };
        var huge = new PluginConfiguration { TailscaleSocket = "/" + new string('a', 5000) };

        SettingsValidator.Apply(withControl);
        SettingsValidator.Apply(huge);

        Assert.Equal("auto", withControl.TailscaleSocket);
        Assert.Equal("auto", huge.TailscaleSocket);
    }

    [Fact]
    public void TheValidator_NeverThrows_OnExtremeValues()
    {
        var values = new[] { int.MinValue, -1, 0, 1, int.MaxValue };
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                var config = new PluginConfiguration
                {
                    SchemaVersion = a,
                    MaxStreamsPerUser = b,
                    SyncIntervalHours = a,
                    SpeedTestIntervalHours = b,
                    MassDeletionMinCount = a,
                    MassDeletionPercent = b,
                    NewItemsMinCount = a,
                    NewItemsPercent = b,
                    DiskQuotaGbPerPeer = a,
                    ParkAfterHours = b,
                    ArtQuality = (ArtQuality)a,
                    TailscaleSocket = null!
                };

                var exception = Record.Exception(() => SettingsValidator.Apply(config));

                Assert.Null(exception);
                Assert.Empty(SettingsValidator.Apply(config)); // after one pass the configuration is stable
            }
        }

        Assert.Empty(SettingsValidator.Apply(null));
    }

    [Fact]
    public async Task TREG2_CoreReplacesTheXmlWithDefaults_PeersAccessModesAndTheHardeningModeSurvive()
    {
        using var dir = new TempDirectory();
        var paths = new ShoalPaths(dir.Path);
        using (var store = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null))
        {
            await store.UpdateAsync(StateSamples.Fill);
        }

        // Core cannot read the (corrupted) plugin XML, so it falls back to defaults.
        var xmlPath = Path.Combine(dir.Path, "Jellyfin.Plugin.Shoal.xml");
        File.WriteAllText(xmlPath, "<PluginConfiguration><MaxStreamsPerUser>not a number</MaxStreamsPerUser>");
        var afterReset = LoadLikeCore(File.ReadAllText(xmlPath));
        Assert.Equal(new PluginConfiguration().MaxStreamsPerUser, afterReset.MaxStreamsPerUser);

        using var restarted = new StateStore(paths, NullLogger.Instance, TimeProvider.System, null);

        Assert.Equal(StateStatus.Ready, restarted.Status);
        var peer = Assert.Single(restarted.Current.Home.Peers);
        Assert.Equal(StateSamples.PeerKeyA, peer.PeerKey);
        Assert.Equal(AccessMode.Everyone, peer.Libraries[0].Access.Mode);
        Assert.Equal([StateSamples.AdminUser, StateSamples.OtherUser], peer.Libraries[0].Access.SelectedUserIds);
        Assert.Equal(HardeningMode.Enforce, restarted.Current.PeerRole.HardeningMode);
        Assert.Single(restarted.Current.PeerRole.Shares);
    }

    [Fact]
    public void TheStateFile_IsIndependentOfTheXmlFile_ByLocationAndByName()
    {
        var paths = new ShoalPaths(Path.Combine(Path.GetTempPath(), "jf", "data"));

        Assert.EndsWith(Path.Combine("data", "shoal", "state.json"), paths.StateFile, StringComparison.Ordinal);
        Assert.DoesNotContain("plugins", paths.StateFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configurations", paths.StateFile, StringComparison.OrdinalIgnoreCase);
        _ = Encoding.UTF8;
    }
}
