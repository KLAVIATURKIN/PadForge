using System.Runtime.CompilerServices;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process off the HID devices of the PC it runs on for
    /// its whole life.
    /// </summary>
    /// <remarks>
    /// Every test hands the code under test a scripted channel. A mutation
    /// run builds the code with a guard taken out, and a guard is often what
    /// stood between a test and the shared helpers that open a device path.
    /// With this set those helpers refuse, so such a build fails its test
    /// and writes nothing to a keyboard, a mouse or a receiver on the bench.
    /// A test that means to reach a device asks for it by name
    /// (<see cref="RealHidAccess.AllowForTest"/>).
    /// </remarks>
    internal static class HidDevicesOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set() => RealHidAccess.SetOffLimitsForTest();
#pragma warning restore CA2255
    }
}
