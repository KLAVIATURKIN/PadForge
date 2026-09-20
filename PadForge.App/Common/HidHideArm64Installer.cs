using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace PadForge.Common
{
    /// <summary>Why an ARM64 HidHide install or removal stopped short.</summary>
    public enum HidHideSetupFailure
    {
        /// <summary>The driver installed and its control device never
        /// opened, so the device was taken back out and no filter added.</summary>
        DriverDidNotStart,

        /// <summary>A class still lists HidHide after the removal, so the
        /// driver was left in place.</summary>
        FilterStillListed,
    }

    /// <summary>A stop the installer chose. It is shown through the same
    /// "Driver operation failed" line every other installer fault uses.</summary>
    public sealed class HidHideSetupException : Exception
    {
        public HidHideSetupFailure Failure { get; }

        public HidHideSetupException(HidHideSetupFailure failure)
            : base(failure == HidHideSetupFailure.DriverDidNotStart
                ? "HidHide installed but its control device did not open, so no class filter was added."
                : "HidHide is still listed as a class filter, so the driver was left in place.")
        {
            Failure = failure;
        }
    }

    /// <summary>
    /// Installs and removes HidHide on Windows ARM64, where upstream's MSI does
    /// not apply.
    ///
    /// <para>Upstream publishes a Microsoft-signed ARM64 driver package
    /// (<c>drivers/HidHide_ARM64.zip</c>, driver 1.6.280.0) and documents the
    /// manual install at
    /// docs.nefarius.at/projects/HidHide/Manual-Installation-ARM64. This class
    /// performs those documented steps with upstream's own tool, nefcon, in
    /// the order upstream's installer uses (HidHide <c>Installer/Program.cs</c>,
    /// OnAfterInstall and OnBeforeUninstall): the device and driver first,
    /// then the three class filters, and on removal the filters first, then
    /// the device.</para>
    ///
    /// <para>nefconc.exe is run as a child process rather than making the
    /// SetupAPI calls from this process. The bundled one is a native ARM64
    /// image, so the install is performed by native code whether PadForge
    /// itself is the ARM64 build or the x64 build running emulated. Microsoft
    /// documents that emulation covers user mode only and says nothing about
    /// driver installation from an emulated caller, and upstream's own ARM64
    /// setup and libwdi both hand the job to a native helper for that
    /// reason.</para>
    ///
    /// <para>One step here is not in the manual procedure. A class filter
    /// entry that names a driver which is not there "can prevent the whole
    /// device class from starting" (nefcon README), and for HIDClass that
    /// class is every keyboard and mouse. Upstream's x64 setup ships a
    /// watchdog service that removes such entries. The manual procedure has
    /// none, so the filters are added here only after the driver's control
    /// device has opened, and <see cref="RemoveDanglingFilters"/> is the
    /// watchdog's check, run at startup.</para>
    /// </summary>
    internal static class HidHideArm64Installer
    {
        internal const string DriverPackageResource = "HidHide_ARM64.zip";
        internal const string NefconResource = "nefconc.exe";
        internal const string HardwareId = @"root\HidHide";
        internal const string ServiceName = "HidHide";

        /// <summary>The three device setup classes HidHide filters, in the
        /// order upstream adds them. HIDClass is the one that matters for a
        /// controller. The other two exist for Xbox 360 era hardware and may
        /// be absent on a given machine, which is why upstream treats a
        /// failure there as ignorable.</summary>
        internal static readonly Guid HidClass = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        internal static readonly Guid XnaCompositeClass = new Guid("d61ca365-5af4-4486-998b-9db4734c6ca3");
        internal static readonly Guid XboxCompositeClass = new Guid("05f5cfe2-4733-4950-a6bb-07aad01a3a84");

        internal static readonly Guid[] FilteredClasses = { HidClass, XnaCompositeClass, XboxCompositeClass };

        // ── nefcon command lines, exactly as upstream writes them ──

        internal static string InstallArgs(string infPath)
            => $"install \"{infPath}\" \"{HardwareId}\" --no-duplicates --remove-duplicates";

        internal static string RemoveDeviceArgs()
            => $"remove \"{HardwareId}\"";

        internal static string AddFilterArgs(Guid deviceClass)
            => $"--add-class-filter --position upper --service-name {ServiceName} --class-guid {deviceClass:D}";

        internal static string RemoveFilterArgs(Guid deviceClass)
            => $"--remove-class-filter --position upper --service-name {ServiceName} --class-guid {deviceClass:D}";

        /// <summary>nefcon answers 0, or 3010 when Windows wants a restart to
        /// finish (NefConUtil.cpp returns ERROR_SUCCESS_REBOOT_REQUIRED from
        /// every command that can need one). An install also answers 259,
        /// ERROR_NO_MORE_ITEMS, when the driver already on the device is as
        /// good as the one offered, which is how
        /// UpdateDriverForPlugAndPlayDevices reports "nothing to do".</summary>
        internal static bool IsSuccess(int exitCode, bool installing)
            => exitCode == 0 || exitCode == 3010 || (installing && exitCode == 259);

        /// <summary>Runs one nefcon command and returns its exit code, or null
        /// when it was still running after the wait.</summary>
        internal delegate int? NefconRunner(string arguments);

        // ── Install ──

        /// <summary>
        /// The install sequence over seams, so its order and its refusals can
        /// be tested without a driver.
        /// </summary>
        /// <param name="run">Runs nefcon.</param>
        /// <param name="infPath">The extracted HidHide.inf.</param>
        /// <param name="driverAnswers">Whether HidHide's control device opens.
        /// Asked after the device is installed and before any filter is
        /// added.</param>
        internal static void Install(NefconRunner run, string infPath, Func<bool> driverAnswers)
        {
            int? installed = run(InstallArgs(infPath));
            Require(installed, installing: true);

            // The gate. With the driver not answering, a HIDClass filter entry
            // would name a driver Windows cannot start, and every HID device
            // on the machine would fail to start with it.
            if (!driverAnswers())
            {
                // Windows itself answered 3010: it will not start this
                // driver until a restart. That is reported as what it is, the
                // installer's own exit code, through the line every installer
                // fault uses. The device stays in and no filter is added, so
                // Install stays available and finishes the job afterward.
                if (installed == 3010) throw new InstallerFailedException(3010);

                // No restart was asked for and the driver still does not
                // answer. Take the device back out and add nothing.
                run(RemoveDeviceArgs());
                throw new HidHideSetupException(HidHideSetupFailure.DriverDidNotStart);
            }

            // HIDClass has to take. The Xbox 360 era classes are best effort,
            // as they are in upstream's installer.
            Require(run(AddFilterArgs(HidClass)), installing: false);
            run(AddFilterArgs(XnaCompositeClass));
            run(AddFilterArgs(XboxCompositeClass));
        }

        // ── Uninstall ──

        /// <summary>
        /// Filters first, confirmed gone, and only then the device, which is
        /// upstream's order and the one nefcon's own
        /// <c>--uninstall-filter-driver</c> enforces: removing the driver
        /// while an entry still names it is the state that stops a device
        /// class from starting.
        /// </summary>
        /// <param name="filterStillListed">Whether HidHide is still in the
        /// class's UpperFilters, read back from the registry.</param>
        internal static void Uninstall(NefconRunner run, Func<Guid, bool> filterStillListed)
        {
            // Upstream takes them off in the reverse of the order it adds them.
            for (int i = FilteredClasses.Length - 1; i >= 0; i--)
                run(RemoveFilterArgs(FilteredClasses[i]));

            foreach (Guid deviceClass in FilteredClasses)
                if (filterStillListed(deviceClass))
                    throw new HidHideSetupException(HidHideSetupFailure.FilterStillListed);

            Require(run(RemoveDeviceArgs()), installing: false);
        }

        private static void Require(int? exitCode, bool installing)
        {
            if (exitCode == null) throw new InstallerFailedException();
            if (!IsSuccess(exitCode.Value, installing)) throw new InstallerFailedException(exitCode.Value);
        }

        // ── What is on this machine ──

        /// <summary>The driver file the HidHide service names, or null when no
        /// such service is registered. This is what tells an ARM64 install
        /// apart, because nothing registers it with Windows Installer.</summary>
        internal static string RegisteredDriverPath()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceName, false);
                return ExpandServiceImagePath(key?.GetValue("ImagePath") as string);
            }
            catch { return null; }
        }

        /// <summary>A service ImagePath in the forms a kernel driver uses, as
        /// a path File can open: <c>\SystemRoot\System32\drivers\x.sys</c>,
        /// <c>System32\drivers\x.sys</c> or <c>\??\C:\...</c>.</summary>
        internal static string ExpandServiceImagePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string p = imagePath.Trim().Trim('"');
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windows, p.Substring(@"\SystemRoot\".Length));
            if (p.StartsWith(@"\??\", StringComparison.Ordinal))
                return p.Substring(@"\??\".Length);
            if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windows, p);
            return Environment.ExpandEnvironmentVariables(p);
        }

        /// <summary>The installed driver's file version, or null.</summary>
        internal static string InstalledDriverVersion()
        {
            try
            {
                string path = RegisteredDriverPath();
                if (path == null || !File.Exists(path)) return null;
                string v = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        /// <summary>Whether HidHide is in a device class's UpperFilters.</summary>
        internal static bool FilterListed(Guid deviceClass)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\" + deviceClass.ToString("B"), false);
                return ListsService(key?.GetValue("UpperFilters") as string[]);
            }
            catch { return false; }
        }

        internal static bool ListsService(string[] upperFilters)
        {
            if (upperFilters == null) return false;
            foreach (string entry in upperFilters)
                if (string.Equals(entry, ServiceName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The classes whose filter list names HidHide while no
        /// HidHide service is registered, which is the state that stops those
        /// classes from starting.</summary>
        internal static List<Guid> DanglingFilters(Func<Guid, bool> filterListed, bool serviceRegistered)
        {
            var dangling = new List<Guid>();
            if (serviceRegistered) return dangling;
            foreach (Guid deviceClass in FilteredClasses)
                if (filterListed(deviceClass)) dangling.Add(deviceClass);
            return dangling;
        }

        // ── The real thing ──

        /// <summary>Installs HidHide on this ARM64 machine. As on x64 it asks
        /// for no restart: a controller picks the filter up the next time it
        /// connects, which the Hide from Games tooltip already says.</summary>
        public static void Install()
        {
            WithStagedTools((run, staging) =>
            {
                string driverDir = Path.Combine(staging, "driver");
                using (var zip = ZipFile.OpenRead(Path.Combine(staging, DriverPackageResource)))
                    zip.ExtractToDirectory(driverDir);

                string[] inf = Directory.GetFiles(driverDir, "HidHide.inf", SearchOption.AllDirectories);
                if (inf.Length == 0) throw new FileNotFoundException("HidHide.inf is not in the ARM64 driver package.");

                Install(run, inf[0], WaitForControlDevice);
            }, needsDriverPackage: true);
        }

        /// <summary>
        /// Whether HidHide is installed the ARM64 way. Nothing registers this
        /// install with Windows Installer, so it is read from what the install
        /// leaves behind: the service, the device node and the HIDClass
        /// filter. All three, because a device with no filter yet is an
        /// install that stopped for a restart, and Install has to stay
        /// available to finish it.
        /// </summary>
        public static bool IsInstalled()
            => IsInstalled(RegisteredDriverPath() != null, DeviceNodePresent(), FilterListed(HidClass));

        internal static bool IsInstalled(bool serviceRegistered, bool deviceNodePresent, bool hidClassFiltered)
            => serviceRegistered && deviceNodePresent && hidClassFiltered;

        private static readonly Guid SystemDeviceClass = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");

        private static bool DeviceNodePresent()
        {
            try
            {
                return Nefarius.Utilities.DeviceManagement.PnP.Devcon.FindInDeviceClassByHardwareId(
                    SystemDeviceClass, HardwareId, out _, true, false);
            }
            catch { return false; }
        }

        /// <summary>Removes HidHide from this ARM64 machine.</summary>
        public static void Uninstall()
            => WithStagedTools((run, _) => Uninstall(run, FilterListed), needsDriverPackage: false);

        /// <summary>
        /// The check upstream's watchdog service makes on x64
        /// (HidHide <c>Watchdog/App.cpp</c>, "Prevents bricked HID devices"):
        /// when a class still lists HidHide and the service is gone, take the
        /// entry out. Only an ARM64 machine needs it from here, because only
        /// there is HidHide installed without that watchdog. Never throws.
        /// </summary>
        public static void RemoveDanglingFilters()
        {
            try
            {
                var dangling = DanglingFilters(FilterListed, RegisteredDriverPath() != null);
                if (dangling.Count == 0) return;
                WithStagedTools((run, _) =>
                {
                    foreach (Guid deviceClass in dangling) run(RemoveFilterArgs(deviceClass));
                }, needsDriverPackage: false);
            }
            catch { }
        }

        /// <summary>The driver starts when its device node does, which can
        /// trail the install by a moment. Five seconds is the wait, in line
        /// with the restart timeouts nefcon itself defaults to.</summary>
        private static bool WaitForControlDevice()
        {
            for (int i = 0; i < 25; i++)
            {
                if (HidHideController.TryProbe(out int error)) return true;
                // Sharing violation: the device is there and another program
                // (HidHide's own client) holds it, which answers the question.
                if (error == 32) return true;
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        private static void WithStagedTools(Action<NefconRunner, string> work, bool needsDriverPackage)
        {
            string staging = Path.Combine(Path.GetTempPath(), "PadForge_HidHide", Guid.NewGuid().ToString("N"));
            bool toolMayStillRun = false;
            try
            {
                string nefcon = DriverInstaller.ExtractEmbeddedResource(NefconResource, staging);
                if (needsDriverPackage) DriverInstaller.ExtractEmbeddedResource(DriverPackageResource, staging);

                int? Run(string arguments)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = nefcon,
                        Arguments = arguments,
                        WorkingDirectory = staging,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return null;
                    if (!proc.WaitForExit(120_000)) { toolMayStillRun = true; return null; }
                    return proc.ExitCode;
                }

                work(Run, staging);
            }
            finally
            {
                // A tool that outlived its wait is still using these files.
                if (!toolMayStillRun) DriverInstaller.CleanupTempDir(staging);
            }
        }
    }
}
