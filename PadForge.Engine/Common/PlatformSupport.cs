using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>
    /// What this build can do on this machine, for the features whose
    /// native half does not exist for every architecture (4.5.1, ARM64).
    ///
    /// <para>Two different architectures decide two different kinds of
    /// feature, and mixing them up is the whole hazard here.</para>
    ///
    /// <para>A KERNEL DRIVER follows the MACHINE. A kernel driver cannot run
    /// emulated, so an x64 PadForge running under emulation on ARM64 Windows
    /// still cannot install an x64 driver. Those features ask
    /// <see cref="RuntimeInformation.OSArchitecture"/>, which reports the
    /// real machine under emulation.</para>
    ///
    /// <para>An IN-PROCESS LIBRARY follows the PROCESS. An x64 DLL loads fine
    /// into an emulated x64 process on an ARM64 machine, and cannot load
    /// into a native ARM64 process at all. Those features ask
    /// <see cref="RuntimeInformation.ProcessArchitecture"/>.</para>
    ///
    /// <para>So the x64 build on an ARM64 machine loses HidHide and keeps
    /// voice macros and Sensa, while the ARM64 build loses all three. Every
    /// rule is a pure function of the architecture it is handed, so each one
    /// is testable on a bench that has only one architecture to offer.</para>
    /// </summary>
    public static class PlatformSupport
    {
        /// <summary>The machine's real architecture, emulation or not.</summary>
        public static Architecture MachineArchitecture => RuntimeInformation.OSArchitecture;

        /// <summary>The architecture this process was built for and runs as.</summary>
        public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

        /// <summary>True on ARM64 Windows, whichever build is running.</summary>
        public static bool IsArm64Machine => MachineArchitecture == Architecture.Arm64;

        // ── Kernel drivers: the machine decides ──────────────────────────

        /// <summary>HidHide is a kernel filter driver and upstream publishes
        /// an x64 package only (nefarius/HidHide#57: the driver builds for
        /// ARM64, its setup does not, and no signed ARM64 release exists).
        /// </summary>
        public static bool HidHideAvailableOn(Architecture machine)
            => machine != Architecture.Arm64;

        /// <inheritdoc cref="HidHideAvailableOn"/>
        public static bool HidHideAvailable => HidHideAvailableOn(MachineArchitecture);

        // ── In-process libraries: the process decides ────────────────────

        /// <summary>The Vosk recognizer rides libvosk.dll, and the Vosk
        /// package carries a Windows x64 build and nothing else. Voice macros
        /// do NOT go away without it: VoiceMacroService falls back to SAPI
        /// whenever the Vosk model store is not ready, and SAPI is managed, so
        /// an ARM64 build keeps voice macros on the fallback recognizer.
        /// </summary>
        public static bool VoskAvailableOn(Architecture process)
            => process != Architecture.Arm64;

        /// <inheritdoc cref="VoskAvailableOn"/>
        public static bool VoskAvailable => VoskAvailableOn(ProcessArchitecture);

        /// <summary>Razer Sensa HD haptics ride HAR.dll and its Razer
        /// provider, which the Interhaptics SDK ships for Win32 and x64 only.
        /// </summary>
        public static bool SensaAvailableOn(Architecture process)
            => process != Architecture.Arm64;

        /// <inheritdoc cref="SensaAvailableOn"/>
        public static bool SensaAvailable => SensaAvailableOn(ProcessArchitecture);
    }
}
