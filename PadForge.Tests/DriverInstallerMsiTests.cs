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
