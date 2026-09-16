using System;
using NAudio.Dmo;
using NAudio.Wave;

namespace PadForge.Common.Input
{
    /// <summary>Reads uncompressed capture samples using their declared width.</summary>
    internal readonly struct CapturePcmFormat
    {
        private readonly bool _floating;
        private readonly int _bits;
        public int Channels { get; }
        public int SampleRate { get; }
        public int BytesPerSample { get; }
        public int BlockAlign => Channels * BytesPerSample;
        public string SampleLabel => (_floating ? "f" : _bits == 8 ? "u" : "i") + _bits;

        private CapturePcmFormat(WaveFormat format, bool floating)
        {
            _floating = floating;
            _bits = format.BitsPerSample;
            Channels = format.Channels;
            SampleRate = format.SampleRate;
            BytesPerSample = _bits / 8;
        }

        internal static bool TryCreate(WaveFormat format, out CapturePcmFormat result)
        {
            result = default;
            if (format == null || format.Channels <= 0 || format.SampleRate <= 0) return false;
            var encoding = format.Encoding;
            if (format is WaveFormatExtensible extended)
            {
                if (extended.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM) encoding = WaveFormatEncoding.Pcm;
                else if (extended.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT) encoding = WaveFormatEncoding.IeeeFloat;
                else return false;
            }
            bool floating = encoding == WaveFormatEncoding.IeeeFloat;
            bool supported = floating ? format.BitsPerSample is 32 or 64
                : encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32;
            if (!supported || format.BlockAlign != format.Channels * (format.BitsPerSample / 8)) return false;
            result = new CapturePcmFormat(format, floating);
            return true;
        }

        internal float Read(byte[] buffer, int offset)
        {
            if (_floating)
                return _bits == 32 ? BitConverter.ToSingle(buffer, offset) : (float)BitConverter.ToDouble(buffer, offset);
            return _bits switch
            {
                8 => buffer[offset] / 128f - 1f,
                16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                24 => ((sbyte)buffer[offset + 2] << 16 | buffer[offset + 1] << 8 | buffer[offset]) / 8388608f,
                32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => throw new InvalidOperationException("Unsupported capture sample width."),
            };
        }
    }
}
