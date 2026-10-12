using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What a LampArray says of itself in its attributes report
    /// (HID Usage Tables 1.6, 26.2): how many lamps it has, the box they sit
    /// in, what kind of device carries them, and how long a host must leave
    /// between two updates.</summary>
    internal readonly record struct LampArrayAttributes(int LampCount, uint WidthUm, uint HeightUm, uint DepthUm,
        uint Kind, uint MinUpdateIntervalUs);

    /// <summary>What one lamp takes in an update (HID Usage Tables 1.6,
    /// 26.9): whether its color can be set at all, and the highest value each
    /// channel accepts. A count of 255 is a full byte, a count of 1 is on or
    /// off, and a count of 0 is a channel the lamp does not have.</summary>
    internal readonly record struct LampLevels(bool Programmable, byte Red, byte Green, byte Blue, byte Intensity);

    /// <summary>A span of neighboring lamps that all take the same values,
    /// so one range report paints them. Both ends are lamps of the span.</summary>
    internal readonly record struct LampRun(int Start, int End, LampLevels Levels);

    /// <summary>One value field of a LampArray collection as Windows' HID
    /// parser lists it (HIDP_VALUE_CAPS): the report it travels in, the
    /// logical collection it sits in, the usages it covers and how wide it
    /// is. A field that covers one usage carries it in both ends.</summary>
    internal readonly record struct LampValueCap(bool Output, byte ReportId, ushort LinkCollection, ushort LinkUsagePage,
        ushort LinkUsage, ushort UsagePage, ushort UsageMin, ushort UsageMax, ushort BitSize, ushort ReportCount);

    /// <summary>Where a device put its LampArray reports: the report ID it
    /// chose for each, which it is free to choose, and the link collection
    /// each sits in, which the parser asks for when two reports hold a field
    /// of the same usage. The control report is optional (HID Usage Tables
    /// 1.6, 26.10.1), so <see cref="HasControl"/> says whether there is one.</summary>
    internal sealed record LampArrayLayout(byte AttributesId, byte RequestId, byte ResponseId, byte RangeId,
        byte ControlId, bool HasControl, int FeatureLength)
    {
        public ushort AttributesLink { get; init; }
        public ushort RequestLink { get; init; }
        public ushort ResponseLink { get; init; }
        public ushort RangeLink { get; init; }
        public ushort ControlLink { get; init; }

        /// <summary>How many lamps one multi update report of the device
        /// sets, 0 for a device with no such report. PadForge sends none.
        /// The count says how many reports of one update the device is
        /// sure to take (<see cref="LampArrayProtocol.BuildRuns"/>).</summary>
        public int MultiUpdateSlots { get; init; }
    }

    /// <summary>
    /// The HID LampArray protocol (usage page 0x59), the one Windows Dynamic
    /// Lighting speaks (#494): the frames PadForge sends, the two reports it
    /// reads, and the rules that turn one color into values a device takes.
    /// Pure, so every byte is tested without a device.
    ///
    /// <para>The layouts are the example descriptor's in the request that
    /// added the page (HUTRR84, hutrr84_-_lighting_and_illumination_page.txt:553-867),
    /// which every host and firmware read here uses unchanged: Microsoft's
    /// packed structs (ArduinoHidForWindows src/LampArrayReports.h:6-58),
    /// OpenRGB's (Controllers/HIDLampArrayController/HIDLampArrayController.h:30-82)
    /// and hid-rgb-ctl's parsers and builders (src/device.rs:242-274,
    /// 338-345 and 592-603). Multi-byte fields are little-endian and byte 0
    /// is the report ID. The usages are fixed and the IDs are the device's
    /// choice: 1 to 6 in Microsoft's sample, 0x41 to 0x46 on ASUS laptops
    /// (lampctl crates/lamparray/src/lib.rs:66-68), 0x30 to 0x35 on a Mistel
    /// keyboard (mistel-lights Sources/BaroccoHID/LampArrayReport.swift:6-11).
    /// So every builder takes the ID, and the scan reads it from the
    /// descriptor (<see cref="CheckShape"/>).</para>
    ///
    /// <para>A device may lay a report out differently and still be a valid
    /// LampArray. PadForge drives only a device whose reports Windows' own
    /// parser lays out exactly as these builders do
    /// (<see cref="CrossCheck"/>), and any other gets no path.</para>
    /// </summary>
    internal static class LampArrayProtocol
    {
        /// <summary>The Lighting And Illumination page and its application
        /// usage, LampArray (HID Usage Tables 1.6, section 26).</summary>
        internal const ushort UsagePage = 0x59;
        internal const ushort LampArrayUsage = 0x01;

        // The logical collections, one per report.
        internal const ushort AttributesReport = 0x02;
        internal const ushort RequestReport = 0x20;
        internal const ushort ResponseReport = 0x22;
        internal const ushort MultiUpdateReport = 0x50;
        internal const ushort RangeUpdateReport = 0x60;
        internal const ushort ControlReport = 0x70;

        // The fields PadForge reads or writes.
        internal const ushort LampCountUsage = 0x03;
        internal const ushort LampArrayKindUsage = 0x07;
        internal const ushort MinUpdateIntervalUsage = 0x08;
        internal const ushort LampIdUsage = 0x21;
        internal const ushort RedLevelCountUsage = 0x28;
        internal const ushort GreenLevelCountUsage = 0x29;
        internal const ushort BlueLevelCountUsage = 0x2A;
        internal const ushort IntensityLevelCountUsage = 0x2B;
        internal const ushort IsProgrammableUsage = 0x2C;
        internal const ushort RedUpdateChannelUsage = 0x51;
        internal const ushort GreenUpdateChannelUsage = 0x52;
        internal const ushort BlueUpdateChannelUsage = 0x53;
        internal const ushort IntensityUpdateChannelUsage = 0x54;
        internal const ushort LampUpdateFlagsUsage = 0x55;
        internal const ushort LampIdStartUsage = 0x61;
        internal const ushort LampIdEndUsage = 0x62;
        internal const ushort AutonomousModeUsage = 0x71;

        /// <summary>LampUpdateComplete, the one update flag: this report is
        /// the last of a batch, and the device now shows the batch at once
        /// (HID Usage Tables 1.6, 26.4.1).</summary>
        internal const byte LampUpdateComplete = 0x01;

        /// <summary>The byte length of each report with its ID. The response
        /// is one byte longer where a device makes InputBinding 16 bits wide
        /// (Apex3TklDynamicLighting Driver.c:300-301). PadForge reads nothing
        /// past the five bytes before it.</summary>
        internal const int AttributesLength = 23;
        internal const int RequestLength = 3;
        internal const int ResponseLength = 29;
        internal const int RangeUpdateLength = 10;
        internal const int ControlLength = 2;

        /// <summary>The most lamps a unit may declare. A count past this is
        /// taken for a report misread, not for a device: the lamps are read
        /// one report each before the first color.</summary>
        internal const int MaxLamps = 4096;

        /// <summary>The fastest PadForge repaints any unit, the effects
        /// dispatcher's own animation tick (HidppBackend.PaintMs), and the
        /// longest it lets a device's declared interval hold a color
        /// back.</summary>
        internal const int PaintFloorMs = 33;
        internal const int MaxIntervalMs = 1000;

        /// <summary>The most runs one update is ever sent as, PadForge's
        /// own bound on how long an update may get. The device's own bound
        /// can be lower (<see cref="BuildRuns"/>).</summary>
        internal const int MaxRuns = 8;

        // ─────────────────────────────────────────────
        //  Frames
        // ─────────────────────────────────────────────

        /// <summary>LampArrayControlReport: AutonomousMode. False takes the
        /// lamps from the device's own effect, the claim, and true gives
        /// them back, the hand-back (HID Usage Tables 1.6, 26.10.1). The
        /// same two bytes in mistel-lights' own test,
        /// Tests/BaroccoHIDTests/LampArrayReportTests.swift:9-12.</summary>
        public static byte[] Control(byte reportId, bool autonomous)
            => new[] { reportId, autonomous ? (byte)0x01 : (byte)0x00 };

        /// <summary>LampAttributesRequestReport: the lamp the next response
        /// report describes (HID Usage Tables 1.6, 26.8.1).</summary>
        public static byte[] AttributesRequest(byte reportId, int lampId)
            => new[] { reportId, (byte)lampId, (byte)(lampId >> 8) };

        /// <summary>LampRangeUpdateReport: one value set for every lamp from
        /// <paramref name="start"/> to <paramref name="end"/>, both included
        /// (HID Usage Tables 1.6, 26.11.2). This is the frame that paints a
        /// whole device one color, and what the single-color hosts send:
        /// hid-rgb-ctl (src/device.rs:592-603), lampctl
        /// (crates/lamparray/src/lib.rs:178-189) and the Acer host
        /// (acer-predator-helios-rgb tools/predator_rgb.py:123-128).</summary>
        public static byte[] RangeUpdate(byte reportId, bool complete, int start, int end,
            byte red, byte green, byte blue, byte intensity)
            => new[]
            {
                reportId, complete ? LampUpdateComplete : (byte)0x00,
                (byte)start, (byte)(start >> 8), (byte)end, (byte)(end >> 8),
                red, green, blue, intensity,
            };

        /// <summary>Every lamp off, the lamps' default state (HID Usage
        /// Tables 1.6, 26.11), in one report. All zeros is within every
        /// lamp's level counts, so it needs no run. It is the hand-back of a
        /// device with no control report, which has no effect of its own to
        /// go back to.</summary>
        public static byte[] AllOff(LampArrayUnit unit)
            => RangeUpdate(unit.RangeId, complete: true, 0, unit.LampCount - 1, 0, 0, 0, 0);

        // ─────────────────────────────────────────────
        //  Reports read from the device
        // ─────────────────────────────────────────────

        /// <summary>LampArrayAttributesReport: ID, LampCount (16 bits), the
        /// bounding box (three of 32), LampArrayKind (32) and
        /// MinUpdateIntervalInMicroseconds (32). False for a buffer too
        /// short to hold them.</summary>
        public static bool TryParseAttributes(ReadOnlySpan<byte> report, out LampArrayAttributes attributes)
        {
            attributes = default;
            if (report.Length < AttributesLength) return false;
            attributes = new LampArrayAttributes(
                BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(1)),
                BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(3)),
                BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(7)),
                BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(11)),
                BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(15)),
                BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(19)));
            return true;
        }

        /// <summary>LampAttributesResponseReport: ID, LampId (16 bits),
        /// position, latency and purposes (five of 32), then the four level
        /// counts and IsProgrammable at bytes 23 to 27. False for a buffer
        /// too short to hold them. A longer one, a 16-bit InputBinding
        /// behind them, parses the same.</summary>
        public static bool TryParseLampResponse(ReadOnlySpan<byte> report, out int lampId, out LampLevels levels)
        {
            lampId = 0;
            levels = default;
            if (report.Length < ResponseLength) return false;
            lampId = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(1));
            levels = new LampLevels(report[27] != 0, report[23], report[24], report[25], report[26]);
            return true;
        }

        // ─────────────────────────────────────────────
        //  From a color to what a device takes
        // ─────────────────────────────────────────────

        /// <summary>A 0 to 255 value on a channel with <paramref name="levels"/>
        /// steps above off, rounded to the nearest step, hid-rgb-ctl's rule
        /// (src/device.rs:33-41). A channel with a full byte of steps takes
        /// the value as it is, and a channel with none takes 0.</summary>
        public static int Scale(int value, int levels)
        {
            if (levels <= 0) return 0;
            if (levels >= 255) return value;
            return (value * levels + 127) / 255;
        }

        /// <summary>
        /// The runs one update is sent as: each a longest span of neighboring
        /// lamps with the same level counts, in lamp order. A device ignores
        /// a whole report in which any channel is above the count of any
        /// lamp in its range (HID Usage Tables 1.6, 26.11.2,
        /// hut1_6.txt:15300-15307), so lamps that differ are never painted
        /// by one report at their own counts.
        ///
        /// <para>How many reports a device takes back to back is its own
        /// to say: it "must be able to receive and process (consecutively)
        /// the minimum number of LampMultiUpdateReports required to update
        /// all Lamps", and may ignore what a host sends past that before
        /// the interval is over (HID Usage Tables 1.6, 26.7). That number
        /// is the lamp count over <paramref name="multiUpdateSlots"/>, the
        /// lamps one multi update report of the device sets, rounded up.
        /// The slot count is the device's, eight in the page's example
        /// descriptor and on the bench board, and a host that sends multi
        /// updates takes it from the descriptor too (hid-rgb-ctl
        /// src/device.rs:639-641). A device with no multi update report
        /// promises nothing past one report.</para>
        ///
        /// <para>An array cut into more runs than that, or than
        /// <see cref="MaxRuns"/>, is painted as one run over every lamp at
        /// the smallest counts instead: each color channel at the smallest
        /// count among the programmable lamps, and the intensity at the
        /// smallest among all of them, hid-rgb-ctl's rule for a uniform
        /// color (src/device.rs:572-590). Every device read about is one
        /// run.</para>
        /// </summary>
        public static LampRun[] BuildRuns(IReadOnlyList<LampLevels> lamps, int multiUpdateSlots)
        {
            if (lamps == null || lamps.Count == 0) return Array.Empty<LampRun>();
            var runs = new List<LampRun>();
            int start = 0;
            for (int i = 1; i <= lamps.Count; i++)
            {
                if (i < lamps.Count && lamps[i] == lamps[start]) continue;
                runs.Add(new LampRun(start, i - 1, lamps[start]));
                start = i;
            }
            int backToBack = multiUpdateSlots > 0 ? (lamps.Count + multiUpdateSlots - 1) / multiUpdateSlots : 1;
            if (runs.Count <= MaxRuns && runs.Count <= backToBack) return runs.ToArray();

            bool programmable = false;
            byte red = 255, green = 255, blue = 255, intensity = 255;
            foreach (var lamp in lamps)
            {
                intensity = Math.Min(intensity, lamp.Intensity);
                if (!lamp.Programmable) continue;
                programmable = true;
                red = Math.Min(red, lamp.Red);
                green = Math.Min(green, lamp.Green);
                blue = Math.Min(blue, lamp.Blue);
            }
            return new[] { new LampRun(0, lamps.Count - 1, new LampLevels(programmable, red, green, blue, intensity)) };
        }

        /// <summary>
        /// The reports that paint a unit one color, 0xRRGGBB: one range
        /// report per run, the last one flagged complete, so the device
        /// shows them as one update.
        ///
        /// <para>A programmable lamp takes each color channel scaled to its
        /// own count, and its full intensity for any lit color, 0 for
        /// black. The count is full brightness: "The highest non-zero
        /// intensity value corresponds to unity gain" (HID Usage Tables 1.6,
        /// 26.9). Devices split here. Microsoft's sample and the Lenovo and
        /// Mistel boards declare one intensity level, others 255, and a host
        /// that sends 255 to every device breaks the level rule on the
        /// first kind while one that sends 1 is dark on the second, where
        /// "I = 1 is effectively dark" (acer-predator-helios-rgb
        /// protocol/PROTOCOL.md:211). The count is the one value right on
        /// both, and what the Mistel host sends, 1 for lit and 0 for off on
        /// a one-level board (mistel-lights
        /// Sources/BaroccoHID/LampArrayReport.swift:23-26).</para>
        ///
        /// <para>A fixed-color lamp has one channel, its intensity. Its
        /// color channels are sent as 0, "as a best practice" (HID Usage
        /// Tables 1.6, 26.11.1), and its intensity is the color's brightest
        /// channel scaled to the lamp's count, never 0 for a color that is
        /// not black. A Windows driver for a single-color keyboard
        /// backlight reads its brightness from that byte: one fixed lamp
        /// with 100 levels (crosrgbkblight
        /// croskblight/croskblight.cpp:428-429 and 1718).</para>
        ///
        /// <para>A unit whose lamps could not be read
        /// (<see cref="LampArrayUnit.Runs"/> is null) is painted blind: one
        /// report over every lamp, a lit color as it is at intensity 255,
        /// the frame of the hosts that never read a lamp: lampctl
        /// (crates/lamparray/src/lib.rs:178-196), the Acer host
        /// (acer-predator-helios-rgb tools/predator_rgb.py:123-128) and
        /// mac-dynamic-lighting (PROTOCOL.md:15-19). Black is all zeros,
        /// which turns every lamp off whatever its counts (HID Usage Tables
        /// 1.6, 26.11.2). A device that holds a host to its level counts
        /// ignores the lit frame, which leaves it where the failed read
        /// already had it.</para>
        /// </summary>
        public static byte[][] Frames(LampArrayUnit unit, int rgb)
        {
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            bool lit = (r | g | b) != 0;
            var runs = unit.Runs;
            if (runs == null || runs.Count == 0)
                return new[]
                {
                    RangeUpdate(unit.RangeId, complete: true, 0, unit.LampCount - 1, r, g, b, lit ? (byte)0xFF : (byte)0x00),
                };

            var frames = new byte[runs.Count][];
            for (int i = 0; i < runs.Count; i++)
            {
                var levels = runs[i].Levels;
                byte red = 0, green = 0, blue = 0, intensity;
                if (levels.Programmable)
                {
                    red = (byte)Scale(r, levels.Red);
                    green = (byte)Scale(g, levels.Green);
                    blue = (byte)Scale(b, levels.Blue);
                    intensity = lit ? levels.Intensity : (byte)0;
                }
                else
                {
                    int level = Scale(Math.Max(r, Math.Max(g, b)), levels.Intensity);
                    // A dim color on a lamp with few levels would round to
                    // off. It is lit, so it takes the lowest level the lamp
                    // has, where it has one at all.
                    if (lit && level == 0 && levels.Intensity > 0) level = 1;
                    intensity = (byte)level;
                }
                frames[i] = RangeUpdate(unit.RangeId, complete: i == runs.Count - 1, runs[i].Start, runs[i].End,
                    red, green, blue, intensity);
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  Timing a device declares
        // ─────────────────────────────────────────────

        /// <summary>How long the first color waits after the claim: the
        /// device's minimum update interval, a millisecond at least. "The
        /// Host with guarantee to wait for MinUpdateIntervalInMicroseconds
        /// before sending its first Lamp*UpdateReport" (HID Usage Tables
        /// 1.6, 26.10.1). One host waits its board's advertised 20 ms there
        /// (hp-omen-35l-rgb docs/protocol.md:27-29) and another 4 ms on a
        /// board that advertises 3 (mistel-lights
        /// Sources/BaroccoHID/LampArrayClient.swift:44-45 and 133-138).</summary>
        public static int SettleMs(uint minUpdateIntervalUs)
            => (int)Math.Clamp((minUpdateIntervalUs + 999L) / 1000, 1, MaxIntervalMs);

        /// <summary>The least time between two updates of one unit: the
        /// device's minimum update interval, and never less than
        /// <see cref="PaintFloorMs"/>. "The Host guarantees to not send more
        /// than 1 update with this flag every
        /// MinUpdateIntervalInMicroseconds" (HID Usage Tables 1.6, 26.11),
        /// and a device may ignore a report that comes sooner (26.7).</summary>
        public static int PaintIntervalMs(uint minUpdateIntervalUs)
            => (int)Math.Clamp((minUpdateIntervalUs + 999L) / 1000, PaintFloorMs, MaxIntervalMs);

        // ─────────────────────────────────────────────
        //  The descriptor: which reports, and whether they are ours
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a device's LampArray reports among its value fields and
        /// checks that each field PadForge uses has the width the layout
        /// gives it. Every field names its logical collection
        /// (<see cref="LampValueCap.LinkUsage"/>), and the collection's
        /// usage says which report it is, the way OpenRGB tells them apart
        /// from the raw descriptor (Controllers/HIDLampArrayController/HIDLampArrayController.cpp:54-138).
        ///
        /// <para>The attributes, the request, the response and the range
        /// update must be there, and the control report is taken when it is
        /// (HID Usage Tables 1.6, 26.10.1: "If this field is absent, it
        /// means no autonomous mode is supported"). The multi update is not
        /// sent, so its shape is not checked. Only its LampId slots are
        /// counted, eight in the page's example descriptor
        /// (hutrr84_-_lighting_and_illumination_page.txt:704-714), for what
        /// they say of the reports the device takes in a row
        /// (<see cref="BuildRuns"/>).</para>
        ///
        /// <para>Only Feature reports are driven. The page allows the two
        /// update reports as Output too
        /// (hutrr84_-_lighting_and_illumination_page.txt:477-490), and no
        /// device or host read here uses that: a mouse or keyboard
        /// collection would not pass one, and one firmware author found
        /// that Windows itself never attaches to such a device (CaYaLight
        /// README.md:62). A device typed that way gets no path.</para>
        ///
        /// <para>Null with <paramref name="rejection"/> saying why when the
        /// device is not one PadForge drives.</para>
        /// </summary>
        internal static LampArrayLayout CheckShape(IReadOnlyList<LampValueCap> caps, int featureLength, out string rejection)
        {
            rejection = null;
            caps ??= Array.Empty<LampValueCap>();

            if (!Group(caps, AttributesReport, "attributes report", required: true, out var attributes, out rejection)
                || !Field(attributes, "attributes report", LampCountUsage, 16, ref rejection)
                || !Field(attributes, "attributes report", LampArrayKindUsage, 32, ref rejection)
                || !Field(attributes, "attributes report", MinUpdateIntervalUsage, 32, ref rejection))
                return null;

            if (!Group(caps, RequestReport, "lamp request report", required: true, out var request, out rejection)
                || !Field(request, "lamp request report", LampIdUsage, 16, ref rejection))
                return null;

            if (!Group(caps, ResponseReport, "lamp response report", required: true, out var response, out rejection)
                || !Field(response, "lamp response report", LampIdUsage, 16, ref rejection))
                return null;
            for (ushort usage = RedLevelCountUsage; usage <= IsProgrammableUsage; usage++)
                if (!Field(response, "lamp response report", usage, 8, ref rejection)) return null;

            if (!Group(caps, RangeUpdateReport, "range update report", required: true, out var range, out rejection)
                || !Field(range, "range update report", LampUpdateFlagsUsage, 8, ref rejection)
                || !Field(range, "range update report", LampIdStartUsage, 16, ref rejection)
                || !Field(range, "range update report", LampIdEndUsage, 16, ref rejection))
                return null;
            for (ushort usage = RedUpdateChannelUsage; usage <= IntensityUpdateChannelUsage; usage++)
                if (!Field(range, "range update report", usage, 8, ref rejection)) return null;

            if (!Group(caps, ControlReport, "control report", required: false, out var control, out rejection)) return null;
            if (control.Count > 0 && !Field(control, "control report", AutonomousModeUsage, 8, ref rejection)) return null;

            // A LampArray has several feature reports, so each has an ID,
            // and no two of them share one.
            var ids = new List<byte> { attributes[0].ReportId, request[0].ReportId, response[0].ReportId, range[0].ReportId };
            if (control.Count > 0) ids.Add(control[0].ReportId);
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == 0)
                {
                    rejection = "one of its reports has no report ID";
                    return null;
                }
                for (int j = 0; j < i; j++)
                {
                    if (ids[j] != ids[i]) continue;
                    rejection = $"two of its reports share report ID 0x{ids[i]:X2}";
                    return null;
                }
            }
            if (featureLength < ResponseLength)
            {
                rejection = $"its feature reports are {featureLength} bytes, too short for a lamp response of {ResponseLength}";
                return null;
            }

            // The multi update's LampId slots, however the descriptor
            // declares them: as one field of several, or as several fields.
            int slots = 0;
            foreach (var cap in caps)
            {
                if (cap.UsagePage != UsagePage || cap.LinkUsagePage != UsagePage || cap.LinkUsage != MultiUpdateReport) continue;
                if (LampIdUsage < cap.UsageMin || LampIdUsage > cap.UsageMax) continue;
                slots += Math.Max(1, (int)cap.ReportCount);
            }

            return new LampArrayLayout(attributes[0].ReportId, request[0].ReportId, response[0].ReportId, range[0].ReportId,
                control.Count > 0 ? control[0].ReportId : (byte)0, control.Count > 0, featureLength)
            {
                AttributesLink = attributes[0].LinkCollection,
                RequestLink = request[0].LinkCollection,
                ResponseLink = response[0].LinkCollection,
                RangeLink = range[0].LinkCollection,
                ControlLink = control.Count > 0 ? control[0].LinkCollection : (ushort)0,
                MultiUpdateSlots = slots,
            };
        }

        /// <summary>The Feature fields of one report: the ones on the
        /// lighting page whose logical collection has the report's usage.
        /// They must share one report ID and one collection. False with the
        /// reason when the report is missing where it is required, is typed
        /// as Output, is spread over several reports, or is declared in two
        /// collections.</summary>
        private static bool Group(IReadOnlyList<LampValueCap> caps, ushort linkUsage, string name, bool required,
            out List<LampValueCap> group, out string rejection)
        {
            rejection = null;
            group = new List<LampValueCap>();
            bool asOutput = false;
            foreach (var cap in caps)
            {
                if (cap.UsagePage != UsagePage || cap.LinkUsagePage != UsagePage || cap.LinkUsage != linkUsage) continue;
                if (cap.Output) asOutput = true;
                else group.Add(cap);
            }
            if (asOutput)
            {
                rejection = $"its {name} (collection 0x{linkUsage:X2}) is an Output report, and only Feature reports are driven";
                return false;
            }
            if (group.Count == 0)
            {
                if (!required) return true;
                rejection = $"it has no {name} (collection 0x{linkUsage:X2}) among its feature reports";
                return false;
            }
            foreach (var cap in group)
            {
                if (cap.ReportId == group[0].ReportId && cap.LinkCollection == group[0].LinkCollection) continue;
                rejection = cap.ReportId != group[0].ReportId
                    ? $"its {name} (collection 0x{linkUsage:X2}) is spread over report IDs 0x{group[0].ReportId:X2} and 0x{cap.ReportId:X2}"
                    : $"its {name} (collection 0x{linkUsage:X2}) is declared twice";
                return false;
            }
            return true;
        }

        /// <summary>One field of a report: there once, as a single value of
        /// the width the layout gives it.</summary>
        private static bool Field(List<LampValueCap> group, string name, ushort usage, int bits, ref string rejection)
        {
            int found = 0;
            LampValueCap field = default;
            foreach (var cap in group)
            {
                if (usage < cap.UsageMin || usage > cap.UsageMax) continue;
                field = cap;
                found++;
            }
            if (found == 0) rejection = $"its {name} has no usage 0x{usage:X2}";
            else if (found > 1) rejection = $"its {name} declares usage 0x{usage:X2} {found} times";
            else if (field.BitSize != bits) rejection = $"usage 0x{usage:X2} of its {name} is {field.BitSize} bits where {bits} are due";
            else if (field.ReportCount != 1) rejection = $"usage 0x{usage:X2} of its {name} is an array of {field.ReportCount}";
            else return true;
            return false;
        }

        /// <summary>Builds one report through Windows' HID parser, with no
        /// device I/O: the report ID, the link collection its fields sit in,
        /// and the value of each usage. The report, or null when the parser
        /// refused a field.</summary>
        internal delegate byte[] ParserBuild(byte reportId, ushort linkCollection, IReadOnlyList<(ushort Usage, uint Value)> fields);

        /// <summary>
        /// Checks the frames above against the layout Windows' own parser
        /// gives this device's descriptor, with no device I/O: the parser
        /// builds each report from known values
        /// (<paramref name="build"/>), and the bytes must be the ones the
        /// builders here make, and the ones the response parser reads. A
        /// device whose range update, control, request or response report
        /// is laid out any other way would take these frames for something
        /// else, so it is rejected. Null when every report agrees, else the
        /// reason.
        /// </summary>
        internal static string CrossCheck(LampArrayLayout layout, ParserBuild build)
        {
            byte[] range = build(layout.RangeId, layout.RangeLink, new (ushort, uint)[]
            {
                (LampUpdateFlagsUsage, LampUpdateComplete), (LampIdStartUsage, 0x1234), (LampIdEndUsage, 0x5678),
                (RedUpdateChannelUsage, 0xA1), (GreenUpdateChannelUsage, 0xB2), (BlueUpdateChannelUsage, 0xC3),
                (IntensityUpdateChannelUsage, 0xD4),
            });
            string mismatch = Mismatch("range update", range,
                RangeUpdate(layout.RangeId, complete: true, 0x1234, 0x5678, 0xA1, 0xB2, 0xC3, 0xD4));
            if (mismatch != null) return mismatch;

            if (layout.HasControl)
            {
                byte[] control = build(layout.ControlId, layout.ControlLink, new (ushort, uint)[] { (AutonomousModeUsage, 1) });
                mismatch = Mismatch("control", control, Control(layout.ControlId, autonomous: true));
                if (mismatch != null) return mismatch;
            }

            byte[] request = build(layout.RequestId, layout.RequestLink, new (ushort, uint)[] { (LampIdUsage, 0x1234) });
            mismatch = Mismatch("lamp request", request, AttributesRequest(layout.RequestId, 0x1234));
            if (mismatch != null) return mismatch;

            byte[] response = build(layout.ResponseId, layout.ResponseLink, new (ushort, uint)[]
            {
                (LampIdUsage, 0x1234), (RedLevelCountUsage, 0xA1), (GreenLevelCountUsage, 0xB2),
                (BlueLevelCountUsage, 0xC3), (IntensityLevelCountUsage, 0xD4), (IsProgrammableUsage, 0x01),
            });
            if (response == null) return "Windows' parser could not build its lamp response report";
            if (response.Length < ResponseLength || response[0] != layout.ResponseId
                || !TryParseLampResponse(response, out int lampId, out var levels)
                || lampId != 0x1234 || levels != new LampLevels(true, 0xA1, 0xB2, 0xC3, 0xD4))
                return "Windows lays its lamp response report out as " + Hex(response, ResponseLength)
                       + ", with the lamp ID or the level counts away from bytes 1 to 2 and 23 to 27";
            return null;
        }

        private static string Mismatch(string name, byte[] built, byte[] expected)
        {
            if (built == null) return $"Windows' parser could not build its {name} report";
            if (built.Length >= expected.Length && built.AsSpan(0, expected.Length).SequenceEqual(expected)) return null;
            return $"Windows lays its {name} report out as {Hex(built, expected.Length)} where the LampArray layout is {Hex(expected, expected.Length)}";
        }

        /// <summary>The first <paramref name="count"/> bytes as hex, for a
        /// diagnostics line.</summary>
        internal static string Hex(byte[] bytes, int count)
        {
            if (bytes == null) return "(none)";
            var text = new StringBuilder();
            for (int i = 0; i < count && i < bytes.Length; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(bytes[i].ToString("X2"));
            }
            return text.ToString();
        }

        /// <summary>A unit's runs for its diagnostics line: each as its
        /// first and last lamp and the level counts its colors are scaled
        /// to, or "fixed" and the intensity count for lamps that take no
        /// color.</summary>
        internal static string DescribeRuns(IReadOnlyList<LampRun> runs)
        {
            if (runs == null || runs.Count == 0) return "no runs";
            var text = new StringBuilder();
            foreach (var run in runs)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append($"{run.Start}-{run.End} ");
                text.Append(run.Levels.Programmable
                    ? $"R{run.Levels.Red}/G{run.Levels.Green}/B{run.Levels.Blue}/I{run.Levels.Intensity}"
                    : $"fixed I{run.Levels.Intensity}");
            }
            return text.ToString();
        }

        /// <summary>A device's lighting fields for the diagnostics line of
        /// a rejected unit, the multi update's left out since it is not
        /// used and is the longest: each as report ID, collection usage,
        /// field usage and bits by count, with a trailing "out" for an
        /// Output field.</summary>
        internal static string DescribeCaps(IReadOnlyList<LampValueCap> caps)
        {
            var text = new StringBuilder();
            int listed = 0;
            foreach (var cap in caps ?? Array.Empty<LampValueCap>())
            {
                if (cap.UsagePage != UsagePage || cap.LinkUsage == MultiUpdateReport) continue;
                if (listed++ > 0) text.Append(", ");
                text.Append($"{cap.ReportId:X2}/{cap.LinkUsage:X2}/{cap.UsageMin:X2}");
                if (cap.UsageMax != cap.UsageMin) text.Append($"-{cap.UsageMax:X2}");
                text.Append($" {cap.BitSize}x{cap.ReportCount}");
                if (cap.Output) text.Append(" out");
            }
            return listed == 0 ? "no lighting fields" : text.ToString();
        }
    }
}
