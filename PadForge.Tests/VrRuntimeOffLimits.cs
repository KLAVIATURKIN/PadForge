using System;
using System.Runtime.CompilerServices;
using System.Threading;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process away from this PC's VR runtimes for its whole
    /// life.
    /// </summary>
    /// <remarks>
    /// Asking an OpenXR runtime for an instance starts that runtime. A test
    /// that ran the opener the app uses did that to SteamVR twice, on
    /// 2026-10-11 and on 2026-10-12: a mutation of the manifest check let a
    /// manifest that is off disk through to the runtimes the PC has
    /// registered. With the gate closed here, the opener and the loader of
    /// the app, and the OpenVR client library, throw in a test in place of
    /// reaching a runtime, whatever the test, the mutation or a later edit
    /// does. One test exists to reach a real runtime, the probe that also
    /// needs PADFORGE_OPENXR_PROBE=1, and it opens the gate for itself by
    /// its name.
    /// </remarks>
    internal static class VrRuntimeOffLimits
    {
        /// <summary>The one test that may reach a real runtime.</summary>
        internal const string Probe = "OpenXrRuntimeProbeTests.TheNativePathReachesARuntimeAndComesBack";

        /// <summary>The variable a person sets to run the probe.</summary>
        internal const string ProbeVariable = "PADFORGE_OPENXR_PROBE";

        // Flows into the threads the test that set it starts, and into no
        // other test's.
        private static readonly AsyncLocal<string> s_allowed = new();

#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => VrRuntimeGate.MayReachForTest = () => s_allowed.Value == Probe;
#pragma warning restore CA2255

        /// <summary>
        /// Opens the gate for one test, by its name, until the returned
        /// scope is disposed, on the calling test's own flow and the
        /// threads it starts. Only the probe may, and only when a person
        /// asked for it.
        /// </summary>
        internal static IDisposable AllowFor(string test)
        {
            if (!string.Equals(test, Probe, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{test} may not reach a VR runtime. The one test that may is {Probe}.");
            if (!string.Equals(Environment.GetEnvironmentVariable(ProbeVariable), "1", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{Probe} reaches a VR runtime only when {ProbeVariable}=1 is set, which starts that runtime.");
            s_allowed.Value = test;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose() => s_allowed.Value = null;
        }
    }
}
