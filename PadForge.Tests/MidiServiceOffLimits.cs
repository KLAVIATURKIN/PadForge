using System.Runtime.CompilerServices;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process away from this PC's MIDI for its whole life:
    /// the MIDI service, Windows MIDI Services sessions and virtual devices,
    /// WinMM ports, and the device nodes of a running PadForge's endpoints.
    /// </summary>
    /// <remarks>
    /// A MIDI test stands a fake in for the backend and scripts the service
    /// restart through a seam, and puts both away when it ends. Work can
    /// outlive the test that caused it (a watchdog hands a restart to a
    /// worker, an input call's watch is a timer, a probe waits behind a
    /// lock), and a change to the line that picks the fake or the seam sends
    /// a call past it. Either way the call found the production path, which
    /// on a PC with a running MIDI service starts a real backend, opens a
    /// session other programs see, stops and starts midisrv when the test
    /// host is elevated, or sweeps away the endpoints of a PadForge running
    /// beside the tests. With these set, each of those refuses in the code
    /// that would make the call, and changes nothing. Nothing in the
    /// application sets them (MachineOffLimitsTests).
    /// </remarks>
    internal static class MidiServiceOffLimits
    {
#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set()
        {
            MidiServiceRecovery.SeamsOnlyForTest = true;
            MidiVirtualController.RealBackendsOffLimitsForTest = true;
            MidiBackendInBox.OffLimitsForTest = true;
            MidiBackendAppSdk.OffLimitsForTest = true;
            WinMmMidi.OffLimitsForTest = true;
            MidiEndpointJanitor.OffLimitsForTest = true;
        }
#pragma warning restore CA2255
    }
}
