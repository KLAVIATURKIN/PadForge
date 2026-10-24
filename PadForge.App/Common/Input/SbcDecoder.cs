using System;

namespace PadForge.Common.Input
{
    /// <summary>Channel modes of an SBC frame, in header order.</summary>
    internal enum SbcChannelMode { Mono = 0, DualChannel = 1, Stereo = 2, JointStereo = 3 }

    /// <summary>One SBC frame's parameters, read from its header.</summary>
    internal readonly struct SbcFrameInfo
    {
        public SbcFrameInfo(int frequencyIndex, int blocks, SbcChannelMode mode,
                            bool snr, int subbands, int bitpool, bool msbc)
        {
            FrequencyIndex = frequencyIndex;
            Blocks = blocks;
            Mode = mode;
            Snr = snr;
            Subbands = subbands;
            Bitpool = bitpool;
            IsMsbc = msbc;
        }

        /// <summary>0 = 16 kHz, 1 = 32 kHz, 2 = 44.1 kHz, 3 = 48 kHz.</summary>
        public int FrequencyIndex { get; }
        public int Blocks { get; }
        public SbcChannelMode Mode { get; }
        /// <summary>True for SNR allocation, false for loudness.</summary>
        public bool Snr { get; }
        public int Subbands { get; }
        public int Bitpool { get; }
        /// <summary>The HFP wideband-speech frame (syncword 0xAD).</summary>
        public bool IsMsbc { get; }

        public int Channels => Mode == SbcChannelMode.Mono ? 1 : 2;
        public int SampleRate => FrequencyIndex switch { 0 => 16000, 1 => 32000, 2 => 44100, _ => 48000 };
        public int SamplesPerChannel => Blocks * Subbands;

        /// <summary>Whole frame in bytes, header included.</summary>
        public int FrameLength
        {
            get
            {
                int bits = 4 * Subbands * Channels
                    + Blocks * Bitpool * (Mode == SbcChannelMode.DualChannel ? 2 : 1)
                    + (Mode == SbcChannelMode.JointStereo ? Subbands : 0);
                return 4 + (bits + 7) / 8;
            }
        }
    }

    /// <summary>
    /// SBC decoder for the DualShock 4 headset microphone over Bluetooth
    /// (#528). The controller sends mSBC (syncword 0xAD, 16 kHz mono, 57
    /// bytes a frame) or plain SBC (syncword 0x9C), so this reads both: A2DP
    /// SBC in every channel mode, 4 or 8 subbands, 4 to 16 blocks, loudness
    /// or SNR allocation, and HFP's fixed mSBC frame (16 kHz, mono, 15
    /// blocks, 8 subbands, loudness, bitpool 26).
    ///
    /// <para>The steps are the A2DP specification's decoder: header and
    /// CRC-8 (G(X) = X⁸+X⁴+X³+X²+1, init 0x0F, over the two configuration
    /// bytes, the join bits and the scale factors), the bit allocation
    /// <see cref="Ds4SbcEncoder"/> already runs, sample reconstruction
    /// ((2q+1)/(2^bits−1) − 1)·2^(scf+1), joint stereo L = S + D and
    /// R = S − D, and the synthesis filterbank: shift V by 2M, matrix
    /// V[k] = Σ N[k][i]·S[i] with N[k][i] = cos((i+½)(k+M/2)π/M), build U
    /// from V's ten M-wide segments, window by D and sum ten taps per output
    /// sample.</para>
    ///
    /// <para>D is −M times the specification's printed prototype,
    /// Proto_4_40 below or Proto_8_80 (<see cref="Ds4SbcEncoder.Proto880"/>).
    /// google/libsbc's synthesis windows are those same numbers in 2.13
    /// fixed point, which SbcDecoderTests pins, and the decoder's output
    /// matches ffmpeg's SBC decoder on ffmpeg-encoded streams.</para>
    /// </summary>
    internal sealed class SbcDecoder
    {
        public const int MsbcFrameLength = 57;
        public const int MsbcSamples = 120;

        /// <summary>Largest frame: 16 blocks of 8 subbands, two channels.</summary>
        public const int MaxSamplesPerChannel = 128;

        // Proto_4_40, the specification's printed 4-subband prototype.
        internal static readonly double[] Proto440 =
        {
            0.00000000E+00,  5.36548976E-04,  1.49188357E-03,  2.73370904E-03,
            3.83720193E-03,  3.89205149E-03,  1.86581691E-03, -3.06012286E-03,
            1.09137620E-02,  2.04385087E-02,  2.88757392E-02,  3.21939290E-02,
            2.58767811E-02,  6.13245186E-03, -2.88217274E-02, -7.76463494E-02,
            1.35593274E-01,  1.94987841E-01,  2.46636662E-01,  2.81828203E-01,
            2.94315332E-01,  2.81828203E-01,  2.46636662E-01,  1.94987841E-01,
           -1.35593274E-01, -7.76463494E-02, -2.88217274E-02,  6.13245186E-03,
            2.58767811E-02,  3.21939290E-02,  2.88757392E-02,  2.04385087E-02,
           -1.09137620E-02, -3.06012286E-03,  1.86581691E-03,  3.89205149E-03,
            3.83720193E-03,  2.73370904E-03,  1.49188357E-03,  5.36548976E-04,
        };

        // Loudness offsets by sampling frequency (16, 32, 44.1, 48 kHz).
        private static readonly int[][] Offset4 =
        {
            new[] { -1, 0, 0, 0 },
            new[] { -2, 0, 0, 1 },
            new[] { -2, 0, 0, 1 },
            new[] { -2, 0, 0, 1 },
        };

        private static readonly int[][] Offset8 =
        {
            new[] { -2, 0, 0, 0, 0, 0, 0, 1 },
            new[] { -3, 0, 0, 0, 0, 0, 1, 2 },
            new[] { -4, 0, 0, 0, 0, 0, 1, 2 },
            new[] { -4, 0, 0, 0, 0, 0, 1, 2 },
        };

        /// <summary>Gain of the synthesis window over the printed prototype.
        /// −M per libsbc's windows, confirmed by the match with ffmpeg.</summary>
        internal const double WindowGainPerSubband = -1.0;

        private static readonly double[] Window4 = BuildWindow(Proto440, 4);
        private static readonly double[] Window8 = BuildWindow(Ds4SbcEncoder.Proto880, 8);
        private static readonly double[,] Matrix4 = BuildMatrix(4);
        private static readonly double[,] Matrix8 = BuildMatrix(8);

        private static double[] BuildWindow(double[] proto, int m)
        {
            var d = new double[proto.Length];
            for (int i = 0; i < d.Length; i++) d[i] = WindowGainPerSubband * m * proto[i];
            return d;
        }

        private static double[,] BuildMatrix(int m)
        {
            var n = new double[2 * m, m];
            for (int k = 0; k < 2 * m; k++)
                for (int i = 0; i < m; i++)
                    n[k, i] = Math.Cos((i + 0.5) * (k + m / 2.0) * Math.PI / m);
            return n;
        }

        // Synthesis state, per channel: V holds 20M values (10 segments of 2M).
        private readonly double[][] _v = { new double[160], new double[160] };
        private int _stateSubbands;

        private readonly int[,] _scf = new int[2, 8];
        private readonly int[,] _bits = new int[2, 8];
        private readonly bool[] _join = new bool[8];
        private readonly double[,,] _sb = new double[16, 2, 8];

        /// <summary>Clears the synthesis history, as after a lost stretch.</summary>
        public void Reset()
        {
            Array.Clear(_v[0]);
            Array.Clear(_v[1]);
        }

        /// <summary>Reads a frame header. False when the syncword is
        /// neither 0x9C nor 0xAD or the parameters cannot form a frame.</summary>
        public static bool TryReadHeader(ReadOnlySpan<byte> data, out SbcFrameInfo info)
        {
            info = default;
            if (data.Length < 4) return false;
            if (data[0] == 0xAD)
            {
                info = new SbcFrameInfo(0, 15, SbcChannelMode.Mono, snr: false, 8, 26, msbc: true);
                return true;
            }
            if (data[0] != 0x9C) return false;
            byte b = data[1];
            int freq = b >> 6;
            int blocks = (((b >> 4) & 3) + 1) * 4;
            var mode = (SbcChannelMode)((b >> 2) & 3);
            bool snr = ((b >> 1) & 1) != 0;
            int subbands = (b & 1) != 0 ? 8 : 4;
            int bitpool = data[2];
            info = new SbcFrameInfo(freq, blocks, mode, snr, subbands, bitpool, msbc: false);
            return BitpoolFits(info);
        }

        // The specification's limit: the samples must fit in 16 bits each
        // across the frame, and a channel's bitpool stays under 16 bits a
        // subband (32 for the stereo modes, which share it).
        private static bool BitpoolFits(in SbcFrameInfo f)
        {
            bool two = f.Channels == 2;
            bool dual = f.Mode == SbcChannelMode.DualChannel;
            bool joint = f.Mode == SbcChannelMode.JointStereo;
            bool stereo = joint || f.Mode == SbcChannelMode.Stereo;
            int maxBits = 16 * f.Subbands * f.Blocks * (two ? 2 : 1) - 32
                - 4 * f.Subbands * (two ? 2 : 1) - (joint ? f.Subbands : 0);
            int maxBitpool = Math.Min(maxBits / (f.Blocks * (dual ? 2 : 1)),
                                      (stereo ? 32 : 16) * f.Subbands);
            return f.Bitpool <= maxBitpool;
        }

        /// <summary>Decodes one frame from the start of <paramref name="frame"/>
        /// into interleaved 16-bit PCM. Returns the samples per channel, or
        /// 0 when the frame is malformed, cut short or fails its CRC.</summary>
        public int Decode(ReadOnlySpan<byte> frame, Span<short> pcm, out SbcFrameInfo info)
        {
            if (!TryReadHeader(frame, out info)) return 0;
            int length = info.FrameLength;
            int nch = info.Channels, nsb = info.Subbands, nblk = info.Blocks;
            if (frame.Length < length || pcm.Length < nblk * nsb * nch) return 0;
            bool joint = info.Mode == SbcChannelMode.JointStereo;

            if (Crc8(frame, nch * nsb * 4 + (joint ? nsb : 0)) != frame[3]) return 0;

            var r = new BitReader(frame.Slice(4, length - 4));
            for (int s = 0; s < 8; s++) _join[s] = false;
            if (joint)
            {
                for (int s = 0; s < nsb; s++) _join[s] = r.Read(1) != 0;
                _join[nsb - 1] = false; // the last join bit is reserved
            }
            for (int ch = 0; ch < nch; ch++)
                for (int s = 0; s < nsb; s++)
                    _scf[ch, s] = r.Read(4);

            if (info.Mode == SbcChannelMode.DualChannel)
            {
                Allocate(info, 0, 1);
                Allocate(info, 1, 1);
            }
            else
            {
                Allocate(info, 0, nch);
            }

            for (int blk = 0; blk < nblk; blk++)
                for (int ch = 0; ch < nch; ch++)
                    for (int s = 0; s < nsb; s++)
                    {
                        int bits = _bits[ch, s];
                        if (bits == 0) { _sb[blk, ch, s] = 0; continue; }
                        int q = r.Read(bits);
                        double levels = (1 << bits) - 1;
                        _sb[blk, ch, s] = ((2.0 * q + 1.0) / levels - 1.0) * (1 << (_scf[ch, s] + 1));
                    }

            if (joint)
                for (int s = 0; s < nsb; s++)
                {
                    if (!_join[s]) continue;
                    for (int blk = 0; blk < nblk; blk++)
                    {
                        double sum = _sb[blk, 0, s], diff = _sb[blk, 1, s];
                        _sb[blk, 0, s] = sum + diff;
                        _sb[blk, 1, s] = sum - diff;
                    }
                }

            // A change of subband count restarts the filterbank: the two
            // histories have different shapes.
            if (_stateSubbands != nsb) { Reset(); _stateSubbands = nsb; }

            Span<double> sbBlock = stackalloc double[8];
            for (int ch = 0; ch < nch; ch++)
                for (int blk = 0; blk < nblk; blk++)
                {
                    for (int s = 0; s < nsb; s++) sbBlock[s] = _sb[blk, ch, s];
                    Synthesize(_v[ch], sbBlock.Slice(0, nsb), nsb, pcm, blk * nsb * nch + ch, nch);
                }
            return nblk * nsb;
        }

        /// <summary>One block of the synthesis filterbank for one channel.
        /// Writes M samples, <paramref name="stride"/> apart from
        /// <paramref name="offset"/>.</summary>
        private static void Synthesize(double[] v, ReadOnlySpan<double> s, int m,
                                       Span<short> pcm, int offset, int stride)
        {
            // Shift V by 2M. The oldest 2M values fall off the end.
            Array.Copy(v, 0, v, 2 * m, 18 * m);
            double[,] n = m == 4 ? Matrix4 : Matrix8;
            for (int k = 0; k < 2 * m; k++)
            {
                double acc = 0;
                for (int i = 0; i < m; i++) acc += n[k, i] * s[i];
                v[k] = acc;
            }

            double[] d = m == 4 ? Window4 : Window8;
            for (int j = 0; j < m; j++)
            {
                double x = 0;
                for (int t = 0; t < 10; t++)
                {
                    // U[i·2M + j] = V[i·4M + j], U[i·2M + M + j] = V[i·4M + 3M + j].
                    int u = j + m * t;
                    int seg = u / (2 * m), rest = u % (2 * m);
                    double val = rest < m ? v[seg * 4 * m + rest] : v[seg * 4 * m + 3 * m + (rest - m)];
                    x += val * d[u];
                }
                pcm[offset + j * stride] = (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue);
            }
        }

        /// <summary>The specification's bit allocation for one channel or for
        /// the two channels of a stereo frame, which share the bitpool.
        /// Remaining bits go out subband by subband, channel by channel.</summary>
        private void Allocate(in SbcFrameInfo f, int firstCh, int nch)
        {
            int nsb = f.Subbands;
            int[] offsets = nsb == 4 ? Offset4[f.FrequencyIndex] : Offset8[f.FrequencyIndex];
            Span<int> need = stackalloc int[16];
            int maxNeed = 0;
            for (int c = 0; c < nch; c++)
                for (int s = 0; s < nsb; s++)
                {
                    int scf = _scf[firstCh + c, s];
                    int bn;
                    if (f.Snr) bn = scf;
                    else if (scf == 0) bn = -5;
                    else
                    {
                        int loudness = scf - offsets[s];
                        bn = loudness > 0 ? loudness / 2 : loudness;
                    }
                    need[c * nsb + s] = bn;
                    if (bn > maxNeed) maxNeed = bn;
                }

            int bitpool = f.Bitpool;
            int bitcount = 0, slicecount = 0, bitslice = maxNeed + 1;
            do
            {
                bitslice--;
                bitcount += slicecount;
                slicecount = 0;
                for (int i = 0; i < nch * nsb; i++)
                {
                    int bn = need[i];
                    if (bn > bitslice + 1 && bn < bitslice + 16) slicecount++;
                    else if (bn == bitslice + 1) slicecount += 2;
                }
            } while (bitcount + slicecount < bitpool && bitslice > -32);

            if (bitcount + slicecount == bitpool)
            {
                bitcount += slicecount;
                bitslice--;
            }

            for (int c = 0; c < nch; c++)
                for (int s = 0; s < nsb; s++)
                {
                    int bn = need[c * nsb + s];
                    _bits[firstCh + c, s] = bn < bitslice + 2 ? 0 : Math.Min(bn - bitslice, 16);
                }

            int ch = 0, sb = 0;
            while (bitcount < bitpool && sb < nsb)
            {
                int b = _bits[firstCh + ch, sb];
                if (b >= 2 && b < 16) { _bits[firstCh + ch, sb] = b + 1; bitcount++; }
                else if (need[ch * nsb + sb] == bitslice + 1 && bitpool > bitcount + 1)
                { _bits[firstCh + ch, sb] = 2; bitcount += 2; }
                if (++ch == nch) { ch = 0; sb++; }
            }
            ch = 0; sb = 0;
            while (bitcount < bitpool && sb < nsb)
            {
                if (_bits[firstCh + ch, sb] < 16) { _bits[firstCh + ch, sb]++; bitcount++; }
                if (++ch == nch) { ch = 0; sb++; }
            }
        }

        /// <summary>CRC-8 over header bytes 1 and 2, then
        /// <paramref name="dataBits"/> bits from byte 4: the join bits and
        /// the scale factors.</summary>
        internal static byte Crc8(ReadOnlySpan<byte> frame, int dataBits)
        {
            byte crc = 0x0F;
            void Feed(int bit)
            {
                bool x = (bit ^ (crc >> 7)) != 0;
                crc = (byte)(crc << 1);
                if (x) crc ^= 0x1D;
            }
            for (int i = 7; i >= 0; i--) Feed((frame[1] >> i) & 1);
            for (int i = 7; i >= 0; i--) Feed((frame[2] >> i) & 1);
            for (int i = 0; i < dataBits; i++) Feed((frame[4 + (i >> 3)] >> (7 - (i & 7))) & 1);
            return crc;
        }

        /// <summary>MSB-first bit reader. Reading past the end yields zeros,
        /// and the frame length was checked before decoding starts.</summary>
        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> _buf;
            private int _bitPos;

            public BitReader(ReadOnlySpan<byte> buf) { _buf = buf; _bitPos = 0; }

            public int Read(int count)
            {
                int v = 0;
                for (int i = 0; i < count; i++)
                {
                    int byteIndex = _bitPos >> 3;
                    int bit = byteIndex < _buf.Length ? (_buf[byteIndex] >> (7 - (_bitPos & 7))) & 1 : 0;
                    v = (v << 1) | bit;
                    _bitPos++;
                }
                return v;
            }
        }
    }
}
