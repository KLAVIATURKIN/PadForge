using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class AntiDeadZoneMigrationTests(ITestOutputHelper output)
{
    static PadSetting RoundTrip(PadSetting settings)
    {
        var serializer = new XmlSerializer(typeof(PadSetting));
        using var saved = new StringWriter();
        serializer.Serialize(saved, settings);
        using var input = new StringReader(saved.ToString());
        return (PadSetting)serializer.Deserialize(input)!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void AuthoredZeroSurvivesRepeatedMigrationAndXmlReload(string initial)
    {
        var settings = new PadSetting
        {
            LeftThumbAntiDeadZone = "20", RightThumbAntiDeadZone = "30",
            LeftThumbAntiDeadZoneX = initial, LeftThumbAntiDeadZoneY = initial,
            RightThumbAntiDeadZoneX = initial, RightThumbAntiDeadZoneY = initial
        };
        settings.MigrateAntiDeadZones();
        Assert.Equal("20", settings.LeftThumbAntiDeadZoneX);
        Assert.Equal("20", settings.LeftThumbAntiDeadZoneY);
        Assert.Equal("30", settings.RightThumbAntiDeadZoneX);
        Assert.Equal("30", settings.RightThumbAntiDeadZoneY);
        settings.LeftThumbAntiDeadZoneX = settings.LeftThumbAntiDeadZoneY = "0";
        settings.RightThumbAntiDeadZoneX = settings.RightThumbAntiDeadZoneY = "0";
        for (int i = 0; i < 3; i++)
        {
            settings = RoundTrip(settings);
            settings.MigrateAntiDeadZones();
            output.WriteLine($"pass={i} left={settings.LeftThumbAntiDeadZoneX},{settings.LeftThumbAntiDeadZoneY} right={settings.RightThumbAntiDeadZoneX},{settings.RightThumbAntiDeadZoneY}");
            Assert.Equal("0", settings.LeftThumbAntiDeadZoneX);
            Assert.Equal("0", settings.LeftThumbAntiDeadZoneY);
            Assert.Equal("0", settings.RightThumbAntiDeadZoneX);
            Assert.Equal("0", settings.RightThumbAntiDeadZoneY);
        }
    }

    [Theory]
    [InlineData("12", "0")]
    [InlineData("0", "14")]
    [InlineData("12", "14")]
    public void ModernAxisValuesTakePrecedenceAndConsumeTheLegacyFallback(string x, string y)
    {
        var settings = new PadSetting
        {
            LeftThumbAntiDeadZone = "20", RightThumbAntiDeadZone = "30",
            LeftThumbAntiDeadZoneX = x, LeftThumbAntiDeadZoneY = y,
            RightThumbAntiDeadZoneX = y, RightThumbAntiDeadZoneY = x
        };
        settings.MigrateAntiDeadZones();
        Assert.Equal(x, settings.LeftThumbAntiDeadZoneX);
        Assert.Equal(y, settings.LeftThumbAntiDeadZoneY);
        Assert.Equal(y, settings.RightThumbAntiDeadZoneX);
        Assert.Equal(x, settings.RightThumbAntiDeadZoneY);
        settings.LeftThumbAntiDeadZoneX = settings.LeftThumbAntiDeadZoneY = "0";
        settings.RightThumbAntiDeadZoneX = settings.RightThumbAntiDeadZoneY = "0";
        settings.MigrateAntiDeadZones();
        Assert.Equal("0", settings.LeftThumbAntiDeadZoneX);
        Assert.Equal("0", settings.RightThumbAntiDeadZoneX);
        Assert.Equal("0", settings.LeftThumbAntiDeadZone);
        Assert.Equal("0", settings.RightThumbAntiDeadZone);
    }

    [Fact]
    public void MigratedValuesAndChecksumAreStableAcrossReloads()
    {
        var settings = new PadSetting { LeftThumbAntiDeadZone = "20", RightThumbAntiDeadZone = "30" };
        settings.MigrateAntiDeadZones();
        string checksum = settings.ComputeChecksum();
        settings = RoundTrip(settings);
        settings.MigrateAntiDeadZones();
        Assert.Equal(checksum, settings.ComputeChecksum());
        Assert.Equal("20", settings.LeftThumbAntiDeadZoneX);
        Assert.Equal("30", settings.RightThumbAntiDeadZoneY);
    }
}
