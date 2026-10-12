using System.Runtime.CompilerServices;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process off the drivers and the TouchSense mice of
    /// the PC it runs on for its whole life.
    /// </summary>
    /// <remarks>
    /// The tests of the driver bindings work out what would be bound from
    /// scripted nodes, and the tests of the TouchSense mice hand their code
    /// a scripted look, a scripted channel and a scripted collection. A
    /// mutation run builds the code with a guard taken out, and a guard is
    /// often what stood between a test and the real call. With these set
    /// the installer changes no driver, and nothing looks for, opens or
    /// writes a real mouse, so such a build fails its test and leaves the
    /// PC as it was. A mouse bound to WinUSB from a test would leave its
    /// owner without a pointer. A test that means to reach the real open
    /// asks for it by name (<see cref="RealTouchSenseAccess.AllowForTest"/>).
    /// Nothing lifts the installer's flag.
    /// </remarks>
    internal static class TouchSenseOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set()
        {
            VendorUsbDriverInstaller.NoDriverChangesForTest = true;
            RealTouchSenseAccess.KeepOutForTest();
        }
#pragma warning restore CA2255
    }
}
