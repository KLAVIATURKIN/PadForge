using System;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The gate in front of every call that loads a VR runtime's library or
    /// asks a runtime for an instance: the OpenXR opener and loader
    /// (OpenXrSession) and the OpenVR client library
    /// (OpenVrConsumerService).
    ///
    /// <para>In the app the gate is always open, and passing it is one null
    /// check. It exists for the test process. Asking a runtime for an
    /// instance starts that runtime, and a test host that reached the OpenXR
    /// opener started SteamVR on the build PC twice, on 2026-10-11 and on
    /// 2026-10-12, both times through a mutation of the manifest check under
    /// a test that still ran the opener the app uses. The test assembly
    /// closes the gate for the life of its process (VrRuntimeOffLimits in
    /// PadForge.Tests), and a call that would reach a runtime then throws
    /// here. One test exists to reach a real runtime, and it opens the gate
    /// for itself by name.</para>
    /// </summary>
    public static class VrRuntimeGate
    {
        /// <summary>Test seam: null in production, and nothing in the app or
        /// the engine sets it. The test assembly sets it for the life of its
        /// process. It answers whether the calling test may reach a real
        /// runtime.</summary>
        internal static volatile Func<bool> MayReachForTest;

        /// <summary>
        /// Passes in the app. In a test process it throws, unless the test
        /// that calls has opened the gate for itself.
        /// <paramref name="what"/> names what was about to be loaded or
        /// asked, for the message.
        /// </summary>
        public static void Enter(string what) => Enter(what, MayReachForTest);

        /// <summary>The same against a seam that is handed in. With none,
        /// which is the app's state, every call passes.</summary>
        internal static void Enter(string what, Func<bool> may)
        {
            if (may == null || may()) return;
            throw new InvalidOperationException(
                $"A test reached {what}. That loads a VR runtime's library or asks a runtime for an instance, " +
                "and asking SteamVR for an instance starts SteamVR. Hand the source or the session a scripted " +
                "opener or loader. No test reaches a real runtime but the runtime probe, by its name.");
        }
    }
}
