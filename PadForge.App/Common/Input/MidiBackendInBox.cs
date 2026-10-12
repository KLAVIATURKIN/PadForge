using System;
using System.Collections.Generic;
using Midi2 = global::Windows.Devices.Midi2;
using Midi2Enum = global::Windows.Devices.Midi2.Enumeration;
using Midi2Virtual = global::Windows.Devices.Midi2.Transports.Virtual;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The in-box Windows MIDI Services API, Windows.Devices.Midi2, which
    /// Windows 11 25H2 carries from the late-November 2026 update. Built
    /// against Microsoft's 0.99.88-preview.10 metadata (PadForge.App.csproj).
    /// PadForge ships none of Microsoft's binaries: activation reaches the
    /// copy Windows registers in System32, and <see cref="MidiApiSelection"/>
    /// confirms that registration before anything here runs.
    ///
    /// <para>The calls mirror <see cref="MidiBackendAppSdk"/> one for one,
    /// and this one also checks the plugin add, which only the in-box API
    /// reports. In-box Preview 1 moved enumeration and endpoint metadata to
    /// Enumeration, the virtual device to Transports.Virtual and the message
    /// builder to Utilities.Messages, put EnsureServiceAvailable on MidiApi,
    /// and dropped the initializer.</para>
    /// </summary>
    internal sealed class MidiBackendInBox : IMidiBackend
    {
        public MidiApiKind Kind => MidiApiKind.InBox;

        /// <summary>Test seam: set by the test assembly for the life of its
        /// process. Every call that reaches the MIDI service then answers as
        /// a service that refuses: no start, no virtual device, no session,
        /// no endpoints. A session or a virtual device made from the test
        /// host is one every other program on this PC sees. False in
        /// production, where nothing sets it.</summary>
        internal static bool OffLimitsForTest;

        public bool Start() => !OffLimitsForTest && Midi2.MidiApi.EnsureServiceAvailable();

        public void Stop(bool skipDispose)
        {
            // Nothing to release: no initializer, the API is part of Windows.
        }

        public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex)
        {
            if (OffLimitsForTest) throw new InvalidOperationException("The test host creates no virtual MIDI device.");
            var declaredEndpointInfo = new Midi2Enum.MidiDeclaredEndpointInfo
            {
                Name = deviceName,
                ProductInstanceId = uniqueId,
                SpecificationVersionMajor = 1,
                SpecificationVersionMinor = 1,
                SupportsMidi10Protocol = true,
                SupportsMidi20Protocol = false,
                SupportsReceivingJitterReductionTimestamps = false,
                SupportsSendingJitterReductionTimestamps = false,
                HasStaticFunctionBlocks = true,
            };

            var declaredDeviceIdentity = new Midi2Enum.MidiDeclaredDeviceIdentity();

            // A fixed text. It ended in the pad index plus one, which is not
            // the number the UI shows for the slot and does not follow the
            // slot when the order changes.
            var userSuppliedInfo = new Midi2Enum.MidiEndpointUserSuppliedInfo
            {
                Name = deviceName,
                Description = "PadForge virtual MIDI controller",
            };

            var config = new Midi2Virtual.MidiVirtualDeviceCreationConfig(
                deviceName,
                "Virtual MIDI controller from PadForge",
                "PadForge",
                declaredEndpointInfo,
                declaredDeviceIdentity,
                userSuppliedInfo);

            // Single function block for MIDI 1.0 output.
            var block = new Midi2Enum.MidiFunctionBlock
            {
                Number = 0,
                Name = "Controller Output",
                IsActive = true,
                UIHint = Midi2Enum.MidiFunctionBlockUIHint.Sender,
                FirstGroup = new Midi2.MidiGroup(0),
                GroupCount = 1,
                Direction = Midi2Enum.MidiFunctionBlockDirection.Bidirectional,
                RepresentsMidi10Connection = Midi2Enum.MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthUnrestricted,
                MaxSystemExclusive8Streams = 0,
                MidiCIMessageVersionFormat = 0,
            };
            config.FunctionBlocks.Add(block);

            Midi2.MidiSession session = null;
            Midi2.MidiEndpointConnection connection = null;
            try
            {
                session = Midi2.MidiSession.Create(deviceName);
                if (session == null)
                    throw new MidiSessionUnavailableException();

                var virtualDevice = Midi2Virtual.MidiVirtualDeviceManager.CreateVirtualDevice(config);
                if (virtualDevice == null)
                    throw new InvalidOperationException("Failed to create virtual MIDI device.");

                virtualDevice.SuppressHandledMessages = true;

                connection = session.CreateEndpointConnection(virtualDevice.DeviceEndpointDeviceId);
                if (connection == null)
                    throw new InvalidOperationException("Failed to create MIDI endpoint connection.");

                // The in-box API reports the add, where the older runtime's
                // returned nothing. Microsoft's virtual device sample fails
                // the start on anything but Succeeded: a device whose plugin
                // is missing never answers endpoint discovery.
                var added = connection.AddMessageProcessingPlugin(virtualDevice);
                if (added != Midi2.MidiMessageProcessingPluginAddResult.Succeeded)
                    throw new InvalidOperationException($"Failed to add the virtual MIDI device to its connection ({added}).");

                if (!connection.Open())
                    throw new InvalidOperationException("Failed to open MIDI endpoint connection.");

                return new Endpoint(session, virtualDevice, connection);
            }
            catch
            {
                // Creation failed partway: tear down what was built. The
                // caller unregisters the endpoint and schedules the janitor
                // for whatever the service stranded.
                try
                {
                    if (connection != null && session != null)
                        session.DisconnectEndpointConnection(connection.ConnectionId);
                }
                catch { /* best effort */ }
                try { session?.Dispose(); } catch { /* best effort */ }
                throw;
            }
        }

        private sealed class Endpoint : IMidiVirtualEndpoint
        {
            private readonly Midi2.MidiSession _session;
            // Held for the endpoint's life, as the connection's message
            // processing plugin.
            private readonly Midi2Virtual.MidiVirtualDevice _virtualDevice;
            private readonly Midi2.MidiEndpointConnection _connection;

            public Endpoint(Midi2.MidiSession session, Midi2Virtual.MidiVirtualDevice virtualDevice,
                Midi2.MidiEndpointConnection connection)
            {
                _session = session;
                _virtualDevice = virtualDevice;
                _connection = connection;
            }

            /// <summary>Sends the word the message builder would pack
            /// (<see cref="MidiUmp.Midi1Word"/>) with no WinRT object per
            /// message: a MidiGroup, a MidiChannel and a MidiMessage32 are
            /// runtime classes, each activated and wrapped on every send. The
            /// API took it unless the result carries Failed, the test
            /// Microsoft's MidiEndpointConnection.SendMessageFailed
            /// makes.</summary>
            public bool Send(Midi1Status status, int channel, int data1, int data2)
            {
                var result = _connection.SendSingleMessageWords(0, MidiUmp.Midi1Word(status, channel, data1, data2));
                return (result & Midi2.MidiSendMessageResults.Failed) != Midi2.MidiSendMessageResults.Failed;
            }

            public bool Lost => false;

            public void DisconnectConnection() => _session.DisconnectEndpointConnection(_connection.ConnectionId);

            public void CloseSession()
            {
                _session.Dispose();
                GC.KeepAlive(_virtualDevice);
            }
        }

        public IMidiInputSession CreateInputSession(string name)
        {
            if (OffLimitsForTest) return null;
            var session = Midi2.MidiSession.Create(name);
            return session == null ? null : new InputSession(session);
        }

        private sealed class InputSession : IMidiInputSession
        {
            private readonly Midi2.MidiSession _session;

            public InputSession(Midi2.MidiSession session) => _session = session;

            public IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords)
            {
                var connection = _session.CreateEndpointConnection(endpointId);
                return connection == null ? null : new InputConnection(_session, connection, onWords);
            }

            public void Dispose() => _session.Dispose();
        }

        private sealed class InputConnection : IMidiInputConnection
        {
            private readonly Midi2.MidiSession _session;
            private readonly Midi2.MidiEndpointConnection _connection;
            private readonly Action<uint, uint> _onWords;

            public InputConnection(Midi2.MidiSession session, Midi2.MidiEndpointConnection connection,
                Action<uint, uint> onWords)
            {
                _session = session;
                _connection = connection;
                _onWords = onWords;
                _connection.MessageReceived += OnMessageReceived;
            }

            public bool Open() => _connection.Open();

            public void Detach() => _connection.MessageReceived -= OnMessageReceived;

            public void Disconnect() => _session.DisconnectEndpointConnection(_connection.ConnectionId);

            private void OnMessageReceived(Midi2.IMidiMessageReceivedEventSource sender, Midi2.MidiMessageReceivedEventArgs args)
            {
                try
                {
                    uint w0 = args.PeekFirstWord();
                    if (w0 >> 28 == 0x4)
                    {
                        // MIDI 2.0 channel voice: the second word carries
                        // the value.
                        if (args.GetMessagePacket() is not Midi2.MidiMessage64 m64) return;
                        _onWords(m64.Word0, m64.Word1);
                        return;
                    }
                    _onWords(w0, 0);
                }
                catch
                {
                    // A malformed packet must never take down the WinRT
                    // callback thread. Drop it.
                }
            }
        }

        public List<(string Id, string Name)> EnumerateNormalEndpoints()
        {
            var result = new List<(string Id, string Name)>();
            if (OffLimitsForTest) return result;
            // A throw partway keeps what was read before it, as the
            // enumeration in MidiInputRuntime always has.
            try
            {
                var endpoints = Midi2Enum.MidiEndpointDeviceInformation.FindAll();
                if (endpoints == null) return result;
                foreach (var ep in endpoints)
                {
                    if (ep == null) continue;
                    if (ep.EndpointPurpose != Midi2Enum.MidiEndpointDevicePurpose.NormalMessageEndpoint)
                        continue;
                    string id = ep.EndpointDeviceId;
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = ep.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = "MIDI Endpoint";
                    result.Add((id, name));
                }
            }
            catch { }
            return result;
        }
    }
}
