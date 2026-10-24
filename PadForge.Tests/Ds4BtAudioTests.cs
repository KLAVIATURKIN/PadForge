using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// DualShock 4 Bluetooth audio (#528): the mode byte, the headphone jack
    /// route and volume, the headset microphone's reports and decode, and the
    /// steady silence. Byte values follow the hbashton DS4Windows fork
    /// (68a5c64), ds4mac and ds4drv, which the source comments cite.
    /// </summary>
    public class Ds4BtAudioTests
    {
        internal static byte[] Msbc(int index)
        {
            byte[] all = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", "msbc.msbc"));
            return all.AsSpan(index * SbcDecoder.MsbcFrameLength, SbcDecoder.MsbcFrameLength).ToArray();
        }

        /// <summary>A Bluetooth input report as a 547-byte read returns it:
        /// state when <paramref name="state"/>, audio flagged when
        /// <paramref name="audio"/>, an audio block at
        /// <paramref name="audioAt"/> (0 for none) and a valid CRC.</summary>
        internal static byte[] InputReport(byte id, bool state, bool audio, int audioAt = 0,
                                           byte target = Ds4BtAudio.MicTargetGenuine, ushort sequence = 0,
                                           byte status = 0x08, params byte[][] frames)
        {
            int length = Ds4BtAudio.InputReportLength(id);
            var r = new byte[Ds4BtAudio.MaxInputReportSize];
            r[0] = id;
            r[1] = (byte)(state ? 0xC0 : 0x40);
            r[2] = (byte)(audio ? 0x80 : 0x00);
            if (state) r[Ds4BtAudio.StatusOffset] = status;
            if (audioAt > 0)
            {
                r[audioAt] = (byte)sequence;
                r[audioAt + 1] = (byte)(sequence >> 8);
                r[audioAt + 2] = target;
                int at = audioAt + 3;
                foreach (var f in frames) { f.CopyTo(r, at); at += f.Length; }
            }
            uint crc = Ds4BtAudio.Crc32(0xA1, r.AsSpan(0, length - 4));
            r[length - 4] = (byte)crc;
            r[length - 3] = (byte)(crc >> 8);
            r[length - 2] = (byte)(crc >> 16);
            r[length - 1] = (byte)(crc >> 24);
            return r;
        }

        // The bitwise form of the Sony Bluetooth CRC: the transaction byte,
        // then the report, polynomial 0xEDB88320 (ds4mac's description and
        // DS4AudioStreamer's seed ~0xEADA2D49, the state after 0xA2).
        private static uint ReferenceCrc(byte prefix, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            void Feed(byte b)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            Feed(prefix);
            foreach (byte b in data) Feed(b);
            return ~crc;
        }

        [Fact]
        public void Crc32_MatchesTheBitwiseReference()
        {
            var rng = new Random(528);
            var data = new byte[543];
            rng.NextBytes(data);
            Assert.Equal(ReferenceCrc(0xA2, data), Ds4BtAudio.Crc32(0xA2, data));
            Assert.Equal(ReferenceCrc(0xA1, data), Ds4BtAudio.Crc32(0xA1, data));
            // DS4AudioStreamer seeds with ~0xEADA2D49: the CRC state after 0xA2.
            uint afterA2 = 0xFFFFFFFFu ^ 0xA2;
            for (int i = 0; i < 8; i++) afterA2 = (afterA2 & 1) != 0 ? (afterA2 >> 1) ^ 0xEDB88320u : afterA2 >> 1;
            Assert.Equal(~0xEADA2D49u, afterA2);
        }

        [Theory]
        [InlineData(true, Ds4BtAudio.ModeSpeaker, Ds4BtAudio.TargetSpeaker)]
        [InlineData(true, Ds4BtAudio.ModeMicrophone, Ds4BtAudio.TargetHeadset)]
        [InlineData(false, Ds4BtAudio.ModeSpeaker, Ds4BtAudio.TargetHeadset)]
        public void StreamHeader_CarriesModeTargetAndCounter(bool fourFrames, byte mode, byte target)
        {
            var r = new byte[462];
            Ds4BtAudio.WriteStreamHeader(r, fourFrames, 0x1234, mode, target);
            Assert.Equal(fourFrames ? 0x17 : 0x14, r[0]);
            Assert.Equal(0x40, r[1]);
            Assert.Equal(mode, r[2]);
            Assert.NotEqual(0xA2, r[2]);
            Assert.Equal(0x34, r[3]);
            Assert.Equal(0x12, r[4]);
            Assert.Equal(target, r[5]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ControlReport_ArmsWithTheModeAndTheVolumeEnablesOnly(bool microphone)
        {
            byte mode = microphone ? Ds4BtAudio.ModeMicrophone : Ds4BtAudio.ModeSpeaker;
            byte[] r = Ds4BtAudio.BuildControlReport(mode, 40, Ds4BtAudio.VolumeMax, microphone);
            Assert.Equal(78, r.Length);
            Assert.Equal(0x11, r[0]);
            Assert.Equal(0xC0, r[1]);
            Assert.Equal(mode, r[2]);
            Assert.Equal(microphone ? 0xF0 : 0xB0, r[3]);
            // Rumble, lightbar and flash belong to the effects writer.
            Assert.Equal(0, r[3] & 0x07);
            for (int i = 4; i <= 20; i++) Assert.Equal(0, r[i]);
            Assert.Equal(40, r[21]);
            Assert.Equal(40, r[22]);
            Assert.Equal(microphone ? Ds4BtAudio.MicGainMax : 0, r[23]);
            Assert.Equal(Ds4BtAudio.VolumeMax, r[24]);
            uint crc = (uint)(r[74] | r[75] << 8 | r[76] << 16 | r[77] << 24);
            Assert.Equal(ReferenceCrc(0xA2, r.AsSpan(0, 74)), crc);
        }

        [Theory]
        [InlineData(0, 0x00)]
        [InlineData(100, 0x4F)]
        [InlineData(50, 40)]
        [InlineData(-5, 0x00)]
        [InlineData(250, 0x4F)]
        public void HeadphoneVolume_MapsOntoTheDs4Range(int percent, int expected)
            => Assert.Equal(expected, Ds4BtAudio.HeadphoneVolumeByte(percent));

        [Theory]
        [InlineData(0, Ds4BtAudio.TargetSpeaker, false)] // Default: the speaker, as before #528
        [InlineData(1, Ds4BtAudio.TargetHeadset, true)]  // Headphones (Stereo)
        [InlineData(2, Ds4BtAudio.TargetHeadset, false)] // Headphones (Mono)
        [InlineData(3, Ds4BtAudio.TargetSpeaker, false)] // Headphones + Speaker: no such route
        [InlineData(4, Ds4BtAudio.TargetSpeaker, false)] // Speaker Only
        public void Route_PicksTargetAndStereo(int resolvedPath, byte target, bool stereo)
        {
            Assert.Equal(target, Ds4BtAudio.TargetFor(resolvedPath));
            Assert.Equal(stereo, Ds4BtAudio.IsStereoRoute(resolvedPath));
        }

        /// <summary>The target byte alone does not isolate the outputs.
        /// DS4-Sound-Link mutes the other output by volume, and the hbashton
        /// fork mutes the speaker for headset-only audio.</summary>
        [Theory]
        [InlineData(0, 40, 0x4F)]  // Default: both on, as before #528
        [InlineData(1, 40, 0)]     // Headphones (Stereo): the speaker is muted
        [InlineData(2, 40, 0)]     // Headphones (Mono): the speaker is muted
        [InlineData(3, 40, 0x4F)]  // Headphones + Speaker resolves as Default
        [InlineData(4, 0, 0x4F)]   // Speaker Only: the jack is muted
        public void Route_SetsBothVolumes(int resolvedPath, int headphone, int speaker)
        {
            var (hp, spk) = Ds4BtAudio.VolumesFor(resolvedPath, 50);
            Assert.Equal(headphone, hp);
            Assert.Equal(speaker, spk);
        }

        [Fact]
        public void FollowHeadphoneJack_ResolvesThroughTheSharedResolver()
        {
            var pad = Guid.NewGuid();
            Assert.Equal(0, AudioPassthroughService.ResolveOutputPath(5, pad));
            AudioPassthroughService.NoteHeadphoneJack(pad, true);
            Assert.Equal(Ds4BtAudio.TargetHeadset, Ds4BtAudio.TargetFor(AudioPassthroughService.ResolveOutputPath(5, pad)));
            AudioPassthroughService.NoteHeadphoneJack(pad, false);
            Assert.Equal(Ds4BtAudio.TargetSpeaker, Ds4BtAudio.TargetFor(AudioPassthroughService.ResolveOutputPath(5, pad)));
        }

        [Fact]
        public void InputReportLengths_FollowTheReportIds()
        {
            Assert.Equal(78, Ds4BtAudio.InputReportLength(0x11));
            Assert.Equal(142, Ds4BtAudio.InputReportLength(0x12));
            Assert.Equal(270, Ds4BtAudio.InputReportLength(0x14));
            Assert.Equal(526, Ds4BtAudio.InputReportLength(0x18));
            Assert.Equal(547, Ds4BtAudio.InputReportLength(0x19));
            Assert.Equal(0, Ds4BtAudio.InputReportLength(0x31));
        }

        [Fact]
        public void Status_IsReadFromValidStateBearingReportsOnly()
        {
            byte status = (byte)(Ds4BtAudio.StatusHeadphones | Ds4BtAudio.StatusMicrophone | 0x07);
            byte[] r = InputReport(0x11, state: true, audio: false, status: status);
            Assert.True(Ds4BtAudio.TryReadStatus(r, 547, out byte read));
            Assert.Equal(status, read);

            Assert.False(Ds4BtAudio.TryReadStatus(InputReport(0x12, state: false, audio: true), 547, out _));
            r[10] ^= 1; // damage the report under its CRC
            Assert.False(Ds4BtAudio.TryReadStatus(r, 547, out _));
            Assert.False(Ds4BtAudio.TryReadStatus(InputReport(0x14, state: true, audio: false), 200, out _));
        }

        private static List<(ushort Seq, byte[] Frame)> Extract(byte[] report, out int count)
        {
            var got = new List<(ushort, byte[])>();
            count = Ds4BtAudio.ExtractMicFrames(report, report.Length, (seq, frame) => got.Add((seq, frame.ToArray())));
            return got;
        }

        [Fact]
        public void MicFrames_FollowTheControllerState()
        {
            byte[] a = Msbc(0), b = Msbc(1);
            byte[] r = InputReport(0x14, state: true, audio: true, audioAt: 78, sequence: 0xFFFF, frames: new[] { a, b });
            var got = Extract(r, out int count);
            Assert.Equal(2, count);
            Assert.Equal((ushort)0xFFFF, got[0].Seq);
            Assert.Equal((ushort)0x0000, got[1].Seq); // the frame number wraps
            Assert.Equal(a, got[0].Frame);
            Assert.Equal(b, got[1].Frame);
        }

        [Fact]
        public void MicFrames_AudioOnly_StaleFlag_AndTheLegacyTarget()
        {
            byte[] a = Msbc(2);
            var audioOnly = Extract(InputReport(0x12, state: false, audio: true, audioAt: 3, sequence: 7, frames: new[] { a }), out int n1);
            Assert.Equal(1, n1);
            Assert.Equal((ushort)7, audioOnly[0].Seq);

            // Firmware that clears the state bit while the state is still there.
            var stale = Extract(InputReport(0x14, state: false, audio: true, audioAt: 78,
                                            target: Ds4BtAudio.MicTargetLegacy, sequence: 9, frames: new[] { a }), out int n2);
            Assert.Equal(1, n2);
            Assert.Equal((ushort)9, stale[0].Seq);
            Assert.Equal(a, stale[0].Frame);
        }

        [Fact]
        public void MicFrames_RejectABadCrc_AndReadNothingWithoutTheAudioBit()
        {
            byte[] r = InputReport(0x14, state: true, audio: true, audioAt: 78, frames: new[] { Msbc(0) });
            r[100] ^= 0x01;
            Extract(r, out int bad);
            Assert.Equal(-1, bad);

            Extract(InputReport(0x14, state: true, audio: false, audioAt: 78, frames: new[] { Msbc(0) }), out int none);
            Assert.Equal(0, none);
        }

        [Fact]
        public void MicFrames_TheShortReportHasNoCrcToFail()
        {
            // Report 0x01, which a Bluetooth DualShock 4 sends until a host
            // reads feature report 0x02 (ds4drv backends/hidraw.py), carries
            // no CRC and no audio. The microphone lane counts -1 as a bad CRC.
            var r = new byte[Ds4BtAudio.MaxInputReportSize];
            r[0] = 0x01;
            r[2] = 0x80;
            Extract(r, out int count);
            Assert.Equal(0, count);
        }

        [Fact]
        public void MicStream_ConcealsShortGaps_AndRestartsAfterLongOnes()
        {
            var stream = new Ds4MicStream();
            var chunks = new List<short[]>();
            void Sink(ReadOnlySpan<short> pcm) => chunks.Add(pcm.ToArray());

            stream.Feed(100, Msbc(0), Sink);
            stream.Feed(101, Msbc(1), Sink);
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal(SbcDecoder.MsbcSamples, c.Length));
            short[] last = chunks[1];

            // Two lost frames: two quieter repeats of the last frame, then the real one.
            stream.Feed(104, Msbc(2), Sink);
            Assert.Equal(5, chunks.Count);
            Assert.Equal(2, stream.FramesConcealed);
            for (int i = 0; i < last.Length; i++)
            {
                Assert.Equal((short)Math.Round(last[i] * Math.Pow(0.82, 1)), chunks[2][i]);
                Assert.Equal((short)Math.Round(last[i] * Math.Pow(0.82, 2)), chunks[3][i]);
            }

            // Five lost frames is past the limit: no repeats, the decoder restarts.
            stream.Feed(110, Msbc(3), Sink);
            Assert.Equal(6, chunks.Count);
            Assert.Equal(1, stream.Discontinuities);
            Assert.Equal(2, stream.FramesConcealed);

            stream.Feed(110, Msbc(3), Sink);  // the same frame again
            stream.Feed(109, Msbc(4), Sink);  // older than the last
            Assert.Equal(6, chunks.Count);
            Assert.Equal(1, stream.OutOfOrder);
            Assert.Equal(0, stream.DecodeFailures);
        }

        private static double Goertzel(short[] x, int from, int count, double freq, double rate)
        {
            double w = 2 * Math.PI * freq / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < count; i++) { double s = x[from + i] + c * s1 - s2; s2 = s1; s1 = s; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }

        [Fact]
        public void Upsampler_HasUnityGain_AndRejectsTheImages()
        {
            var up = new Upsampler3x();
            var dc = Enumerable.Repeat((short)1000, 200).ToArray();
            var outDc = new short[600];
            Assert.Equal(600, up.Process(dc, outDc));
            for (int i = 200; i < 600; i++) Assert.InRange((int)outDc[i], 999, 1001);

            up.Reset();
            var tone = new short[1600];
            for (int i = 0; i < tone.Length; i++) tone[i] = (short)(10000 * Math.Sin(2 * Math.PI * 1000 * i / 16000.0));
            var outTone = new short[4800];
            up.Process(tone, outTone);
            double wanted = Goertzel(outTone, 480, 4320, 1000, 48000);
            double image15 = Goertzel(outTone, 480, 4320, 15000, 48000);
            double image17 = Goertzel(outTone, 480, 4320, 17000, 48000);
            Assert.True(10 * Math.Log10(wanted / image15) > 60, "15 kHz image");
            Assert.True(10 * Math.Log10(wanted / image17) > 60, "17 kHz image");
            double rms = Math.Sqrt(outTone.Skip(480).Average(v => (double)v * v));
            Assert.InRange(20 * Math.Log10(rms / (10000 / Math.Sqrt(2))), -0.2, 0.2);
        }

        [Fact]
        public void MicBlockRate_AveragesDownToTheEndpointRate()
        {
            var pcm = new byte[480 * 2 * 2];
            for (int f = 0; f < 480; f++)
                for (int c = 0; c < 2; c++)
                {
                    short v = (short)(f * 10 + c);
                    pcm[(f * 2 + c) * 2] = (byte)v;
                    pcm[(f * 2 + c) * 2 + 1] = (byte)(v >> 8);
                }
            int frames = AudioPassthroughService.ConvertMicBlockRate(pcm, 480, 2, 16000, new AudioPassthroughService.MicRateState());
            Assert.Equal(160, frames);
            for (int f = 0; f < 160; f++)
                for (int c = 0; c < 2; c++)
                {
                    short got = (short)(pcm[(f * 2 + c) * 2] | pcm[(f * 2 + c) * 2 + 1] << 8);
                    int expected = ((f * 3) * 10 + c + (f * 3 + 1) * 10 + c + (f * 3 + 2) * 10 + c) / 3;
                    Assert.Equal(expected, got);
                }

            // A rate that does not divide 48 kHz goes through the interpolator.
            var odd = new byte[480 * 2];
            int oddFrames = AudioPassthroughService.ConvertMicBlockRate(odd, 480, 1, 44100, new AudioPassthroughService.MicRateState());
            Assert.InRange(oddFrames, 438, 444);

            // The DualSense's own endpoint rate passes through untouched.
            Assert.Equal(480, AudioPassthroughService.ConvertMicBlockRate(odd, 480, 1, 48000, new AudioPassthroughService.MicRateState()));
        }

        [Theory]
        [InlineData(true, true, 9000, 0, true)]      // persona wanted 1 s ago
        [InlineData(true, true, 0, 9500, true)]      // voice wanted 0.5 s ago
        [InlineData(true, true, 7000, 0, false)]     // the want is 3 s old
        [InlineData(true, false, 9000, 0, false)]    // no microphone plugged in
        [InlineData(false, true, 9000, 9000, false)] // the lane is off (SDL#41)
        [InlineData(true, true, 0, 0, false)]        // nobody wants it
        public void ArmedRule_NeedsTheGateAMicrophoneAndARecentWant(bool enabled, bool plugged,
                                                                    long persona, long voice, bool armed)
            => Assert.Equal(armed, AudioPassthroughService.Ds4MicArmedRule(enabled, plugged, persona, voice, 10000));

        [Fact]
        public void MicLane_StaysOffUntilSdl41Lands()
        {
            Assert.False(AudioPassthroughService.EnableDs4BtMic);
            Assert.Equal(Ds4BtAudio.ModeSpeaker, AudioPassthroughService.Ds4AudioMode(Guid.NewGuid()));
        }

        [Fact]
        public void EffectsPacket_CarriesTheModeAndOnlyTheChangedLevels()
        {
            var plain = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0);
            Assert.Equal((byte)0x07, plain["validFlag0"]);
            Assert.Equal(Ds4BtAudio.ModeSpeaker, plain["btReserved"]);

            var (hp, spk) = Ds4BtAudio.VolumesFor(1, 50); // Headphones (Stereo)
            var changed = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0,
                headphoneVolume: hp, speakerVolume: spk, assertVolumes: true,
                audioMode: Ds4BtAudio.ModeMicrophone, assertMicGain: true);
            Assert.Equal((byte)0xF7, changed["validFlag0"]);
            Assert.Equal(Ds4BtAudio.ModeMicrophone, changed["btReserved"]);
            Assert.Equal((byte)40, changed["headphoneVolumeLeft"]);
            Assert.Equal((byte)40, changed["headphoneVolumeRight"]);
            Assert.Equal((byte)0, changed["speakerVolume"]);
            Assert.Equal(Ds4BtAudio.MicGainMax, changed["micVolume"]);
        }

        [Theory]
        [InlineData(0x05C4, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&05c4", true)]
        [InlineData(0x09CC, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc", true)]
        [InlineData(0x09CC, @"\\?\HID#VID_054C&PID_09CC&MI_03#7&1f882a25&0&0000", false)] // wired
        [InlineData(0x0BA0, @"\\?\HID#VID_054C&PID_0BA0&MI_03#7&1f882a25&0&0000", false)] // Sony's USB adapter
        [InlineData(0x0CE6, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6", false)]
        public void BluetoothDs4_IsRecognizedByModelAndTransport(int pid, string path, bool expected)
            => Assert.Equal(expected, AudioPassthroughService.IsBluetoothDs4(0x054C, (ushort)pid, path));

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static int Count(string text, string what)
        {
            int n = 0;
            for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        [Fact]
        public void PadPage_ShowsTheDualSenseRowsForABluetoothDs4_AndHidesTheBothRoute()
        {
            string xaml = RepoFile("PadForge.App", "Views", "PadPage.xaml");
            int both = xaml.IndexOf("Pad_Audio_OutputPath_Both", StringComparison.Ordinal);
            Assert.True(both > 0);
            Assert.Contains("SelectedDeviceHasHeadsetAndSpeakerPath", xaml.Substring(both, 300));

            string vm = RepoFile("PadForge.App", "ViewModels", "PadViewModel.cs");
            int jack = vm.IndexOf("public bool SelectedDeviceHasHeadphoneJack", StringComparison.Ordinal);
            Assert.True(jack > 0);
            Assert.Contains("IsBluetoothDs4", vm.Substring(jack, 800));
            int raisedJack = Count(vm, "OnPropertyChanged(nameof(SelectedDeviceHasHeadphoneJack));");
            Assert.True(raisedJack >= 3);
            Assert.Equal(raisedJack, Count(vm, "OnPropertyChanged(nameof(SelectedDeviceHasHeadsetAndSpeakerPath));"));
        }

        [Fact]
        public void AudioLane_WritesTheModeByte_AndStreamsThroughSilence()
        {
            string src = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.DoesNotContain("report[2] = 0xA2", src);
            Assert.DoesNotContain("vol[2] = 0xA2", src);
            Assert.Contains("Ds4BtAudio.WriteStreamHeader(", src);
            Assert.Contains("Ds4BtAudio.BuildControlReport(", src);

            // The DualShock 4 branch runs before the DualSense idle gate.
            int tick = src.IndexOf("_personaLastAudible = audible;", StringComparison.Ordinal);
            Assert.True(tick > 0);
            int ds4 = src.IndexOf("Ds4BtTick(s, pull, inFrames);", tick, StringComparison.Ordinal);
            int gate = src.IndexOf("if (!audible)", tick, StringComparison.Ordinal);
            Assert.True(ds4 > 0 && gate > 0 && ds4 < gate);
        }
    }

    /// <summary>The jack watch's DualShock 4 shape, against a file standing
    /// in for the pad, the way JackWatchRetryTests drives the DualSense's.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class Ds4JackWatchTests
    {
        [Theory]
        [InlineData(0x20, true)]
        [InlineData(0x40, false)] // a microphone alone is not headphones
        public void ABluetoothDs4ReportSetsTheJackFromBit5(byte status, bool plugged)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(AudioPassthroughService);
            var ensure = type.GetMethod("EnsureJackWatch", flags)!;
            var watches = (IDictionary)type.GetField("_jackWatch", flags)!.GetValue(null);
            var gate = type.GetField("_jackLock", flags)!.GetValue(null);
            var states = (ConcurrentDictionary<Guid, bool>)type.GetField("s_padJackState", flags)!.GetValue(null);
            var mics = (IDictionary)type.GetField("_ds4Mic", flags)!.GetValue(null);
            Guid id = Guid.NewGuid();
            string path = Path.Combine(Path.GetTempPath(), "padforge-ds4jack-" + Guid.NewGuid() + ".bin");
            File.WriteAllBytes(path, Ds4BtAudioTests.InputReport(0x11, state: true, audio: false, status: status)[..78]);
            object watch = null;
            try
            {
                ensure.Invoke(null, [id, path, true, true]);
                lock (gate) watch = watches[id];
                Assert.NotNull(watch);
                Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(id) == plugged, 2000));
            }
            finally
            {
                if (watch != null)
                {
                    watch.GetType().GetField("Stop")!.SetValue(watch, true);
                    Assert.True(((Thread)watch.GetType().GetField("Thread")!.GetValue(watch)).Join(2000));
                }
                lock (gate) watches.Remove(id);
                states.TryRemove(id, out _);
                mics.Remove(id);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
