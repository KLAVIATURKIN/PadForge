using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>The MIDI 1.0 channel voice messages PadForge sends.</summary>
    internal enum Midi1Status : byte
    {
        NoteOff = 0x8,
        NoteOn = 0x9,
        ControlChange = 0xB,
    }

    /// <summary>Universal MIDI Packet words, built in managed code.</summary>
    internal static class MidiUmp
    {
        /// <summary>One MIDI 1.0 channel voice message as a 32-bit UMP word
        /// on group 0: message type 2, the status nibble, the channel, then
        /// the two data bytes at 7 bits each. The same word Microsoft's
        /// MidiMessageBuilder.BuildMidi1ChannelVoiceMessage packs
        /// (MidiMessageBuilder.cpp), and the one
        /// <see cref="MidiInputDevice.OnUmp"/> decodes. A Windows MIDI
        /// Services send takes it through SendSingleMessageWords with no
        /// WinRT object per message.</summary>
        internal static uint Midi1Word(Midi1Status status, int channel, int data1, int data2)
            => 0x20000000u
               | ((uint)status & 0xF) << 20
               | ((uint)channel & 0xF) << 16
               | ((uint)data1 & 0x7F) << 8
               | ((uint)data2 & 0x7F);
    }

    /// <summary>
    /// One MIDI API PadForge drives. The two Windows MIDI Services APIs, the
    /// in-box Windows.Devices.Midi2 (<see cref="MidiBackendInBox"/>) and the
    /// older App SDK runtime (<see cref="MidiBackendAppSdk"/>), expose the
    /// same calls under different namespaces and class IDs. The legacy WinMM
    /// API (<see cref="MidiBackendLegacy"/>) serves a PC where neither runs.
    /// It cannot create a port, so its output ports are listed and opened
    /// through calls of its own (<see cref="MidiBackendLegacy.OpenOutputPort"/>).
    /// The rest of PadForge's MIDI work goes through this seam, so the bounded
    /// lifecycle around it (<see cref="MidiVirtualController"/>,
    /// <see cref="MidiInputDevice"/>, <see cref="MidiInputRuntime"/>) stays
    /// one copy.
    ///
    /// <para>Every member can be service RPC and can hang when the service
    /// is broken. Callers bound each call and never wait on one from the
    /// polling thread.</para>
    /// </summary>
    internal interface IMidiBackend
    {
        MidiApiKind Kind { get; }

        /// <summary>Loads the API and asks the service to be available.
        /// False when it is not.</summary>
        bool Start();

        /// <summary>Drops what <see cref="Start"/> created: the App SDK
        /// runtime's initializer, and nothing for the other APIs. Dropping it
        /// does not unload the runtime's DLLs. Microsoft's projection has the
        /// native Shutdown commented out of ShutdownSdkRuntime (rc-3 and rc-4
        /// MidiDesktopAppSdkInitializer.cs), so Dispose makes no native call.
        /// <paramref name="skipDispose"/> drops the initializer without its
        /// Dispose. No production caller passes it.</summary>
        void Stop(bool skipDispose);

        /// <summary>Creates one virtual device with a single MIDI 1.0
        /// function block, connects to its device side and opens the
        /// connection. On failure it tears down what it built and throws
        /// <see cref="InvalidOperationException"/> naming the failed step.
        /// <paramref name="padIndex"/> says which slot asks. Nothing the
        /// endpoint shows carries it: the description is one fixed text,
        /// since the pad index is not the number the UI shows for the
        /// slot.</summary>
        IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex);

        /// <summary>A session for input connections, or null when the
        /// service refuses one.</summary>
        IMidiInputSession CreateInputSession(string name);

        /// <summary>The endpoints a client can connect to: normal message
        /// endpoints only, with their names.</summary>
        List<(string Id, string Name)> EnumerateNormalEndpoints();
    }

    /// <summary>An open virtual device, or under the legacy API an open
    /// output port.</summary>
    internal interface IMidiVirtualEndpoint
    {
        /// <summary>Sends one MIDI 1.0 channel voice message on group 0 and
        /// says whether the API took it. False when the service or the driver
        /// refused it: a full buffer, a closed connection, a port whose device
        /// left. Neither Windows MIDI Services send throws. The in-box IDL
        /// marks its sends noexcept (MidiEndpointConnection.idl). The App
        /// SDK's IDL carries no such attribute, but its implementation is
        /// declared noexcept and catches inside, answering Failed (rc-3 and
        /// rc-4 MidiEndpointConnection.h and
        /// MidiEndpointConnection_SendSingleMessage.cpp). The legacy send
        /// answers from midiOutShortMsg's MMRESULT rather than
        /// throwing.</summary>
        bool Send(Midi1Status status, int channel, int data1, int data2);

        /// <summary>True once a send showed the port itself is gone, which
        /// only the legacy API reports. Step 5 then rebuilds the
        /// slot.</summary>
        bool Lost { get; }

        /// <summary>Disconnects the device-side connection from the session.</summary>
        void DisconnectConnection();

        /// <summary>Closes the session.</summary>
        void CloseSession();
    }

    /// <summary>The shared session MIDI input connections live on.</summary>
    internal interface IMidiInputSession : IDisposable
    {
        /// <summary>Creates a connection to <paramref name="endpointId"/> and
        /// subscribes <paramref name="onWords"/> before it opens, so no
        /// message is missed. The callback gets each message's first two UMP
        /// words. Null when the service refuses the connection.</summary>
        IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords);
    }

    /// <summary>One input connection.</summary>
    internal interface IMidiInputConnection
    {
        bool Open();

        /// <summary>Stops delivering messages to the callback.</summary>
        void Detach();

        /// <summary>Removes the connection from its session.</summary>
        void Disconnect();
    }
}
