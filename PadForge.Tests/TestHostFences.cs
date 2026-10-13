using System.Runtime.CompilerServices;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Tablets;
using PadForge.Services;
using PadForge.SteamWorkshop.Api;

// Each class below keeps the test process off one part of the PC it runs on,
// for its whole life. A test hands the code under test a fake, a seam or a
// path of its own. A mutation run builds the code with a guard taken out, and
// a guard is often what stood between a test and the real call. With these
// set the real calls refuse, so such a build fails its test and leaves the
// desktop, the devices, the radio, the drivers, the sound, the other vendors'
// programs and the network of the bench as they were. Nothing in the app sets
// any of them (TestHostFenceTests).
namespace PadForge.Tests
{
#pragma warning disable CA2255 // Only the test host loads this assembly.

    /// <summary>No device is opened by the writers that make their own
    /// handles, and SDL's device layer and the smart card service are not
    /// started.</summary>
    internal static class DeviceOutputOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => DirectDeviceAccess.OffLimitsForTest = true;
    }

    /// <summary>Nothing is handed to the shell: no page, folder or program
    /// is opened, and the clipboard stays as it is.</summary>
    internal static class ShellOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => ShellLaunch.OffLimitsForTest = true;
    }

    /// <summary>No socket or listener is bound beyond loopback.</summary>
    internal static class ListenerOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => NetworkListenerGate.OffLimitsForTest = true;
    }

    /// <summary>No Bluetooth link is dropped, no controller powered off, no
    /// device paired, unpaired or rebound.</summary>
    internal static class BluetoothOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => BluetoothLinkHelper.OffLimitsForTest = true;
    }

    /// <summary>No virtual controller device is created, no HIDMaestro
    /// context set up, and no sweep of the PC's virtual controllers
    /// run.</summary>
    internal static class VirtualControllerOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => HMaestroVirtualController.OffLimitsForTest = true;
    }

    /// <summary>The FreeTrack memory and mutex every tracker and game
    /// shares are not opened.</summary>
    internal static class FreeTrackOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => FreeTrackReader.OffLimitsForTest = true;
    }

    /// <summary>No key, button, wheel turn or cursor move is injected, and
    /// no low-level hook installed.</summary>
    internal static class DesktopInputOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => InputHookManager.OffLimitsForTest = true;
    }

    /// <summary>The logon task and the legacy Run value are left
    /// alone.</summary>
    internal static class StartupOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => StartupHelper.OffLimitsForTest = true;
    }

    /// <summary>No driver is installed, signed or forced onto a device, no
    /// certificate added, no radio cycled, and no pairing record
    /// written.</summary>
    internal static class Ds3DriverOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => Ds3DriverInstaller.OffLimitsForTest = true;
    }

    /// <summary>The web controller's certificate, its http.sys binding and
    /// its firewall rules are not made.</summary>
    internal static class WebControllerSetupOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => WebControllerTls.OffLimitsForTest = true;
    }

    /// <summary>The mutexes other analog keyboard programs share are not
    /// taken.</summary>
    internal static class AnalogKeyboardMutexOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => AnalogKeyboardNamedMutex.OffLimitsForTest = true;
    }

    /// <summary>No tablet collection is captured or restarted.</summary>
    internal static class TabletOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => TabletNative.OffLimitsForTest = true;
    }

    /// <summary>No sound is played on an audio endpoint of the PC, and its
    /// volume, its mixer, its endpoints and its default output are left as
    /// they are.</summary>
    internal static class AudioEndpointOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => AudioEndpointAccess.OffLimitsForTest = true;
    }

    /// <summary>The programs other vendors run on the PC are not called:
    /// Razer's Chroma server and Sensa runtime, SteelSeries GG, and
    /// Logitech's LED engine and G-key library.</summary>
    internal static class VendorProgramsOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => VendorProgramGate.OffLimitsForTest = true;
    }

    /// <summary>Nothing is sent beyond loopback: no call to a peer, a relay
    /// or a STUN server, and no name looked up for one.</summary>
    internal static class OutboundOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => OutboundNetworkGate.OffLimitsForTest = true;
    }

    /// <summary>No request is sent to Steam, and the Steam client connects
    /// to no server.</summary>
    internal static class SteamOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => SteamHttp.OffLimitsForTest = true;
    }

#pragma warning restore CA2255
}
