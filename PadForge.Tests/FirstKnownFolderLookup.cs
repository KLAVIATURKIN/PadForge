using System;
using System.Runtime.CompilerServices;

namespace PadForge.Tests
{
    /// <summary>
    /// Makes the test process's first known-folder lookup before any test runs.
    /// </summary>
    /// <remarks>
    /// The first known-folder lookup in a process resolves windows.storage's delay-loaded
    /// SHCreatePropertyBagOnRegKey while it holds the known-folder lock, and resolving it
    /// waits for the loader. On a PC with an NVIDIA GPU, WPF's render thread loads the
    /// driver's NvMemMapStoragex.dll the first time it creates Direct3D, and that DLL's
    /// DllMain looks up a known folder while it holds the loader. When a test made the
    /// first lookup while a WPF test started rendering, each thread held what the other
    /// needed, and the run hung for hours (2026-09-23). This lookup resolves the import
    /// before any test can start WPF.
    /// </remarks>
    internal static class FirstKnownFolderLookup
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Run() => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
#pragma warning restore CA2255
    }
}
