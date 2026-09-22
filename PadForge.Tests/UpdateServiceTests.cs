using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// In-app updates (#457). The decisions are pinned against the release
/// shapes GitHub serves today: releases publish PadForge-v{version}-win-x64.zip
/// and -win-arm64.zip, and the rolling dev release latest-v4-dev, titled
/// "PadForge r{count}@{hash}", publishes PadForge.zip and PadForge-arm64.zip.
/// </summary>
public class UpdateServiceTests
{
    private const string Digest = "sha256:be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b";

    // The v4.5.2 release as the API returned it, trimmed to the fields read.
    private const string StableJson = """
        {"tag_name":"v4.5.2","name":"PadForge v4.5.2","draft":false,"prerelease":false,
         "html_url":"https://github.com/hifihedgehog/PadForge/releases/tag/v4.5.2",
         "assets":[
          {"name":"PadForge-v4.5.2-win-arm64.zip","size":284476236,
           "digest":"sha256:8f9cf85a3f298eb818a5565e37c0d95c5666d4356c3ea3461442047bc5969a63",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-arm64.zip"},
          {"name":"PadForge-v4.5.2-win-x64.zip","size":304809444,
           "digest":"sha256:be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip"}]}
        """;

    // latest-v4-dev as the API returned it, trimmed the same way.
    private const string DevJson = """
        {"tag_name":"latest-v4-dev","name":"PadForge r3676@b500441","draft":false,"prerelease":true,
         "html_url":"https://github.com/hifihedgehog/PadForge/releases/tag/latest-v4-dev",
         "assets":[
          {"name":"PadForge-arm64.zip","size":283002879,
           "digest":"sha256:7b7adf4fe9a8dbd895c527406b66d972a4d2e4550da4e667efadfb418b494531",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/latest-v4-dev/PadForge-arm64.zip"},
          {"name":"PadForge.zip","size":303209889,
           "digest":"sha256:72a6b70cd01653600f8e16315d71dd42a86bfd73bdeea89c70e7a9b4221dcbc6",
           "browser_download_url":"https://github.com/hifihedgehog/PadForge/releases/download/latest-v4-dev/PadForge.zip"}]}
        """;

    private static GitHubRelease Parse(string json) => JsonSerializer.Deserialize<GitHubRelease>(json);

    // ── Releases ──

    [Theory]
    [InlineData(false, "PadForge-v4.5.2-win-x64.zip", "be1713ec")]
    [InlineData(true, "PadForge-v4.5.2-win-arm64.zip", "8f9cf85a")]
    public void ANewerReleaseOffersTheZipForThisMachine(bool arm64, string asset, string shaPrefix)
    {
        var outcome = UpdateService.Evaluate(Parse(StableJson), preReleaseChannel: false,
            new Version(4, 5, 1, 0), 3600, "aaaaaaa", arm64, out var offer);
        Assert.Equal(UpdateCheckOutcome.Available, outcome);
        Assert.Equal("4.5.2", offer.DisplayVersion);
        Assert.False(offer.IsPreRelease);
        Assert.Equal(asset, offer.AssetName);
        Assert.StartsWith(shaPrefix, offer.Sha256);
        Assert.Equal("v4.5.2", offer.StageKey);
        Assert.Equal("https://github.com/hifihedgehog/PadForge/releases/tag/v4.5.2", offer.ReleasePageUrl);
    }

    [Theory]
    [InlineData(4, 5, 2)]   // SharedVersion carries a fourth part, the tag does not
    [InlineData(4, 6, 0)]
    [InlineData(5, 0, 0)]
    public void TheSameOrAnOlderReleaseIsUpToDate(int major, int minor, int patch)
    {
        var outcome = UpdateService.Evaluate(Parse(StableJson), false,
            new Version(major, minor, patch, 0), 0, "", false, out var offer);
        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Null(offer);
    }

    [Theory]
    [InlineData("v4.6.0-rc1")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void ATagThatIsNotAVersionIsRefused(string tag)
    {
        var release = Parse(StableJson);
        release.TagName = tag;
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    [Fact]
    public void ADraftIsNeverOffered()
    {
        var release = Parse(StableJson);
        release.Draft = true;
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    // ── Pre-releases ──

    [Theory]
    [InlineData(false, "PadForge.zip")]
    [InlineData(true, "PadForge-arm64.zip")]
    public void ANewerDevBuildOffersTheZipForThisMachine(bool arm64, string asset)
    {
        var outcome = UpdateService.Evaluate(Parse(DevJson), preReleaseChannel: true,
            new Version(4, 5, 2, 0), 3650, "1234567", arm64, out var offer);
        Assert.Equal(UpdateCheckOutcome.Available, outcome);
        Assert.True(offer.IsPreRelease);
        Assert.Equal(3676, offer.BuildNumber);
        Assert.Equal("r3676 (b500441)", offer.DisplayVersion);
        Assert.Equal("r3676", offer.StageKey);
        Assert.Equal(asset, offer.AssetName);
    }

    [Theory]
    [InlineData(3676, "b500441")]   // this very build
    [InlineData(3677, "2b081e7")]   // a newer local build
    public void TheSameOrANewerBuildIsUpToDate(int build, string commit)
    {
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), build, commit, false, out _));
    }

    [Fact]
    public void TheSameCountOnAnotherCommitIsARebuiltHistoryAndNewer()
    {
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), 3676, "deadbee", false, out _));
    }

    [Fact]
    public void ABuildWithoutACountTakesAnyPublishedBuild()
    {
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(Parse(DevJson), true,
            new Version(4, 5, 2, 0), 0, "", false, out _));
    }

    [Theory]
    [InlineData("PadForge dev build archive")]
    [InlineData("PadForge v4.5.2")]
    [InlineData("PadForge r@b500441")]
    [InlineData("PadForge r0@b500441")]
    public void ADevTitleOutsideTheCiFormatIsRefused(string title)
    {
        var release = Parse(DevJson);
        release.Name = title;
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 1, "0000000", false, out _));
    }

    [Fact]
    public void TheDevFeedFollowsTheRunningMajorVersion()
    {
        Assert.Equal("https://api.github.com/repos/hifihedgehog/PadForge/releases/tags/latest-v4-dev",
            UpdateService.DevEndpoint(new Version(4, 5, 2, 0)));
        Assert.Equal("https://api.github.com/repos/hifihedgehog/PadForge/releases/latest",
            UpdateService.StableEndpoint);
    }

    // ── What is fetched ──

    [Fact]
    public void ADevBuildWithNoArm64ZipHasNothingForAnArm64Pc()
    {
        var release = Parse(DevJson);
        release.Assets.RemoveAll(a => a.Name == "PadForge-arm64.zip");
        Assert.Equal(UpdateCheckOutcome.NoBuildForThisPc, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 3600, "aaaaaaa", arm64Machine: true, out _));
        Assert.Equal(UpdateCheckOutcome.Available, UpdateService.Evaluate(release, true,
            new Version(4, 5, 2, 0), 3600, "aaaaaaa", arm64Machine: false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("sha256:1234")]
    [InlineData("sha256:zz1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b")]
    public void AnAssetWithoutAUsableDigestIsNeverOffered(string digest)
    {
        var release = Parse(StableJson);
        foreach (var a in release.Assets) a.Digest = digest;
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateService.Evaluate(release, false,
            new Version(4, 0, 0, 0), 0, "", false, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void TheDigestIsReadAsLowercaseHex()
    {
        Assert.Equal("be1713ecbf7de65844a9b563c56dbcd9a96cbc12d53c4c101b88d783e3cd525b",
            UpdateService.ParseSha256Digest(Digest.ToUpperInvariant().Replace("SHA256", "sha256")));
    }

    [Theory]
    [InlineData("https://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", true)]
    [InlineData("http://github.com/hifihedgehog/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", false)]
    [InlineData("https://github.com.evil.example/hifihedgehog/PadForge/releases/download/v4.5.2/x.zip", false)]
    [InlineData("https://github.com/someone/PadForge/releases/download/v4.5.2/PadForge-v4.5.2-win-x64.zip", false)]
    [InlineData("https://github.com/hifihedgehog/PadForge/archive/refs/tags/v4.5.2.zip", false)]
    [InlineData("not a url", false)]
    public void OnlyThisRepositorysReleaseDownloadsAreTrusted(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateService.IsTrustedDownloadUrl(url));
        var release = Parse(StableJson);
        foreach (var a in release.Assets) a.DownloadUrl = url;
        Assert.Equal(trusted ? UpdateCheckOutcome.Available : UpdateCheckOutcome.Failed,
            UpdateService.Evaluate(release, false, new Version(4, 0, 0, 0), 0, "", false, out _));
    }

    [Fact]
    public void OnlyTheZipsRootExeIsExtracted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PadForgeUpdateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string zip = Path.Combine(dir, "a.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "nested/PadForge.exe", "wrong");
                Write(archive, "PadForge.exe", "right");
                Write(archive, "readme.txt", "ignored");
            }
            string exe = Path.Combine(dir, "PadForge.exe");
            string sha = UpdateService.ExtractExe(zip, exe);
            Assert.Equal("right", File.ReadAllText(exe));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("right"))).ToLowerInvariant(), sha);
            Assert.Equal(2, Directory.GetFiles(dir).Length);

            string empty = Path.Combine(dir, "b.zip");
            using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create))
                Write(archive, "nested/PadForge.exe", "wrong");
            Assert.Throws<InvalidDataException>(() => UpdateService.ExtractExe(empty, Path.Combine(dir, "x.exe")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static void Write(ZipArchive archive, string name, string text)
        {
            using var stream = archive.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
    }

    // ── Replacing the running exe ──

    /// <summary>The staged exe sits in the user's temp folder and runs
    /// elevated, so a file that changed after it was written never starts.
    /// The check happens before any process is created.</summary>
    [Fact]
    public void AStagedExeThatChangedAfterItWasWrittenIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), "PadForgeUpdateTest_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "not the file that was verified");
        try
        {
            Assert.Throws<UpdateVerificationException>(() => UpdateService.StartInstaller(
                new StagedUpdate(path, new string('0', 64)), Array.Empty<string>()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheInstallerGetsTheTargetThePidAndTheLaunchArguments()
    {
        var args = UpdateService.BuildInstallerArgs(@"C:\PadForge\PadForge.exe", 4242,
            new[] { "--profile", "Rocket League", "--updated" });
        Assert.Equal(new[] { "--apply-update", @"C:\PadForge\PadForge.exe", "4242", "--profile", "Rocket League" }, args);
    }

    [Fact]
    public void ANormalLaunchIsNotTheInstaller()
    {
        Assert.False(UpdateService.TryRunApplyMode(Array.Empty<string>(), null));
        Assert.False(UpdateService.TryRunApplyMode(new[] { "--profile", "X", "Y" }, null));
        Assert.False(UpdateService.TryRunApplyMode(new[] { "--apply-update", @"C:\PadForge\PadForge.exe" }, null));
    }

    [Fact]
    public void TheCopyWaitsForTheOldCopyToExitFirst()
    {
        bool copied = false;
        string error = UpdateService.Apply(@"C:\temp\new.exe", @"C:\PadForge\PadForge.exe",
            waitForOldCopy: () => false, copy: (_, _) => copied = true, attempts: 3, TimeSpan.Zero);
        Assert.Same(UpdateService.OldCopyStillRunning, error);
        Assert.False(copied);
    }

    [Fact]
    public void ALockedFileIsRetriedUntilTheCopyLands()
    {
        int calls = 0;
        string error = UpdateService.Apply(@"C:\temp\new.exe", @"C:\PadForge\PadForge.exe", () => true,
            (_, _) => { if (++calls < 4) throw new IOException("in use"); }, attempts: 10, TimeSpan.Zero);
        Assert.Null(error);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void AFileThatStaysLockedReportsWhy()
    {
        int calls = 0;
        string error = UpdateService.Apply(@"C:\temp\new.exe", @"C:\PadForge\PadForge.exe", () => true,
            (_, _) => { calls++; throw new UnauthorizedAccessException("denied"); }, attempts: 5, TimeSpan.Zero);
        Assert.Equal("denied", error);
        Assert.Equal(5, calls);
    }

    [Fact]
    public void TheInstallerNeverCopiesOntoItself()
    {
        string error = UpdateService.Apply(@"C:\PadForge\PadForge.exe", @"C:\PadForge\..\PadForge\PadForge.exe",
            () => true, (_, _) => throw new InvalidOperationException("must not copy"), 3, TimeSpan.Zero);
        Assert.NotNull(error);
    }

    // ── Install at next launch ──

    [Theory]
    [InlineData(false, 0, "4.6.0", 4, 5, 2, 0, true)]
    [InlineData(false, 0, "4.5.2", 4, 5, 2, 0, false)]
    [InlineData(true, 3700, null, 4, 5, 2, 3676, true)]
    [InlineData(true, 3676, null, 4, 5, 2, 3676, false)]
    [InlineData(true, 3700, null, 4, 5, 2, 0, true)]
    [InlineData(true, 0, null, 4, 5, 2, 0, false)]
    public void APendingUpdateInstallsOnlyWhenItIsNewer(bool pre, int build, string version,
        int major, int minor, int patch, int currentBuild, bool newer)
    {
        var pending = new PendingUpdate { IsPreRelease = pre, BuildNumber = build, Version = version };
        Assert.Equal(newer, UpdateService.IsNewerThan(pending, new Version(major, minor, patch, 0), currentBuild));
    }

    [Fact]
    public void ThisBuildKnowsWhichCommitItIs()
    {
        // StampBuildIdentity runs git, so a checkout always stamps both.
        if (!Directory.Exists(Path.Combine(Root(), ".git"))) return;
        Assert.True(BuildIdentity.BuildNumber > 0);
        Assert.Matches("^[0-9a-f]{7}$", BuildIdentity.Commit);
        Assert.Equal($"{BuildIdentity.VersionText(BuildIdentity.Version)} (r{BuildIdentity.BuildNumber}@{BuildIdentity.Commit})",
            BuildIdentity.Display);
    }

    // ── Settings ──

    [Fact]
    public void TheThreeSettingsPersistWithTheirDefaults()
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new AppSettingsData
        {
            CheckForUpdatesAutomatically = false,
            InstallUpdatesAutomatically = true,
            IncludePreReleaseUpdates = true,
        });
        using var reader = new StringReader(writer.ToString());
        var restored = (AppSettingsData)serializer.Deserialize(reader);
        Assert.False(restored.CheckForUpdatesAutomatically);
        Assert.True(restored.InstallUpdatesAutomatically);
        Assert.True(restored.IncludePreReleaseUpdates);

        // A settings file from before #457: checking is on, the rest off.
        using var older = new StringReader("<AppSettingsData><CloseToTray>true</CloseToTray></AppSettingsData>");
        var old = (AppSettingsData)serializer.Deserialize(older);
        Assert.True(old.CheckForUpdatesAutomatically);
        Assert.False(old.InstallUpdatesAutomatically);
        Assert.False(old.IncludePreReleaseUpdates);

        var vm = new SettingsViewModel();
        Assert.True(vm.CheckForUpdatesAutomatically);
        Assert.False(vm.InstallUpdatesAutomatically);
        Assert.False(vm.IncludePreReleaseUpdates);
        vm.CheckForUpdatesAutomatically = false;
        vm.InstallUpdatesAutomatically = true;
        vm.IncludePreReleaseUpdates = true;
        foreach (string name in new[] { nameof(SettingsViewModel.CheckForUpdatesAutomatically),
                                        nameof(SettingsViewModel.InstallUpdatesAutomatically),
                                        nameof(SettingsViewModel.IncludePreReleaseUpdates) })
        {
            Assert.True(SettingsViewModel.CanResetSetting(name));
            vm.ResetSettingCommand.Execute(name);
        }
        Assert.True(vm.CheckForUpdatesAutomatically);
        Assert.False(vm.InstallUpdatesAutomatically);
        Assert.False(vm.IncludePreReleaseUpdates);
    }

    [Fact]
    public void TheSettingsReachLoadSaveTheDirtyListAndTheCard()
    {
        string settings = Read("PadForge.App/Services/SettingsService.cs");
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string view = Read("PadForge.App/Views/SettingsPage.xaml");
        string load = Between(settings, "private void LoadAppSettings(", "private AppSettingsData BuildAppSettings(");
        string save = settings[settings.IndexOf("private AppSettingsData BuildAppSettings(", StringComparison.Ordinal)..];
        string dirty = Between(window, "_viewModel.Settings.PropertyChanged +=", "_viewModel.Dashboard.PropertyChanged +=");
        foreach (string name in new[] { "CheckForUpdatesAutomatically", "InstallUpdatesAutomatically", "IncludePreReleaseUpdates" })
        {
            Assert.Contains($"vm.{name} = appSettings.{name};", load);
            Assert.Contains($"{name} = vm.{name},", save);
            Assert.Contains($"nameof(SettingsViewModel.{name})", dirty);
            Assert.Contains($"IsChecked=\"{{Binding {name}}}\"", view);
        }
        foreach (string command in new[] { "CheckForUpdatesNowCommand", "InstallUpdateCommand", "OpenReleaseNotesCommand" })
            Assert.Contains($"Command=\"{{Binding {command}}}\"", view);
    }

    [Fact]
    public void EveryUpdateStringExistsInEveryLocale()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "PadForge.App/Resources/Strings"), "Strings*.resx");
        Assert.Equal(10, files.Length);
        string designer = Read("PadForge.App/Resources/Strings/Strings.Designer.cs");
        var keys = XDocument.Load(files.Single(f => Path.GetFileName(f) == "Strings.resx")).Root.Elements("data")
            .Select(d => (string)d.Attribute("name"))
            .Where(k => k.StartsWith("Update_", StringComparison.Ordinal)
                || k is "Settings_Updates" or "Settings_UpdatesDesc" or "Settings_CheckForUpdates" or "Settings_CheckForUpdatesTip"
                    or "Settings_InstallUpdatesAutomatically" or "Settings_InstallUpdatesAutomaticallyTip"
                    or "Settings_IncludePreReleases" or "Settings_IncludePreReleasesTip"
                    or "Settings_CheckNow" or "Settings_InstallAndRestart" or "Settings_ReleaseNotes")
            .ToList();
        Assert.Equal(26, keys.Count);
        foreach (var path in files)
        {
            var resources = XDocument.Load(path).Root.Elements("data").ToList();
            foreach (string key in keys)
            {
                var resource = Assert.Single(resources, node => (string)node.Attribute("name") == key);
                string value = (string)resource.Element("value");
                Assert.False(string.IsNullOrWhiteSpace(value), path + ": " + key);
                if (key.EndsWith("_Format", StringComparison.Ordinal))
                    Assert.Contains("{0}", value);
            }
        }
        foreach (string key in keys)
            Assert.Contains($"public string {key} => Get(\"{key}\");", designer);
    }

    private static string Root([CallerFilePath] string me = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));

    private static string Read(string rel) =>
        File.ReadAllText(Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return text[a..b];
    }
}
