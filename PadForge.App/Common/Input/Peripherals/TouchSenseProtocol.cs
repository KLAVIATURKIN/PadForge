using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One output value of a HID collection as hid.dll describes
    /// it (HIDP_VALUE_CAPS): the fields that tell a TouchSense mouse's buzz
    /// report from any other output report.</summary>
    internal readonly record struct TouchSenseValueCap(
        byte ReportId, ushort UsagePage, ushort Usage, ushort BitSize, ushort ReportCount);

    /// <summary>
    /// The wire format of the buzz motor in a TouchSense mouse (#494): the
    /// Logitech iFeel Mouse and iFeel MouseMan. Pure, with no I/O, so every
    /// frame and every rule here is tested byte for byte.
    ///
    /// <para>A command is seven bytes with no report ID, no checksum and no
    /// reply. It is the one output report the mouse declares, and the mouse
    /// declares it inside its mouse collection (the report descriptor in
    /// tactile-cvs tactile/support/logs/ifeel.log,v:216-224), where Windows'
    /// mouse class driver passes no output report down, which is why the
    /// frames reach the mouse only once it is bound to WinUSB
    /// (<see cref="TouchSenseChannel"/>). Four sources give the same bytes:
    /// the 2001 capture of the vendor's own driver stack
    /// (tactile/support/logs/ifeel.data,v:30-220), Dan Moore's write-up and
    /// BeOS program (iFeel index.html:38-105, code/ifeel/ifeel-beos.cpp:25-28),
    /// Joshua Bobruk's Linux driver (tactile-ifeel iFeel/ifeel.c:73 and
    /// 92-96) and the LibUsbDotNet program in Logitech-iFeel-Vibration-POC
    /// (MouseVibrationCore/Program.cs:107-120). The vendor's 2003 driver,
    /// LMouFrc.dll 9.41.0 of MouseWare 9.80 (touchsense-vendor-drivers
    /// unpacked/mw980enu/Win2K_XP), builds the same frames and shows what
    /// the others left out: the delay is 16 bits wide and the strength is
    /// lifted onto 0x50 to 0xFF. Its code is cited by virtual address.</para>
    /// </summary>
    internal static class TouchSenseProtocol
    {
        public const ushort LogitechVid = 0x046D;

        /// <summary>The iFeel Mouse, the one model with a descriptor and a
        /// working buzz on record.</summary>
        public const ushort IFeelMousePid = 0xC030;

        /// <summary>The iFeel MouseMan. The vendor's driver serves it with
        /// the device class it gives the iFeel Mouse (LMouFrc.dll
        /// 0x100062D1 to 0x10006315).</summary>
        public const ushort IFeelMouseManPid = 0xC032;

        /// <summary>The second iFeel Mouse ID, which Windows lists as the
        /// iFeel Mouse+ (C:/Windows/INF/input.inf:302 and 737). The vendor's
        /// driver gives it a device class of its own, with four channels
        /// (LMouFrc.dll 0x100062AE to 0x100062CF), which writes the buzz
        /// frame's layout for a pulse effect on channel 0 (0x10008D3D to
        /// 0x10008DB7).</summary>
        public const ushort IFeelMouse2Pid = 0xC031;

        /// <summary>The mice this build drives, with the names Logitech's
        /// own package gives them (touchsense-vendor-drivers
        /// text/mw980enu/Win2K_XP/LM2KPLUS.INF:637-639). 046D:C033, the
        /// second MouseMan ID, is in that INF and in no entry of the
        /// vendor's driver, so it stays out until one is tried. Each mouse
        /// is offered its binding only when its own descriptor shows the
        /// buzz report (<see cref="MouseCollectionDeclaresBuzz"/>).</summary>
        internal static readonly (ushort VendorId, ushort ProductId, string Name)[] Mice =
        {
            (LogitechVid, IFeelMousePid, "Logitech iFeel Mouse"),
            (LogitechVid, IFeelMouseManPid, "Logitech iFeel MouseMan"),
            (LogitechVid, IFeelMouse2Pid, "Logitech iFeel Mouse"),
        };

        public static bool IsMouse(ushort vendorId, ushort productId) => NameOf(vendorId, productId) != null;

        /// <summary>The model's name, or null for any other device.</summary>
        public static string NameOf(ushort vendorId, ushort productId)
        {
            foreach (var mouse in Mice)
                if (mouse.VendorId == vendorId && mouse.ProductId == productId) return mouse.Name;
            return null;
        }

        // ─────────────────────────────────────────────
        //  Frames
        // ─────────────────────────────────────────────

        public const int FrameLength = 7;

        /// <summary>The first byte is a channel in its high four bits and an
        /// operation in its low four: 0x10 is channel 0, 1 starts an effect
        /// and 3 stops one, so 0xF3 stops all four channels of the second
        /// generation (LMouFrc.dll 0x10008CA8 to 0x10008DB7).</summary>
        public const byte CommandStart0 = 0x11;
        public const byte CommandStop0 = 0x13;
        public const byte CommandStopAll = 0xF3;
        public const byte CommandKeepAlive = 0x14;

        /// <summary>The one effect on record: a train of pulses. All 124
        /// buzz frames of the capture carry it (ifeel.data,v:54-199).</summary>
        public const byte EffectPulses = 0x0A;

        /// <summary>The buzz: 11 0A, the strength, the delay between pulses
        /// in milliseconds as 16 bits with the low byte first, the number of
        /// pulses, 00. Moore, Bobruk and the capture show the delay as one
        /// byte with 00 after it. The vendor's driver writes that 00 as the
        /// delay's high byte (LMouFrc.dll 0x100084F8 to 0x1000852A).</summary>
        public static byte[] Buzz(byte strength, ushort delayMs, byte count)
            => new byte[] { CommandStart0, EffectPulses, strength, (byte)delayMs, (byte)(delayMs >> 8), count, 0x00 };

        /// <summary>The stop: 13 and six zeros. It ends the pulses that are
        /// playing, where a buzz frame runs out its count (iFeel
        /// index.html:68). The vendor's driver sends it when nothing has any
        /// strength left (LMouFrc.dll 0x100085BB to 0x100085E5).</summary>
        public static byte[] Stop()
            => new byte[] { CommandStop0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>The vendor driver's last frame before its thread ends,
        /// on both generations: F3 and six zeros (LMouFrc.dll 0x10007F0A to
        /// 0x10007F31, three times in the capture, the first at
        /// ifeel.data,v:33).</summary>
        public static byte[] StopAll()
            => new byte[] { CommandStopAll, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>The vendor driver's frame after a second without one:
        /// 14 and six zeros (LMouFrc.dll 0x10008045 to 0x100080B7). What the
        /// mouse does with it is on no record, and three programs that never
        /// send it report a buzzing mouse, so it is built and not sent by
        /// default (<see cref="TouchSenseBackend"/>).</summary>
        public static byte[] KeepAlive()
            => new byte[] { CommandKeepAlive, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // ─────────────────────────────────────────────
        //  Control requests on interface 0
        // ─────────────────────────────────────────────

        /// <summary>HID SET_REPORT for the output report with no ID, the
        /// transfer the vendor's own stack used 191 times in the capture:
        /// 21 09 00 02 00 00 07 00 (ifeel.log,v:280).</summary>
        public static byte[] SetReportSetup(int length = FrameLength)
            => new byte[] { 0x21, 0x09, 0x00, 0x02, 0x00, 0x00, (byte)length, (byte)(length >> 8) };

        /// <summary>HID SET_IDLE with no duration, sent once at open, as
        /// Windows' HID driver sends it in the capture (ifeel.log,v:195) and
        /// Bobruk's driver in its probe (ifeel.c:282).</summary>
        public static byte[] SetIdleSetup()
            => new byte[] { 0x21, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>GET_DESCRIPTOR for the interface's report descriptor
        /// (ifeel.log,v:226, where Windows asked for more than the 74 bytes
        /// the mouse has and got 74).</summary>
        public static byte[] ReportDescriptorSetup(int length)
            => new byte[] { 0x81, 0x06, 0x00, 0x22, 0x00, 0x00, (byte)length, (byte)(length >> 8) };

        /// <summary>The longest report descriptor read from a mouse. The
        /// iFeel Mouse's is 74 bytes.</summary>
        public const int MaxReportDescriptor = 1024;

        /// <summary>The length of interface 0's report descriptor, from the
        /// HID descriptor inside a configuration descriptor (09 21 00 01 21
        /// 01 22 4A 00 on the iFeel Mouse, ifeel.log,v:85-89), or 0 when
        /// there is none or it is longer than
        /// <see cref="MaxReportDescriptor"/>.</summary>
        public static int ReportDescriptorLength(ReadOnlySpan<byte> configuration)
        {
            const byte InterfaceType = 0x04, HidType = 0x21, ReportType = 0x22;
            bool inInterface0 = false;
            for (int at = 0; at + 2 <= configuration.Length;)
            {
                int length = configuration[at];
                if (length < 2 || at + length > configuration.Length) return 0;
                byte type = configuration[at + 1];
                if (type == InterfaceType && length >= 4)
                {
                    inInterface0 = configuration[at + 2] == 0 && configuration[at + 3] == 0;
                }
                else if (type == HidType && inInterface0 && length >= 6)
                {
                    int count = configuration[at + 5];
                    for (int i = 0; i < count && 6 + 3 * i + 3 <= length; i++)
                    {
                        int entry = at + 6 + 3 * i;
                        if (configuration[entry] != ReportType) continue;
                        int size = configuration[entry + 1] | (configuration[entry + 2] << 8);
                        return size > 0 && size <= MaxReportDescriptor ? size : 0;
                    }
                    return 0;
                }
                at += length;
            }
            return 0;
        }

        // ─────────────────────────────────────────────
        //  From a rumble level to a frame
        // ─────────────────────────────────────────────

        /// <summary>The vendor driver's lift, the first of its two steps
        /// from a magnitude to a strength byte: 0x50 + magnitude x 0xAF /
        /// 0xFF for any magnitude above 5 in 255 (LMouFrc.dll 0x1000829A to
        /// 0x100082B5). The second step is <see cref="DelayFactor"/>.</summary>
        public const byte MinStrength = 0x50;
        public const byte StrengthSpan = 0xAF;

        /// <summary>The level under which the motor is left silent, the
        /// vendor's cut: a magnitude of 5 in 255 or less gets no lift.</summary>
        public const double OffBelow = 0.02;

        /// <summary>The delay between pulses, the one the vendor's first
        /// effect in the capture uses (ifeel.data,v:54) and inside its range
        /// of 3 to 10.</summary>
        public const ushort DelayMs = 10;

        /// <summary>How long a frame plays when nothing follows it, so a
        /// PadForge that died leaves a third of a second of buzz at most.
        /// The vendor asks for 0.8 to 2 s per frame and ends it with a stop
        /// frame, which a process that is gone never sends.</summary>
        public const int HoldMs = 300;
        public const byte Count = (HoldMs + DelayMs - 1) / DelayMs;

        /// <summary>How often a held level is sent again, so the motor never
        /// reaches the end of its count. The vendor's 2001 stack resent
        /// every 110 ms (ifeel.data,v:54-57).</summary>
        public const int ResendMs = 100;

        /// <summary>The least time between two buzz frames of a level that
        /// keeps changing, the effects dispatcher's own tick.</summary>
        public const int MinGapMs = 33;

        /// <summary>The vendor's keep-alive interval.</summary>
        public const int KeepAliveMs = 1000;

        /// <summary>
        /// The vendor driver's second step, for a frame of more than one
        /// pulse: the lifted strength times a factor for the delay between
        /// pulses, out of 0xFF (LMouFrc.dll 0x10008307 to 0x1000831C and
        /// 0x1000A290 to 0x1000A2C1). The factor is read from a table the
        /// driver builds for each model, a notch over the delay: FF under
        /// P1, a ramp down to the attenuation from P1 to P2, the
        /// attenuation to P3, a ramp back up to P4, and FF from there on
        /// (0x100098AE to 0x10009964). The iFeel Mouse has 9, 16, 20, 24
        /// and an attenuation of 0x91 under either of its product IDs, and
        /// the iFeel MouseMan 9, 13, 24, 35 and 0x8C (0x10009B73 to
        /// 0x10009BE3 and 0x1000A6B9 to 0x1000A730). Any other product
        /// gets FF at every delay, as it does there. A registry value can
        /// replace the table in the vendor's driver, and the INF of its
        /// package sets none.
        /// </summary>
        public static byte DelayFactor(ushort productId, int delayMs)
        {
            int p1, p2, p3, p4, attenuation;
            switch (productId)
            {
                case IFeelMousePid:
                case IFeelMouse2Pid:
                    (p1, p2, p3, p4, attenuation) = (9, 16, 20, 24, 0x91);
                    break;
                case IFeelMouseManPid:
                    (p1, p2, p3, p4, attenuation) = (9, 13, 24, 35, 0x8C);
                    break;
                default:
                    return 0xFF;
            }
            if (delayMs < p1 || delayMs >= p4) return 0xFF;
            if (delayMs < p2) return (byte)(0xFF - (delayMs - p1) * (0xFF - attenuation) / (p2 - p1));
            if (delayMs < p3) return (byte)attenuation;
            return (byte)(attenuation + (delayMs - p3) * (0xFF - attenuation) / (p4 - p3));
        }

        /// <summary>The strength byte for a level of 0 to 1 on one model: 0
        /// under <see cref="OffBelow"/>, else the vendor's two steps. The
        /// lift, 0x50 + level x 0xAF with halves rounded up as the vendor's
        /// MulDiv rounds, then that times the model's factor at this
        /// build's delay, out of 0xFF and rounded the same way. A level
        /// above 1 counts as 1.</summary>
        public static byte StrengthFor(double level, ushort productId)
        {
            // Written so that a level that is no number plays nothing.
            if (!(level >= OffBelow)) return 0;
            if (level > 1) level = 1;
            int lifted = MinStrength + (int)Math.Round(level * StrengthSpan, MidpointRounding.AwayFromZero);
            return (byte)((lifted * DelayFactor(productId, DelayMs) + 0xFF / 2) / 0xFF);
        }

        /// <summary>The frame that plays a level on one model: the buzz at
        /// its strength with this build's delay and count, or the stop
        /// frame for a level that plays nothing.</summary>
        public static byte[] FrameFor(double level, ushort productId)
        {
            byte strength = StrengthFor(level, productId);
            return strength == 0 ? Stop() : Buzz(strength, DelayMs, Count);
        }

        // ─────────────────────────────────────────────
        //  The input report
        // ─────────────────────────────────────────────

        /// <summary>The iFeel Mouse's input report: four bytes with no
        /// report ID (the descriptor, ifeel.log,v:216-224).</summary>
        public const int InputReportLength = 4;

        /// <summary>Byte 0. The descriptor declares three buttons and five
        /// vendor bits, and Bobruk's driver reads the first two of those as
        /// the side and the extra button (ifeel.c:140-144).</summary>
        public const byte ButtonLeft = 0x01;
        public const byte ButtonRight = 0x02;
        public const byte ButtonMiddle = 0x04;
        public const byte ButtonSide = 0x08;
        public const byte ButtonExtra = 0x10;

        /// <summary>The buttons, the motion and the wheel of one input
        /// report, each of the three a signed count (ifeel.c:146-148). False
        /// for anything that is not four bytes long.</summary>
        public static bool TryDecodeInput(ReadOnlySpan<byte> report, out byte buttons, out sbyte dx, out sbyte dy, out sbyte wheel)
        {
            buttons = 0;
            dx = dy = wheel = 0;
            if (report.Length != InputReportLength) return false;
            buttons = report[0];
            dx = (sbyte)report[1];
            dy = (sbyte)report[2];
            wheel = (sbyte)report[3];
            return true;
        }

        // ─────────────────────────────────────────────
        //  Descriptor shapes
        // ─────────────────────────────────────────────

        /// <summary>
        /// Whether a mouse still on Windows' HID driver declares the buzz
        /// report, read from what hid.dll already parsed, with no request to
        /// the device: a mouse collection (usage page 1, usage 2) whose only
        /// output is one value of seven bytes on usage page 0xFF00, usage 2,
        /// with no report ID, which makes its output report 8 bytes with
        /// the ID byte Windows adds, and with no feature report. One usage
        /// over seven fields is one value with a report count of seven to
        /// hid.dll, a usage value array (wine dlls/hid/hidp.c:112 and
        /// 772-776). Only the iFeel Mouse's descriptor is on record, so
        /// this is what makes the binding safe to offer for its siblings:
        /// a mouse that does not show the report is never offered it.
        /// </summary>
        public static bool MouseCollectionDeclaresBuzz(ushort usagePage, ushort usage, int outputReportLength,
            int featureReportLength, int outputButtonCaps, IReadOnlyList<TouchSenseValueCap> outputValues)
            => usagePage == 0x0001 && usage == 0x0002
               && outputReportLength == FrameLength + 1 && featureReportLength == 0
               && outputButtonCaps == 0 && outputValues != null && outputValues.Count == 1
               && outputValues[0] == new TouchSenseValueCap(0, 0xFF00, 0x0002, 8, FrameLength);

        /// <summary>Whether a report descriptor declares the buzz report: no
        /// Report ID item anywhere and exactly one Output item, of seven
        /// 8-bit fields. Checked on the descriptor the bound mouse itself
        /// returns before any frame is sent to it.</summary>
        public static bool DeclaresBuzzReport(ReadOnlySpan<byte> reportDescriptor)
        {
            if (!TryWalk(reportDescriptor, out bool reportId, out _, out var outputs)) return false;
            return !reportId && outputs.Count == 1 && outputs[0].BitSize == 8 && outputs[0].Count == FrameLength;
        }

        /// <summary>Whether a report descriptor's input report is the iFeel
        /// Mouse's, which <see cref="TryDecodeInput"/> reads: no report ID,
        /// buttons 1 to 3 in the low bits of byte 0, the five vendor bits
        /// above them, then X, Y and the wheel as one relative byte each
        /// (ifeel.log,v:216-224). No sibling's input report is on record,
        /// and the MouseMan has a fourth button, so a mouse whose
        /// descriptor differs in any of these is read as no pointer at
        /// all, never through a layout that was guessed.</summary>
        public static bool DeclaresPointerReport(ReadOnlySpan<byte> reportDescriptor)
        {
            if (!TryWalk(reportDescriptor, out bool reportId, out var inputs, out _) || reportId) return false;
            long bits = 0;
            foreach (var field in inputs) bits += (long)field.BitSize * field.Count;
            if (bits != InputReportLength * 8) return false;

            // Every field by the bit it starts at. A field with fewer
            // usages than its count repeats the last one, and one with no
            // usage at all is padding (linux-input drivers/hid/hid-core.c:
            // 334-335 and 348-353).
            var at = new Dictionary<int, (int BitSize, ushort Page, ushort Usage, uint Flags)>();
            int offset = 0;
            foreach (var field in inputs)
            {
                for (int i = 0; i < field.Count; i++)
                {
                    if (field.Usages.Length > 0)
                    {
                        var (page, usage) = field.Usages[Math.Min(i, field.Usages.Length - 1)];
                        at[offset] = (field.BitSize, page, usage, field.Flags);
                    }
                    offset += field.BitSize;
                }
            }

            const ushort Desktop = 0x01, Button = 0x09, Vendor = 0xFF00, X = 0x30, Y = 0x31, Wheel = 0x38;
            bool Is(int bit, int size, ushort page, ushort usage, bool relative)
                => at.TryGetValue(bit, out var f) && f.BitSize == size && f.Page == page && f.Usage == usage
                   // Data and Variable, and Relative or Absolute as asked.
                   && (f.Flags & 0x03) == 0x02 && ((f.Flags & 0x04) != 0) == relative;
            // The five bits above the buttons, the first two of which are
            // read as the side and the extra button (ButtonSide).
            for (int bit = 3; bit < 8; bit++)
                if (!Is(bit, 1, Vendor, 0x01, false)) return false;
            return Is(0, 1, Button, 1, false) && Is(1, 1, Button, 2, false) && Is(2, 1, Button, 3, false)
                   && Is(8, 8, Desktop, X, true) && Is(16, 8, Desktop, Y, true) && Is(24, 8, Desktop, Wheel, true);
        }

        /// <summary>One Input or Output item: the size and number of its
        /// fields, its flags, and its usages with their pages.</summary>
        private readonly record struct Field(int BitSize, int Count, uint Flags, (ushort Page, ushort Usage)[] Usages);

        /// <summary>
        /// Walks a report descriptor's items and collects its Input and
        /// Output items, the way Linux's parser reads one
        /// (linux-input drivers/hid/hid-core.c): an item's prefix gives its
        /// size, type and tag (:787-821), a usage of one or two bytes takes
        /// the last usage page declared before its main item (:608-640), a
        /// Usage Maximum adds the whole range from the Usage Minimum
        /// (:568-598), and every main item ends the local state (:681).
        /// False for a descriptor that ends inside an item, pops a state it
        /// never pushed, carries a size, a count or a global tag that parser
        /// refuses (:478-509), or holds more items than a mouse has.
        /// </summary>
        private static bool TryWalk(ReadOnlySpan<byte> d, out bool reportId, out List<Field> inputs, out List<Field> outputs)
        {
            const int MaxUsages = 64, MaxFields = 64, MaxReportSize = 256, MaxReportCount = 12288, MaxStack = 4;
            reportId = false;
            inputs = new List<Field>();
            outputs = new List<Field>();
            if (d.IsEmpty) return false;

            ushort usagePage = 0;
            int reportSize = 0, reportCount = 0;
            var saved = new Stack<(ushort Page, int Size, int Count)>();
            // Each usage as written, with whether it came with its own page.
            var usages = new List<(uint Value, bool Extended)>();
            uint usageMin = 0;

            for (int at = 0; at < d.Length;)
            {
                byte prefix = d[at++];
                int type = (prefix >> 2) & 0x03, tag = prefix >> 4;
                if (tag == 0x0F)
                {
                    // A long item: its data length, its tag, then its data.
                    if (d.Length - at < 2) return false;
                    int longSize = d[at];
                    at += 2;
                    if (d.Length - at < longSize) return false;
                    at += longSize;
                    continue;
                }
                int size = (1 << (prefix & 0x03)) >> 1;
                if (d.Length - at < size) return false;
                uint value = 0;
                for (int i = 0; i < size; i++) value |= (uint)d[at + i] << (8 * i);
                at += size;

                if (type == 1)
                {
                    switch (tag)
                    {
                        case 0x0: usagePage = (ushort)value; break;
                        // The logical and physical ranges and the unit,
                        // which no check here reads.
                        case 0x1: case 0x2: case 0x3: case 0x4: case 0x5: case 0x6: break;
                        case 0x7:
                            if (value > MaxReportSize) return false;
                            reportSize = (int)value;
                            break;
                        case 0x8: reportId = true; break;
                        case 0x9:
                            if (value > MaxReportCount) return false;
                            reportCount = (int)value;
                            break;
                        case 0xA:
                            if (saved.Count == MaxStack) return false;
                            saved.Push((usagePage, reportSize, reportCount));
                            break;
                        case 0xB:
                            if (saved.Count == 0) return false;
                            (usagePage, reportSize, reportCount) = saved.Pop();
                            break;
                        default: return false;
                    }
                }
                else if (type == 2)
                {
                    switch (tag)
                    {
                        case 0x0:
                            if (usages.Count < MaxUsages) usages.Add((value, size > 2));
                            break;
                        case 0x1: usageMin = value; break;
                        case 0x2:
                            for (uint u = usageMin; u <= value && usages.Count < MaxUsages; u++)
                                usages.Add((u, size > 2));
                            break;
                    }
                }
                else if (type == 0)
                {
                    if (tag == 0x8 || tag == 0x9)
                    {
                        var list = tag == 0x8 ? inputs : outputs;
                        if (list.Count == MaxFields) return false;
                        var resolved = new (ushort Page, ushort Usage)[usages.Count];
                        for (int i = 0; i < resolved.Length; i++)
                            resolved[i] = (usages[i].Extended ? (ushort)(usages[i].Value >> 16) : usagePage,
                                (ushort)usages[i].Value);
                        list.Add(new Field(reportSize, reportCount, value, resolved));
                    }
                    usages.Clear();
                    usageMin = 0;
                }
            }
            return true;
        }
    }
}
