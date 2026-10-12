using System.Runtime.CompilerServices;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process off the mutex third-party Razer tools share
    /// for its whole life.
    /// </summary>
    /// <remarks>
    /// The Razer direct worker takes Global\RazerLinkReadWriteGuardMutex
    /// around each request and its reply, and so do the tools on the bench
    /// PC that drive a Razer device. Every worker test hands the worker a
    /// scripted lock, and every test of the lock itself gives it a mutex of
    /// its own. A mutation run builds the code with a guard taken out, and
    /// such a build could reach the shared name and hold up a tool that
    /// waits for it. With this set, a lock on that name is never made,
    /// opened or taken: the guard goes on without one, as it does where
    /// the mutex cannot be had.
    /// </remarks>
    internal static class RazerMutexOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => RazerGuard.KeepOffTheSharedMutexForTest();
#pragma warning restore CA2255
    }
}
