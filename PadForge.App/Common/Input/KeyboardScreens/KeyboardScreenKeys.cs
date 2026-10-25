using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The keys beside the keyboard screens (issue #519), as every engine
    /// that reads them reports them. Each engine reports its own held set by
    /// a name of its own, and the row reads their union, so a G15 and a G19
    /// on one PC both reach it. A key that goes down is held asserted for at
    /// least <see cref="PulseMs"/>, the G-key row's rule: a tap can begin and
    /// end between two polls, and a macro still has to see the edge.
    /// </summary>
    internal static class KeyboardScreenKeys
    {
        public const int PulseMs = 175;

        private static readonly object _lock = new();
        private static readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
        private static readonly long[] _pulseUntil = new long[KeyboardScreenFormats.KeyCount];

        /// <summary>An engine's held keys, as the row's bits. A key newly
        /// down starts its pulse.</summary>
        public static void Report(string source, int bits)
        {
            if (string.IsNullOrEmpty(source)) return;
            bits &= (1 << KeyboardScreenFormats.KeyCount) - 1;
            long now = Environment.TickCount64;
            lock (_lock)
            {
                _held.TryGetValue(source, out int before);
                int pressed = bits & ~before;
                for (int b = 0; b < KeyboardScreenFormats.KeyCount; b++)
                    if ((pressed & (1 << b)) != 0) _pulseUntil[b] = now + PulseMs;
                if (bits == 0) _held.Remove(source);
                else _held[source] = bits;
            }
        }

        /// <summary>An engine that stopped holds nothing.</summary>
        public static void Clear(string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            lock (_lock) _held.Remove(source);
        }

        /// <summary>The feature switched off or the engine stopped.</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                _held.Clear();
                Array.Clear(_pulseUntil);
            }
        }

        /// <summary>Every key held by any engine or still in its pulse, into
        /// <paramref name="buttons"/>. Written for every index, pressed or
        /// not, so a released key gives its falling edge.</summary>
        public static void Read(bool[] buttons, long now)
        {
            if (buttons == null) return;
            lock (_lock)
            {
                int held = 0;
                foreach (int bits in _held.Values) held |= bits;
                int n = Math.Min(buttons.Length, KeyboardScreenFormats.KeyCount);
                for (int b = 0; b < buttons.Length; b++)
                {
                    bool pressed = b < n && ((held & (1 << b)) != 0 || (_pulseUntil[b] != 0 && now < _pulseUntil[b]));
                    if (!pressed && b < n) _pulseUntil[b] = 0;
                    buttons[b] = pressed;
                }
            }
        }
    }
}
