using System;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Whether this process may open or write a real HID device through the
    /// shared HID helpers (<see cref="HidReportChannel"/>,
    /// <see cref="RawHidOutput"/>, <see cref="VendorHidReader"/>). Always
    /// yes in the app, where nothing sets the flag. The test host turns it
    /// off for its whole life, so a test that reaches one of those helpers
    /// with the path of a device on the bench PC gets a refusal in place of
    /// a write. A test hands its code a scripted channel, and that keeps it
    /// off the hardware while the code is right. This is for the build in
    /// which the code is wrong: one with a guard taken out to see whether a
    /// test notices, which is the build that would have reached the device.
    /// </summary>
    internal static class RealHidAccess
    {
        private static int s_offLimits;
        private static int s_allowed;

        /// <summary>True while the helpers refuse: the test host is loaded
        /// and no test has asked for a real device.</summary>
        internal static bool Refused
            => Volatile.Read(ref s_offLimits) != 0 && Volatile.Read(ref s_allowed) == 0;

        /// <summary>Set once by the test host. Nothing lifts it.</summary>
        internal static void SetOffLimitsForTest() => Volatile.Write(ref s_offLimits, 1);

        /// <summary>For a test that means to reach the real open or a real
        /// device: the helpers work until the returned scope is disposed.</summary>
        internal static IDisposable AllowForTest()
        {
            Interlocked.Increment(ref s_allowed);
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref s_allowed);
            }
        }
    }
}
