using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// What a HIDMaestro keyboard or mouse profile can send, read from the
    /// profile's own report descriptor exactly the way HIDMaestro's
    /// KeyboardMousePacker (HIDMaestro 1.12.0, Internal/KeyboardMousePacker.cs)
    /// packs it. The SDK keeps that packer internal, so this reads the same
    /// collections and reports by the same rules: the first Generic Desktop
    /// Keyboard collection and its first input report with keyboard-page
    /// fields, the first Consumer Control collection and its first
    /// consumer-page input report, and every input report of the first
    /// Generic Desktop Mouse collection.
    ///
    /// <para>Two things go past the packer, both to answer "does this reach
    /// Windows", which the packer does not ask. An array element only names a
    /// usage while its value sits inside the field's logical range
    /// (hid-core.c hid_array_value_is_valid), so a usage whose index puts it
    /// past the logical maximum is not reachable even though the packer
    /// would write it. And a mouse button past the fifth is not counted:
    /// Raw Input's RAWMOUSE carries five, mouhid reports no more, and no
    /// catalog mouse has the second vendor collection HIDMaestro would send
    /// the rest through.</para>
    /// </summary>
    internal sealed class KbmDeviceCaps
    {
        private const ushort PageGenericDesktop = 0x01;
        private const ushort PageKeyboard = 0x07;
        private const ushort PageLed = 0x08;
        private const ushort PageButton = 0x09;
        private const ushort PageConsumer = 0x0C;

        /// <summary>Raw Input reports five mouse buttons (RAWMOUSE
        /// usButtonFlags: left, right, middle, back, forward).</summary>
        internal const int WindowsMouseButtons = 5;

        private readonly bool[] _keys = new bool[256];
        private readonly bool[] _keyBits = new bool[256];
        private readonly HashSet<ushort> _consumer = new();
        private readonly HashSet<ushort> _consumerBits = new();
        private readonly bool[] _mouseButtons = new bool[WindowsMouseButtons];

        /// <summary>The profile declares a keyboard collection with keys.</summary>
        public bool HasKeyboard { get; private set; }

        /// <summary>The keyboard collection declares an LED output report, so
        /// Windows sends it Num Lock, Caps Lock and Scroll Lock.</summary>
        public bool HasKeyboardLeds { get; private set; }

        /// <summary>How many non-modifier keys the keyboard report holds at
        /// once through its key array, or 0 when every key it reaches is a bit
        /// of its own (an N-key rollover report).</summary>
        public int KeyArraySlots { get; private set; }

        /// <summary>True when every key the report reaches has its own bit, so
        /// any number can be held at once.</summary>
        public bool NKeyRollover => HasKeyboard && KeyArraySlots == 0;

        /// <summary>The profile declares a consumer control collection
        /// (media keys).</summary>
        public bool HasConsumer { get; private set; }

        /// <summary>How many consumer usages the consumer report holds at once
        /// through its usage array. A usage with a bit of its own does not
        /// count against it.</summary>
        public int ConsumerArraySlots { get; private set; }

        /// <summary>The profile declares a mouse collection.</summary>
        public bool HasMouse { get; private set; }

        /// <summary>The mouse collection has a vertical wheel (Generic
        /// Desktop Wheel).</summary>
        public bool HasWheel { get; private set; }

        /// <summary>The mouse collection has a horizontal wheel or tilt
        /// (Consumer AC Pan, usage 0x238).</summary>
        public bool HasPan { get; private set; }

        /// <summary>The highest Windows mouse button the device reaches, 0 to
        /// 5.</summary>
        public int MouseButtonCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _mouseButtons.Length; i++) if (_mouseButtons[i]) n = i + 1;
                return n;
            }
        }

        /// <summary>True when the keyboard report reaches this Keyboard/Keypad
        /// page usage (modifiers 0xE0 to 0xE7 included).</summary>
        public bool CanSendKey(byte usage) => _keys[usage];

        /// <summary>True when the keyboard report has a bit for this usage.
        /// HIDMaestro sets a key's bit when it has one and puts it in the key
        /// array only when it has none (KeyboardMousePacker.PackKeyboard), so a
        /// key with a bit never takes an array slot.</summary>
        public bool IsKeyBit(byte usage) => _keyBits[usage];

        /// <summary>True when the consumer report reaches this Consumer page
        /// usage.</summary>
        public bool CanSendConsumer(ushort usage) => _consumer.Contains(usage);

        /// <summary>True when the consumer report has a bit for this usage,
        /// which then takes no array slot (KeyboardMousePacker.PackConsumer).</summary>
        public bool IsConsumerBit(ushort usage) => _consumerBits.Contains(usage);

        /// <summary>True when the mouse reaches Windows mouse button
        /// <paramref name="index"/> (0 left, 1 right, 2 middle, 3 back, 4
        /// forward), the order of KbmRawState.MouseButtons.</summary>
        public bool CanSendMouseButton(int index) =>
            index >= 0 && index < _mouseButtons.Length && _mouseButtons[index];

        private static readonly ConcurrentDictionary<string, KbmDeviceCaps> s_byProfile =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The capabilities of a catalog profile, read once.</summary>
        public static KbmDeviceCaps For(HMProfile profile)
        {
            if (profile == null) return null;
            return s_byProfile.GetOrAdd(profile.Id ?? string.Empty, _ =>
            {
                byte[] d;
                try { d = profile.GetDescriptorBytes(); }
                catch { d = null; }
                return FromDescriptor(d);
            });
        }

        /// <summary>Reads a report descriptor. An empty or unreadable one has
        /// no capabilities.</summary>
        public static KbmDeviceCaps FromDescriptor(byte[] descriptor)
        {
            var caps = new KbmDeviceCaps();
            if (descriptor == null || descriptor.Length == 0) return caps;
            var map = ReportMap.Parse(descriptor);

            var mouse = map.FirstCollection(PageGenericDesktop, 0x02);
            if (mouse != null)
            {
                foreach (var r in map.ReportsOf(mouse.Value, Kind.Input))
                {
                    foreach (var f in r.Fields)
                    {
                        if (f.Collection != mouse.Value || f.Constant || !f.Variable) continue;
                        for (int e = 0; e < f.Count; e++)
                        {
                            uint u = f.UsageAt(e);
                            ushort page = (ushort)(u >> 16), id = (ushort)u;
                            if (page == PageButton && id >= 1 && id <= WindowsMouseButtons)
                            {
                                caps._mouseButtons[id - 1] = true;
                                caps.HasMouse = true;
                            }
                            else if (page == PageGenericDesktop && (id == 0x30 || id == 0x31))
                                caps.HasMouse = true;
                            else if (page == PageGenericDesktop && id == 0x38)
                                caps.HasWheel = caps.HasMouse = true;
                            else if (page == PageConsumer && id == 0x238)
                                caps.HasPan = caps.HasMouse = true;
                        }
                    }
                }
            }

            var kb = map.FirstCollection(PageGenericDesktop, 0x06);
            if (kb != null)
            {
                foreach (var r in map.ReportsOf(kb.Value, Kind.Input))
                {
                    if (!HasPage(r, kb.Value, PageKeyboard)) continue;
                    foreach (var f in r.Fields)
                    {
                        if (f.Constant) continue;
                        if (f.Variable)
                        {
                            for (int e = 0; e < f.Count; e++)
                            {
                                uint u = f.UsageAt(e);
                                if ((u >> 16) == PageKeyboard && (u & 0xFFFF) <= 0xFF)
                                {
                                    caps._keys[u & 0xFF] = true;
                                    caps._keyBits[u & 0xFF] = true;
                                    caps.HasKeyboard = true;
                                }
                            }
                        }
                        else if (f.Usages.Length > 0 && (f.Usages[0].Min >> 16) == PageKeyboard)
                        {
                            int reach = 0;
                            foreach (uint u in f.ReachableArrayUsages())
                            {
                                if ((u >> 16) != PageKeyboard || (u & 0xFFFF) > 0xFF) continue;
                                // Usages 0 to 3 are the array's "no key" and
                                // error codes (HID Usage Tables, section 10),
                                // never a key to press.
                                if ((u & 0xFF) <= 0x03) continue;
                                caps._keys[u & 0xFF] = true;
                                reach++;
                            }
                            if (reach > 0)
                            {
                                caps.HasKeyboard = true;
                                caps.KeyArraySlots += f.Count;
                            }
                        }
                    }
                    break;   // the packer writes the first such report only
                }
                foreach (var r in map.ReportsOf(kb.Value, Kind.Output))
                    if (HasPage(r, kb.Value, PageLed)) { caps.HasKeyboardLeds = true; break; }
            }

            var cc = map.FirstCollection(PageConsumer, 0x01);
            if (cc != null)
            {
                foreach (var r in map.ReportsOf(cc.Value, Kind.Input))
                {
                    if (!HasPage(r, cc.Value, PageConsumer)) continue;
                    foreach (var f in r.Fields)
                    {
                        if (f.Constant) continue;
                        if (f.Variable)
                        {
                            for (int e = 0; e < f.Count; e++)
                            {
                                uint u = f.UsageAt(e);
                                if ((u >> 16) != PageConsumer || (u & 0xFFFF) == 0) continue;
                                caps._consumer.Add((ushort)u);
                                caps._consumerBits.Add((ushort)u);
                            }
                        }
                        else
                        {
                            int reach = 0;
                            foreach (uint u in f.ReachableArrayUsages())
                            {
                                if ((u >> 16) != PageConsumer || (u & 0xFFFF) == 0) continue;
                                caps._consumer.Add((ushort)u);
                                reach++;
                            }
                            if (reach > 0) caps.ConsumerArraySlots += f.Count;
                        }
                    }
                    caps.HasConsumer = caps._consumer.Count > 0;
                    break;   // first consumer report only, as the packer
                }
            }
            return caps;
        }

        private static bool HasPage(Report r, int collection, ushort page)
        {
            foreach (var f in r.Fields)
            {
                if (f.Collection != collection || f.Constant) continue;
                foreach (var u in f.Usages)
                    if ((u.Min >> 16) == page) return true;
            }
            return false;
        }

        // ── A minimal report descriptor reader ──────────────────────────────
        //
        // The HID 1.11 item rules HIDMaestro's HidReportMap follows (section
        // 6.2.2): globals persist and stack with Push and Pop, locals reset
        // after every main item, a 4-byte usage carries its own page, a Usage
        // Page declared after its usages completes them at the main item
        // (hid-core.c hid_concatenate_last_usage_page), only the first
        // delimiter set counts, and a field belongs to the top-level
        // collection it sits in.

        private enum Kind : byte { Input, Output, Feature }

        private readonly struct UsageRange
        {
            public readonly uint Min, Max;
            public UsageRange(uint min, uint max) { Min = min; Max = max; }
            public int Count => (int)(Max - Min) + 1;
        }

        private sealed class Field
        {
            public Kind Kind;
            public int Count;
            public int LogicalMin, LogicalMax;
            public bool Constant, Variable;
            public int Collection = -1;
            public UsageRange[] Usages = Array.Empty<UsageRange>();

            /// <summary>Element <paramref name="i"/>'s usage. Elements past the
            /// list take its last usage (HID 1.11, 6.2.2.8).</summary>
            public uint UsageAt(int i)
            {
                foreach (var r in Usages)
                {
                    if (i < r.Count) return r.Min + (uint)i;
                    i -= r.Count;
                }
                return Usages.Length == 0 ? 0 : Usages[^1].Max;
            }

            /// <summary>The usages an array element can select: list index
            /// i is selected by the value LogicalMin + i, which must fall
            /// inside the logical range.</summary>
            public IEnumerable<uint> ReachableArrayUsages()
            {
                long span = (long)LogicalMax - LogicalMin;
                if (span < 0) yield break;
                long index = 0;
                foreach (var r in Usages)
                {
                    for (uint u = r.Min; ; u++)
                    {
                        if (index > span) yield break;
                        yield return u;
                        index++;
                        if (u == r.Max) break;
                    }
                }
            }
        }

        private sealed class Report
        {
            public Kind Kind;
            public byte Id;
            public readonly List<Field> Fields = new();
        }

        private sealed class ReportMap
        {
            private readonly List<(ushort Page, ushort Usage)> _collections = new();
            private readonly List<Report> _reports = new();

            public int? FirstCollection(ushort page, ushort usage)
            {
                for (int i = 0; i < _collections.Count; i++)
                    if (_collections[i].Page == page && _collections[i].Usage == usage) return i;
                return null;
            }

            public IEnumerable<Report> ReportsOf(int collection, Kind kind)
            {
                foreach (var r in _reports)
                {
                    if (r.Kind != kind) continue;
                    foreach (var f in r.Fields)
                        if (f.Collection == collection) { yield return r; break; }
                }
            }

            private struct Globals
            {
                public ushort UsagePage;
                public int LogicalMin;
                public uint LogicalMaxRaw;
                public int LogicalMaxSize;
                public int ReportCount;
                public byte ReportId;
            }

            private struct LocalUsage
            {
                public uint Min, Max;
                public bool Extended;
            }

            public static ReportMap Parse(byte[] d)
            {
                var map = new ReportMap();
                var g = new Globals();
                var stack = new Stack<Globals>();
                var locals = new List<LocalUsage>();
                uint? usageMin = null;
                int usageMinSize = 0;
                int delimiterDepth = 0, delimiterBranch = 0;
                int depth = 0, tlc = -1;

                int i = 0;
                while (i < d.Length)
                {
                    byte prefix = d[i];
                    if (prefix == 0xFE)
                    {
                        if (i + 1 >= d.Length) break;
                        i += 3 + d[i + 1];
                        continue;
                    }
                    int size = prefix & 0x03;
                    if (size == 3) size = 4;
                    if (i + 1 + size > d.Length) break;
                    uint data = 0;
                    for (int b = 0; b < size; b++) data |= (uint)d[i + 1 + b] << (8 * b);
                    int sdata = size switch { 1 => (sbyte)data, 2 => (short)data, 4 => (int)data, _ => 0 };
                    int type = (prefix >> 2) & 0x03;
                    int tag = prefix >> 4;
                    i += 1 + size;

                    switch (type)
                    {
                        case 0:   // Main
                            ConcatenateLastUsagePage(locals, g.UsagePage);
                            switch (tag)
                            {
                                case 0x8: map.AddField(Kind.Input, data, g, locals, tlc); break;
                                case 0x9: map.AddField(Kind.Output, data, g, locals, tlc); break;
                                case 0xB: map.AddField(Kind.Feature, data, g, locals, tlc); break;
                                case 0xA:
                                    if (depth == 0)
                                    {
                                        uint u = locals.Count > 0 ? locals[0].Min : (uint)g.UsagePage << 16;
                                        tlc = map._collections.Count;
                                        map._collections.Add(((ushort)(u >> 16), (ushort)u));
                                    }
                                    depth++;
                                    break;
                                case 0xC:
                                    if (depth > 0) depth--;
                                    break;
                            }
                            locals.Clear();
                            usageMin = null;
                            delimiterDepth = delimiterBranch = 0;
                            break;
                        case 1:   // Global
                            switch (tag)
                            {
                                case 0x0: g.UsagePage = (ushort)data; break;
                                case 0x1: g.LogicalMin = sdata; break;
                                case 0x2: g.LogicalMaxRaw = data; g.LogicalMaxSize = size; break;
                                case 0x8: g.ReportId = (byte)data; break;
                                case 0x9: g.ReportCount = (int)data; break;
                                case 0xA: stack.Push(g); break;
                                case 0xB: if (stack.Count > 0) g = stack.Pop(); break;
                            }
                            break;
                        case 2:   // Local
                            if (tag == 0xA)
                            {
                                if (data != 0) { delimiterDepth++; delimiterBranch++; }
                                else if (delimiterDepth > 0) delimiterDepth--;
                                break;
                            }
                            if (delimiterBranch > 1 && tag <= 0x2) break;
                            switch (tag)
                            {
                                case 0x0:
                                    locals.Add(new LocalUsage
                                    {
                                        Min = Complete(data, size, g.UsagePage),
                                        Max = Complete(data, size, g.UsagePage),
                                        Extended = size == 4,
                                    });
                                    break;
                                case 0x1:
                                    usageMin = data;
                                    usageMinSize = size;
                                    break;
                                case 0x2:
                                    if (usageMin is uint min)
                                    {
                                        uint hi = Complete(data, size, g.UsagePage);
                                        uint lo = size == 4
                                            ? (usageMinSize == 4 ? min : (hi & 0xFFFF0000) | (min & 0xFFFF))
                                            : Complete(min, 2, g.UsagePage);
                                        if (hi >= lo) locals.Add(new LocalUsage { Min = lo, Max = hi, Extended = size == 4 });
                                    }
                                    usageMin = null;
                                    break;
                            }
                            break;
                    }
                }
                return map;
            }

            private static uint Complete(uint data, int size, ushort page) =>
                size == 4 ? data : ((uint)page << 16) | (data & 0xFFFF);

            private static void ConcatenateLastUsagePage(List<LocalUsage> locals, ushort page)
            {
                for (int i = locals.Count - 1; i >= 0; i--)
                {
                    var u = locals[i];
                    if (u.Extended) continue;
                    if ((ushort)(u.Max >> 16) == page) break;
                    u.Min = ((uint)page << 16) | (u.Min & 0xFFFF);
                    u.Max = ((uint)page << 16) | (u.Max & 0xFFFF);
                    locals[i] = u;
                }
            }

            private void AddField(Kind kind, uint flags, Globals g, List<LocalUsage> locals, int tlc)
            {
                Report layout = null;
                foreach (var r in _reports)
                    if (r.Kind == kind && r.Id == g.ReportId) { layout = r; break; }
                if (layout == null)
                {
                    layout = new Report { Kind = kind, Id = g.ReportId };
                    _reports.Add(layout);
                }
                int logicalMax = g.LogicalMaxSize switch
                {
                    1 => (sbyte)g.LogicalMaxRaw,
                    2 => (short)g.LogicalMaxRaw,
                    4 => (int)g.LogicalMaxRaw,
                    _ => 0,
                };
                if (g.LogicalMin >= 0 && logicalMax < g.LogicalMin)
                    logicalMax = (int)Math.Min(g.LogicalMaxRaw, int.MaxValue);
                var usages = new UsageRange[locals.Count];
                for (int k = 0; k < locals.Count; k++) usages[k] = new UsageRange(locals[k].Min, locals[k].Max);
                layout.Fields.Add(new Field
                {
                    Kind = kind,
                    Count = g.ReportCount,
                    LogicalMin = g.LogicalMin,
                    LogicalMax = logicalMax,
                    Constant = (flags & 0x01) != 0,
                    Variable = (flags & 0x02) != 0,
                    Collection = tlc,
                    Usages = usages,
                });
            }
        }
    }
}
