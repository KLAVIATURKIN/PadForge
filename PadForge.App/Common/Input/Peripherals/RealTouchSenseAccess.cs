using System;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Whether this process may reach a real TouchSense mouse (#494): look
    /// for the bound mice on this PC
    /// (<see cref="PadForge.Services.TouchSenseBinding.FindBound"/>), open
    /// one's WinUSB interface (<see cref="TouchSenseChannel.Open(string)"/>,
    /// <see cref="WinUsbTouchSenseUsb.Open"/>), or make a transfer on one.
    /// Always yes in the app, where nothing sets the flag. The test host
    /// turns it off for its whole life, the rule
    /// <see cref="RealHidAccess"/> keeps for the HID helpers: a test hands
    /// its code a scripted look and a scripted channel, and that keeps it
    /// off the hardware while the code is right. This is for the build in
    /// which the code is wrong, one with a guard taken out to see whether a
    /// test notices. Other WinUSB devices are on a bench PC, and a mouse
    /// opened or written to from a test is the owner's own.
    /// </summary>
    internal static class RealTouchSenseAccess
    {
        private static int s_keptOut;
        private static int s_allowed;

        /// <summary>True while the look, the open and every transfer
        /// refuse: the test host is loaded and no test has asked for the
        /// real ones.</summary>
        internal static bool Refused
            => Volatile.Read(ref s_keptOut) != 0 && Volatile.Read(ref s_allowed) == 0;

        /// <summary>Set once by the test host. Nothing lifts it.</summary>
        internal static void KeepOutForTest() => Volatile.Write(ref s_keptOut, 1);

        /// <summary>For a test that means to reach the real open, with a
        /// path that names no device, or a live test of a real mouse: the
        /// refusal is lifted until the returned scope is disposed.</summary>
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
