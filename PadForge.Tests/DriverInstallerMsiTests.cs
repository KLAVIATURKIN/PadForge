using System.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The two decisions behind a driver uninstall that used to be made
    /// wrongly without anyone being told.
    ///
    /// <para>The HidHide uninstall handed msiexec the BUNDLED package, which
    /// names the product inside that package and no other. Against an install
    /// with a different ProductCode msiexec answers 1605, and the exit code
    /// was never read, so the button reported success and removed nothing. The
    /// uninstall now names the installed product by its registered
    /// ProductCode, and the runner reads the code msiexec hands back. Both
    /// rules are pure, so they are pinned here without running an
    /// installer.</para>
    /// </summary>
    public class DriverInstallerMsiTests
    {
        // ── What msiexec's exit code means ──

        [Theory]
        [InlineData(0)]       // ERROR_SUCCESS
        [InlineData(3010)]    // ERROR_SUCCESS_REBOOT_REQUIRED
        [InlineData(1641)]    // ERROR_SUCCESS_REBOOT_INITIATED
        public void SuccessAndTheTwoRestartCodes_AreSuccessForEveryOperation(int exitCode)
        {
            Assert.True(DriverInstaller.IsMsiSuccess(exitCode, absentIsSuccess: false));
            Assert.True(DriverInstaller.IsMsiSuccess(exitCode, absentIsSuccess: true));
        }

        /// <summary>1605 says the product named is not installed. For an
        /// uninstall that named an INSTALLED product that is the goal, reached
        /// by someone else first. For an install, and for the fallback that
        /// names the bundled package while some other HidHide is still
        /// registered, it means nothing was done.</summary>
        [Fact]
        public void UnknownProduct_FinishesOnlyAnUninstallThatNamedAnInstalledProduct()
        {
            Assert.True(DriverInstaller.IsMsiSuccess(1605, absentIsSuccess: true));
            Assert.False(DriverInstaller.IsMsiSuccess(1605, absentIsSuccess: false));
        }

        [Theory]
        [InlineData(1602)]    // ERROR_INSTALL_USEREXIT
        [InlineData(1603)]    // ERROR_INSTALL_FAILURE
        [InlineData(1618)]    // ERROR_INSTALL_ALREADY_RUNNING
        [InlineData(1)]
        [InlineData(-1)]
        public void EverythingElse_IsAFailureForEveryOperation(int exitCode)
        {
            Assert.False(DriverInstaller.IsMsiSuccess(exitCode, absentIsSuccess: false));
            Assert.False(DriverInstaller.IsMsiSuccess(exitCode, absentIsSuccess: true));
        }

        [Fact]
        public void AFailureCarriesItsFacts_AndKnowsACancelFromAFault()
        {
            var failed = new InstallerFailedException(1603);
            Assert.Equal(1603, failed.ExitCode);
            Assert.False(failed.TimedOut);
            Assert.False(failed.UserCanceled);

            Assert.True(new InstallerFailedException(1602).UserCanceled);

            var timedOut = new InstallerFailedException();
            Assert.True(timedOut.TimedOut);
            Assert.False(timedOut.UserCanceled);
        }

        /// <summary>An installer Windows started without a process to follow
        /// is not a timeout. It used to read as one, and the user was told the
        /// installer was still running after three minutes when nothing had
        /// been waited on. Either way the outcome is unknown, so the staging
        /// folder stays.</summary>
        [Fact]
        public void AnUntrackedInstallerIsNotATimeout()
        {
            var untracked = InstallerFailedException.Untracked();
            Assert.True(untracked.NotTracked);
            Assert.False(untracked.TimedOut);
            Assert.True(untracked.OutcomeUnknown);
            Assert.False(untracked.UserCanceled);

            Assert.True(InstallerFailedException.NoExitCode(true).NotTracked);
            var waited = InstallerFailedException.NoExitCode(false);
            Assert.True(waited.TimedOut);
            Assert.False(waited.NotTracked);
            Assert.True(waited.OutcomeUnknown);
            Assert.False(new InstallerFailedException(1603).OutcomeUnknown);
        }

        /// <summary>The runner reports a missing process, every caller turns
        /// that into the untracked failure, every staging cleanup holds for
        /// either unknown outcome, and the status line has words for
        /// it.</summary>
        [Fact]
        public void EveryRunnerCallerKeepsTheUntrackedCaseApart()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string di = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Common", "DriverInstaller.cs"));
            Assert.Contains("if (proc == null)\n            {\n                notTracked = true;", di.Replace("\r\n", "\n"));
            Assert.DoesNotContain("when (ex.TimedOut)", di);
            // HidHide's install and uninstall, and the MIDI runtime's install.
            // The Visual C++ redistributable's runs carry theirs inside the
            // check failure that names that step, and the MIDI install keeps
            // its staging folder for it too.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(di, @"when \(ex\.OutcomeUnknown\)").Count);
            Assert.Contains("inner: InstallerFailedException.NoExitCode(untracked));", di);
            Assert.Contains("catch (DriverCheckException ex) when (ex.Failure == DriverCheckFailure.VcRedistStillRunning)", di);
            Assert.DoesNotContain("throw new InstallerFailedException();", di);

            string window = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "MainWindow.xaml.cs"));
            Assert.Contains("? (failed.NotTracked\n                    ? Strings.Instance.Status_InstallerNotTracked", window.Replace("\r\n", "\n"));

            string resx = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Resources", "Strings", "Strings.resx"));
            Assert.Matches("name=\"Status_InstallerNotTracked\"[^>]*><value>Windows started the installer", resx);
        }

        // ── Words for PadForge's own checks ──

        private static string Described(System.Exception ex, string culture)
        {
            var thread = System.Threading.Thread.CurrentThread;
            var before = thread.CurrentUICulture;
            try
            {
                thread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                return PadForge.MainWindow.DescribeDriverFailure(ex);
            }
            finally { thread.CurrentUICulture = before; }
        }

        /// <summary>The value the resx file of <paramref name="culture"/>
        /// holds for <paramref name="key"/>.</summary>
        private static string LocaleValue(string culture, string key)
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string file = culture == "en" ? "Strings.resx" : $"Strings.{culture}.resx";
            var data = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Resources", "Strings", file))
                .Root.Elements("data").Single(d => (string)d.Attribute("name") == key);
            return data.Element("value").Value;
        }

        /// <summary>Restart and reboot, as each language says them. No line
        /// here tells anyone to restart anything.</summary>
        private static readonly (string culture, string[] words)[] RestartWords =
        {
            ("en", new[] { "restart", "reboot" }),
            ("de", new[] { "neu starten", "neustart" }),
            ("es", new[] { "reinicia" }),
            ("fr", new[] { "redémarr" }),
            ("it", new[] { "riavvia" }),
            ("ja", new[] { "再起動" }),
            ("ko", new[] { "다시 시작", "재시작", "재부팅" }),
            ("nl", new[] { "opnieuw opstarten", "herstart" }),
            ("pt-BR", new[] { "reinicia" }),
            ("zh-Hans", new[] { "重启", "重新启动" }),
        };

        /// <summary>A stop on one of PadForge's own checks, and an ARM64
        /// HidHide setup that stopped short, carry their reason, and the
        /// status line words it in the UI language. The English sentence on
        /// the exception is the log's, and the line used to show it in every
        /// language. Each value the wording names reaches it, and no
        /// language's line says to restart.</summary>
        [Fact]
        public void EveryCheckFailureIsWordedInTheUiLanguage()
        {
            const string fallback = "The English sentence for the log.";
            var failures = new System.Collections.Generic.List<System.Exception>();
            foreach (var failure in System.Enum.GetValues<DriverCheckFailure>())
                failures.Add(new DriverCheckException(failure, fallback, @"C:\Games\SteamVR", "Update state (0x61) downloading"));
            foreach (var failure in System.Enum.GetValues<HidHideSetupFailure>())
                failures.Add(new HidHideSetupException(failure));
            // A Visual C++ Redistributable still running showed its English
            // message in every language, as a kind of its own that the
            // wording did not know. The release that timed out before a MIDI
            // runtime uninstall went the other way: its UI-language sentence
            // was the exception's message, so that is what the log got.
            var stillRunning = new DriverCheckException(DriverCheckFailure.VcRedistStillRunning,
                "The Visual C++ Redistributable installer is still running. Select Install again once it finishes.",
                inner: InstallerFailedException.Untracked());
            var releaseTimedOut = new DriverCheckException(DriverCheckFailure.MidiReleaseTimedOut, fallback);
            var releaseOnDriver = new DriverCheckException(DriverCheckFailure.MidiReleaseTimedOutOnDriver, fallback);

            foreach (var (culture, words) in RestartWords)
            {
                foreach (var ex in failures)
                {
                    string text = Described(ex, culture);
                    Assert.False(string.IsNullOrWhiteSpace(text), culture + " " + ex.GetType().Name);
                    Assert.NotEqual(fallback, text);
                    foreach (var word in words)
                        Assert.DoesNotContain(word, text, System.StringComparison.OrdinalIgnoreCase);
                }
                // German reads German, not the English fallback.
                if (culture == "de")
                    foreach (var ex in failures)
                        Assert.NotEqual(Described(ex, "en"), Described(ex, "de"));
                // The still-running line names the Visual C++ step in every
                // language, and only English reads the exception's own words.
                Assert.Contains("Visual C++", Described(stillRunning, culture));
                if (culture != "en")
                    Assert.NotEqual(stillRunning.Message, Described(stillRunning, culture));
                Assert.Equal(LocaleValue(culture, "Status_VcRedistStillRunning"), Described(stillRunning, culture));
                Assert.Equal(LocaleValue(culture, "Status_MidiReleaseTimedOut"), Described(releaseTimedOut, culture));
                // A release that waited behind a classic driver names the
                // driver, not the MIDI service.
                Assert.Equal(LocaleValue(culture, "Status_MidiReleaseTimedOutDriver"), Described(releaseOnDriver, culture));
            }

            var old = new DriverCheckException(DriverCheckFailure.VcRuntimeStillOld, fallback, "14.51");
            var notRemoved = new DriverCheckException(DriverCheckFailure.SteamVrNotRemoved, fallback, @"C:\Games\SteamVR");
            var didNotLand = new DriverCheckException(DriverCheckFailure.SteamVrDidNotLand, fallback, @"C:\Games\SteamVR", "Update state (0x61)");
            foreach (var (culture, _) in RestartWords)
            {
                Assert.Contains("14.51", Described(old, culture));
                Assert.Contains(@"C:\Games\SteamVR", Described(notRemoved, culture));
                Assert.Contains(@"C:\Games\SteamVR", Described(didNotLand, culture));
                Assert.Contains("Update state (0x61)", Described(didNotLand, culture));
            }

            // An exception of no known kind keeps its own message.
            Assert.Equal("disk full", Described(new System.IO.IOException("disk full"), "de"));
        }

        /// <summary>The status line is in the UI language, so the English
        /// sentence on the exception goes to the diagnostics log, from both
        /// places a driver operation's failure is caught.</summary>
        [Fact]
        public void AFailedOperationReachesTheLogInEnglish()
        {
            string fallback = "The English sentence for the log " + System.Guid.NewGuid().ToString("N") + ".";
            PadForge.MainWindow.LogDriverFailure(new DriverCheckException(DriverCheckFailure.VcRuntimeHeld, fallback));
            Assert.Contains("DRIVER operation failed: DriverCheckException: " + fallback, PadForge.Engine.SdlDiagLog.Snapshot());

            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string window = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "MainWindow.xaml.cs"));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(window, @"LogDriverFailure\(ex\);").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(window, @"_Format, DescribeDriverFailure\(ex\)\), persist: true\);").Count);
        }

        // ── The SteamVR folder that will not delete ──

        /// <summary>A SteamVR folder Windows still holds after every try
        /// fails as the typed check the status line words in the UI
        /// language. The last try's own exception left the uninstall
        /// instead, in Windows' words, the one failure of that method that
        /// was not typed.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASteamVrFolderThatWillNotDelete_FailsAsTheTypedCheck(bool accessDenied)
        {
            const string folder = @"C:\Games\SteamVR";
            int tries = 0, pauses = 0;
            var ex = Assert.Throws<DriverCheckException>(() => DriverInstaller.DeleteSteamVrFolder(folder,
                _ =>
                {
                    tries++;
                    throw accessDenied
                        ? new System.UnauthorizedAccessException("Access to the path is denied.")
                        : new System.IO.IOException("The process cannot access the file because it is being used by another process.");
                },
                _ => true,
                () => pauses++));
            Assert.Equal(DriverCheckFailure.SteamVrNotRemoved, ex.Failure);
            Assert.Equal(folder, ex.Detail);
            Assert.Equal(DriverInstaller.SteamVrDeleteTries, tries);
            Assert.Equal(DriverInstaller.SteamVrDeleteTries - 1, pauses);
            Assert.Contains(folder, Described(ex, "de"));
            Assert.NotEqual(ex.Message, Described(ex, "de"));
        }

        /// <summary>A delete that gets through on a later try removes the
        /// folder, and one that throws as the folder goes is done as
        /// well.</summary>
        [Fact]
        public void ASteamVrFolderThatDeletesOnALaterTry_IsRemoved()
        {
            int tries = 0, pauses = 0;
            bool there = true;
            DriverInstaller.DeleteSteamVrFolder(@"C:\Games\SteamVR",
                _ =>
                {
                    if (++tries < 3) throw new System.IO.IOException("in use");
                    there = false;
                },
                _ => there,
                () => pauses++);
            Assert.Equal(3, tries);
            Assert.Equal(2, pauses);

            // Every try throws, and the folder is gone all the same.
            tries = 0;
            DriverInstaller.DeleteSteamVrFolder(@"C:\Games\SteamVR",
                _ => { tries++; throw new System.IO.DirectoryNotFoundException("gone"); },
                _ => false,
                () => { });
            Assert.Equal(DriverInstaller.SteamVrDeleteTries, tries);
        }

        // ── Which registry entry names a product msiexec can act on ──

        private const string Code = "{01E0AB21-D1CC-42B4-9DFF-84FFE4F26DAF}";

        /// <summary>The shape Windows Installer writes, read off a machine
        /// with HidHide 1.5.230 installed: the key is the ProductCode and
        /// WindowsInstaller is the DWORD 1.</summary>
        [Fact]
        public void AWindowsInstallerProduct_GivesItsKeyAsTheProductCode()
        {
            Assert.Equal(Code, DriverInstaller.MsiProductCodeOrNull(Code, 1));
        }

        /// <summary>A bootstrapper registers itself under a brace GUID as
        /// well, and that GUID names a bundle, which msiexec answers 1605 to.
        /// The key's shape alone used to decide this.</summary>
        [Theory]
        [InlineData(null)]    // no WindowsInstaller value at all
        [InlineData(0)]
        [InlineData("1")]     // a string is not the DWORD Windows Installer writes
        public void ABraceGuidThatIsNotAnMsiProduct_GivesNoProductCode(object windowsInstaller)
        {
            Assert.Null(DriverInstaller.MsiProductCodeOrNull(Code, windowsInstaller));
        }

        [Theory]
        [InlineData("HidHide 1.5.230")]
        [InlineData("{not-a-guid}")]
        [InlineData("01E0AB21-D1CC-42B4-9DFF-84FFE4F26DAF")]
        [InlineData("")]
        [InlineData(null)]
        public void AKeyThatIsNotABraceGuid_GivesNoProductCode(string key)
        {
            Assert.Null(DriverInstaller.MsiProductCodeOrNull(key, 1));
        }
    }
}
