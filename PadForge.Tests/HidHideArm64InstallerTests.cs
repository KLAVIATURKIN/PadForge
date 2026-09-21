using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    /// plus what stands in for the watchdog service an ARM64 machine does not
    /// get: no class filter for a driver that has not been seen running, and
    /// a startup check that takes a filter back out when the driver behind
    /// it is not there to load.</para>
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

        /// <summary>HidHide Installer/Program.cs: OnAfterInstall adds, and
        /// OnBeforeInstall removes when it is uninstalling. The three class
        /// GUIDs are the manual's.</summary>
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

        /// <summary>The wait nefcon gets is the one the timeout message
        /// states to the user, which is the x64 installer's too. It was two
        /// minutes under a message that says three.</summary>
        [Fact]
        public void TheWaitIsTheThreeMinutesTheTimeoutMessageStates()
        {
            string arm64 = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "HidHideArm64Installer.cs")));
            string x64 = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "DriverInstaller.cs")));
            string strings = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "Strings", "Strings.resx")));

            Assert.Contains("WaitForExit(180_000)", arm64);
            Assert.Contains("WaitForExit(180_000)", x64);
            Assert.Matches("name=\"Status_InstallerTimedOut\"[^>]*>\\s*<value>[^<]*three minutes", strings);
        }

        // ── The install sequence ──

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
        /// that does not answer gets no filter at all, and the removal of
        /// its device is the last thing asked for.</summary>
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

        /// <summary>The fault reported is the driver's, even when taking the
        /// device back out fails too, never comes back, or throws.</summary>
        [Theory]
        [InlineData(1, false)]
        [InlineData(null, false)]
        [InlineData(0, true)]
        public void Install_ReportsTheDriver_WhateverBecomesOfTheRollback(int? rollbackAnswer, bool rollbackThrows)
        {
            var s = new Script();
            s.Answers["remove "] = rollbackAnswer;
            HidHideArm64Installer.NefconRunner run = a =>
            {
                if (rollbackThrows && a.StartsWith("remove ", StringComparison.Ordinal))
                    throw new InvalidOperationException("nefcon could not be started");
                return s.Run(a);
            };
            var ex = Assert.Throws<HidHideSetupException>(() =>
                HidHideArm64Installer.Install(run, @"C:\x\HidHide.inf", driverAnswers: () => false));
            Assert.Equal(HidHideSetupFailure.DriverDidNotStart, ex.Failure);
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

        // ── Whether the driver answers ──

        /// <summary>HidHide's control device is exclusive, and a second open
        /// answers access denied: HidHideCLI FilterDriverProxy.h says
        /// "Returns ACCESS_DENIED when in use", and a second open measured on
        /// the x64 bench answered 5. The installer took 32, a sharing
        /// violation, for that case, which nothing produces. So with
        /// HidHide's own client open during an install, a driver that was
        /// running fine read as one that had not started, and its device was
        /// taken back out.</summary>
        [Theory]
        [InlineData(true, 0, true)]      // it opened
        [InlineData(false, 5, true)]     // another client holds it
        [InlineData(false, 2, false)]    // no such device
        [InlineData(false, 32, false)]   // what the installer used to wait for
        [InlineData(false, -1, false)]   // the probe itself threw
        public void TheDriverAnswers_WhenItsControlDeviceOpensOrIsHeld(bool opened, int error, bool answers)
            => Assert.Equal(answers, HidHideArm64Installer.ControlDeviceAnswers(opened, error));

        // ── The removal sequence ──

        /// <summary>The machine as the installer sees it: which classes list
        /// HidHide, whether the device node is there, whether the driver
        /// answers, and what nefcon says to each command. A command that
        /// succeeds changes the machine the way nefcon would.</summary>
        private sealed class Machine
        {
            public readonly HashSet<Guid> Listed = new HashSet<Guid>();
            public bool? Node = true;
            public bool DriverAnswers = true;
            /// <summary>A command that outlived its wait may have done its
            /// work before it hung.</summary>
            public bool ACommandThatTimesOutStillDoesItsWork;
            public Func<string, bool> Throws = _ => false;
            public readonly List<string> Ran = new List<string>();
            public readonly List<string> Log = new List<string>();
            private readonly List<(string Prefix, int? Code)> _answers = new List<(string, int?)>();

            public Machine(params Guid[] listed) { foreach (Guid c in listed) Listed.Add(c); }

            public void Answer(string prefix, int? code) => _answers.Add((prefix, code));

            public int? Run(string arguments)
            {
                Ran.Add(arguments);
                if (Throws(arguments)) throw new InvalidOperationException("nefcon could not be started");
                int? code = 0;
                foreach (var a in _answers)
                    if (arguments.StartsWith(a.Prefix, StringComparison.Ordinal)) { code = a.Code; break; }
                if (code == 0 || (code == null && ACommandThatTimesOutStillDoesItsWork)) Apply(arguments);
                return code;
            }

            private void Apply(string arguments)
            {
                foreach (Guid c in HidHideArm64Installer.FilteredClasses)
                {
                    if (arguments == HidHideArm64Installer.AddFilterArgs(c)) Listed.Add(c);
                    if (arguments == HidHideArm64Installer.RemoveFilterArgs(c)) Listed.Remove(c);
                }
                if (arguments == HidHideArm64Installer.RemoveDeviceArgs()) Node = false;
            }

            public void Uninstall()
                => HidHideArm64Installer.Uninstall(Run, c => Listed.Contains(c), () => Node, () => DriverAnswers, Log.Add);

            public int Adds => Ran.Count(a => a.StartsWith("--add-class-filter", StringComparison.Ordinal));
        }

        private static Machine FullInstall()
            => new Machine(HidHideArm64Installer.HidClass, HidHideArm64Installer.XnaCompositeClass,
                           HidHideArm64Installer.XboxCompositeClass);

        /// <summary>Filters off first, read back as gone, and only then the
        /// device node, which is upstream's order.</summary>
        [Fact]
        public void Uninstall_TakesTheFiltersOffFirst_ThenTheDevice()
        {
            var m = FullInstall();
            m.Uninstall();

            Assert.Equal(4, m.Ran.Count);
            Assert.All(m.Ran.Take(3), a => Assert.StartsWith("--remove-class-filter", a));
            Assert.Equal(HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.XboxCompositeClass), m.Ran[0]);
            Assert.Equal(HidHideArm64Installer.RemoveDeviceArgs(), m.Ran[3]);
            Assert.Empty(m.Listed);
            Assert.False(m.Node);
            Assert.Empty(m.Log);
        }

        /// <summary>A removal that stops part way used to leave the filters
        /// off and the device in. That reads as "not installed", so the
        /// Uninstall button went away from the person who had just pressed
        /// it, and the way back was to install again first. Now the filters
        /// that were listed at the start are added again and the failure is
        /// reported. Here every add succeeds, so the state is a complete
        /// install that Uninstall can be tried on again.</summary>
        [Fact]
        public void Uninstall_PutsTheFiltersBack_WhenTheDeviceWillNotComeOut()
        {
            var m = FullInstall();
            m.Answer("remove ", 1);

            var ex = Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.Equal(1, ex.ExitCode);
            Assert.True(m.Listed.SetEquals(HidHideArm64Installer.FilteredClasses));
            Assert.True(m.Node);
            Assert.Empty(m.Log);
        }

        /// <summary>Install treats two of the three classes as best effort,
        /// so a complete install need not list all three. Only what was
        /// listed goes back: a failed removal must not leave a class
        /// filtered that was not filtered before it.</summary>
        [Fact]
        public void Uninstall_PutsBackOnlyTheFiltersThatWereThere()
        {
            var m = new Machine(HidHideArm64Installer.HidClass);
            m.Answer("remove ", 1);

            Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.True(m.Listed.SetEquals(new[] { HidHideArm64Installer.HidClass }));
            Assert.Equal(1, m.Adds);
        }

        /// <summary>nefcon answers 1 for "no device matched" as well as for a
        /// removal that failed (NefConUtil.cpp, the remove handler). A node
        /// that went away between the status refresh and the click is a
        /// finished removal, not a failed one.</summary>
        [Fact]
        public void Uninstall_IsDone_WhenTheDeviceNodeIsAlreadyGone()
        {
            var m = FullInstall();
            m.Node = false;
            m.Answer("remove ", 1);

            m.Uninstall();

            Assert.Empty(m.Listed);
            Assert.Equal(0, m.Adds);
        }

        /// <summary>A node that cannot be read back is not a node that is
        /// gone. The failure is reported, and nothing is written on a
        /// guess.</summary>
        [Fact]
        public void Uninstall_ReportsTheFailure_AndWritesNothing_WhenTheNodeCannotBeReadBack()
        {
            var m = FullInstall();
            m.Answer("remove ", 1);
            m.Node = null;

            var ex = Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.Equal(1, ex.ExitCode);
            Assert.Equal(0, m.Adds);
        }

        /// <summary>A filter that will not come off stops the removal before
        /// the device, and with the device node still there, whatever did
        /// come off goes back.</summary>
        [Fact]
        public void Uninstall_LeavesTheDeviceIn_AndRestoresTheRest_WhileAFilterStillNamesIt()
        {
            var m = FullInstall();
            m.Answer(HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.XnaCompositeClass), 5);

            var ex = Assert.Throws<HidHideSetupException>(() => m.Uninstall());

            Assert.Equal(HidHideSetupFailure.FilterStillListed, ex.Failure);
            Assert.DoesNotContain(HidHideArm64Installer.RemoveDeviceArgs(), m.Ran);
            Assert.True(m.Listed.SetEquals(HidHideArm64Installer.FilteredClasses));
            Assert.True(m.Node);
        }

        /// <summary>A command that outlived its wait may still be running,
        /// and a filter written now could land on either side of it. So
        /// nothing is put back, at whichever command it happened. What is
        /// reported is whatever stopped the removal: the timeout, or the
        /// outright failure of a later command. A removal that goes on to
        /// finish is reported as the success it is.</summary>
        [Theory]
        [InlineData("--remove-class-filter", true)]    // a filter removal, and that filter is still listed
        [InlineData("--remove-class-filter", false)]   // a filter removal that did its work before it hung, then the device fails
        [InlineData("remove ", true)]                  // the device removal
        public void Uninstall_PutsNothingBack_AfterACommandThatOutlivedItsWait(string command, bool filterStays)
        {
            var m = FullInstall();
            m.Answer(command, null);
            if (command == "--remove-class-filter" && !filterStays)
            {
                m.ACommandThatTimesOutStillDoesItsWork = true;
                m.Answer("remove ", 1);
            }

            var ex = Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.Equal(0, m.Adds);
            if (command == "--remove-class-filter" && !filterStays) Assert.Equal(1, ex.ExitCode);
            else Assert.True(ex.TimedOut);
        }

        /// <summary>Putting filters back completes an install, so there has
        /// to be one to complete. With the device node gone, or not readable,
        /// a filter that stayed listed is reported and the ones that came off
        /// stay off. The first version put them back without looking, which
        /// left a machine with no HidHide device filtered by HidHide.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public void Uninstall_PutsNothingBack_WhenAFilterStays_AndTheDeviceNodeIsNotThere(bool? node)
        {
            var m = FullInstall();
            m.Node = node;
            m.Answer(HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.XnaCompositeClass), 5);

            var ex = Assert.Throws<HidHideSetupException>(() => m.Uninstall());

            Assert.Equal(HidHideSetupFailure.FilterStillListed, ex.Failure);
            Assert.Equal(0, m.Adds);
            Assert.True(m.Listed.SetEquals(new[] { HidHideArm64Installer.XnaCompositeClass }));
        }

        /// <summary>nefcon can fail to start at all, and then the runner
        /// throws where it would have returned a code. That stops the removal
        /// as surely as an exit code does, so it gets the same recovery, and
        /// the exception that stopped it is the one the caller hears.</summary>
        [Theory]
        [InlineData("remove ")]                 // the filters are off, and the device removal throws
        [InlineData("--remove-class-filter")]   // the very first command throws
        public void Uninstall_RecoversTheSameWay_WhenACommandThrows(string command)
        {
            var m = FullInstall();
            m.Throws = a => a.StartsWith(command, StringComparison.Ordinal);

            Assert.Throws<InvalidOperationException>(() => m.Uninstall());

            Assert.True(m.Listed.SetEquals(HidHideArm64Installer.FilteredClasses));
            Assert.True(m.Node);
        }

        /// <summary>The same gate as Install: no filter for a driver that has
        /// not been seen running. The original failure is still the one
        /// reported.</summary>
        [Fact]
        public void Uninstall_PutsNothingBack_ForADriverThatDoesNotAnswer()
        {
            var m = FullInstall();
            m.Answer("remove ", 1);
            m.DriverAnswers = false;

            var ex = Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.Equal(1, ex.ExitCode);
            Assert.Equal(0, m.Adds);
            Assert.Single(m.Log);
        }

        /// <summary>Putting the filters back can fail too, by exit code or by
        /// throwing. Either way the removal's own failure is what the caller
        /// hears, and the second fault goes to the log.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Uninstall_KeepsItsOwnFailure_WhenPuttingTheFiltersBackFailsToo(bool byThrowing)
        {
            var m = FullInstall();
            m.Answer("remove ", 1);
            if (byThrowing) m.Throws = a => a.StartsWith("--add-class-filter", StringComparison.Ordinal);
            else m.Answer("--add-class-filter", 5);

            var ex = Assert.Throws<InstallerFailedException>(() => m.Uninstall());

            Assert.Equal(1, ex.ExitCode);
            Assert.NotEmpty(m.Log);
        }

        // ── What counts as installed ──

        /// <summary>Nothing registers an ARM64 install with Windows Installer,
        /// so it is read from what it leaves behind. All three, because a
        /// device with no filter yet is an install that stopped part way, and
        /// Install has to stay available to finish it.</summary>
        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        public void Installed_MeansTheServiceTheHidClassFilterAndTheDevice(
            bool service, bool filter, bool device, bool installed)
            => Assert.Equal(installed, HidHideArm64Installer.IsInstalled(() => service, () => filter, () => device));

        /// <summary>The status timer asks every five seconds on the UI
        /// thread. The device enumeration is the costly one of the three, so
        /// it runs only after both registry reads have said yes, and a
        /// machine without HidHide enumerates nothing.</summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void TheDevicesAreNotEnumerated_WhenTheRegistryAlreadySaysNo(bool service, bool filter)
        {
            bool enumerated = false;
            Assert.False(HidHideArm64Installer.IsInstalled(() => service, () => filter,
                                                          () => { enumerated = true; return true; }));
            Assert.False(enumerated);
        }

        /// <summary>One look per refresh. There were two methods, one for
        /// "installed" and one for the version, the status timer called both,
        /// and each made the whole look again.</summary>
        [Fact]
        public void TheStatusRefreshLooksOnce()
        {
            string window = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "MainWindow.xaml.cs")));
            string installer = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "DriverInstaller.cs")));

            Assert.Single(Regex.Matches(window, @"DriverInstaller\.TryGetHidHideStatus\("));
            Assert.DoesNotContain("IsHidHideInstalled()", installer);
            Assert.DoesNotContain("GetHidHideVersion()", installer);
        }

        // ── The watchdog's check ──

        /// <summary>A filter is taken out when the driver behind it is not
        /// there to load: the service is gone, or it is registered and
        /// stopped, which is upstream's watchdog condition and what a driver
        /// Windows refuses to load looks like. The check asked only whether
        /// the service key existed, so that second case was missed and the
        /// keyboards and mice it breaks would have stayed broken.</summary>
        [Theory]
        [InlineData("Absent", true)]
        [InlineData("Stopped", true)]
        [InlineData("Running", false)]
        [InlineData("Unknown", false)]
        public void AFilterIsDangling_WhenItsServiceIsGoneOrStopped(string state, bool dangling)
        {
            Func<Guid, bool> hidOnly = c => c == HidHideArm64Installer.HidClass;
            var found = HidHideArm64Installer.DanglingFilters(hidOnly, State(state));
            if (dangling) Assert.Equal(new[] { HidHideArm64Installer.HidClass }, found);
            else Assert.Empty(found);
        }

        [Fact]
        public void NothingIsDangling_WhenNoClassListsHidHide()
            => Assert.Empty(HidHideArm64Installer.DanglingFilters(_ => false, HidHideArm64Installer.ServiceState.Absent));

        /// <summary>A healthy driver, and a question that could not be asked,
        /// both end the check before the registry is read. An unknown state
        /// must never cost a working install its filters, which is what
        /// upstream's watchdog does with a failed query too.</summary>
        [Theory]
        [InlineData("Running")]
        [InlineData("Unknown")]
        public void TheRegistryIsNotRead_ForADriverThatRunsOrCannotBeAskedAbout(string state)
        {
            Func<Guid, bool> mustNotBeAsked = _ => throw new InvalidOperationException("the filter list was read");
            Assert.Empty(HidHideArm64Installer.DanglingFilters(mustNotBeAsked, State(state)));
        }

        // The enum is internal and a test method is public, so it travels by name.
        private static HidHideArm64Installer.ServiceState State(string name)
            => Enum.Parse<HidHideArm64Installer.ServiceState>(name);

        /// <summary>SERVICE_STOPPED is 1 and SERVICE_RUNNING is 4 (winsvc.h).
        /// The pending states in between and beyond are a service on its way
        /// somewhere, and the check runs once and puts nothing back, so it
        /// leaves them alone.</summary>
        [Theory]
        [InlineData(1u, "Stopped")]
        [InlineData(4u, "Running")]
        [InlineData(2u, "Unknown")]   // START_PENDING
        [InlineData(3u, "Unknown")]   // STOP_PENDING
        [InlineData(7u, "Unknown")]   // PAUSED
        [InlineData(0u, "Unknown")]
        public void OnlyStoppedAndRunningAreSettledStates(uint currentState, string expected)
            => Assert.Equal(State(expected), HidHideArm64Installer.FromCurrentState(currentState));

        /// <summary>The real question, asked of this machine. A name nothing
        /// registers is the one answer that counts as absent, and the RPC
        /// service runs on every Windows there is.</summary>
        [Fact]
        public void TheServiceControlManagerIsAskedForReal()
        {
            Assert.Equal(HidHideArm64Installer.ServiceState.Absent,
                         HidHideArm64Installer.QueryServiceState("PadForgeNoSuchService" + Guid.NewGuid().ToString("N")));
            Assert.Equal(HidHideArm64Installer.ServiceState.Running,
                         HidHideArm64Installer.QueryServiceState("RpcSs"));
        }

        /// <summary>The check used to discard every result and swallow every
        /// exception, so a filter it could not take out left no trace. Each
        /// removal that does not succeed is reported by class. The ones that
        /// do are covered by the line written before the first command, which
        /// names the service state and every class about to be acted on.</summary>
        [Fact]
        public void ARemovalThatDoesNotSucceedIsReported()
        {
            var s = new Script();
            s.Answers[HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.HidClass)] = 5;
            s.Answers[HidHideArm64Installer.RemoveFilterArgs(HidHideArm64Installer.XnaCompositeClass)] = null;
            var log = new List<string>();

            HidHideArm64Installer.RemoveFilters(s.Run, HidHideArm64Installer.FilteredClasses, log.Add);

            Assert.Equal(3, s.Ran.Count);
            Assert.Equal(2, log.Count);
            Assert.Contains("exit code 5", log[0]);
            Assert.Contains("still running", log[1]);
        }

        /// <summary>The startup check runs on its own thread. Unlocked, a
        /// check that had already listed what to remove could take a filter
        /// back out after an install had just put it in. Each of the three
        /// entry points holds the one lock for its whole body, and the check
        /// lists what to remove inside it.</summary>
        [Fact]
        public void InstallRemovalAndTheStartupCheckTakeTurns()
        {
            string source = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "HidHideArm64Installer.cs")));

            // An empty lock followed by the work would hold nothing, so the
            // pin is on what sits INSIDE the lock's braces.
            AssertInsideTheLock(source, "public static void Install()", "WithStagedTools(");
            AssertInsideTheLock(source, "public static void Uninstall()", "WithStagedTools(");
            AssertInsideTheLock(source, "public static void RemoveDanglingFilters()", "QueryServiceState(");
            AssertInsideTheLock(source, "public static void RemoveDanglingFilters()", "DanglingFilters(FilterListed");
            AssertInsideTheLock(source, "public static void RemoveDanglingFilters()", "WithStagedTools(");
        }

        private static void AssertInsideTheLock(string source, string entry, string work)
        {
            int at = source.IndexOf(entry, StringComparison.Ordinal);
            Assert.True(at >= 0, entry + " is gone");

            // Everything is looked for inside THIS method's braces. Unbounded,
            // an emptied method passed by finding the next method's lock.
            var method = Braces(source, at);
            Assert.True(method.Close > method.Open, entry + " has no body");

            int lockAt = source.IndexOf("lock (OperationLock)", method.Open, StringComparison.Ordinal);
            Assert.True(lockAt >= 0 && lockAt < method.Close, entry + " takes no lock");
            var held = Braces(source, lockAt);
            Assert.True(held.Close > held.Open && held.Close < method.Close, "the lock in " + entry + " has no body");

            int workAt = source.IndexOf(work, method.Open, StringComparison.Ordinal);
            Assert.True(workAt > held.Open && workAt < held.Close, work + " runs outside the lock in " + entry);
        }

        /// <summary>The first brace at or after a position, and the one that
        /// closes it.</summary>
        private static (int Open, int Close) Braces(string source, int from)
        {
            int open = source.IndexOf('{', from);
            if (open < 0) return (-1, -1);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return (open, i);
            }
            return (open, -1);
        }

        /// <summary>The pin above has to be able to tell a lock that covers
        /// the work from one that covers nothing, and a method that holds the
        /// lock from one that leaves it to the method after it.</summary>
        [Theory]
        [InlineData("public static void Install() { lock (OperationLock) { } { WithStagedTools(x); } }")]
        [InlineData("public static void Install() { } public static void Uninstall() { lock (OperationLock) { WithStagedTools(x); } }")]
        public void TheLockPinSeesALockThatCoversNothing(string hollow)
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                AssertInsideTheLock(hollow, "public static void Install()", "WithStagedTools("));
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
