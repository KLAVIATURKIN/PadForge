using System.Runtime.CompilerServices;
using PadForge.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process from installing, uninstalling, downloading or
    /// deleting anything on this PC through <see cref="DriverInstaller"/>,
    /// for its whole life.
    /// </summary>
    /// <remarks>
    /// The installs and uninstalls are tested through stand-ins: a scripted
    /// run in place of the Visual C++ Redistributable, a delete and an
    /// exists check in place of the SteamVR folder's, a seam that stops the
    /// SteamVR install after its argument checks. A test that gets past its
    /// stand-in, or a change that removes the check in front of the real
    /// call, would otherwise run msiexec, a runtime's uninstaller or
    /// steamcmd on the PC the tests run on, download an installer, write to
    /// the registry, or delete the SteamVR folder. With this set, every
    /// process start, download and registry write in the class refuses, and
    /// so does a folder delete outside the temp folder. Nothing in the
    /// application sets it (MachineOffLimitsTests).
    /// </remarks>
    internal static class DriverInstallerOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => DriverInstaller.OffLimitsForTest = true;
#pragma warning restore CA2255
    }
}
