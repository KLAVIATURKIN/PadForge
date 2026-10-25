using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The light bar color each DualShock 4 and DualSense was last sent, by
    /// device and slot (issue #519, the Lightbar mode @eVenent asked for in
    /// discussion #507). The effects dispatcher records a color where it
    /// builds the final bytes for the pad, and only when the frame asserts
    /// the bar: a DualShock 4 frame always does, and a DualSense frame does
    /// with its light bar enable bit, so a color the pad never took is never
    /// shown. The screen service reads it on the UI thread.
    /// </summary>
    internal static class LightbarColors
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<Guid, (int Slot, byte R, byte G, byte B)> _byDevice = new();

        public static void Record(int slot, Guid device, byte r, byte g, byte b)
        {
            lock (_lock) _byDevice[device] = (slot, r, g, b);
        }

        public static void Clear()
        {
            lock (_lock) _byDevice.Clear();
        }

        /// <summary>The color of the lowest slot with one, among the devices
        /// <paramref name="current"/> says are still on the slot their color
        /// came from. A device that left the slot is forgotten.</summary>
        public static bool TryGetLowest(Func<Guid, int, bool> current, out (byte R, byte G, byte B) color)
            => TryGetLowest(current, null, out color);

        /// <summary>The same, among the slots <paramref name="slots"/>
        /// allows, or every slot when it is null: a keyboard screen follows
        /// the slots its row feeds.</summary>
        public static bool TryGetLowest(Func<Guid, int, bool> current, Func<int, bool> slots, out (byte R, byte G, byte B) color)
        {
            color = default;
            int best = int.MaxValue;
            List<Guid> gone = null;
            lock (_lock)
            {
                foreach (var (device, entry) in _byDevice)
                {
                    if (current != null && !current(device, entry.Slot))
                    {
                        (gone ??= new List<Guid>()).Add(device);
                        continue;
                    }
                    if (entry.Slot >= best || (slots != null && !slots(entry.Slot))) continue;
                    best = entry.Slot;
                    color = (entry.R, entry.G, entry.B);
                }
                if (gone != null) foreach (var device in gone) _byDevice.Remove(device);
            }
            return best != int.MaxValue;
        }
    }
}
