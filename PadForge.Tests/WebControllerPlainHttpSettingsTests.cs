using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

/// <summary>The plain HTTP address's settings are machine settings: they
/// ride PadForge.xml through the real save and load, never a profile, and
/// the access code is stored encrypted.</summary>
public partial class ProfileServiceToggleTests
{
    [Fact]
    public void PlainHttpSettingsSurviveTheActualSaveAndLoad()
    {
        var (vm, settings) = Arrange();
        var d = vm.Dashboard;
        d.EnableWebControllerPlainHttp = true;
        d.WebControllerPlainHttpPort = 9123;
        d.WebControllerPlainHttpLocalOnly = true;
        string code = d.WebControllerAccessCode;

        var built = BuildApp(settings);
        Assert.True(built.EnableWebControllerPlainHttp);
        Assert.Equal(9123, built.WebControllerPlainHttpPort);
        Assert.True(built.WebControllerPlainHttpLocalOnly);
        Assert.NotNull(built.WebControllerAccessCodeProtected);
        Assert.DoesNotContain(code, built.WebControllerAccessCodeProtected);

        var loaded = RoundTripApp(built);
        var (vm2, settings2) = Arrange();
        Assert.NotEqual(code, vm2.Dashboard.WebControllerAccessCode);
        LoadApp(settings2, loaded);
        Assert.True(vm2.Dashboard.EnableWebControllerPlainHttp);
        Assert.Equal(9123, vm2.Dashboard.WebControllerPlainHttpPort);
        Assert.True(vm2.Dashboard.WebControllerPlainHttpLocalOnly);
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
    public void OlderSettingsLoadWithPlainHttpOffAndAFreshCode()
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var reader = new StringReader(
            "<AppSettingsData><EnableWebController>true</EnableWebController><WebControllerPort>8080</WebControllerPort></AppSettingsData>");
        var old = (AppSettingsData)serializer.Deserialize(reader);
        Assert.False(old.EnableWebControllerPlainHttp);
        Assert.Equal(WebControllerServer.DefaultPlainPort, old.WebControllerPlainHttpPort);
        Assert.False(old.WebControllerPlainHttpLocalOnly);
        Assert.Null(old.WebControllerAccessCodeProtected);

        var (vm, settings) = Arrange();
        string fresh = vm.Dashboard.WebControllerAccessCode;
        Assert.True(WebControllerAccess.IsValid(fresh));
        LoadApp(settings, old);
        Assert.False(vm.Dashboard.EnableWebControllerPlainHttp);
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
        string path = Path.Combine(Path.GetTempPath(), "PadForgePlainHttp_" + System.Guid.NewGuid().ToString("N") + ".xml");
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
    public void AnOutOfRangePlainPortLoadsAsTheDefault()
    {
        var (vm, settings) = Arrange();
        LoadApp(settings, new AppSettingsData { WebControllerPlainHttpPort = 80 });
        Assert.Equal(WebControllerServer.DefaultPlainPort, vm.Dashboard.WebControllerPlainHttpPort);
    }

    [Fact]
    public void PlainHttpNeverRidesProfiles()
    {
        foreach (var name in new[] { "EnableWebControllerPlainHttp", "WebControllerPlainHttpPort",
                     "WebControllerPlainHttpLocalOnly", "WebControllerAccessCode", "WebControllerAccessCodeProtected" })
            Assert.Null(typeof(ProfileData).GetProperty(name));
    }

    [Fact]
    public void ThePlainHttpTogglesResetAndTheCodeRejectsForeignValues()
    {
        var d = new DashboardViewModel
        {
            EnableWebControllerPlainHttp = true,
            WebControllerPlainHttpLocalOnly = true,
            WebControllerPlainHttpPort = 70000,
        };
        Assert.Equal(65535, d.WebControllerPlainHttpPort);
        d.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableWebControllerPlainHttp));
        d.ResetSettingCommand.Execute(nameof(DashboardViewModel.WebControllerPlainHttpLocalOnly));
        d.ResetWebPlainPortCommand.Execute(null);
        Assert.False(d.EnableWebControllerPlainHttp);
        Assert.False(d.WebControllerPlainHttpLocalOnly);
        Assert.Equal(WebControllerServer.DefaultPlainPort, d.WebControllerPlainHttpPort);

        string code = d.WebControllerAccessCode;
        d.WebControllerAccessCode = null;
        d.WebControllerAccessCode = "short";
        Assert.Equal(code, d.WebControllerAccessCode);
        d.WebControllerAccessCode = "mnpqrstvwx";
        Assert.Equal("MNPQRSTVWX", d.WebControllerAccessCode);
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
