using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The keys beside the keyboard screens (issue #519), by screen. Each
    /// engine reports a screen's held set under a source name of its own,
    /// and the screen's row reads their union, so a key LGS hands over and
    /// the applet key read over HID both reach the same row while two
    /// keyboards stay apart. A key that goes down is held asserted for at
    /// least <see cref="PulseMs"/>, the G-key row's rule: a tap can begin and
    /// end between two polls, and a macro still has to see the edge.
    /// </summary>
    internal static class KeyboardScreenKeys
    {
        public const int PulseMs = 175;

        private sealed class Screen
        {
            public readonly Dictionary<string, int> Held = new(StringComparer.Ordinal);
            public readonly long[] PulseUntil = new long[KeyboardScreenFormats.MaxKeys];
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Screen> _screens = new(StringComparer.Ordinal);

        /// <summary>A screen's keys an engine holds, as the row's bits. A key
        /// newly down starts its pulse.</summary>
        public static void Report(string screen, string source, int bits)
        {
            if (string.IsNullOrEmpty(screen) || string.IsNullOrEmpty(source)) return;
            bits &= (1 << KeyboardScreenFormats.MaxKeys) - 1;
            long now = Environment.TickCount64;
            lock (_lock)
            {
                if (!_screens.TryGetValue(screen, out var s))
                {
                    if (bits == 0) return;
                    _screens[screen] = s = new Screen();
                }
                s.Held.TryGetValue(source, out int before);
                int pressed = bits & ~before;
                for (int b = 0; b < KeyboardScreenFormats.MaxKeys; b++)
                    if ((pressed & (1 << b)) != 0) s.PulseUntil[b] = now + PulseMs;
                if (bits == 0) s.Held.Remove(source);
                else s.Held[source] = bits;
            }
        }

        /// <summary>An engine that stopped holds nothing on the screen.</summary>
        public static void Clear(string screen, string source)
        {
            if (string.IsNullOrEmpty(screen) || string.IsNullOrEmpty(source)) return;
            lock (_lock)
                if (_screens.TryGetValue(screen, out var s)) s.Held.Remove(source);
        }

        /// <summary>The screen left: its keys and pulses go.</summary>
        public static void ClearScreen(string screen)
        {
            if (string.IsNullOrEmpty(screen)) return;
            lock (_lock) _screens.Remove(screen);
        }

        /// <summary>The feature switched off or the engines stopped.</summary>
        public static void ClearAll()
        {
            lock (_lock) _screens.Clear();
        }

        /// <summary>Every key of the screen any engine holds or that is still
        /// in its pulse, into <paramref name="buttons"/>. Written for every
        /// index, pressed or not, so a released key gives its falling
        /// edge.</summary>
        public static void Read(string screen, bool[] buttons, long now)
        {
            if (buttons == null) return;
            lock (_lock)
            {
                if (screen == null || !_screens.TryGetValue(screen, out var s))
                {
                    Array.Clear(buttons);
                    return;
                }
                int held = 0;
                foreach (int bits in s.Held.Values) held |= bits;
                int n = Math.Min(buttons.Length, KeyboardScreenFormats.MaxKeys);
                for (int b = 0; b < buttons.Length; b++)
                {
                    bool pressed = b < n && ((held & (1 << b)) != 0 || (s.PulseUntil[b] != 0 && now < s.PulseUntil[b]));
                    if (!pressed && b < n) s.PulseUntil[b] = 0;
                    buttons[b] = pressed;
                }
            }
        }
    }
}
