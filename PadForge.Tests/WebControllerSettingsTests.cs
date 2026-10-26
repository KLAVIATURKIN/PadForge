using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

/// <summary>Protocol and optional HTTP access settings survive the real save
/// and load, and the access code is stored encrypted.</summary>
public partial class ProfileServiceToggleTests
{
    [Fact]
    public void WebProtocolAndAccessSettingsSurviveTheActualSaveAndLoad()
    {
        var (vm, settings) = Arrange();
        var d = vm.Dashboard;
        d.WebControllerUseHttps = true;
        d.WebControllerPort = 9123;
        d.WebControllerUseAccessCode = true;
        string code = d.WebControllerAccessCode;

        var built = BuildApp(settings);
        Assert.True(built.WebControllerUseHttps);
        Assert.Equal(9123, built.WebControllerPort);
        Assert.True(built.WebControllerUseAccessCode);
        Assert.NotNull(built.WebControllerAccessCodeProtected);
        Assert.DoesNotContain(code, built.WebControllerAccessCodeProtected);

        var loaded = RoundTripApp(built);
        var (vm2, settings2) = Arrange();
        Assert.NotEqual(code, vm2.Dashboard.WebControllerAccessCode);
        LoadApp(settings2, loaded);
        Assert.True(vm2.Dashboard.WebControllerUseHttps);
        Assert.Equal(9123, vm2.Dashboard.WebControllerPort);
        Assert.True(vm2.Dashboard.WebControllerUseAccessCode);
        Assert.Equal(code, vm2.Dashboard.WebControllerAccessCode);

        // An unchanged code keeps its encrypted form, so a save does not
        // rewrite it. A new code gets a new one.
        Assert.Equal(loaded.WebControllerAccessCodeProtected, BuildApp(settings2).WebControllerAccessCodeProtected);
        vm2.Dashboard.NewWebAccessCodeCommand.Execute(null);
        Assert.NotEqual(code, vm2.Dashboard.WebControllerAccessCode);
        Assert.Equal(vm2.Dashboard.WebControllerAccessCode,
            WebControllerAccess.UnprotectFromStorage(BuildApp(settings2).WebControllerAccessCodeProtected));
    }

    [Fact]
    public void OlderSettingsDefaultToHttpWithoutAccessControl()
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var reader = new StringReader(
            "<AppSettingsData><EnableWebController>true</EnableWebController><WebControllerPort>8080</WebControllerPort></AppSettingsData>");
        var old = (AppSettingsData)serializer.Deserialize(reader);
        Assert.False(old.WebControllerUseHttps);
        Assert.Equal(8080, old.WebControllerPort);
        Assert.False(old.WebControllerUseAccessCode);
        Assert.Null(old.WebControllerAccessCodeProtected);

        var (vm, settings) = Arrange();
        string fresh = vm.Dashboard.WebControllerAccessCode;
        Assert.True(WebControllerAccess.IsValid(fresh));
        LoadApp(settings, old);
        Assert.False(vm.Dashboard.WebControllerUseHttps);
        Assert.Equal(fresh, vm.Dashboard.WebControllerAccessCode);
    }

    /// <summary>A file with no usable code (older, damaged, or from another
    /// PC) loads with the fresh code, and the reload re-arms the save after
    /// its own dirty clear, or that code would be replaced on every launch.
    /// A file with a usable code leaves nothing to save.</summary>
    [Fact]
    public void AReloadWithNoUsableCodeSavesTheCodeInUse()
    {
        var (_, settings) = Arrange();
        string path = Path.Combine(Path.GetTempPath(), "PadForgeWebAccess_" + System.Guid.NewGuid().ToString("N") + ".xml");
        var pathField = typeof(SettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            WriteSettingsFile(path, new AppSettingsData());
            pathField.SetValue(settings, path);
            settings.Reload();
            Assert.True(settings.IsDirty);

            var (vm2, settings2) = Arrange();
            string stored = WebControllerAccess.ProtectForStorage("MNPQRSTVWX");
            WriteSettingsFile(path, new AppSettingsData { WebControllerAccessCodeProtected = stored });
            pathField.SetValue(settings2, path);
            settings2.Reload();
            Assert.Equal("MNPQRSTVWX", vm2.Dashboard.WebControllerAccessCode);
            Assert.False(settings2.IsDirty);
        }
        finally { File.Delete(path); }
    }

    private static void WriteSettingsFile(string path, AppSettingsData app)
    {
        var serializer = new XmlSerializer(typeof(SettingsFileData));
        using var writer = new StreamWriter(path);
        serializer.Serialize(writer, new SettingsFileData { AppSettings = app });
    }

    [Fact]
    public void AnOutOfRangePortLoadsAsTheDefault()
    {
        var (vm, settings) = Arrange();
        LoadApp(settings, new AppSettingsData { WebControllerPort = 80 });
        Assert.Equal(8080, vm.Dashboard.WebControllerPort);
    }

    [Fact]
    public void AccessControlNeverRidesProfiles()
    {
        foreach (var name in new[] { "WebControllerUseAccessCode", "WebControllerAccessCode", "WebControllerAccessCodeProtected" })
            Assert.Null(typeof(ProfileData).GetProperty(name));
    }

    [Fact]
    public void TheWebTogglesResetAndTheCodeRejectsForeignValues()
    {
        var d = new DashboardViewModel
        {
            WebControllerUseHttps = true,
            WebControllerUseAccessCode = true,
        };

        d.ResetSettingCommand.Execute(nameof(DashboardViewModel.WebControllerUseHttps));
        d.ResetSettingCommand.Execute(nameof(DashboardViewModel.WebControllerUseAccessCode));
        d.ResetWebPortCommand.Execute(null);
        Assert.False(d.WebControllerUseHttps);
        Assert.False(d.WebControllerUseAccessCode);
        Assert.Equal(8080, d.WebControllerPort);

        string code = d.WebControllerAccessCode;
        d.WebControllerAccessCode = null;
        d.WebControllerAccessCode = "short";
        Assert.Equal(code, d.WebControllerAccessCode);
        d.WebControllerAccessCode = "mnpqrstvwx";
        Assert.Equal("MNPQRSTVWX", d.WebControllerAccessCode);
    }

    [Fact]
    public void ProtocolDefaultsToHttpAndSwitchesBothDirections()
    {
        var dashboard = new DashboardViewModel();
        Assert.Equal(0, dashboard.WebControllerProtocolIndex);
        Assert.True(dashboard.IsWebControllerHttp);
        Assert.False(dashboard.WebControllerUseAccessCode);
        dashboard.WebControllerProtocolIndex = 1;
        Assert.True(dashboard.WebControllerUseHttps);
        Assert.False(dashboard.IsWebControllerHttp);
        dashboard.WebControllerProtocolIndex = 0;
        Assert.False(dashboard.WebControllerUseHttps);
        Assert.True(dashboard.IsWebControllerHttp);
    }

    [Fact]
    public void ApplyingWebSettingsStartsOnlyWithTheFinalEndpoint()
    {
        var dashboard = new DashboardViewModel { EnableWebController = true };
        var starts = new System.Collections.Generic.List<(int, bool)>();
        dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.EnableWebController) && dashboard.EnableWebController)
                starts.Add((dashboard.WebControllerPort, dashboard.WebControllerUseHttps));
        };
        dashboard.ApplyWebControllerSettings(true, 9123, true);
        Assert.Equal((9123, true), Assert.Single(starts));
        dashboard.ApplyWebControllerSettings(true, 9123, true);
        Assert.Single(starts);
    }

    private static AppSettingsData BuildApp(SettingsService settings)
        => (AppSettingsData)typeof(SettingsService).GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(settings, null);

    private static void LoadApp(SettingsService settings, AppSettingsData data)
        => typeof(SettingsService).GetMethod("LoadAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(settings, new object[] { data });

    private static AppSettingsData RoundTripApp(AppSettingsData data)
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var writer = new StringWriter();
        serializer.Serialize(writer, data);
        using var reader = new StringReader(writer.ToString());
        return (AppSettingsData)serializer.Deserialize(reader);
    }
}
