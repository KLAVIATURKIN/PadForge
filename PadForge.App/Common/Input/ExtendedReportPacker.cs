using System;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Packs a PadForge-authored Extended slot's raw state into the HID input
    /// report its own descriptor declares.
    ///
    /// <para>The fixed gamepad state the slot normally submits through carries
    /// 32 buttons and exactly one hat, so a slot that advertises 128 buttons
    /// and 4 hats lost everything past those. Emitting fewer controls than the
    /// grid offers is the one option the project rules forbid, so a layout the
    /// fixed state cannot express is packed here and submitted raw
    /// instead.</para>
    ///
    /// <para>The layout is not guessed. PadForge builds the descriptor itself,
    /// in this order: one <c>AddStick</c> per stick (two 16-bit unsigned axes),
    /// one <c>AddTrigger</c> or <c>AddAxis</c> per trigger (one 16-bit unsigned
    /// axis), one <c>AddHat</c> per POV (8 bits), then <c>AddButtons</c> (one
    /// bit each, the declared count rounded up so the report ends on a byte).
    /// Fields land at the running bit offset in declaration order, multi-byte
    /// values little-endian. No report ID byte rides the frame: the descriptor
    /// carries one only when force feedback is on, and the raw submit takes
    /// data bytes either way.</para>
    ///
    /// <para>Every legal layout fits the 64-byte submit buffer. The widest is
    /// eight axes, four hats and 128 buttons, which is 36 bytes.</para>
    /// </summary>
    internal static class ExtendedReportPacker
    {
        /// <summary>The widest report any legal layout produces: 8 axes at 16
        /// bits, 4 hats at 8, and 128 buttons.</summary>
        internal const int MaxReportSize = (8 * 16 + 4 * 8 + 128) / 8;

        /// <summary>True when the fixed gamepad state cannot carry the layout,
        /// which is the only reason to leave the proven path. The fixed state
        /// holds 32 buttons in a uint and exactly one hat.</summary>
        internal static bool NeedsRawReport(in CustomControllerLayout layout)
            => layout.Buttons > 32 || layout.Povs > 1;

        /// <summary>Report size in bytes for a layout, matching what the
        /// descriptor builder computes from the same counts.</summary>
        internal static int ReportSize(in CustomControllerLayout layout)
        {
            int sticks = Math.Max(0, layout.Sticks);
            int triggers = Math.Max(0, layout.Triggers);
            int povs = Math.Max(0, layout.Povs);
            int buttons = Math.Max(0, layout.Buttons);
            int bits = sticks * 32 + triggers * 16 + povs * 8;
            // The button block's pad rounds the running total up to a byte.
            // Everything before it is already byte-aligned, so the pad depends
            // on the button count alone.
            bits += buttons + ((8 - (buttons % 8)) % 8);
            return bits / 8;
        }

        /// <summary>Writes one frame. Returns the number of bytes written.
        /// </summary>
        internal static int Pack(in RawHidState raw, in CustomControllerLayout layout, byte[] dest)
        {
            int size = ReportSize(layout);
            if (dest == null || dest.Length < size) return 0;
            Array.Clear(dest, 0, size);

            int sticks = Math.Max(0, layout.Sticks);
            int triggers = Math.Max(0, layout.Triggers);
            int povs = Math.Max(0, layout.Povs);
            int buttons = Math.Max(0, layout.Buttons);

            // The raw surface interleaves sticks with triggers, so the axis a
            // control reads from is not its position in the descriptor. Same
            // arithmetic as ExtendedSlotConfig.ComputeAxisLayout.
            int interleave = Math.Min(sticks, triggers);
            int StickX(int g) =>
                g < interleave ? g * 3
                : g < sticks ? interleave * 3 + (g - interleave) * 2
                             : -1;
            int TriggerIdx(int g) =>
                g < interleave ? g * 3 + 2
                : g < triggers ? interleave * 3 + Math.Max(0, sticks - interleave) * 2 + (g - interleave)
                               : -1;

            short[] axes = raw.Axes;
            int off = 0;
            for (int s = 0; s < sticks; s++)
            {
                int xi = StickX(s);
                WriteAxis(dest, off, axes, xi);
                WriteAxis(dest, off + 2, axes, xi >= 0 ? xi + 1 : -1);
                off += 4;
            }
            for (int t = 0; t < triggers; t++)
            {
                WriteAxis(dest, off, axes, TriggerIdx(t));
                off += 2;
            }
            for (int h = 0; h < povs; h++)
            {
                dest[off] = HatByte(raw.Povs, h);
                off++;
            }
            for (int b = 0; b < buttons; b++)
            {
                if (raw.IsButtonPressed(b))
                    dest[off + (b >> 3)] |= (byte)(1 << (b & 7));
            }

            return size;
        }

        /// <summary>One 16-bit unsigned axis field. The raw surface rests a
        /// trigger at short.MinValue and a stick at 0, and the descriptor
        /// declares every axis unsigned across the full range, so one shift
        /// serves both: rest becomes 0 for a trigger and center for a stick.
        /// Little-endian, matching the SDK's own writer.</summary>
        private static void WriteAxis(byte[] dest, int byteOff, short[] axes, int axisIndex)
        {
            short v = axes != null && axisIndex >= 0 && axisIndex < axes.Length
                ? axes[axisIndex]
                : (short)0;
            int u = v + 32768;
            dest[byteOff] = (byte)(u & 0xFF);
            dest[byteOff + 1] = (byte)((u >> 8) & 0xFF);
        }

        /// <summary>One hat's wire byte: 0 through 7 clockwise from north, and
        /// 8 for centered. The centered value is the null the descriptor's
        /// logical range leaves free, which is what the SDK's own encoder
        /// writes for a released hat.</summary>
        private static byte HatByte(int[] povs, int pov)
        {
            if (povs == null || pov < 0 || pov >= povs.Length) return 8;
            int centidegrees = povs[pov];
            if (centidegrees < 0) return 8;
            return (byte)(((centidegrees + 2250) / 4500) % 8);
        }
    }
}
