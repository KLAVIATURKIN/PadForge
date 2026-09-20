using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The bundled BthPS3 package is the signed 3.0.0 build, whose filter
    /// attaches to BTHX radios (PCIe and UART Bluetooth, not only USB) as
    /// well as USB ones. Issue #204 brought the 2.12.0 build that closed the
    /// remote-disconnect use-after-free, and a machine carrying either older
    /// bundle is upgraded in place the next time the pairing flow runs. The
    /// INF is the only version source, and the bundle's layout follows what
    /// the INFs say about where the binaries sit.
    /// </summary>
    public class BthPs3DriverUpgradeTests
    {
        [Theory]
        [InlineData("DriverVer = 09/18/2026,3.0.0.2082", "3.0.0.2082")]
        [InlineData("DriverVer=02/22/2025,2.10.470.0 ; trailing comment", "2.10.470.0")]
        [InlineData("  driverver = 01/01/2020,1.2.3.4", "1.2.3.4")]
        public void ParsesTheDriverVerLine(string line, string expected)
        {
            string inf = "[Version]\nSignature=\"$WINDOWS NT$\"\n" + line + "\n[Strings]\n";
            Assert.Equal(Version.Parse(expected), Ds3DriverInstaller.ParseInfDriverVersion(inf));
        }

        [Fact]
        public void NoDriverVerMeansNoVersion()
        {
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion("[Version]\nSignature=x\n"));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion(""));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion(null));
            Assert.Null(Ds3DriverInstaller.ParseInfDriverVersion("DriverVer = 09/15/2026,notaversion"));
        }

        [Fact]
        public void UpgradesOnlyWhenTheBundleIsStrictlyNewer()
        {
            // The upgrade this release actually performs: a machine on the
            // previous bundle moves to 3.0.0.
            var installed = new Version(2, 12, 0, 2037);
            var bundled = new Version(3, 0, 0, 2082);
            Assert.True(Ds3DriverInstaller.ShouldUpgrade(installed, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(bundled, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(bundled, installed));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(null, bundled));
            Assert.False(Ds3DriverInstaller.ShouldUpgrade(installed, null));
        }

        [Fact]
        public void BundledInfsAgreeOnOneVersionAndPlaceTheBinariesWhereTheyPoint()
        {
            string root = Path.Combine(Root(), "PadForge.App", "Resources", "BthPS3");
            string profile = File.ReadAllText(Path.Combine(root, "BthPS3", "BthPS3.inf"));
            string nullPdo = File.ReadAllText(Path.Combine(root, "BthPS3", "BthPS3_PDO_NULL_Device.inf"));
            string filter = File.ReadAllText(Path.Combine(root, "BthPS3PSM", "BthPS3PSM.inf"));
            var v = Ds3DriverInstaller.ParseInfDriverVersion(profile);
            Assert.NotNull(v);
            Assert.True(v >= new Version(3, 0, 0, 2082), v.ToString());
            Assert.Equal(v, Ds3DriverInstaller.ParseInfDriverVersion(nullPdo));
            Assert.Equal(v, Ds3DriverInstaller.ParseInfDriverVersion(filter));

            // [SourceDisksFiles.amd64] BthPS3.sys = 1,x64 means the binary
            // lives in an x64 folder beside the INF, and the catalog beside it.
            // The same INF carries an arm64 section pointing at an ARM64
            // folder, and Windows installs whichever matches the machine. A
            // bundle holding the INF without the binary one of its sections
            // names fails on exactly the machines that section is for, so both
            // are pinned, including the one this bench cannot run.
            foreach (var (inf, folder, sys, cat) in new[]
            {
                (profile, "BthPS3", "BthPS3.sys", "bthps3.cat"),
                (filter, "BthPS3PSM", "BthPS3PSM.sys", "bthps3psm.cat"),
            })
            {
                foreach (string arch in new[] { "amd64", "arm64" })
                {
                    var m = Regex.Match(inf, @"\[SourceDisksFiles\." + arch + @"\]\s*" + Regex.Escape(sys) + @"\s*=\s*1\s*,\s*(\w+)", RegexOptions.IgnoreCase);
                    Assert.True(m.Success, sys + " has no " + arch + " source entry");
                    Assert.True(File.Exists(Path.Combine(root, folder, m.Groups[1].Value, sys)), sys + " missing under " + m.Groups[1].Value);
                }
                Assert.True(File.Exists(Path.Combine(root, folder, cat)), cat + " missing");
            }
            Assert.True(File.Exists(Path.Combine(root, "BthPS3", "bthps3_pdo_null_device.cat")));
        }

        [Fact]
        public void InstalledBranchUpgradesBeforeArmingTheFilter()
        {
            string src = File.ReadAllText(Path.Combine(Root(), "PadForge.App", "Services", "Ds3DriverInstaller.cs")).Replace("\r\n", "\n");
            int at = src.IndexOf("public static bool EnsureInstalled", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, src.IndexOf("log(\"Installing PlayStation Bluetooth drivers (one time)...\");", at, StringComparison.Ordinal) - at);
            int upgrade = body.IndexOf("UpgradeInstalledDriversIfOlder(log);", StringComparison.Ordinal);
            int arm = body.IndexOf("EnsurePsmPatch(log);", StringComparison.Ordinal);
            Assert.True(upgrade > 0 && arm > upgrade, "the upgrade must run inside the installed branch, before the patch is armed");
            string method = src.Substring(src.IndexOf("private static void UpgradeInstalledDriversIfOlder(", StringComparison.Ordinal));
            method = method.Substring(0, method.IndexOf("\n        }\n", StringComparison.Ordinal));
            Assert.Contains("BthPS3PSM.inf", method);
            Assert.Contains("BthPS3_PDO_NULL_Device.inf", method);
            Assert.Contains("CycleBluetoothRadio(log);", method);
            Assert.Contains("if (!ShouldUpgrade(installed, bundled)) return;", method);
        }

        private static string Root()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }
    }
}
