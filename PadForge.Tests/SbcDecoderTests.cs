using System;
using System.IO;
using PadForge.Common.Input;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The SBC decoder behind the DualShock 4 headset microphone (#528).
    /// The vectors in TestData/Sbc were encoded by ffmpeg 8.1 from one
    /// multitone-plus-chirp signal, and each .pcm beside them is ffmpeg's own
    /// decode of the same frames: mSBC, 16 kHz mono SBC, 32 kHz stereo SBC
    /// with 8 subbands, and 48 kHz stereo SBC with 4 subbands, all loudness
    /// allocation. Joint stereo and SNR allocation come from PadForge's own
    /// encoder, which writes both.
    /// </summary>
    public class SbcDecoderTests
    {
        private readonly ITestOutputHelper _out;

        public SbcDecoderTests(ITestOutputHelper output) => _out = output;

        // google/libsbc src/sbc.c synthesis windows (fixed 2.13), the first
        // ten taps of each row: window[j][t] is the tap for output sample j.
        private static readonly int[,] LibsbcWindow4 =
        {
            {   0, -126,  -358, -848, -4443, -9644, 4443,  -848,  358, -126 },
            { -18, -128,  -670, -201, -6389, -9235, 2544, -1055,  100,  -90 },
            { -49,  -61,  -946,  944, -8082, -8082,  944,  -946,  -61,  -49 },
            { -90,  100, -1055, 2544, -9235, -6389, -201,  -670, -128,  -18 },
        };

        private static readonly int[,] LibsbcWindow8 =
        {
            {    0, -132,  -371, -848, -4456, -9631, 4456,  -848,  371, -132 },
            {  -10, -138,  -526, -580, -5438, -9528, 3486, -1004,  229, -117 },
            {  -22, -131,  -685, -192, -6395, -9224, 2561, -1063,  108,  -97 },
            {  -36, -106,  -835,  322, -7287, -8734, 1711, -1042,   12,  -75 },
            {  -54,  -59,  -960,  959, -8078, -8078,  959,  -960,  -59,  -54 },
            {  -75,   12, -1042, 1711, -8734, -7287,  322,  -835, -106,  -36 },
            {  -97,  108, -1063, 2561, -9224, -6395, -192,  -685, -131,  -22 },
            { -117,  229, -1004, 3486, -9528, -5438, -580,  -526, -138,  -10 },
        };

        [Fact]
        public void Prototypes_MatchLibsbcWindows()
        {
            for (int j = 0; j < 4; j++)
                for (int t = 0; t < 10; t++)
                    Assert.InRange(-4 * 8192 * SbcDecoder.Proto440[j + 4 * t] - LibsbcWindow4[j, t], -1.0, 1.0);
            for (int j = 0; j < 8; j++)
                for (int t = 0; t < 10; t++)
                    Assert.InRange(-8 * 8192 * Ds4SbcEncoder.Proto880[j + 8 * t] - LibsbcWindow8[j, t], -1.0, 1.0);
        }

        [Theory]
        [InlineData("msbc.msbc", 57, 120, 1)]
        [InlineData("sbc16m.sbc", 64, 128, 1)]
        [InlineData("sbc32s.sbc", 128, 128, 2)]
        [InlineData("sbc48s.sbc", 42, 64, 2)]
        public void Decode_MatchesFfmpeg(string name, int frameLength, int samplesPerChannel, int channels)
        {
            byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", name));
            byte[] refBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", name + ".pcm"));
            var expected = new short[refBytes.Length / 2];
            Buffer.BlockCopy(refBytes, 0, expected, 0, refBytes.Length);

            var dec = new SbcDecoder();
            var pcm = new short[SbcDecoder.MaxSamplesPerChannel * 2];
            var got = new short[expected.Length];
            int outPos = 0;
            for (int off = 0; off < stream.Length; off += frameLength)
            {
                int n = dec.Decode(stream.AsSpan(off), pcm, out var info);
                Assert.Equal(samplesPerChannel, n);
                Assert.Equal(frameLength, info.FrameLength);
                Assert.Equal(channels, info.Channels);
                Array.Copy(pcm, 0, got, outPos, n * channels);
                outPos += n * channels;
            }
            Assert.Equal(expected.Length, outPos);

            double signal = 0, noise = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                signal += (double)expected[i] * expected[i];
                double e = expected[i] - got[i];
                noise += e * e;
            }
            double snrDb = 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
            _out.WriteLine($"{name}: {snrDb:F1} dB against ffmpeg's decode");
            Assert.True(snrDb > 60, $"{name}: {snrDb:F1} dB against ffmpeg's decode");
        }

        [Fact]
        public void PadForgeEncoder_RoundTrips_JointStereoSnr()
        {
            const int frames = 60;
            var enc = new Ds4SbcEncoder();
            var dec = new SbcDecoder();
            var input = new short[frames * Ds4SbcEncoder.PcmSamplesPerFrame];
            for (int i = 0; i < input.Length / 2; i++)
            {
                double t = i / 32000.0;
                double l = 0.3 * Math.Sin(2 * Math.PI * 440 * t) + 0.1 * Math.Sin(2 * Math.PI * 2900 * t);
                double r = 0.25 * Math.Sin(2 * Math.PI * 660 * t) + 0.1 * Math.Sin(2 * Math.PI * 2900 * t);
                input[i * 2] = (short)(l * 32767);
                input[i * 2 + 1] = (short)(r * 32767);
            }
            var frame = new byte[Ds4SbcEncoder.FrameBytes];
            var output = new short[input.Length];
            var pcm = new short[SbcDecoder.MaxSamplesPerChannel * 2];
            for (int f = 0; f < frames; f++)
            {
                enc.Encode(input.AsSpan(f * Ds4SbcEncoder.PcmSamplesPerFrame), frame);
                int n = dec.Decode(frame, pcm, out var info);
                Assert.Equal(128, n);
                Assert.Equal(SbcChannelMode.JointStereo, info.Mode);
                Assert.True(info.Snr);
                Array.Copy(pcm, 0, output, f * 256, 256);
            }

            // Analysis plus synthesis delay the stream by a fixed number of
            // samples. Find it, then measure each channel past the warm-up.
            double best = double.MinValue; int lag = 0;
            for (int d = 0; d < 300; d++)
            {
                double c = 0;
                for (int i = 4000; i < 6000; i++) c += (double)input[i * 2] * output[(i + d) * 2];
                if (c > best) { best = c; lag = d; }
            }
            for (int ch = 0; ch < 2; ch++)
            {
                double signal = 0, noise = 0;
                for (int i = 2000; i < input.Length / 2 - lag; i++)
                {
                    double x = input[i * 2 + ch], y = output[(i + lag) * 2 + ch];
                    signal += x * x;
                    noise += (x - y) * (x - y);
                }
                double snrDb = 10 * Math.Log10(signal / noise);
                _out.WriteLine($"channel {ch}: {snrDb:F1} dB at lag {lag}");
                Assert.True(snrDb > 40, $"channel {ch}: {snrDb:F1} dB at lag {lag}");
            }
        }

        [Fact]
        public void Decode_RejectsABadCrcAndAShortFrame()
        {
            byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", "msbc.msbc"));
            var pcm = new short[SbcDecoder.MaxSamplesPerChannel * 2];
            var dec = new SbcDecoder();
            Assert.Equal(SbcDecoder.MsbcSamples, dec.Decode(stream.AsSpan(0, 57), pcm, out _));

            byte[] bad = stream.AsSpan(0, 57).ToArray();
            bad[5] ^= 0x10; // a scale-factor bit, inside the CRC's reach
            Assert.Equal(0, new SbcDecoder().Decode(bad, pcm, out _));

            Assert.Equal(0, new SbcDecoder().Decode(stream.AsSpan(0, 56), pcm, out _));
        }

        [Fact]
        public void Header_ReadsMsbcAndSbc()
        {
            Assert.True(SbcDecoder.TryReadHeader(new byte[] { 0xAD, 0, 0, 0 }, out var m));
            Assert.True(m.IsMsbc);
            Assert.Equal(16000, m.SampleRate);
            Assert.Equal(57, m.FrameLength);
            Assert.Equal(120, m.SamplesPerChannel);

            // The DualShock 4 speaker frame PadForge sends: 32 kHz, 16 blocks,
            // joint stereo, SNR, 8 subbands, bitpool 48, 109 bytes.
            Assert.True(SbcDecoder.TryReadHeader(new byte[] { 0x9C, 0x7F, 48, 0 }, out var s));
            Assert.Equal(32000, s.SampleRate);
            Assert.Equal(SbcChannelMode.JointStereo, s.Mode);
            Assert.Equal(Ds4SbcEncoder.FrameBytes, s.FrameLength);

            Assert.False(SbcDecoder.TryReadHeader(new byte[] { 0x9D, 0x7F, 48, 0 }, out _));
            // Mono 4 blocks of 4 subbands cannot carry a bitpool of 200.
            Assert.False(SbcDecoder.TryReadHeader(new byte[] { 0x9C, 0x00, 200, 0 }, out _));
        }
    }
}
