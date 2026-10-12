using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The legacy WinMM backend, over a stand-in for winmm.dll that records
    /// every call. The sequences under test follow RtMidi (MidiInWinMM,
    /// MidiOutWinMM), PortMidi (pmwinmm.c) and NAudio (MidiIn, MidiOut):
    /// output opens with no callback and closes without midiOutReset, input
    /// opens with a function callback, starts, and closes with stop, reset,
    /// close.
    /// </summary>
    [Collection("MidiBackendStatics")]
    public class MidiBackendLegacyTests : IDisposable
    {
        public MidiBackendLegacyTests()
        {
            MidiEndpointJanitor.SweepDisabledForTest = true;
            MidiInputRuntime.Shutdown();
        }

        public void Dispose()
        {
            // A restart a test's worker is still in ends before the next
            // test's first ask, which would answer no inside it.
            SpinWait.SpinUntil(() => !MidiServiceRecovery.RestartInFlight, 10_000);
            MidiInputRuntime.Shutdown();
            MidiVirtualController.UseBackendFactoryForTest(null);
            MidiApiSelection.OsBuildSeam = null;
            MidiEndpointJanitor.SweepDisabledForTest = false;
            MidiServiceRecovery.RecoverSeam = null;
            MidiServiceRecovery.CooldownMs = 120_000;
            MidiServiceRecovery.ResetCooldownForTest();
            MidiVirtualController.ConnectTimeoutMs = 15_000;
            MidiVirtualController.RefusalRecheckMs = 10_000;
        }

        internal sealed class FakeWinMm : IWinMmMidi
        {
            public List<string> Inputs = new();
            public List<string> Outputs = new();
            public int OpenResult;
            public int StartResult;
            public int SendResult;
            public readonly List<string> Calls = new();
            public readonly List<uint> Sent = new();
            public WinMmMidi.MidiInProc Callback;
            private int _nextHandle = 0x100;

            public int InputCount() => Inputs.Count;
            /// <summary>Holds OutputCount until set, the way wdmaud2 holds a
            /// port count behind a port open hung in the MIDI service.</summary>
            public System.Threading.ManualResetEventSlim OutputCountGate;
            public int OutputCountEntries;

            /// <summary>Holds SendShort until set, the way midiOutShortMsg
            /// waits for a message to reach the wire.</summary>
            public System.Threading.ManualResetEventSlim SendGate;

            /// <summary>Holds OpenOutput until set, the way a classic driver
            /// can hang an open.</summary>
            public System.Threading.ManualResetEventSlim OpenGate;

            public int OutputCount()
            {
                System.Threading.Interlocked.Increment(ref OutputCountEntries);
                OutputCountGate?.Wait();
                return Outputs.Count;
            }
            public string InputName(int id) => Inputs[id];
            public string OutputName(int id) => Outputs[id];

            // Closes run on worker threads, so the log is locked.
            private void Log(string call) { lock (Calls) Calls.Add(call); }
            public bool Has(string call) { lock (Calls) return Calls.Contains(call); }

            /// <summary>Makes each output port serve one client at a time,
            /// as the classic MIDI stack does: a second open of a port
            /// answers MMSYSERR_ALLOCATED until the first closes.</summary>
            public bool OneClientPerOutput;
            private readonly Dictionary<IntPtr, int> _openOutputs = new();

            public int OpenOutput(int id, out IntPtr handle)
            {
                OpenGate?.Wait();
                Log($"OpenOutput {id}");
                lock (_openOutputs)
                {
                    if (OneClientPerOutput && _openOutputs.ContainsValue(id))
                    {
                        handle = IntPtr.Zero;
                        return WinMmMidi.MMSYSERR_ALLOCATED;
                    }
                    handle = OpenResult == 0 ? (IntPtr)_nextHandle++ : IntPtr.Zero;
                    if (OpenResult == 0) _openOutputs[handle] = id;
                }
                return OpenResult;
            }

            public int SendShort(IntPtr handle, uint message)
            {
                SendGate?.Wait();
                Log($"Send {handle}");
                lock (Calls) Sent.Add(message);
                return SendResult;
            }

            public int CloseOutput(IntPtr handle)
            {
                Log($"CloseOutput {handle}");
                lock (_openOutputs) _openOutputs.Remove(handle);
                return 0;
            }

            public int OpenInput(int id, WinMmMidi.MidiInProc callback, out IntPtr handle)
            {
                Log($"OpenInput {id}");
                Callback = callback;
                handle = OpenResult == 0 ? (IntPtr)_nextHandle++ : IntPtr.Zero;
                return OpenResult;
            }

            public int StartInput(IntPtr handle) { Log("StartInput"); return StartResult; }
            public int StopInput(IntPtr handle) { Log("StopInput"); return 0; }
            public int ResetInput(IntPtr handle) { Log("ResetInput"); return 0; }
            public int CloseInput(IntPtr handle) { Log("CloseInput"); return 0; }

            /// <summary>What the driver's thread does when a short message arrives.</summary>
            public void Deliver(uint packed, uint wMsg = WinMmMidi.MIM_DATA)
                => Callback(IntPtr.Zero, wMsg, IntPtr.Zero, (IntPtr)packed, IntPtr.Zero);
        }

        // ── Port names ──

        [Fact]
        public void Ports_AreKeyedByName_WithTheSecondOfTwoNumbered()
        {
            var keys = MidiBackendLegacy.NamePorts(new[] { "USB MIDI", "Synth", "USB MIDI", "USB MIDI" });
            Assert.Equal(new[] { "USB MIDI", "Synth", "USB MIDI (2)", "USB MIDI (3)" }, keys.Select(k => k.Key));
            Assert.Equal(new[] { 0, 1, 2, 3 }, keys.Select(k => k.Id));
        }

        /// <summary>A port Windows cannot describe gets no key, and a real
        /// name that collides with a generated one is numbered in turn, so
        /// every key names exactly one port.</summary>
        [Fact]
        public void Ports_WithNoName_AreSkipped_AndNoTwoKeysCollide()
        {
            var keys = MidiBackendLegacy.NamePorts(new[] { "Foo", null, "  ", "Foo", "Foo (2)" });
            Assert.Equal(new[] { "Foo", "Foo (2)", "Foo (2) (2)" }, keys.Select(k => k.Key));
            Assert.Equal(new[] { 0, 3, 4 }, keys.Select(k => k.Id));
        }

        // ── Messages ──

        /// <summary>Status byte low, then the two data bytes, as
        /// midiOutShortMsg takes them (RtMidi MidiOutWinMM::sendMessage).</summary>
        [Fact]
        public void AShortMessage_PacksStatusLow_ThenTheDataBytes()
        {
            Assert.Equal(0x005A3C92u, MidiBackendLegacy.PackShortMessage(Midi1Status.NoteOn, 2, 60, 90));
            Assert.Equal(0x006407B0u, MidiBackendLegacy.PackShortMessage(Midi1Status.ControlChange, 0, 7, 100));
            Assert.Equal(0x00003C82u, MidiBackendLegacy.PackShortMessage(Midi1Status.NoteOff, 2, 60, 0));
            // Data bytes keep 7 bits and the channel 4, so nothing spills
            // into the status byte.
            Assert.Equal(0x007F7FBFu, MidiBackendLegacy.PackShortMessage(Midi1Status.ControlChange, 0x1F, 0xFF, 0xFF));
        }

        /// <summary>The words the Windows MIDI Services loopback delivered
        /// for the same three messages in the 2026-10-05 hands-on run
        /// (0x20B20764, 0x20923C5A, 0x20823C00).</summary>
        [Fact]
        public void AReceivedMessage_BecomesTheSameUmpWordWindowsMidiServicesDelivers()
        {
            Assert.Equal(0x20B20764u, MidiBackendLegacy.ShortMessageToUmp(0x006407B2));
            Assert.Equal(0x20923C5Au, MidiBackendLegacy.ShortMessageToUmp(0x005A3C92));
            Assert.Equal(0x20823C00u, MidiBackendLegacy.ShortMessageToUmp(0x00003C82));
            Assert.Equal(0x20E07F00u, MidiBackendLegacy.ShortMessageToUmp(0x00007FE0));
        }

        [Fact]
        public void ADataByteWhereTheStatusBelongs_OrASystemMessage_IsDropped()
        {
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x0000643C));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x000000F8));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x000000FE));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x00000000));
        }

        /// <summary>Program change and channel pressure carry one data
        /// byte, so whatever sits in the third byte is not passed on.</summary>
        [Fact]
        public void AOneDataByteMessage_ClearsTheSecondDataByte()
        {
            Assert.Equal(0x20C00500u, MidiBackendLegacy.ShortMessageToUmp(0x007705C0));
            Assert.Equal(0x20D04000u, MidiBackendLegacy.ShortMessageToUmp(0x003340D0));
        }

        [Fact]
        public void TheWordsAMidiInputDeviceGets_DecodeToTheSameState()
        {
            var dev = new MidiInputDevice(MidiBackendLegacy.EndpointPrefix + "Keys", "Keys");
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x006407B2), 0);
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x005A3C92), 0);
            var s = dev.GetCurrentState().Midi;
            Assert.Equal(100, s.Cc[7]);
            Assert.True(s.Notes[60]);
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x00003C82), 0);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
        }

        // ── Output ──

        [Fact]
        public void NoPortPicked_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            var ex = Assert.Throws<MidiPortException>(() => new MidiBackendLegacy(fake).OpenOutputPort(""));
            Assert.Contains("Pick an output port", ex.Message);
            Assert.Equal(MidiPortProblem.NotPicked, ex.Problem);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void APortThatIsGone_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            // Its own type: Step 5 retries a slot that failed this way once a
            // port listing shows the port again.
            var ex = Assert.Throws<MidiPortNotConnectedException>(() => new MidiBackendLegacy(fake).OpenOutputPort("loopMIDI Port"));
            Assert.Contains("is not connected", ex.Message);
            Assert.Empty(fake.Calls);
        }

        /// <summary>The open's answer says the port is in use, and not by
        /// whom: another program, or another of PadForge's own slots.</summary>
        [Fact]
        public void APortAlreadyInUse_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            var ex = Assert.Throws<MidiPortException>(() => new MidiBackendLegacy(fake).OpenOutputPort("Synth"));
            Assert.Contains("'Synth' is already in use", ex.Message);
            Assert.DoesNotContain("program", ex.Message);
            Assert.Equal(MidiPortProblem.InUse, ex.Problem);
        }

        /// <summary>The port is found by name at each open, so a device
        /// that came or went and shifted the indexes still gets the right
        /// port. Sends go to the handle that open returned, and close never
        /// resets the port: the interface does not even carry
        /// midiOutReset.</summary>
        [Fact]
        public void APort_OpensByName_SendsToItsHandle_AndClosesOnce()
        {
            var fake = new FakeWinMm { Outputs = { "New Device", "Synth", "Synth" } };
            var ep = new MidiBackendLegacy(fake).OpenOutputPort("Synth (2)");
            Assert.Equal("OpenOutput 2", fake.Calls[0]);

            ep.Send(Midi1Status.ControlChange, 0, 7, 100);
            Assert.Equal(new[] { 0x006407B0u }, fake.Sent);

            ep.DisconnectConnection();
            ep.CloseSession();
            ep.CloseSession();
            Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("CloseOutput", StringComparison.Ordinal)));

            // A send after the close goes nowhere.
            ep.Send(Midi1Status.NoteOn, 0, 60, 127);
            Assert.Single(fake.Sent);
        }

        /// <summary>A refused send reports that it was not taken, so the
        /// controller sends a refused CC or Note Off again on the next submit.
        /// A busy driver refuses one message. A port whose device left marks
        /// the port lost.</summary>
        [Theory]
        [InlineData(67, false)]   // MIDIERR_NOTREADY: busy, this message only
        [InlineData(1, false)]    // MMSYSERR_ERROR
        [InlineData(6, true)]     // MMSYSERR_NODRIVER: wdmaud2's answer for a removed port
        [InlineData(5, true)]     // MMSYSERR_INVALHANDLE
        [InlineData(68, true)]    // MIDIERR_NODEVICE
        public void AFailedSend_ReportsItWasNotTaken(int mmResult, bool lost)
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, SendResult = mmResult };
            var ep = new MidiBackendLegacy(fake).OpenOutputPort("Synth");
            Assert.False(ep.Send(Midi1Status.NoteOn, 0, 60, 127));
            Assert.Equal(lost, ep.Lost);
            fake.SendResult = 0;
            Assert.True(ep.Send(Midi1Status.NoteOff, 0, 60, 0));
            Assert.Equal(lost, ep.Lost);
        }

        [Fact]
        public void TheLegacyApi_NeverCreatesAPort()
        {
            Assert.Throws<NotSupportedException>(() =>
                new MidiBackendLegacy(new FakeWinMm()).CreateVirtualEndpoint("PadForge MIDI 1", "PADFORGE_MIDI_1_X", 0));
        }

        // ── Input ──

        [Fact]
        public void InputPorts_AreListedUnderTheirOwnPrefix()
        {
            var fake = new FakeWinMm { Inputs = { "Keys", "Keys" } };
            var list = new MidiBackendLegacy(fake).EnumerateNormalEndpoints();
            Assert.Equal(new[] { ("winmm:Keys", "Keys"), ("winmm:Keys (2)", "Keys (2)") }, list);
        }

        [Fact]
        public void AnInputPort_Opens_Starts_Delivers_AndClosesStopResetClose()
        {
            var fake = new FakeWinMm { Inputs = { "Other", "Keys" } };
            var words = new List<uint>();
            using var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            Assert.Null(session.CreateConnection("MIDIU_APPPUB_SOMETHING", (a, b) => words.Add(a)));

            var conn = session.CreateConnection("winmm:Keys", (a, b) => words.Add(a));
            Assert.True(conn.Open());
            Assert.Equal(new[] { "OpenInput 1", "StartInput" }, fake.Calls);

            fake.Deliver(0x005A3C92);
            fake.Deliver(0x005A3C92, wMsg: 0x3C1); // MIM_OPEN carries no message
            Assert.Equal(new[] { 0x20923C5Au }, words);

            conn.Detach();
            fake.Deliver(0x006407B2);
            Assert.Single(words);

            fake.Calls.Clear();
            conn.Disconnect();
            conn.Disconnect();
            Assert.Equal(new[] { "StopInput", "ResetInput", "CloseInput" }, fake.Calls);
        }

        /// <summary>PortMidi's failed-start path: reset, then close.</summary>
        [Fact]
        public void AnInputThatWillNotStart_IsResetAndClosed()
        {
            var fake = new FakeWinMm { Inputs = { "Keys" }, StartResult = 7 };
            using var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            var conn = session.CreateConnection("winmm:Keys", (a, b) => { });
            Assert.False(conn.Open());
            Assert.Equal(new[] { "OpenInput 0", "StartInput", "ResetInput", "CloseInput" }, fake.Calls);
        }

        [Fact]
        public void DisposingTheSession_ClosesEveryOpenPort_AndRefusesNewOnes()
        {
            var fake = new FakeWinMm { Inputs = { "Keys", "Pads" } };
            var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            var a = session.CreateConnection("winmm:Keys", (x, y) => { });
            var b = session.CreateConnection("winmm:Pads", (x, y) => { });
            Assert.True(a.Open());
            Assert.True(b.Open());
            fake.Calls.Clear();

            session.Dispose();
            Assert.Equal(2, fake.Calls.Count(c => c == "CloseInput"));

            fake.Calls.Clear();
            var late = session.CreateConnection("winmm:Keys", (x, y) => { });
            Assert.False(late.Open());
            Assert.Contains("CloseInput", fake.Calls);
        }

        // ── The probe and the controller ──

        private sealed class RefusingBackend : IMidiBackend
        {
            public int Stops;
            public MidiApiKind Kind => MidiApiKind.AppSdk;
            public bool Start() => false;
            public void Stop(bool skipDispose) => Stops++;
            public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex) => throw new InvalidOperationException();
            public IMidiInputSession CreateInputSession(string name) => null;
            public List<(string Id, string Name)> EnumerateNormalEndpoints() => new();
        }

        /// <summary>Legacy API mode makes EnsureServiceAvailable return
        /// false. The probe releases that API and takes the legacy one.</summary>
        [Fact]
        public void AWindowsMidiServicesApiThatWillNotStart_FallsBackToTheLegacyApi()
        {
            var refusing = new RefusingBackend();
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            MidiVirtualController.UseBackendFactoryForTest(() => refusing, () => new MidiBackendLegacy(fake));

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
            Assert.False(MidiVirtualController.ProbeFailed);
            Assert.Equal(1, refusing.Stops);
        }

        [Fact]
        public void NoWindowsMidiServicesApi_FallsBackToTheLegacyApi()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(new FakeWinMm()));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
        }

        [Fact]
        public void ALegacyMidiSlot_OpensItsPickedPort_AndRecordsTheApi()
        {
            var fake = new FakeWinMm { Outputs = { "Synth", "loopMIDI Port" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "loopMIDI Port", CcNumbers = new[] { 1 }, NoteNumbers = new[] { 60 } };
            vc.Connect();
            Assert.True(vc.IsConnected);
            Assert.Equal(MidiApiKind.Legacy, vc.ApiKind);
            Assert.Equal("OpenOutput 1", fake.Calls[0]);

            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 10 }, Notes = new[] { true } });
            Assert.True(vc.WaitForSends());
            Assert.Equal(new[] { 0x000A01B0u, 0x007F3C90u }, fake.Sent);

            // Disconnect sends a note off for the held note, then closes.
            vc.Dispose();
            Assert.Equal(0x00003C80u, fake.Sent.Last());
            Assert.Contains(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));
        }

        /// <summary>A channel edit on a running legacy slot keeps its port
        /// open. The held note ends on the old channel, and the next submit
        /// presses it on the new one.</summary>
        [Fact]
        public void ALegacyMidiSlot_TakesAChannelEdit_OnTheSamePort()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "Synth" };
            vc.ApplyLayout(channel: 0, startCc: 1, ccCount: 0, startNote: 60, noteCount: 1, velocity: 127);
            vc.Connect();
            var held = new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { true } };
            vc.SubmitMidiRawState(held);

            vc.ApplyLayout(channel: 9, startCc: 1, ccCount: 0, startNote: 60, noteCount: 1, velocity: 127);
            vc.SubmitMidiRawState(held);
            Assert.True(vc.WaitForSends());

            // Status byte low: note on and off on channel 1, then note on on channel 10.
            Assert.Equal(new[] { 0x007F3C90u, 0x00003C80u, 0x007F3C99u }, fake.Sent);
            Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("OpenOutput", StringComparison.Ordinal)));
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));
            vc.Dispose();
        }

        [Fact]
        public void ALegacyMidiSlotWithNoPort_FailsToConnect()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(new FakeWinMm { Outputs = { "Synth" } }));
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, 0, 1);
            var ex = Assert.Throws<MidiPortException>(() => vc.Connect());
            Assert.Contains("Pick an output port", ex.Message);
            Assert.False(vc.IsConnected);
        }

        /// <summary>The legacy sweep closes a port whose slot assignment
        /// went and opens it again when one comes. A close returns the
        /// state to rest, so a held note does not survive into the next
        /// open.</summary>
        [Fact]
        public void AnInputDevice_ClosesToRest_AndOpensAgain()
        {
            var fake = new FakeWinMm { Inputs = { "Keys" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));

            var dev = new MidiInputDevice("winmm:Keys", "Keys");
            Assert.Equal(MidiInputDevice.InstanceGuidFor("winmm:Keys"), dev.InstanceGuid);
            Assert.False(dev.IsOpen);
            Assert.True(dev.Open());
            Assert.True(dev.IsOpen);
            fake.Deliver(0x005A3C92);
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);

            dev.Close();
            Assert.False(dev.IsOpen);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
            Assert.True(SpinWaitFor(() => fake.Has("CloseInput")));

            Assert.True(dev.Open());
            Assert.True(dev.IsOpen);
            dev.Dispose();
        }

        private static bool SpinWaitFor(Func<bool> condition)
            => System.Threading.SpinWait.SpinUntil(condition, 3_000);

        // ── A port that comes and goes (Step 5) ──

        private const int WatchPad = 2;
        private static readonly Guid WatchDevice = new("3c6f1b2a-8d4e-4f51-9a7b-2e0d5c6b7a81");

        /// <summary>Runs <paramref name="body"/> against an InputManager with
        /// one created, enabled MIDI slot whose device is online and whose
        /// bar names <paramref name="port"/>, on the legacy API over
        /// <paramref name="fake"/>. This collection runs with nothing beside
        /// it, so the settings statics are borrowed and put back.</summary>
        private static void WithLegacyMidiSlot(FakeWinMm fake, string port, Action<InputManager> body)
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            InputManager im = null;
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                Assert.True(MidiVirtualController.IsAvailable());

                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[WatchPad] = true;
                var ud = new UserDevice { InstanceGuid = WatchDevice, ProductName = "Port Watch Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = WatchDevice, MapTo = WatchPad });

                im = new InputManager { LegacyPortListingIntervalMs = 0 };
                im.SlotControllerTypes[WatchPad] = VirtualControllerType.Midi;
                im._midiConfigs[WatchPad] = new MidiSlotConfig { OutputPort = port };
                body(im);
            }
            finally
            {
                if (im != null)
                {
                    WaitForWork(im);
                    foreach (var slot in Slots(im)) slot?.Dispose();
                }
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        private static T PrivateField<T>(InputManager im, string name)
            => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        private static IVirtualController[] Slots(InputManager im) => PrivateField<IVirtualController[]>(im, "_virtualControllers");

        private static bool Failed(InputManager im, int pad = WatchPad) => PrivateField<bool[]>(im, "_createFailed")[pad];

        private static bool Running(InputManager im, int pad = WatchPad)
            => Slots(im)[pad] is MidiVirtualController { IsConnected: true };

        /// <summary>Waits for every worker Step 5 starts: each slot's create
        /// or dispose, and the port listing.</summary>
        private static void WaitForWork(InputManager im)
        {
            Assert.True(System.Threading.SpinWait.SpinUntil(() =>
            {
                var connects = PrivateField<System.Threading.Tasks.Task[]>(im, "_pendingConnectTask");
                var disposes = PrivateField<System.Threading.Tasks.Task[]>(im, "_pendingDisposeTask");
                return connects.All(t => t == null || t.IsCompleted)
                    && disposes.All(t => t == null || t.IsCompleted)
                    && !PrivateField<bool>(im, "_legacyPortListingRunning");
            }, 10_000), "a Step 5 worker did not finish");
        }

        /// <summary>Adds a second created, enabled MIDI slot on
        /// <paramref name="pad"/>, with a device of its own online, whose
        /// bar names <paramref name="port"/>. Returns that device.</summary>
        private static UserDevice AddLegacyMidiSlot(InputManager im, int pad, string port)
        {
            var device = new Guid("9e41c7d2-5b08-4a6f-b3d9-0f6a2c8e1b47");
            SettingsManager.SlotCreated[pad] = true;
            var ud = new UserDevice { InstanceGuid = device, ProductName = "Second Port Pad", IsOnline = true, InputState = new CustomInputState() };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device, MapTo = pad });
            im.SlotControllerTypes[pad] = VirtualControllerType.Midi;
            im._midiConfigs[pad] = new MidiSlotConfig { OutputPort = port };
            return ud;
        }

        /// <summary>One Step 5 pass, with no wait for the work it
        /// starts.</summary>
        private static void Pass(InputManager im)
            => typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(im, null);

        private static void Step(InputManager im)
        {
            Pass(im);
            WaitForWork(im);
        }

        private static bool StepUntil(InputManager im, Func<bool> done)
        {
            for (int i = 0; i < 20 && !done(); i++) Step(im);
            return done();
        }

        /// <summary>A slot whose port is missing fails, then starts once the
        /// port appears, as when a synth is switched on after PadForge. The
        /// port has to show missing first, so a port another program holds
        /// does not retry in a loop.</summary>
        [Fact]
        public void ALegacySlot_StartsOnceItsPortAppears()
        {
            var fake = new FakeWinMm();
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                for (int i = 0; i < 3; i++) Step(im);
                Assert.True(Failed(im));
                Assert.Null(Slots(im)[WatchPad]);

                fake.Outputs.Add("Synth");
                Assert.True(StepUntil(im, () => Running(im)));
                Assert.False(Failed(im));
                Assert.True(fake.Has("OpenOutput 0"));
            });
        }

        /// <summary>The trace names why a failed slot tries again. A port
        /// that came back read as "reconfigured to Midi", though no setting
        /// had changed.</summary>
        [Fact]
        public void ALegacySlotWhosePortCameBack_SaysSoInTheTrace()
        {
            var fake = new FakeWinMm();
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                string mark = "MidiBackendLegacyTests mark " + Guid.NewGuid().ToString("N");
                SdlDiagLog.WriteLine(mark);

                fake.Outputs.Add("Synth");
                Assert.True(StepUntil(im, () => Running(im)));

                var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
                int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
                Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
                var after = lines.Skip(at + 1).ToList();
                Assert.Contains(after, l => l.EndsWith($"VCTRACE slot={WatchPad} createFailed CLEAR (MIDI port is back)", StringComparison.Ordinal));
                Assert.DoesNotContain(after, l => l.Contains($"slot={WatchPad} createFailed CLEAR (reconfigured", StringComparison.Ordinal));
            });
        }

        /// <summary>A port that was there all along but would not open stays
        /// failed until the slot is reconfigured, with no retry loop.</summary>
        [Fact]
        public void ALegacySlotWhosePortIsHeld_DoesNotRetry()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                for (int i = 0; i < 5; i++) Step(im);
                Assert.True(Failed(im));
                lock (fake.Calls) Assert.Single(fake.Calls, c => c.StartsWith("OpenOutput", StringComparison.Ordinal));
            });
        }

        /// <summary>A running slot whose port goes away closes it and fails,
        /// then reopens it by name once it is back, as with a replugged
        /// interface.</summary>
        [Fact]
        public void ALegacySlot_ReopensItsPortAfterAReplug()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var first = Slots(im)[WatchPad];
                for (int i = 0; i < 3; i++) Step(im);
                Assert.Same(first, Slots(im)[WatchPad]);

                fake.Outputs.Remove("Synth");
                Assert.True(StepUntil(im, () => Failed(im)));
                Assert.False(first.IsConnected);
                lock (fake.Calls) Assert.Contains(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));

                fake.Outputs.Add("Synth");
                Assert.True(StepUntil(im, () => Running(im)));
                Assert.NotSame(first, Slots(im)[WatchPad]);
            });
        }

        // ── Audit 2026-10-08 ──

        private sealed class FlipBackend : IMidiBackend
        {
            public volatile bool Answers;
            public int Attempts;
            public MidiApiKind Kind => MidiApiKind.AppSdk;
            public bool Start() { Interlocked.Increment(ref Attempts); return Answers; }
            public void Stop(bool skipDispose) { }
            public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex) => throw new InvalidOperationException();
            public IMidiInputSession CreateInputSession(string name) => null;
            public List<(string Id, string Name)> EnumerateNormalEndpoints() => new();
        }

        /// <summary>A Windows MIDI Services API that refused the probe, as at
        /// sign-in while the service was still starting, is tried again on
        /// the side, and the probe takes it once it starts. The legacy answer
        /// stood for the life of the process. In Legacy API mode, where the
        /// service never runs, nothing is tried.</summary>
        [Theory]
        [InlineData(MidiApiSelection.ApiModeFull, true)]
        [InlineData(MidiApiSelection.ApiModeLegacy, false)]
        public void ARefusedWindowsMidiServicesApi_IsTakenOnceItStarts_OutsideLegacyApiMode(int mode, bool moves)
        {
            var flip = new FlipBackend();
            MidiVirtualController.UseBackendFactoryForTest(() => flip, () => new MidiBackendLegacy(new FakeWinMm()));
            MidiApiSelection.ApiModeSeam = () => mode;
            MidiVirtualController.RefusalRecheckMs = 100;
            MidiServiceRecovery.CooldownMs = 300;
            int generation = MidiVirtualController.Generation;

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
            flip.Answers = true;

            bool moved = SpinWait.SpinUntil(() =>
            {
                MidiVirtualController.IsAvailable();
                return MidiVirtualController.ActiveApi == MidiApiKind.AppSdk;
            }, moves ? 5_000 : 1_500);
            Assert.Equal(moves, moved);
            Assert.Equal(moves, MidiVirtualController.Generation > generation);
            if (!moves) Assert.Equal(1, Volatile.Read(ref flip.Attempts));
        }

        /// <summary>The polling thread never waits on a WinMM send, which can
        /// block until the message is on the wire. The worker sends in order,
        /// and teardown sends a held note's Note Off before the port
        /// closes.</summary>
        [Fact]
        public void ALegacySend_NeverHoldsTheSubmit()
        {
            var gate = new System.Threading.ManualResetEventSlim(false);
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "Synth" };
            vc.ApplyLayout(0, 7, 1, 60, 1, 100);
            vc.Connect();
            try
            {
                fake.SendGate = gate;
                var submitted = new ManualResetEventSlim(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { true } });
                    vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { false } });
                    vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { true } });
                    submitted.Set();
                });
                Assert.True(submitted.Wait(5_000), "the submit waited on the send");
                gate.Set();
                Assert.True(vc.WaitForSends());
                Assert.Equal(new[] { 0x006407B0u, 0x00643C90u, 0x00003C80u, 0x00643C90u }, fake.Sent);

                vc.Dispose();
                lock (fake.Calls)
                {
                    int lastSend = fake.Calls.FindLastIndex(c => c.StartsWith("Send", StringComparison.Ordinal));
                    int close = fake.Calls.FindIndex(c => c.StartsWith("CloseOutput", StringComparison.Ordinal));
                    Assert.True(lastSend >= 0 && close > lastSend);
                }
                Assert.Equal(0x00003C80u, fake.Sent[^1]);
            }
            finally
            {
                gate.Set();
                vc.Dispose();
            }
        }

        /// <summary>A port whose handle died with its device while its name
        /// stayed listed, as after a replug faster than one listing, fails
        /// every send for good. The slot opens the port again.</summary>
        [Fact]
        public void OutputThatFailsWithNoDriver_ReopensWhileItsPortStaysListed()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var first = (MidiVirtualController)Slots(im)[WatchPad];
                fake.SendResult = WinMmMidi.MMSYSERR_NODRIVER;
                im.CombinedMidiRawStates[WatchPad] = new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { false } };
                Step(im);
                Assert.True(first.WaitForSends());
                Assert.True(first.EndpointLost);
                fake.SendResult = 0;

                Assert.True(StepUntil(im, () => Running(im) && !ReferenceEquals(Slots(im)[WatchPad], first)));
                lock (fake.Calls) Assert.Equal(2, fake.Calls.Count(c => c.StartsWith("OpenOutput", StringComparison.Ordinal)));
            });
        }

        /// <summary>A legacy open that hangs gets a midisrv restart only where
        /// WinMM runs through the service. On Windows before 24H2, in Legacy
        /// API mode and in Hybrid mode it is a classic driver's hang, which
        /// no service restart reaches, so none is made, and the failure names
        /// the port. Windows before 24H2 has no API mode value, which reads
        /// as full mode, so the build decides there.</summary>
        [Theory]
        [InlineData(26100, MidiApiSelection.ApiModeFull, 1)]
        [InlineData(26100, MidiApiSelection.ApiModeLegacy, 0)]
        [InlineData(26100, MidiApiSelection.ApiModeHybrid, 0)]
        [InlineData(22631, MidiApiSelection.ApiModeFull, 0)]
        [InlineData(19045, MidiApiSelection.ApiModeFull, 0)]
        public void ALegacyOpenThatHangs_RestartsTheServiceOnlyWhereThatCanHelp(int build, int mode, int expectedRestarts)
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenGate = new System.Threading.ManualResetEventSlim(false) };
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                MidiApiSelection.OsBuildSeam = () => build;
                MidiApiSelection.ApiModeSeam = () => mode;
                Assert.True(MidiVirtualController.IsAvailable());
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                MidiVirtualController.ConnectTimeoutMs = 300;

                var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "Synth" };
                var ex = Assert.Throws<MidiPortException>(() => vc.Connect());
                Assert.Contains("'Synth'", ex.Message);
                Assert.Equal(MidiPortProblem.DidNotOpen, ex.Problem);
                Assert.Equal(expectedRestarts, Volatile.Read(ref restarts));
            }
            finally { fake.OpenGate.Set(); }
        }

        /// <summary>The port picker keeps one listing in flight. While WinMM
        /// hangs, each dropdown open waits on the same listing instead of
        /// parking one more worker, and once it finishes the next open lists
        /// again.</summary>
        [Fact]
        public void ThePortPicker_KeepsOneListingInFlight_WhileWinMmHangs()
        {
            var gate = new System.Threading.ManualResetEventSlim(false);
            var fake = new FakeWinMm { Outputs = { "Synth" }, OutputCountGate = gate };
            try
            {
                Assert.Empty(MidiBackendLegacy.ListOutputPortsBounded(fake, 50));
                Assert.True(SpinWaitFor(() => Volatile.Read(ref fake.OutputCountEntries) == 1));
                Assert.Empty(MidiBackendLegacy.ListOutputPortsBounded(fake, 50));
                Assert.Empty(MidiBackendLegacy.ListOutputPortsBounded(fake, 50));
                Assert.Equal(1, Volatile.Read(ref fake.OutputCountEntries));
            }
            finally { gate.Set(); }
            // The listing in flight finishes and hands over its ports. The
            // open after that lists again.
            Assert.True(SpinWaitFor(() => MidiBackendLegacy.ListOutputPortsBounded(fake, 500).SequenceEqual(new[] { "Synth" })));
            int before = Volatile.Read(ref fake.OutputCountEntries);
            Assert.Equal(new[] { "Synth" }, MidiBackendLegacy.ListOutputPortsBounded(fake, 2_000));
            Assert.Equal(before + 1, Volatile.Read(ref fake.OutputCountEntries));
        }

        /// <summary>A callback already past its detach check when the input
        /// closes can set a note after the close rested the state. The next
        /// open rests it again, so that note is not held.</summary>
        [Fact]
        public void ANoteThatLandsAfterTheClose_IsNotHeldAtTheNextOpen()
        {
            var fake = new FakeWinMm { Inputs = { "Keys" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            var dev = new MidiInputDevice("winmm:Keys", "Keys");
            Assert.True(dev.Open());
            dev.Close();
            dev.OnUmp(0x20903C5Au, 0);
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);
            Assert.True(dev.Open());
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
            dev.Dispose();
        }

        /// <summary>A port listing stuck behind the MIDI service gets the
        /// service restart, the stuck listing returns, and listings carry on,
        /// so a port that goes away is still noticed. The stuck listing held
        /// its gate for the session.</summary>
        [Fact]
        public void ALegacyPortListingThatHangs_RestartsTheServiceAndListsAgain()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            var gate = new System.Threading.ManualResetEventSlim(false);
            try
            {
                WithLegacyMidiSlot(fake, "Synth", im =>
                {
                    Assert.True(StepUntil(im, () => Running(im)));
                    int restarts = 0;
                    MidiServiceRecovery.ResetCooldownForTest();
                    MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); gate.Set(); return true; };
                    im.LegacyPortListingHungMs = 200;
                    fake.OutputCountGate = gate;
                    var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.True(SpinWaitFor(() => { step5.Invoke(im, null); return Volatile.Read(ref restarts) == 1; }));
                    fake.OutputCountGate = null;
                    Assert.True(SpinWaitFor(() => !PrivateField<bool>(im, "_legacyPortListingRunning")));
                    // The input sweep re-probes after the restart in a running
                    // PadForge. This test runs Step 5 alone, and asks once the
                    // restart is over: an ask inside it answers no.
                    Assert.True(SpinWaitFor(() => !MidiServiceRecovery.RestartInFlight));
                    Assert.True(MidiVirtualController.IsAvailable());

                    fake.Outputs.Remove("Synth");
                    Assert.True(StepUntil(im, () => Failed(im)));
                    Assert.Equal(1, Volatile.Read(ref restarts));
                });
            }
            finally { gate.Set(); }
        }

        /// <summary>The listing watchdog's line says what follows. It said
        /// "restarting the MIDI service" before anything had been asked: in
        /// Legacy API mode and on Windows before 24H2 the listing hangs in a
        /// classic driver, which no service restart reaches, and nothing is
        /// restarted.</summary>
        [Theory]
        [InlineData(26100, MidiApiSelection.ApiModeLegacy)]
        [InlineData(22631, MidiApiSelection.ApiModeFull)]
        public void TheLegacyPortListingWatchdog_SaysNoRestartFollows_WhereNoneCanHelp(int build, int mode)
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            var gate = new System.Threading.ManualResetEventSlim(false);
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                try
                {
                    Assert.True(StepUntil(im, () => Running(im)));
                    MidiApiSelection.OsBuildSeam = () => build;
                    MidiApiSelection.ApiModeSeam = () => mode;
                    int restarts = 0;
                    MidiServiceRecovery.ResetCooldownForTest();
                    MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                    im.LegacyPortListingHungMs = 0;
                    string mark = "MidiBackendLegacyTests mark " + Guid.NewGuid().ToString("N");
                    SdlDiagLog.WriteLine(mark);
                    List<string> Watchdog()
                    {
                        var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
                        int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
                        Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
                        return lines.Skip(at + 1).Where(l => l.Contains("legacy MIDI port listing in flight")).ToList();
                    }

                    // A listing starts and hangs, and the watchdog fires on
                    // the first step that finds it in flight.
                    fake.OutputCountGate = gate;
                    int entries = Volatile.Read(ref fake.OutputCountEntries);
                    var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.True(SpinWaitFor(() =>
                    {
                        step5.Invoke(im, null);
                        return Volatile.Read(ref fake.OutputCountEntries) > entries;
                    }));
                    step5.Invoke(im, null);

                    var line = Assert.Single(Watchdog());
                    Assert.EndsWith("no MIDI service restart reaches a classic MIDI driver", line, StringComparison.Ordinal);
                    Assert.DoesNotContain("restarting the MIDI service", line);
                }
                finally
                {
                    fake.OutputCountGate = null;
                    gate.Set();
                }
            });
        }

        private static string SlotName(int shownNumber)
            => string.Format(PadForge.Resources.Strings.Strings.Instance.Main_VirtualController_Format, shownNumber);

        /// <summary>A new legacy slot with no port tells the user to pick one,
        /// naming the slot the way the UI does, and the line clears once the
        /// slot starts. The status bar read "Failed to create Midi virtual
        /// controller for pad 2". The slot sits on pad 2 and is the only one,
        /// so the UI shows it as Virtual Controller 1, and that is the number
        /// the line carries.</summary>
        [Fact]
        public void ALegacySlotWithNoPort_TellsTheUserToPickOne_AndClearsItOnceItRuns()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            using var orders = new SlotOrdersScope(midi: new[] { WatchPad });
            WithLegacyMidiSlot(fake, "", im =>
            {
                var raised = new List<string>();
                var resolved = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                im.ErrorResolved += (_, m) => { lock (resolved) resolved.Add(m); };
                string expected = string.Format(PadForge.Resources.Strings.Strings.Instance.Status_MidiPortNotPicked_Format, SlotName(1));

                Assert.True(StepUntil(im, () => Failed(im)));
                lock (raised) Assert.Equal(new[] { expected }, raised);

                im._midiConfigs[WatchPad].ChooseOutputPort("Synth");
                Assert.True(StepUntil(im, () => Running(im)));
                lock (resolved) Assert.Contains(expected, resolved);
            });
        }

        /// <summary>On the classic MIDI stack a port serves one client, and
        /// PadForge's own second slot on the same port is such a client. The
        /// line blamed another program, and the user looked for one that did
        /// not exist. It names the slot that is set to the same port, and
        /// clears once the slot has a port of its own.</summary>
        [Fact]
        public void ASlotWhosePortAnotherSlotIsSetTo_NamesThatSlot()
        {
            const int second = 5;
            var fake = new FakeWinMm { Outputs = { "Synth", "Piano" }, OneClientPerOutput = true };
            // The UI shows pad 2 as Virtual Controller 1 and pad 5 as
            // Virtual Controller 2.
            using var orders = new SlotOrdersScope(midi: new[] { WatchPad, second });
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var raised = new List<string>();
                var resolved = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                im.ErrorResolved += (_, m) => { lock (resolved) resolved.Add(m); };

                AddLegacyMidiSlot(im, second, "Synth");
                Assert.True(StepUntil(im, () => Failed(im, second)));
                string expected = string.Format(PadForge.Resources.Strings.Strings.Instance.Status_MidiPortShared_Format,
                    SlotName(2), "Synth", SlotName(1));
                lock (raised) Assert.Equal(new[] { expected }, raised);
                Assert.True(Running(im));

                im._midiConfigs[second].ChooseOutputPort("Piano");
                Assert.True(StepUntil(im, () => Running(im, second)));
                lock (resolved) Assert.Equal(new[] { expected }, resolved);
            });
        }

        /// <summary>The line says to pick another port for one of the two
        /// slots, and it works on either: once the other slot has moved off
        /// the port, the slot that failed opens it by itself. While both
        /// stay on the port it is not tried again. It stayed failed, with
        /// the line still up, until its own port was changed.</summary>
        [Fact]
        public void ASlotWhosePortTheOtherSlotLetGoOf_StartsByItself()
        {
            const int second = 5;
            var fake = new FakeWinMm { Outputs = { "Synth", "Piano" }, OneClientPerOutput = true };
            using var orders = new SlotOrdersScope(midi: new[] { WatchPad, second });
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var raised = new List<string>();
                var resolved = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                im.ErrorResolved += (_, m) => { lock (resolved) resolved.Add(m); };
                int Opens(int id) { lock (fake.Calls) return fake.Calls.Count(c => c == $"OpenOutput {id}"); }

                AddLegacyMidiSlot(im, second, "Synth");
                Assert.True(StepUntil(im, () => Failed(im, second)));
                for (int i = 0; i < 5; i++) Step(im);
                Assert.True(Failed(im, second));
                Assert.Equal(2, Opens(0));

                // The first slot moves to another port.
                im._midiConfigs[WatchPad].ChooseOutputPort("Piano");
                Assert.True(StepUntil(im, () => Running(im, second) && Running(im)));
                Assert.Equal(3, Opens(0));
                Assert.Equal(1, Opens(1));
                string line;
                lock (raised) line = Assert.Single(raised);
                lock (resolved) Assert.Equal(new[] { line }, resolved);
            });
        }

        /// <summary>A create that fails for a port another MIDI slot is set
        /// to marks its slot for the latch that follows, so the slot tries
        /// again once the other slot lets go of the port. A failure that
        /// takes no latch, as when the slot's controller drops out during
        /// the create, left the mark behind, and the latch of the slot's
        /// next failure took it, whatever that failure was: the slot then
        /// tried once more as soon as no other MIDI slot was set to its
        /// port. Every create starts with no mark.</summary>
        [Fact]
        public void ASharedPortFailureThatTookNoLatch_LeavesNoMarkForTheSlotsNextFailure()
        {
            const int second = 5;
            var fake = new FakeWinMm { Outputs = { "Synth", "Piano" }, OneClientPerOutput = true };
            using var orders = new SlotOrdersScope(midi: new[] { WatchPad, second });
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var raised = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                var strings = PadForge.Resources.Strings.Strings.Instance;

                // The second slot's open waits at a gate, its controller
                // drops out meanwhile, and the open then finds the port
                // taken. The slot is no longer eligible, so nothing latches.
                var device = AddLegacyMidiSlot(im, second, "Synth");
                fake.OpenGate = new ManualResetEventSlim(false);
                try
                {
                    Pass(im);
                    Assert.NotNull(PrivateField<System.Threading.Tasks.Task[]>(im, "_pendingConnectTask")[second]);
                    device.IsOnline = false;
                }
                finally { fake.OpenGate.Set(); }
                WaitForWork(im);
                fake.OpenGate = null;
                string shared = string.Format(strings.Status_MidiPortShared_Format, SlotName(2), "Synth", SlotName(1));
                lock (raised) Assert.Equal(new[] { shared }, raised);
                Assert.False(Failed(im, second));

                // No MIDI API runs any more, the first slot moves to another
                // port, and the second slot's controller is back. Its create
                // fails for want of an API, which no port has a part in.
                MidiVirtualController.UseBackendFactoryForTest(() => null);
                Assert.False(MidiVirtualController.IsAvailable());
                im._midiConfigs[WatchPad].ChooseOutputPort("Piano");
                device.IsOnline = true;
                Assert.True(StepUntil(im, () => Failed(im, second)));
                for (int i = 0; i < 5; i++) Step(im);

                // One failure, and no second attempt on account of a port
                // that was never the trouble.
                string notRunning = string.Format(strings.Status_MidiNotRunning_Format, SlotName(2));
                lock (raised) Assert.Equal(new[] { shared, notRunning }, raised);
                Assert.True(Failed(im, second));
            });
        }

        /// <summary>A port that is in use with no other slot set to it says
        /// so without naming a culprit. A slot that is not created does not
        /// count as one, whatever its bar still names.</summary>
        [Fact]
        public void APortInUseElsewhere_IsNotPutDownToAnotherSlot()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            using var orders = new SlotOrdersScope(midi: new[] { WatchPad });
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                im.SlotControllerTypes[5] = VirtualControllerType.Midi;
                im._midiConfigs[5] = new MidiSlotConfig { OutputPort = "Synth" };
                var raised = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };

                Assert.True(StepUntil(im, () => Failed(im)));
                string expected = string.Format(PadForge.Resources.Strings.Strings.Instance.Status_MidiPortInUse_Format,
                    SlotName(1), "Synth");
                lock (raised) Assert.Equal(new[] { expected }, raised);
                Assert.DoesNotContain("program", expected, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>A MIDI status line names the slot by the number the UI
        /// shows, which counts the slots in group order with MIDI last. It
        /// named the pad index plus one, so with an Xbox slot made after the
        /// MIDI slot the line sent the user to the Xbox controller's
        /// page.</summary>
        [Fact]
        public void AMidiStatusLine_NamesTheSlotByTheNumberTheUiShows()
        {
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedOrders = new[]
            {
                SettingsManager.XboxSlotOrder, SettingsManager.PlayStationSlotOrder, SettingsManager.NintendoSlotOrder,
                SettingsManager.ExtendedSlotOrder, SettingsManager.KeyboardMouseSlotOrder, SettingsManager.MidiSlotOrder,
                SettingsManager.VrSlotOrder,
            };
            try
            {
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                SettingsManager.SlotCreated[0] = true;   // the MIDI slot, made first
                SettingsManager.SlotCreated[1] = true;   // an Xbox slot, made second
                SettingsManager.XboxSlotOrder = new List<int> { 1 };
                SettingsManager.PlayStationSlotOrder = new List<int>();
                SettingsManager.NintendoSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int> { 0 };
                SettingsManager.VrSlotOrder = new List<int>();
                var label = typeof(InputManager).GetMethod("SlotLabel", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(label);
                string Label(int pad) => (string)label.Invoke(null, new object[] { pad });
                static string Numbered(int n)
                    => string.Format(PadForge.Resources.Strings.Strings.Instance.Main_VirtualController_Format, n);

                Assert.Equal(2, SettingsManager.SlotOrders.GetGlobalSlotNumber(0));
                Assert.Equal(Numbered(2), Label(0));
                Assert.Equal(Numbered(1), Label(1));
                // A slot in no group's order has no shown number, and its
                // line falls back to the pad.
                Assert.Equal(Numbered(4), Label(3));
            }
            finally
            {
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                SettingsManager.XboxSlotOrder = savedOrders[0];
                SettingsManager.PlayStationSlotOrder = savedOrders[1];
                SettingsManager.NintendoSlotOrder = savedOrders[2];
                SettingsManager.ExtendedSlotOrder = savedOrders[3];
                SettingsManager.KeyboardMouseSlotOrder = savedOrders[4];
                SettingsManager.MidiSlotOrder = savedOrders[5];
                SettingsManager.VrSlotOrder = savedOrders[6];
            }
        }

        /// <summary>A failed slot gets a fresh attempt once a later restart
        /// starts a new generation, the rule a running slot follows. The
        /// latch held while the API kind stayed the same.</summary>
        [Fact]
        public void AFailedMidiSlot_RetriesInANewGeneration()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                for (int i = 0; i < 3; i++) Step(im);
                Assert.True(Failed(im));

                fake.OpenResult = 0;
                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.True(StepUntil(im, () => Running(im)));
            });
        }

        /// <summary>A None picked for a profile is a choice: it clears the
        /// slot's port. A profile that never picked one keeps the slot's
        /// port.</summary>
        [Fact]
        public void AnAuthoredNone_ClearsTheSlotsPort()
        {
            var config = new MidiSlotConfig();
            config.ChooseOutputPort("Synth");
            new MidiSlotConfigData { OutputPort = "" }.ApplyOutputPortTo(config);
            Assert.Equal(string.Empty, config.OutputPort);
            Assert.True(config.OutputPortChosen);
            Assert.Equal(string.Empty, config.OutputPortSnapshot);

            config.ChooseOutputPort("Synth");
            new MidiSlotConfigData().ApplyOutputPortTo(config);
            Assert.Equal("Synth", config.OutputPort);
            Assert.False(config.OutputPortChosen);
            Assert.Null(config.OutputPortSnapshot);
        }

        /// <summary>Choosing None on a slot that has no port is a choice the
        /// settings file records, so it has to reach whoever marks the file
        /// changed. The port itself does not change there, and nothing was
        /// raised, so a choice of None made by the port's reset was saved
        /// only if something else changed too.</summary>
        [Fact]
        public void ChoosingNone_OnASlotWithNoPort_IsAChangeTheSettingsHear()
        {
            var config = new MidiSlotConfig();
            var raised = new List<string>();
            config.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            config.ChooseOutputPort(string.Empty);
            Assert.True(config.OutputPortChosen);
            Assert.Equal(string.Empty, config.OutputPortSnapshot);
            Assert.Equal(new[] { nameof(MidiSlotConfig.OutputPortChosen) }, raised);

            // The same choice again changes nothing.
            raised.Clear();
            config.ChooseOutputPort(string.Empty);
            Assert.Empty(raised);

            // A snapshot with no choice takes the mark back, and that is a
            // change as well.
            new MidiSlotConfigData().ApplyOutputPortTo(config);
            Assert.False(config.OutputPortChosen);
            Assert.Equal(new[] { nameof(MidiSlotConfig.OutputPortChosen) }, raised);

            // The window marks the settings changed on any of them.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            Assert.Contains("pad.MidiConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();",
                File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "MainWindow.xaml.cs")));
        }

        /// <summary>Remote Link shares every online MIDI input, and under the
        /// legacy API one no local slot has was listed but never opened, so a
        /// peer that assigned it read rest forever. A peer's demand opens the
        /// port as a local slot does, and its lapse closes it.</summary>
        [Fact]
        public void APeersSlot_OpensAnUnassignedLegacyInput_AndItsLapseClosesIt()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var fake = new FakeWinMm { Inputs = { "Keys" } };
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                bool wanted = false;
                var im = new InputManager
                {
                    RemoteMidiInputDemand = g => wanted && g == MidiInputDevice.InstanceGuidFor("winmm:Keys"),
                };
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.True(SpinWaitFor(() => { sweep.Invoke(im, null); return SettingsManager.UserDevices.Items.Count == 1; }));
                for (int i = 0; i < 3; i++) sweep.Invoke(im, null);
                Assert.False(fake.Has("OpenInput 0"));

                wanted = true;
                Assert.True(SpinWaitFor(() => { sweep.Invoke(im, null); return fake.Has("OpenInput 0"); }));
                Assert.False(fake.Has("CloseInput"));

                wanted = false;
                Assert.True(SpinWaitFor(() => { sweep.Invoke(im, null); return fake.Has("CloseInput"); }));
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The consumer ships the MIDI input demand as kind 4, at
        /// most once a second per device, and the owner stamps it and hands
        /// the engine the freshness check.</summary>
        [Fact]
        public void TheMidiInputDemand_ShipsAsItsOwnKind_AndTheOwnerReadsIt()
        {
            const string route = "peer://midi-demand-test/keys";
            var savedSend = RemoteLinkOutputRouter.SendSourceDemand;
            var sent = new List<byte[]>();
            try
            {
                RemoteLinkOutputRouter.SendSourceDemand = (_, _, payload) => { lock (sent) sent.Add(payload); };
                RemoteLinkOutputRouter.Register(route, "midi-demand-peer", 0);
                RemoteLinkOutputRouter.ShipMidiInputDemand(route);
                RemoteLinkOutputRouter.ShipMidiInputDemand(route);
                lock (sent) Assert.Equal(new[] { RemoteLinkOutputRouter.DemandKindMidiInput }, Assert.Single(sent));
                Assert.Equal(4, RemoteLinkOutputRouter.DemandKindMidiInput);
            }
            finally
            {
                RemoteLinkOutputRouter.Unregister(route);
                RemoteLinkOutputRouter.SendSourceDemand = savedSend;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string service = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("frame.Payload[0] == RemoteLinkOutputRouter.DemandKindMidiInput)", service);
            Assert.Contains("_remoteMidiDemandMs[device.InstanceGuid] = Environment.TickCount64;", service);
            Assert.Contains("_inputManager.RemoteMidiInputDemand = HasFreshRemoteMidiDemand;", service);
            Assert.Contains("RemoteLinkOutputRouter.ShipMidiInputDemand(path);", service);
        }

        /// <summary>The consumer half, run as the service runs it: on the
        /// idle cadence, a created slot here that has a peer's MIDI input
        /// assigned ships the demand to that input's owner. A peer input no
        /// slot has, one on a slot that is not created, a peer device that is
        /// not MIDI, and a MIDI input of this PC ship nothing. The walk had
        /// no test of its own, only substrings of its source, which stayed
        /// true with its call removed and with each of its tests turned
        /// around.</summary>
        [Fact]
        public void TheConsumer_ShipsTheMidiInputDemand_OnlyForAPeersInputOnACreatedSlot()
        {
            const int createdSlot = 1, emptySlot = 2;
            var savedSend = RemoteLinkOutputRouter.SendSourceDemand;
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var routes = new List<string>();
            var sent = new List<string>();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                SettingsManager.SlotCreated[createdSlot] = true;
                // The same tick ships the other demand kinds (NFC, for one,
                // while another test's NFC read is still fresh). Only the
                // MIDI input demand is heard here.
                RemoteLinkOutputRouter.SendSourceDemand = (owner, _, payload) =>
                {
                    if (payload[0] == RemoteLinkOutputRouter.DemandKindMidiInput) lock (sent) sent.Add(owner);
                };

                // Every device has a route to an owner, so a walk that ships
                // for the wrong one is heard.
                void Add(string owner, string path, int capType, int? slot)
                {
                    var guid = Guid.NewGuid();
                    var ud = new UserDevice { InstanceGuid = guid, ProductName = owner, DevicePath = path, IsOnline = true, CapType = capType };
                    lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                    if (slot is int mapTo)
                        lock (SettingsManager.UserSettings.SyncRoot)
                            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = mapTo });
                    RemoteLinkOutputRouter.Register(path, owner, 0);
                    routes.Add(path);
                }
                Add("on a created slot", "peer://midi-consumer-test/assigned", InputDeviceType.Midi, createdSlot);
                Add("on no slot", "peer://midi-consumer-test/unassigned", InputDeviceType.Midi, null);
                Add("on a slot not created", "peer://midi-consumer-test/empty-slot", InputDeviceType.Midi, emptySlot);
                Add("not MIDI", "peer://midi-consumer-test/gamepad", InputDeviceType.Gamepad, createdSlot);
                Add("this PC's own", "midi://midi-consumer-test-local", InputDeviceType.Midi, createdSlot);

                var service = new PadForge.Services.InputService(new MainViewModel());
                typeof(PadForge.Services.InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(service, new InputManager());
                var idleTick = typeof(PadForge.Services.InputService).GetMethod("UpdateIdleState", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(idleTick);
                idleTick.Invoke(service, null);

                lock (sent) Assert.Equal(new[] { "on a created slot" }, sent);
            }
            finally
            {
                foreach (var route in routes) RemoteLinkOutputRouter.Unregister(route);
                RemoteLinkOutputRouter.SendSourceDemand = savedSend;
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
            }
        }

        // ── The saved port ──

        /// <summary>A snapshot with no choice of port leaves the attribute
        /// out. A choice of None is written as an empty attribute and reads
        /// back as None, not as no choice, since a slot set to None does not
        /// start.</summary>
        [Fact]
        public void TheSavedPort_IsLeftOutOfTheFile_UntilOneIsPicked()
        {
            var serializer = new XmlSerializer(typeof(MidiSlotConfigData));
            string Xml(MidiSlotConfigData d)
            {
                using var w = new StringWriter();
                serializer.Serialize(w, d);
                return w.ToString();
            }

            Assert.DoesNotContain("OutputPort", Xml(new MidiSlotConfigData { OutputPort = null }));
            string xml = Xml(new MidiSlotConfigData { OutputPort = "loopMIDI Port" });
            Assert.Contains("OutputPort=\"loopMIDI Port\"", xml);
            var back = (MidiSlotConfigData)serializer.Deserialize(new StringReader(xml));
            Assert.Equal("loopMIDI Port", back.OutputPort);

            string none = Xml(new MidiSlotConfigData { OutputPort = "" });
            Assert.Contains("OutputPort=\"\"", none);
            Assert.Equal(string.Empty, ((MidiSlotConfigData)serializer.Deserialize(new StringReader(none))).OutputPort);
            Assert.Null(((MidiSlotConfigData)serializer.Deserialize(new StringReader(Xml(new MidiSlotConfigData())))).OutputPort);
        }

        /// <summary>A profile saved before a port was picked keeps the
        /// slot's current port. One that has a port applies it.</summary>
        [Fact]
        public void ASnapshotWithNoPort_KeepsTheSlotsPort()
        {
            var config = new MidiSlotConfig { OutputPort = "Synth" };
            new MidiSlotConfigData().ApplyOutputPortTo(config);
            Assert.Equal("Synth", config.OutputPort);
            new MidiSlotConfigData { OutputPort = "loopMIDI Port" }.ApplyOutputPortTo(config);
            Assert.Equal("loopMIDI Port", config.OutputPort);
        }

        [Fact]
        public void ResettingTheSlot_ClearsThePort()
        {
            var config = new MidiSlotConfig { OutputPort = "Synth" };
            config.ResetToDefaults();
            Assert.Equal(string.Empty, config.OutputPort);
            config.OutputPort = null;
            Assert.Equal(string.Empty, config.OutputPort);
        }
    }

    /// <summary>The runtime install's checks, off the network.</summary>
    public class MidiRuntimeInstallTests
    {
        /// <summary>Only Microsoft's bundle takes tools Microsoft no
        /// longer offers with it, so only its uninstall asks first.</summary>
        [Theory]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.MicrosoftBundle, true)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.MicrosoftPackage, false)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.PadForgePackage, false)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.None, false)]
        public void OnlyMicrosoftsBundle_AsksBeforeItsUninstall(int owner, bool asks)
        {
            Assert.Equal(asks, DriverInstaller.MidiUninstallIsIrreversible((DriverInstaller.MidiRuntimeOwner)owner));
        }

        [Theory]
        [InlineData(14, 51, true)]
        [InlineData(14, 52, true)]
        [InlineData(15, 0, true)]
        [InlineData(14, 50, false)]
        [InlineData(14, 44, false)]
        [InlineData(13, 99, false)]
        public void TheVisualCppRuntime_MustBeAtLeastTheLinkersVersion(int major, int minor, bool expected)
        {
            Assert.Equal(expected, DriverInstaller.VcVersionAtLeast(major, minor, DriverInstaller.MidiRuntimeVcMinimum));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(3010, true)]
        [InlineData(1641, true)]
        [InlineData(1638, true)]
        [InlineData(1603, false)]
        [InlineData(1602, false)]
        public void TheRedistributablesExitCode_SaysWhetherTheRuntimeIsThere(int exitCode, bool expected)
        {
            Assert.Equal(expected, DriverInstaller.IsVcRedistSuccess(exitCode));
        }

        /// <summary>Drives <see cref="DriverInstaller.InstallVcRuntime"/> with
        /// scripted answers and records each run and pause.</summary>
        private sealed class VcRedistScript
        {
            public readonly List<string> Runs = new();
            public int Pauses;
            public (int? Exit, bool Untracked)[] Answers = { (3010, false) };
            public int LandsAfterRun = 1;
            public bool Queued;

            public void Install() => DriverInstaller.InstallVcRuntime(
                arguments =>
                {
                    Runs.Add(arguments);
                    return Answers[Math.Min(Runs.Count, Answers.Length) - 1];
                },
                () => LandsAfterRun > 0 && Runs.Count >= LandsAfterRun,
                () => Pauses++,
                () => Queued);
        }

        /// <summary>A 3010 usually leaves the new runtime in place already,
        /// since Windows Installer renames a loaded DLL and copies the new one
        /// in. One install then does it.</summary>
        [Fact]
        public void TheRedistributable_RunsOnce_WhenTheRuntimeLands()
        {
            var script = new VcRedistScript();
            script.Install();
            Assert.Equal(new[] { "/install /quiet /norestart" }, script.Runs);
            Assert.Equal(0, script.Pauses);
        }

        /// <summary>A program holding the old file open makes Windows queue
        /// the new copy for the next startup. The install used to say restart
        /// Windows there. It runs again as a repair, since another install of
        /// the registered version plans nothing.</summary>
        [Fact]
        public void AHeldRuntime_IsRepaired_UntilItLands()
        {
            var script = new VcRedistScript { LandsAfterRun = 2 };
            script.Install();
            Assert.Equal(new[] { "/install /quiet /norestart", "/repair /quiet /norestart" }, script.Runs);
            Assert.Equal(1, script.Pauses);
        }

        /// <summary>Nothing PadForge says about the runtime tells anyone to
        /// restart. A copy Windows queued behind a held file names the
        /// hold.</summary>
        [Theory]
        [InlineData(true, "A program holds the old Visual C++ runtime open")]
        [InlineData(false, "still older than 14.51")]
        public void ARuntimeThatNeverLands_FailsWithoutARestartLine(bool queued, string expected)
        {
            var script = new VcRedistScript { LandsAfterRun = 0, Queued = queued };
            var ex = Assert.Throws<DriverCheckException>(script.Install);
            // The reason rides on the exception for the status line to word
            // in the UI language, and the message is the log's English.
            Assert.Equal(queued ? DriverCheckFailure.VcRuntimeHeld : DriverCheckFailure.VcRuntimeStillOld, ex.Failure);
            Assert.Equal(queued ? string.Empty : "14.51", ex.Detail);
            Assert.Contains(expected, ex.Message);
            Assert.DoesNotContain("restart", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reboot", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DriverInstaller.VcRedistRuns, script.Runs);
            Assert.Equal(DriverInstaller.VcRedistRuns.Length - 1, script.Pauses);
        }

        [Fact]
        public void AFailedRun_StopsTheRepairs()
        {
            var script = new VcRedistScript { LandsAfterRun = 0, Answers = new (int?, bool)[] { (3010, false), (1603, false) } };
            var ex = Assert.Throws<InstallerFailedException>(script.Install);
            Assert.Equal(1603, ex.ExitCode);
            Assert.Equal(2, script.Runs.Count);
        }

        /// <summary>A redistributable PadForge could not follow leaves the
        /// outcome unknown, and the runtime's own installer never ran, so the
        /// line names the Visual C++ step and asks for Install again. The
        /// generic line said the card would show a result it never
        /// could.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARunWithNoExitCode_LeavesTheOutcomeUnknown(bool untracked)
        {
            var script = new VcRedistScript { Answers = new (int?, bool)[] { (null, untracked) } };
            var ex = Assert.Throws<DriverCheckException>(script.Install);
            Assert.Equal(DriverCheckFailure.VcRedistStillRunning, ex.Failure);
            Assert.Contains("Visual C++", ex.Message);
            Assert.Contains("Install again", ex.Message);
            var inner = Assert.IsType<InstallerFailedException>(ex.InnerException);
            Assert.True(inner.OutcomeUnknown);
            Assert.Equal(untracked, inner.NotTracked);
            Assert.Single(script.Runs);
        }

        /// <summary>The list holds pairs, the queued file and the file it
        /// replaces. Only a target in the system folder counts, and a delete
        /// of an old runtime copy is not a queued replacement.</summary>
        [Theory]
        [InlineData(new[] { @"\??\C:\Config.Msi\3f2a1.tmp", @"!\??\C:\WINDOWS\system32\msvcp140.dll" }, true)]
        [InlineData(new[] { @"\??\C:\Config.Msi\3f2a1.tmp", @"\??\C:\Windows\System32\VCRUNTIME140_1.dll" }, true)]
        [InlineData(new[] { @"\??\C:\Config.Msi\9c.rbf", "", @"\??\C:\Config.Msi\3f2a1.tmp", @"!\??\C:\WINDOWS\system32\msvcp140_atomic_wait.dll" }, true)]
        [InlineData(new[] { @"\??\C:\WINDOWS\system32\msvcp140.dll", "" }, false)]
        [InlineData(new[] { @"\??\C:\Config.Msi\3f2a1.tmp", @"!\??\C:\WINDOWS\SysWOW64\msvcp140.dll" }, false)]
        [InlineData(new[] { @"\??\C:\Config.Msi\3f2a1.tmp", @"!\??\C:\WINDOWS\system32\drivers\msvcp140.dll" }, false)]
        [InlineData(new[] { @"\??\C:\Config.Msi\3f2a1.tmp", @"!\??\C:\WINDOWS\system32\ucrtbase.dll" }, false)]
        [InlineData(new string[0], false)]
        public void ThePendingRenameList_FindsAQueuedRuntimeDll(string[] pending, bool expected)
        {
            Assert.Equal(expected, DriverInstaller.VcRuntimeReplacementQueued(pending, @"C:\WINDOWS\system32"));
        }

        [Fact]
        public void NoPendingRenameList_QueuesNothing()
        {
            Assert.False(DriverInstaller.VcRuntimeReplacementQueued(null, @"C:\WINDOWS\system32"));
            Assert.False(DriverInstaller.VcRuntimeReplacementQueued(new[] { "a", null }, @"C:\WINDOWS\system32"));
        }

        [Fact]
        public void TheHash_IsSha256InHex()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "abc");
                Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", DriverInstaller.Sha256Hex(path));
            }
            finally { File.Delete(path); }
        }

        /// <summary>The positive control is a DLL Microsoft signs inside the
        /// file. A copy with one byte changed must fail: the check verifies
        /// the signature over the content, not only who signed it.</summary>
        [Fact]
        public void OnlyAnIntactMicrosoftSignature_Passes()
        {
            string signed = Path.Combine(Environment.SystemDirectory, "msvcp140.dll");
            Assert.True(DriverInstaller.IsSignedByMicrosoft(signed));

            string copy = Path.Combine(Path.GetTempPath(), $"pf-sigtest-{Guid.NewGuid():N}.dll");
            try
            {
                byte[] bytes = File.ReadAllBytes(signed);
                bytes[bytes.Length / 3] ^= 0xFF;
                File.WriteAllBytes(copy, bytes);
                Assert.False(DriverInstaller.IsSignedByMicrosoft(copy));

                File.WriteAllText(copy, "not a signed file");
                Assert.False(DriverInstaller.IsSignedByMicrosoft(copy));
            }
            finally { File.Delete(copy); }
        }

        /// <summary>The download URLs and hashes name the published
        /// assets.</summary>
        [Fact]
        public void TheRuntimeAssets_ArePinned()
        {
            Assert.StartsWith("https://github.com/hifihedgehog/PadForge-MIDI-Runtime/releases/download/", DriverInstaller.MidiRuntimeReleaseUrl);
            Assert.EndsWith("-x64.msi", DriverInstaller.MidiRuntimeMsiX64);
            Assert.EndsWith("-arm64.msi", DriverInstaller.MidiRuntimeMsiArm64);
            Assert.Matches("^[0-9a-f]{64}$", DriverInstaller.MidiRuntimeSha256X64);
            Assert.Matches("^[0-9a-f]{64}$", DriverInstaller.MidiRuntimeSha256Arm64);
            Assert.NotEqual(DriverInstaller.MidiRuntimeSha256X64, DriverInstaller.MidiRuntimeSha256Arm64);
        }
    }
}
