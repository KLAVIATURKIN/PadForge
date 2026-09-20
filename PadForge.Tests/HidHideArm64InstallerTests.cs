using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HidHide on ARM64 Windows. 4.5.1 switched it off there on the belief that
    /// upstream had no ARM64 driver. Upstream publishes a Microsoft-signed one
    /// and documents a manual install
    /// (docs.nefarius.at/projects/HidHide/Manual-Installation-ARM64), which
    /// <see cref="HidHideArm64Installer"/> performs with upstream's own tool.
    ///
    /// <para>No bench here can run an ARM64 driver install, so two things are
    /// pinned instead. The bundled files are exactly the ones upstream
    /// published, down to the byte. And the sequence is exactly upstream's,
    /// plus the one refusal that stands in for the watchdog service an ARM64
    /// machine does not get: no class filter for a driver that has not been
    /// seen running.</para>
    /// </summary>
    public class HidHideArm64InstallerTests
    {
        // ── The bundled files are upstream's, byte for byte ──

        private static string Resource(string name)
            => AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "HidHideArm64", name));

        private static string Sha256(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s));
        }

        /// <summary>github.com/nefarius/HidHide, drivers/HidHide_ARM64.zip at
        /// fb5615c0, the file the manual links.</summary>
        [Fact]
        public void TheDriverPackageIsTheOneUpstreamPublished()
        {
            Assert.Equal("6CB54A891918737075C091484D928919D9B9EE99A9E6CD72E9965D1D8BE9CAB0",
                         Sha256(Resource(HidHideArm64Installer.DriverPackageResource)));
        }

        /// <summary>nefcon v1.20.0, ARM64/nefconc.exe.</summary>
        [Fact]
        public void TheInstallToolIsNefcon120ForArm64()
        {
            string path = Resource(HidHideArm64Installer.NefconResource);
            Assert.Equal("D4F9A0BB93F914429FEAF40643AEF374009C93AB5A09BF697D65F2A48CC980A6", Sha256(path));
            Assert.Equal(0xAA64, ReadMachine(File.ReadAllBytes(path)));
        }

        /// <summary>Both files ride inside the app in BOTH builds, because the
        /// x64 build also runs on ARM64 Windows. A wrong path in the project
        /// file would leave them out without a build error, and the first
        /// anyone heard of it would be a failed install on an ARM64 machine.
        /// So the bytes the app carries are hashed too.</summary>
        [Theory]
        [InlineData(HidHideArm64Installer.DriverPackageResource, "6CB54A891918737075C091484D928919D9B9EE99A9E6CD72E9965D1D8BE9CAB0")]
        [InlineData(HidHideArm64Installer.NefconResource, "D4F9A0BB93F914429FEAF40643AEF374009C93AB5A09BF697D65F2A48CC980A6")]
        public void TheAppCarriesBothFiles_UnderTheNamesTheInstallerAsksFor(string name, string sha256)
        {
            var app = typeof(HidHideArm64Installer).Assembly;
            string resource = app.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
            Assert.True(resource != null, name + " is not embedded in the app");

            using var s = app.GetManifestResourceStream(resource);
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(s)));
        }

        /// <summary>What the installer looks for inside the package has to be
        /// there: an INF for root\HidHide on NTARM64, an ARM64 driver, and
        /// the catalog that carries Microsoft's signature over both.</summary>
        [Fact]
        public void ThePackageHoldsAnArm64DriverForTheHardwareIdTheInstallerNames()
        {
            using var zip = ZipFile.OpenRead(Resource(HidHideArm64Installer.DriverPackageResource));
            var names = zip.Entries.Select(e => e.Name.ToLowerInvariant()).ToList();
            Assert.Contains("hidhide.inf", names);
            Assert.Contains("hidhide.sys", names);
            Assert.Contains("hidhide.cat", names);

            string inf = ReadText(zip, "HidHide.inf");
            Assert.Contains("NTARM64", inf, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(HidHideArm64Installer.HardwareId, inf, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1.6.280.0", inf);

            Assert.Equal(0xAA64, ReadMachine(ReadBytes(zip, "HidHide.sys")));
        }

        // ── The command lines are upstream's ──

        /// <summary>HidHide Installer/Program.cs, OnAfterInstall and
        /// OnBeforeUninstall, and the manual's three class GUIDs.</summary>
        [Fact]
        public void TheCommandLinesAreTheOnesUpstreamsInstallerRuns()
        {
            Assert.Equal("install \"C:\\x\\HidHide.inf\" \"root\\HidHide\" --no-duplicates --remove-duplicates",
                         HidHideArm64Installer.InstallArgs(@"C:\x\HidHide.inf"));
            Assert.Equal("remove \"root\\HidHide\"", HidHideArm64Installer.RemoveDeviceArgs());
            Assert.Equal("--add-class-filter --position upper --service-name HidHide --class-guid 745a17a0-74d3-11d0-b6fe-00a0c90f57da",
                         HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.HidClass));
            Assert.Equal("--remove-class-filter --position upper --service-name HidHide --class-guid d61ca365-5af4-4486-998b-9db4734c6ca3",
                         HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.XnaCompositeClass));
            Assert.Equal(new Guid("05f5cfe2-4733-4950-a6bb-07aad01a3a84"), HidHideArm64Installer.XboxCompositeClass);
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(3010, true, true)]     // done, Windows wants a restart
        [InlineData(259, true, true)]      // the driver on the device is already as good
        [InlineData(259, false, false)]    // which only an install can mean
        [InlineData(1, false, false)]
        [InlineData(5, true, false)]       // access denied
        public void NefconsExitCodes(int exitCode, bool installing, bool success)
            => Assert.Equal(success, HidHideArm64Installer.IsSuccess(exitCode, installing));

        // ── The sequence ──

        private sealed class Script
        {
            public readonly List<string> Ran = new List<string>();
            public readonly Dictionary<string, int?> Answers = new Dictionary<string, int?>();
            public int? Run(string arguments)
            {
                Ran.Add(arguments);
                foreach (var pair in Answers)
                    if (arguments.StartsWith(pair.Key, StringComparison.Ordinal)) return pair.Value;
                return 0;
            }
        }

        [Fact]
        public void Install_PutsTheDriverInFirst_ThenTheThreeFilters()
        {
            var s = new Script();
            HidHideArm64Installer.Install(s.Run, @"C:\x\HidHide.inf", driverAnswers: () => true);

            Assert.Equal(4, s.Ran.Count);
            Assert.StartsWith("install ", s.Ran[0]);
            Assert.Equal(HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.HidClass), s.Ran[1]);
            Assert.Equal(HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.XnaCompositeClass), s.Ran[2]);
            Assert.Equal(HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.XboxCompositeClass), s.Ran[3]);
        }

        /// <summary>THE RULE. A HIDClass filter entry naming a driver that is
        /// not running stops every keyboard and mouse from starting, and an
        /// ARM64 machine has no watchdog to take it back out. So a driver
        /// that does not answer gets its device removed and no filter at
        /// all.</summary>
        [Fact]
        public void Install_AddsNoFilter_ForADriverThatDoesNotAnswer()
        {
            var s = new Script();
            var ex = Assert.Throws<HidHideSetupException>(() =>
                HidHideArm64Installer.Install(s.Run, @"C:\x\HidHide.inf", driverAnswers: () => false));

            Assert.Equal(HidHideSetupFailure.DriverDidNotStart, ex.Failure);
            Assert.DoesNotContain(s.Ran, a => a.StartsWith("--add-class-filter", StringComparison.Ordinal));
            Assert.Equal(HidHideArm64Installer.RemoveDeviceArgs(), s.Ran.Last());
        }

        /// <summary>The same silence after Windows answered 3010 is Windows
        /// saying the driver starts after a restart. Still no filter, but the
        /// device stays, so the next Install finishes the job, and the fault
        /// is reported as the exit code it is.</summary>
        [Fact]
        public void Install_AfterARestartDemand_LeavesTheDeviceIn_AndStillAddsNoFilter()
        {
            var s = new Script();
            s.Answers["install "] = 3010;
            var ex = Assert.Throws<InstallerFailedException>(() =>
                HidHideArm64Installer.Install(s.Run, @"C:\x\HidHide.inf", driverAnswers: () => false));

            Assert.Equal(3010, ex.ExitCode);
            Assert.Single(s.Ran);
        }

        [Fact]
        public void Install_StopsBeforeAnything_WhenTheDriverInstallFails()
        {
            var s = new Script();
            s.Answers["install "] = 5;
            bool asked = false;
            var ex = Assert.Throws<InstallerFailedException>(() =>
                HidHideArm64Installer.Install(s.Run, @"C:\x\HidHide.inf", () => { asked = true; return true; }));

            Assert.Equal(5, ex.ExitCode);
            Assert.False(asked);
            Assert.Single(s.Ran);
        }

        /// <summary>The two Xbox 360 era classes are best effort, as they are
        /// upstream (TryRun). HIDClass is not.</summary>
        [Fact]
        public void Install_NeedsHidClass_AndToleratesTheOtherTwo()
        {
            var tolerant = new Script();
            tolerant.Answers[HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.XnaCompositeClass)] = 2;
            tolerant.Answers[HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.XboxCompositeClass)] = 2;
            HidHideArm64Installer.Install(tolerant.Run, @"C:\x\HidHide.inf", () => true);

            var strict = new Script();
            strict.Answers[HidHideArm64Installer.AddFilterArgs(HidHideArm64Installer.HidClass)] = 2;
            Assert.Throws<InstallerFailedException>(() =>
                HidHideArm64Installer.Install(strict.Run, @"C:\x\HidHide.inf", () => true));
        }

        /// <summary>Filters off first, read back as gone, and only then the
        /// device. Removing the driver under a filter entry is the state that
        /// stops a device class from starting.</summary>
        [Fact]
        public void Uninstall_TakesTheFiltersOffFirst_ThenTheDevice()
        {
            var s = new Script();
            HidHideArm64Installer.Uninstall(s.Run, filterStillListed: _ => false);

            Assert.Equal(4, s.Ran.Count);
            Assert.All(s.Ran.Take(3), a => Assert.StartsWith("--remove-class-filter", a));
            Assert.Equal(HidHideArm64Installer.RemoveDeviceArgs(), s.Ran[3]);
        }

        [Fact]
        public void Uninstall_LeavesTheDriverIn_WhileAnyFilterStillNamesIt()
        {
            var s = new Script();
            var ex = Assert.Throws<HidHideSetupException>(() =>
                HidHideArm64Installer.Uninstall(s.Run, c => c == HidHideArm64Installer.HidClass));

            Assert.Equal(HidHideSetupFailure.FilterStillListed, ex.Failure);
            Assert.DoesNotContain(HidHideArm64Installer.RemoveDeviceArgs(), s.Ran);
        }

        // ── What counts as installed, and the watchdog's check ──

        /// <summary>Nothing registers an ARM64 install with Windows Installer,
        /// so it is read from what it leaves behind. All three, because a
        /// device with no filter yet is an install that stopped part way, and
        /// Install has to stay available to finish it.</summary>
        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        public void Installed_MeansTheServiceTheDeviceAndTheHidClassFilter(
            bool service, bool device, bool filter, bool installed)
            => Assert.Equal(installed, HidHideArm64Installer.IsInstalled(service, device, filter));

        [Fact]
        public void AFilterIsDangling_OnlyWhenTheServiceBehindItIsGone()
        {
            Func<Guid, bool> hidOnly = c => c == HidHideArm64Installer.HidClass;

            Assert.Empty(HidHideArm64Installer.DanglingFilters(hidOnly, serviceRegistered: true));
            Assert.Equal(new[] { HidHideArm64Installer.HidClass },
                         HidHideArm64Installer.DanglingFilters(hidOnly, serviceRegistered: false));
            Assert.Empty(HidHideArm64Installer.DanglingFilters(_ => false, serviceRegistered: false));
        }

        [Fact]
        public void TheFilterListIsMatchedByServiceName_WhateverItsCase()
        {
            Assert.True(HidHideArm64Installer.ListsService(new[] { "mshidkmdf", "hidhide" }));
            Assert.False(HidHideArm64Installer.ListsService(new[] { "HidHideX", "mshidkmdf" }));
            Assert.False(HidHideArm64Installer.ListsService(null));
        }

        [Theory]
        [InlineData(@"\SystemRoot\System32\drivers\HidHide.sys", @"System32\drivers\HidHide.sys")]
        [InlineData(@"System32\drivers\HidHide.sys", @"System32\drivers\HidHide.sys")]
        public void AServiceImagePath_BecomesAPathUnderTheWindowsFolder(string imagePath, string tail)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.Equal(Path.Combine(windows, tail), HidHideArm64Installer.ExpandServiceImagePath(imagePath));
        }

        [Fact]
        public void AnNtPathAndNothingAtAll()
        {
            Assert.Equal(@"C:\Drivers\HidHide.sys", HidHideArm64Installer.ExpandServiceImagePath(@"\??\C:\Drivers\HidHide.sys"));
            Assert.Null(HidHideArm64Installer.ExpandServiceImagePath(null));
            Assert.Null(HidHideArm64Installer.ExpandServiceImagePath("  "));
        }

        // ── helpers ──

        private static byte[] ReadBytes(ZipArchive zip, string name)
        {
            var entry = zip.Entries.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ReadText(ZipArchive zip, string name)
            => System.Text.Encoding.UTF8.GetString(ReadBytes(zip, name));

        private static int ReadMachine(byte[] image)
        {
            int pe = BitConverter.ToInt32(image, 0x3C);
            return BitConverter.ToUInt16(image, pe + 4);
        }
    }
}
