using System;
using System.Collections.Concurrent;

namespace PadForge.Common.Input
{
    /// <summary>
    /// DualShock 4 Bluetooth audio beyond the speaker stream (#528): the
    /// per-pad mode byte, the headset microphone, and the headphone volume
    /// the arm report carries. The stream itself stays in
    /// <see cref="Ds4BtTick"/>.
    /// </summary>
    internal static partial class AudioPassthroughService
    {
        /// <summary>Owning slot and device's Headphone Volume, 0 to 100.
        /// Wired by InputService. Null or a throw resolves to 100, the level
        /// the arm report always sent before #528.</summary>
        internal static Func<int, Guid, int> DeviceHeadphoneVolumeProvider;

        private static int ResolveHeadphoneVolume(int slot, Guid device)
        {
            try { return DeviceHeadphoneVolumeProvider?.Invoke(slot, device) ?? 100; }
            catch { return 100; }
        }

        // ── Headset microphone mode ─────────────────────────────────────

        /// <summary>The DualShock 4 headset microphone. Off until PadForge
        /// takes an SDL build with hifihedgehog/SDL#41: with the microphone
        /// armed the controller sends its state inside input reports larger
        /// than 0x11, and the SDL driver checks their CRC at byte 78 and
        /// drops them, which would stop the pad's input. The DualSense
        /// microphone waited the same way on hifihedgehog/SDL#20
        /// (<see cref="EnableBtMic"/>). A readonly field rather than a
        /// constant, so the gated code stays compiled and warning-free.</summary>
        internal static readonly bool EnableDs4BtMic = false;

        /// <summary>How long a reader's last "wanted" keeps the microphone
        /// armed, so a consumer that blinks does not flap the mode.</summary>
        private const long Ds4MicHoldMs = 2000;

        private sealed class Ds4MicState
        {
            public int Slot = -1;
            public volatile bool Plugged;
            public long PersonaWantedTicks;
            public long VoiceWantedTicks;
            public bool LastArmed;
            public readonly object Gate = new();
        }

        private static readonly ConcurrentDictionary<Guid, Ds4MicState> _ds4Mic = new();

        /// <summary>The arming rule, pure so tests can pin it: the gate is
        /// on, the status byte shows a microphone, and a consumer wanted it
        /// within <see cref="Ds4MicHoldMs"/>.</summary>
        internal static bool Ds4MicArmedRule(bool enabled, bool plugged, long personaWanted,
                                             long voiceWanted, long now)
            => enabled && plugged
               && ((personaWanted != 0 && now - personaWanted <= Ds4MicHoldMs)
                   || (voiceWanted != 0 && now - voiceWanted <= Ds4MicHoldMs));

        private static bool ArmedNow(Ds4MicState st, long now)
            => Ds4MicArmedRule(EnableDs4BtMic, st.Plugged,
                               System.Threading.Volatile.Read(ref st.PersonaWantedTicks),
                               System.Threading.Volatile.Read(ref st.VoiceWantedTicks), now);

        /// <summary>True while this pad's headset microphone is armed, which
        /// puts 0xA1 in byte 2 of its effects and audio reports.</summary>
        internal static bool IsDs4MicArmed(Guid pad)
            => _ds4Mic.TryGetValue(pad, out var st) && ArmedNow(st, Environment.TickCount64);

        /// <summary>Byte 2 for this pad's next effects or audio report.</summary>
        internal static byte Ds4AudioMode(Guid pad)
            => IsDs4MicArmed(pad) ? Ds4BtAudio.ModeMicrophone : Ds4BtAudio.ModeSpeaker;

        /// <summary>True while any DualShock 4 on this slot has its
        /// microphone armed. The effects dispatcher keeps its timer alive
        /// for it, so a 0xA1 report reaches the pad well inside the
        /// controller's four-second microphone timeout (DS4Device.cs:2064 in
        /// the hbashton fork).</summary>
        internal static bool SlotHasArmedDs4Mic(int slot)
        {
            if (!EnableDs4BtMic || slot < 0) return false;
            long now = Environment.TickCount64;
            foreach (var kv in _ds4Mic)
                if (kv.Value.Slot == slot && ArmedNow(kv.Value, now)) return true;
            return false;
        }

        private static void NoteDs4MicPlugged(Guid pad, bool plugged)
        {
            if (pad == Guid.Empty) return;
            _ds4Mic.GetOrAdd(pad, _ => new Ds4MicState()).Plugged = plugged;
        }

        /// <summary>A reader reports, once per input report, whether its
        /// consumer wants the microphone. A change of the armed state wakes
        /// the slot's effects dispatcher, which carries the new byte 2.</summary>
        private static void SetDs4MicWanted(Guid pad, int slot, bool persona, bool wanted)
        {
            if (pad == Guid.Empty) return;
            var st = _ds4Mic.GetOrAdd(pad, _ => new Ds4MicState());
            long now = Environment.TickCount64;
            bool changed, armed;
            lock (st.Gate)
            {
                if (slot >= 0) st.Slot = slot;
                if (wanted)
                {
                    if (persona) System.Threading.Volatile.Write(ref st.PersonaWantedTicks, now);
                    else System.Threading.Volatile.Write(ref st.VoiceWantedTicks, now);
                }
                armed = ArmedNow(st, now);
                changed = armed != st.LastArmed;
                st.LastArmed = armed;
            }
            if (changed) OnDs4MicArmedChanged(pad, st.Slot, armed);
        }

        /// <summary>A reader leaving withdraws its want at once, so the pad
        /// returns to 0xA0 without waiting out the hold.</summary>
        private static void ClearDs4MicWant(Guid pad, bool persona)
        {
            if (!_ds4Mic.TryGetValue(pad, out var st)) return;
            bool changed, armed;
            lock (st.Gate)
            {
                if (persona) System.Threading.Volatile.Write(ref st.PersonaWantedTicks, 0);
                else System.Threading.Volatile.Write(ref st.VoiceWantedTicks, 0);
                armed = ArmedNow(st, Environment.TickCount64);
                changed = armed != st.LastArmed;
                st.LastArmed = armed;
            }
            if (changed) OnDs4MicArmedChanged(pad, st.Slot, armed);
        }

        private static void OnDs4MicArmedChanged(Guid pad, int slot, bool armed)
        {
            Engine.SdlDiagLog.WriteLine("DS4MIC " + (armed ? "armed" : "disarmed")
                + " pad=" + pad.ToString("N").Substring(0, 8) + " slot=" + slot);
            if (slot >= 0) UserEffectsDispatcher.NotifySoundRoutingChanged(slot);
        }

        internal static bool IsBluetoothDs4(ushort vendorId, ushort productId, string path)
            => vendorId == SonyVid && productId is 0x05C4 or 0x09CC
               && (path ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Persona microphone lane ─────────────────────────────────────

        /// <summary>The Bluetooth DualShock 4 counterpart of
        /// <see cref="BtMicLoop"/>: same handle ownership, generation checks
        /// and whole-block submits. It arms the microphone through the mode
        /// byte instead of a session report, decodes SBC or mSBC, and feeds
        /// the virtual controller's microphone at whatever rate it declares,
        /// plus the pad's voice-macro session at 48 kHz.</summary>
        private static void Ds4BtMicLoop(PersonaFeed feed, string hidPath, int gen, int generation)
        {
            PersonaReaderOpening?.Invoke(feed, true);
            IntPtr h = NativeMethods.OpenHidSync(hidPath);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Engine.SdlDiagLog.WriteLine("PERSONA ds4mic bt-reader open FAILED");
                return;
            }
            var reader = new PersonaReader { Thread = System.Threading.Thread.CurrentThread, Handle = h };
            Guid pad;
            lock (feed.HidLaneLock)
            {
                if (feed.Retired || feed.RouteGeneration != generation || feed.BtMicGen != gen) { NativeMethods.CloseHandle(h); return; }
                feed.BtMicHandle = h;
                feed.BtMicReader = reader;
                pad = feed.BtMicPadGuid;
            }
            var stream = new Ds4MicStream();
            var writer = new Ds4PersonaMicWriter();
            var voiceUp = new Upsampler3x();
            var voice48 = new short[SbcDecoder.MaxSamplesPerChannel * 3];
            bool lost = false;
            Ds4MicStream.Pcm16kSink sink = pcm =>
            {
                if (lost) return;
                // Pre-gain, as the DualSense lane tees it: gain shapes what
                // Windows hears, not what recognition should. Only while this
                // reader still owns the feed, the DualSense lane's rule.
                if (!feed.Retired && feed.RouteGeneration == generation && feed.BtMicGen == gen
                    && PadForge.Services.VoiceMacroService.PadMicWanted(pad))
                {
                    int n48 = voiceUp.Process(pcm, voice48);
                    PadForge.Services.VoiceMacroService.SubmitPadMic48k(pad, voice48.AsSpan(0, n48), 1);
                }
                float gain = feed.MicMuted ? 0f : feed.MicGain;
                if (!writer.Write(feed, pcm, gain, generation, gen)) lost = true;
            };
            Ds4BtAudio.MicFrameHandler onFrame = (seq, frame) => stream.Feed(seq, frame, sink);
            var report = new byte[Ds4BtAudio.MaxInputReportSize];
            bool wasArmed = false;
            long lastLog = 0, rxReports = 0, badCrc = 0;
            try
            {
                while (!lost && !feed.Retired && feed.RouteGeneration == generation && !feed.BtMicStop && feed.BtMicGen == gen)
                {
                    PersonaReadBoundary?.Invoke(feed, true, true, report, 0);
                    bool read = NativeMethods.ReadFileSync(h, report, report.Length, out int got);
                    PersonaReadBoundary?.Invoke(feed, true, false, report, got);
                    lock (feed.CallbackGate)
                    lock (feed.HidLaneLock)
                        if (feed.Retired || feed.RouteGeneration != generation || feed.BtMicGen != gen) break;
                    if (!read || got < Ds4BtAudio.ControlReportSize)
                    {
                        if (feed.BtMicStop || feed.BtMicGen != gen) break;
                        System.Threading.Thread.Sleep(50);
                        continue;
                    }
                    if (Ds4BtAudio.TryReadStatus(report, got, out byte status))
                    {
                        NoteHeadphoneJack(pad, (status & Ds4BtAudio.StatusHeadphones) != 0);
                        NoteDs4MicPlugged(pad, (status & Ds4BtAudio.StatusMicrophone) != 0);
                    }

                    // Armed only while something listens: the host has the
                    // virtual microphone open, or a voice session wants this
                    // pad. The hbashton fork arms on the same rule, since an
                    // armed microphone spends Bluetooth bandwidth
                    // (ControllerMicrophoneRoutePolicy at 68a5c64). The
                    // DualSense lane's ring-room test does not carry over:
                    // there it only skips a decode, and here it would switch
                    // the controller's report mode.
                    var mic = feed.Audio.Microphone;
                    bool wanted = mic.IsStreaming || PadForge.Services.VoiceMacroService.PadMicWanted(pad);
                    SetDs4MicWanted(pad, feed.Slot, persona: true, wanted);
                    bool armed = IsDs4MicArmed(pad);
                    if (armed != wasArmed)
                    {
                        // Each arming starts a fresh stream: the frame numbers
                        // and the filter histories from before mean nothing.
                        stream.Reset(); writer.Reset(); voiceUp.Reset();
                        wasArmed = armed;
                    }
                    if (armed)
                    {
                        int frames = Ds4BtAudio.ExtractMicFrames(report, got, onFrame);
                        if (frames < 0) badCrc++;
                        else rxReports += frames > 0 ? 1 : 0;
                    }

                    long now = Environment.TickCount64;
                    if (now - lastLog >= 2000)
                    {
                        lastLog = now;
                        Engine.SdlDiagLog.WriteLine("PERSONA ds4mic armed=" + armed
                            + " plugged=" + IsDs4MicPluggedFor(pad)
                            + " reports=" + rxReports + " badCrc=" + badCrc
                            + " decoded=" + stream.FramesDecoded + " concealed=" + stream.FramesConcealed
                            + " gaps=" + stream.Discontinuities + " failed=" + stream.DecodeFailures
                            + " dropped=" + writer.BlocksDropped
                            + " endpoint=" + mic.SampleRateHz + "Hz/" + mic.Channels + "ch"
                            + " hostStreaming=" + mic.IsStreaming);
                    }
                }
            }
            finally
            {
                ClosePersonaReader(feed, reader, true);
                ClearDs4MicWant(pad, persona: true);
            }
        }

        private static bool IsDs4MicPluggedFor(Guid pad)
            => _ds4Mic.TryGetValue(pad, out var st) && st.Plugged;

        /// <summary>Turns 16 kHz mono microphone samples into the persona
        /// endpoint's own format and submits whole 10 ms blocks. The virtual
        /// DualShock 4 declares 16 kHz mono and takes the samples as they
        /// come. A 48 kHz endpoint, the DualSense's, goes through
        /// <see cref="Upsampler3x"/>, and any other rate through a linear
        /// resampler.</summary>
        private sealed class Ds4PersonaMicWriter
        {
            private readonly Upsampler3x _up = new();
            private short[] _rate = new short[SbcDecoder.MaxSamplesPerChannel * 3];
            private byte[] _pending = new byte[4096];
            private int _pendingBytes;
            private double _phase;
            private short _carry;
            public long BlocksDropped;

            public void Reset()
            {
                _up.Reset();
                _pendingBytes = 0;
                _phase = 0;
                _carry = 0;
            }

            /// <summary>False once the lane no longer owns the feed.</summary>
            public bool Write(PersonaFeed feed, ReadOnlySpan<short> mono16k, float gain, int generation, int gen)
            {
                var mic = feed.Audio.Microphone;
                int rate = mic.SampleRateHz > 0 ? mic.SampleRateHz : Ds4MicStream.SourceRate;
                int channels = Math.Max(1, mic.Channels);

                ReadOnlySpan<short> atRate;
                if (rate == Ds4MicStream.SourceRate) atRate = mono16k;
                else if (rate == Ds4MicStream.SourceRate * 3)
                {
                    int n = _up.Process(mono16k, _rate);
                    atRate = _rate.AsSpan(0, n);
                }
                else atRate = ResampleLinear(mono16k, rate);

                int blockBytes = rate / 100 * channels * 2;
                int need = _pendingBytes + atRate.Length * channels * 2;
                if (_pending.Length < need) Array.Resize(ref _pending, Math.Max(need, _pending.Length * 2));
                foreach (short x in atRate)
                {
                    short s = (short)Math.Clamp(x * gain, short.MinValue, short.MaxValue);
                    for (int c = 0; c < channels; c++)
                    {
                        _pending[_pendingBytes++] = (byte)s;
                        _pending[_pendingBytes++] = (byte)(s >> 8);
                    }
                }

                int offset = 0;
                while (_pendingBytes - offset >= blockBytes)
                {
                    if (MicRingFits(mic, blockBytes))
                    {
                        lock (feed.CallbackGate)
                        lock (feed.HidLaneLock)
                        {
                            if (feed.Retired || feed.RouteGeneration != generation || feed.BtMicGen != gen) return false;
                            mic.Submit(_pending.AsSpan(offset, blockBytes));
                        }
                    }
                    else BlocksDropped++;
                    offset += blockBytes;
                }
                if (offset > 0)
                {
                    Array.Copy(_pending, offset, _pending, 0, _pendingBytes - offset);
                    _pendingBytes -= offset;
                }
                return true;
            }

            private ReadOnlySpan<short> ResampleLinear(ReadOnlySpan<short> src, int rate)
            {
                double step = Ds4MicStream.SourceRate / (double)rate;
                int cap = (int)(src.Length / step) + 4;
                if (_rate.Length < cap) _rate = new short[cap];
                int o = 0;
                double pos = _phase;
                while (pos < src.Length)
                {
                    int i = (int)pos;
                    double t = pos - i;
                    double a = i == 0 ? _carry : src[i - 1];
                    double b = src[i];
                    _rate[o++] = (short)Math.Round(a + (b - a) * t);
                    pos += step;
                }
                _phase = pos - src.Length;
                if (src.Length > 0) _carry = src[src.Length - 1];
                return _rate.AsSpan(0, o);
            }
        }

        /// <summary>Resampler state for <see cref="ConvertMicBlockRate"/>,
        /// one per reader thread.</summary>
        internal sealed class MicRateState
        {
            public double Phase;
            public float[] Carry = Array.Empty<float>();
            public float[] Src = Array.Empty<float>();
            public float[] Dst = Array.Empty<float>();
        }

        /// <summary>Converts an interleaved 48 kHz 16-bit block in place to
        /// a persona microphone's own rate and returns the frames it now
        /// holds. A whole-number ratio averages each group of samples, the
        /// decimation VoiceMacroService uses for 48 to 16 kHz. Any other rate
        /// goes through <see cref="LinearResampleInterleaved"/>, as the USB
        /// lane's does.</summary>
        internal static int ConvertMicBlockRate(byte[] pcm, int frames, int channels, int rate, MicRateState st)
        {
            if (rate <= 0 || rate == Rate || frames <= 0 || channels <= 0) return frames;
            if (Rate % rate == 0)
            {
                int d = Rate / rate, outFrames = frames / d;
                for (int f = 0; f < outFrames; f++)
                    for (int c = 0; c < channels; c++)
                    {
                        int acc = 0;
                        for (int k = 0; k < d; k++)
                        {
                            int i = ((f * d + k) * channels + c) * 2;
                            acc += (short)(pcm[i] | pcm[i + 1] << 8);
                        }
                        short v = (short)(acc / d);
                        int o = (f * channels + c) * 2;
                        pcm[o] = (byte)v;
                        pcm[o + 1] = (byte)(v >> 8);
                    }
                return outFrames;
            }

            int samples = frames * channels;
            if (st.Src.Length < samples) st.Src = new float[samples];
            for (int i = 0; i < samples; i++) st.Src[i] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;
            if (st.Carry.Length != channels) { st.Carry = new float[channels]; st.Phase = 0; }
            int cap = (int)(frames * (double)rate / Rate) + 4;
            if (st.Dst.Length < cap * channels) st.Dst = new float[cap * channels];
            double phase = st.Phase;
            int written = LinearResampleInterleaved(st.Src.AsSpan(0, samples), frames, channels,
                                                    st.Dst, Rate / (double)rate, ref phase, st.Carry);
            st.Phase = phase;
            written = Math.Min(written, pcm.Length / (2 * channels));
            for (int i = 0; i < written * channels; i++)
            {
                short v = (short)Math.Clamp(st.Dst[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)v;
                pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            return written;
        }

        /// <summary>Whole-block guard for any persona microphone, sized from
        /// the endpoint's own format: HIDMaestro's ring holds 256 one-ms
        /// intervals (the DualSense's 192 bytes an interval gives
        /// <see cref="HmMicRingBytes"/>).</summary>
        internal static bool MicRingFits(HIDMaestro.HMMicrophoneInput mic, int blockBytes)
        {
            int rate = mic.SampleRateHz > 0 ? mic.SampleRateHz : Rate;
            int ring = rate / 1000 * Math.Max(1, mic.Channels) * 2 * 256;
            return ring - 1 - mic.BufferedBytes >= blockBytes;
        }

        // ── Voice-macro microphone lane ─────────────────────────────────

        /// <summary>The DualShock 4 counterpart of
        /// <see cref="VoiceBtMicLoop"/>. With no session report to open or
        /// close, the lane only states its want and decodes. It yields while
        /// the persona lane decodes the same pad, which tees into the same
        /// voice session.</summary>
        private static void Ds4VoiceBtMicLoop(VoiceMicLane lane)
        {
            IntPtr h = NativeMethods.OpenHidSync(lane.Path);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Engine.SdlDiagLog.WriteLine("VOICE ds4mic open FAILED pad=" + lane.Pad.ToString("N").Substring(0, 8));
                _voiceMicLanes.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, VoiceMicLane>(lane.Pad, lane));
                return;
            }
            lane.Handle = h;
            var stream = new Ds4MicStream();
            var up = new Upsampler3x();
            var pcm48 = new short[SbcDecoder.MaxSamplesPerChannel * 3];
            Ds4MicStream.Pcm16kSink sink = pcm =>
            {
                int n = up.Process(pcm, pcm48);
                PadForge.Services.VoiceMacroService.SubmitPadMic48k(lane.Pad, pcm48.AsSpan(0, n), 1);
            };
            Ds4BtAudio.MicFrameHandler onFrame = (seq, frame) => stream.Feed(seq, frame, sink);
            var report = new byte[Ds4BtAudio.MaxInputReportSize];
            bool wasArmed = false;
            // The slot lookup walks the settings under their lock, too much
            // for every input report, so it refreshes every two seconds.
            int slot = ResolveVoiceMicSlot(lane.Pad);
            long slotAt = Environment.TickCount64;
            try
            {
                while (!lane.Stop)
                {
                    if (!NativeMethods.ReadFileSync(h, report, report.Length, out int got)
                        || got < Ds4BtAudio.ControlReportSize)
                    {
                        if (lane.Stop) break;
                        System.Threading.Thread.Sleep(50);
                        continue;
                    }
                    if (Ds4BtAudio.TryReadStatus(report, got, out byte status))
                    {
                        NoteHeadphoneJack(lane.Pad, (status & Ds4BtAudio.StatusHeadphones) != 0);
                        NoteDs4MicPlugged(lane.Pad, (status & Ds4BtAudio.StatusMicrophone) != 0);
                    }
                    long now = Environment.TickCount64;
                    if (now - slotAt >= 2000) { slot = ResolveVoiceMicSlot(lane.Pad); slotAt = now; }
                    bool wanted = PadForge.Services.VoiceMacroService.PadMicWanted(lane.Pad);
                    SetDs4MicWanted(lane.Pad, slot, persona: false, wanted);
                    bool armed = IsDs4MicArmed(lane.Pad);
                    if (armed != wasArmed) { stream.Reset(); up.Reset(); wasArmed = armed; }
                    // The persona lane already tees this pad's microphone into
                    // its voice session, and two producers garble one recognizer.
                    if (!armed || IsBtMicLaneActive(lane.Pad)) continue;
                    if (Ds4BtAudio.ExtractMicFrames(report, got, onFrame) > 0) lane.RxFrames++;
                }
            }
            finally
            {
                ClearDs4MicWant(lane.Pad, persona: false);
                NativeMethods.CloseHandle(h);
                _voiceMicLanes.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, VoiceMicLane>(lane.Pad, lane));
            }
        }
    }
}
