using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The UDP interface DSX game mods speak (#529), parsed and encoded
    /// without a socket or a controller, so every byte is testable.
    ///
    /// <para>A packet is JSON, <c>{"instructions":[{"type":1,"parameters":[0,2,13,0,8]}]}</c>.
    /// The instruction types, trigger modes, custom modes, parameter ranges
    /// and reply come from the official DSX v3.1+ mod example (Paliverse/DSX
    /// at 1614f00, Mod System (DSX v3)/Mod System (DSX v3.1+)/DSX_UDP_Example,
    /// Resources.cs and Program.cs) and the v2 example (Paliverse/DualSenseX).
    /// DSX itself is closed, so where the examples say nothing PadForge
    /// follows the open servers that speak the protocol: DualSenseY in both
    /// its versions, DSZ and the hbashton DS4Windows fork. Each choice below
    /// names its source.</para>
    /// </summary>
    internal static class GameModProtocol
    {
        /// <summary>The port DSX listens on unless told another, and the
        /// one every example falls back to.</summary>
        internal const int DefaultPort = 6969;

        // Instruction types, DSX v3.1+ Resources.cs InstructionType. The v2
        // example names type 0 "Invalid" and sends it for every unused entry
        // of its fixed-size instruction array.
        internal const int GetDsxStatus = 0;
        internal const int TriggerUpdate = 1;
        internal const int RgbUpdate = 2;
        internal const int PlayerLed = 3;
        internal const int TriggerThreshold = 4;
        internal const int MicLed = 5;
        internal const int PlayerLedNewRevision = 6;
        internal const int ResetToUserSettings = 7;

        private static readonly string[] TypeNames =
        {
            "GetDSXStatus", "TriggerUpdate", "RGBUpdate", "PlayerLED",
            "TriggerThreshold", "MicLED", "PlayerLEDNewRevision", "ResetToUserSettings",
        };

        /// <summary>Trigger sides, the examples' Trigger enum.</summary>
        internal const int LeftTrigger = 1;
        internal const int RightTrigger = 2;

        /// <summary>An 11-byte trigger block: the mode, then ten parameters.</summary>
        internal const int TriggerBlockLength = 11;

        /// <summary>DSX's reply text. DualSenseY's first version copied its
        /// reply class from DSX's decompiled code (UDP.cs:62, the token
        /// comments at 106-112), and its current version keeps the text
        /// (udp.cpp:84). No reviewed mod compares it, but one that did
        /// would expect this.</summary>
        internal const string ReplyStatus = "DSX Received UDP Instructions";

        /// <summary>One instruction: its type and its parameters, the first
        /// of which is the controller index.</summary>
        internal readonly struct Instruction
        {
            public readonly int Type;
            public readonly int[] Parameters;

            public Instruction(int type, int[] parameters)
            {
                Type = type;
                Parameters = parameters ?? Array.Empty<int>();
            }
        }

        private static readonly UTF8Encoding LenientUtf8 = new(false, false);

        private static readonly JsonDocumentOptions ParseOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>The instructions in one datagram, in order, or null
        /// when it is not a packet.
        ///
        /// <para>As lenient as DSZ (udp.rs, parse and num), because mods
        /// built from different examples send different shapes. Keys match
        /// whatever their case. A type is a number or a name. A null or
        /// missing parameter list is empty, which is what the v2 example's
        /// unused entries carry. A parameter is a number, a numeric string or
        /// a boolean (the five player lights), and anything else reads as 0,
        /// the value DSX's own conversion gives a null. That matters for the
        /// v3.1 example's custom-trigger helper, which writes its values one
        /// place early and leaves a null at the end
        /// (Program.cs, AddCustomAdaptiveTriggerToPacket). An instruction
        /// whose type cannot be read is skipped, and the rest still
        /// apply.</para></summary>
        internal static List<Instruction> Parse(ReadOnlySpan<byte> datagram)
        {
            if (datagram.IsEmpty) return null;
            string text = LenientUtf8.GetString(datagram).Trim('\0', ' ', '\t', '\r', '\n', '\uFEFF');
            if (text.Length == 0) return null;

            JsonDocument document;
            try { document = JsonDocument.Parse(text, ParseOptions); }
            catch (JsonException) { return null; }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!TryGetProperty(root, "instructions", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                    return null;

                var result = new List<Instruction>(list.GetArrayLength());
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetProperty(item, "type", out var typeValue)
                        || !TryReadType(typeValue, out int type))
                        continue;

                    int[] parameters = Array.Empty<int>();
                    if (TryGetProperty(item, "parameters", out var values)
                        && values.ValueKind == JsonValueKind.Array)
                    {
                        parameters = new int[values.GetArrayLength()];
                        int i = 0;
                        foreach (var value in values.EnumerateArray())
                            parameters[i++] = ReadNumber(value);
                    }
                    result.Add(new Instruction(type, parameters));
                }
                return result;
            }
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static bool TryReadType(JsonElement value, out int type)
        {
            type = -1;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    type = ReadNumber(value);
                    return true;
                case JsonValueKind.String:
                    string name = value.GetString()?.Trim() ?? "";
                    for (int i = 0; i < TypeNames.Length; i++)
                    {
                        if (string.Equals(name, TypeNames[i], StringComparison.OrdinalIgnoreCase))
                        {
                            type = i;
                            return true;
                        }
                    }
                    if (string.Equals(name, "Invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        type = GetDsxStatus;
                        return true;
                    }
                    if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out type))
                        return true;
                    type = -1;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>A parameter as an integer: numbers truncate, booleans are
        /// 1 and 0, numeric strings parse, and anything else is 0.</summary>
        internal static int ReadNumber(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long whole)) return ClampToInt(whole);
                    return FromDouble(value.GetDouble());
                case JsonValueKind.True:
                    return 1;
                case JsonValueKind.False:
                    return 0;
                case JsonValueKind.String:
                    string text = value.GetString()?.Trim() ?? "";
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return FromDouble(d);
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return 1;
                    return 0;
                default:
                    return 0;
            }
        }

        private static int FromDouble(double d)
            => double.IsFinite(d) ? ClampToInt((long)Math.Clamp(Math.Truncate(d), long.MinValue, long.MaxValue)) : 0;

        private static int ClampToInt(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

        private static byte ToByte(int v) => (byte)Math.Clamp(v, 0, 255);

        // ─────────────────────────────────────────────
        //  Triggers
        // ─────────────────────────────────────────────

        // Nielk1's mode bytes (ExtendInput.DataTools.DualSense
        // .TriggerEffectGenerator, MIT, TriggerEffectType).
        private const byte ModeOff = 0x05;
        private const byte ModeFeedback = 0x21;
        private const byte ModeBow = 0x22;
        private const byte ModeGalloping = 0x23;
        private const byte ModeWeapon = 0x25;
        private const byte ModeVibration = 0x26;
        private const byte ModeMachine = 0x27;

        /// <summary>The named presets DSX never published, as DualSenseY
        /// sends them (source/scePadCustomTriggers.cpp). Its first version
        /// carries the same patterns as packed constants copied from DSX's
        /// decompiled code (MainWindow.xaml.cs:556-626), the closest evidence
        /// there is of DSX's own bytes. DSZ substitutes effects of its own and
        /// the DS4Windows fork refuses these modes, and DualSense4Rockstar
        /// uses Hardest and Choppy in live game logic.</summary>
        private static readonly Dictionary<int, byte[]> Presets = new()
        {
            [1]  = new byte[] { 0x02, 144, 160, 255, 0, 0, 0, 0, 0, 0, 0 },     // GameCube
            [2]  = new byte[] { 0x02, 128, 160, 255, 0, 0, 0, 0, 0, 0, 0 },     // VerySoft
            [3]  = new byte[] { 0x21, 69, 160, 255, 0, 0, 0, 0, 0, 0, 0 },      // Soft
            [4]  = new byte[] { 0x21, 32, 160, 255, 255, 255, 255, 255, 0, 0, 0 },  // Hard
            [5]  = new byte[] { 0x21, 16, 160, 255, 255, 255, 255, 255, 0, 0, 0 },  // VeryHard
            [6]  = new byte[] { 0x02, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0 },   // Hardest
            [7]  = new byte[] { 0x01, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0 },         // Rigid
            [9]  = new byte[] { 0x21, 2, 39, 33, 39, 38, 2, 0, 0, 0, 0 },       // Choppy
            [10] = new byte[] { 0x22, 2, 35, 1, 6, 6, 1, 33, 0, 0, 0 },         // Medium
            [11] = new byte[] { 0x27, 37, 35, 6, 39, 33, 35, 34, 0, 0, 0 },     // VibrateTriggerPulse
        };

        /// <summary>Custom trigger value modes 0 to 16 to the mode byte.
        /// Off, then Rigid and Pulse, each plain, with A (0x20), with B (0x04)
        /// and with both: the flag arithmetic in the Wujek Dualsense API's
        /// TriggerType.cs, pydualsense's TriggerModes and the DS4Windows fork
        /// (DSXUdpServer.cs:543). DualSenseY sends 0x27 for PulseAB and the
        /// three others send 0x26, which the arithmetic gives. Modes 9 to 16
        /// are DualSenseY's alone (CustomTriggerCustomTriggerValue): DSZ
        /// drops them and the fork refuses them.</summary>
        private static readonly byte[] CustomModeBytes =
        {
            0x00, 0x01, 0x21, 0x05, 0x25, 0x02, 0x22, 0x06, 0x26,  // Off, Rigid.., Pulse..
            0x06, 0x26, 0x06, 0x26,                                // VibrateResistance, A, B, AB
            0x27, 0x27, 0x27, 0x27,                                // VibratePulse, A, B, AB
        };

        /// <summary>Encodes a TriggerUpdate's mode and its parameters (what
        /// follows the controller index and the side) into an 11-byte block.
        /// False, with <paramref name="block"/> untouched, for an unknown mode,
        /// missing parameters or parameters out of range, the way Nielk1's
        /// generator returns false and leaves its destination alone. A mod
        /// that sends a bad instruction keeps whatever it set before.</summary>
        internal static bool TryEncodeTrigger(int mode, ReadOnlySpan<int> parameters, Span<byte> block)
        {
            if (block.Length < TriggerBlockLength) return false;
            Span<byte> p = parameters.Length <= 16 ? stackalloc byte[parameters.Length] : new byte[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) p[i] = ToByte(parameters[i]);

            Span<byte> b = stackalloc byte[TriggerBlockLength];
            b.Clear();
            bool ok;
            switch (mode)
            {
                case 0:   // Normal
                case 20:  // OFF
                    ok = Off(b);
                    break;
                case 8:   // VibrateTrigger: its one parameter is a frequency (the v3.1
                          // example, "Frequency: 0-255", and DSZ). Ten when absent, the
                          // value both examples send.
                    ok = Vibration(b, 0, 8, p.Length > 0 ? p[0] : (byte)10);
                    break;
                case 19:  // VIBRATE_TRIGGER_10Hz, "Vibration Effect with 10 Hz"
                    ok = Vibration(b, 0, 8, 10);
                    break;
                case 12:  // CustomTriggerValue
                    ok = CustomValue(b, p);
                    break;
                case 13:  // Resistance: start 0-9, force 0-8
                case 21:  // FEEDBACK
                    ok = p.Length >= 2 && Feedback(b, p[0], p[1]);
                    break;
                case 14:  // Bow: start, end, force, snap force
                    ok = p.Length >= 4 && Bow(b, p[0], p[1], p[2], p[3]);
                    break;
                case 15:  // Galloping: start, end, first foot, second foot, frequency
                    ok = p.Length >= 5 && Galloping(b, p[0], p[1], p[2], p[3], p[4]);
                    break;
                case 16:  // SemiAutomaticGun: start 2-7, end, force
                case 22:  // WEAPON
                    ok = p.Length >= 3 && Weapon(b, p[0], p[1], p[2]);
                    break;
                case 17:  // AutomaticGun: start 0-9, strength 0-8, frequency
                case 23:  // VIBRATION
                    ok = p.Length >= 3 && Vibration(b, p[0], p[1], p[2]);
                    break;
                case 18:  // Machine: start, end, strength A, strength B, frequency, period
                    ok = p.Length >= 6 && Machine(b, p[0], p[1], p[2], p[3], p[4], p[5]);
                    break;
                case 24:  // SLOPE_FEEDBACK: start, end, start strength, end strength
                    ok = p.Length >= 4 && SlopeFeedback(b, p[0], p[1], p[2], p[3]);
                    break;
                case 25:  // MULTIPLE_POSITION_FEEDBACK: ten zone strengths
                    ok = p.Length >= 10 && MultiplePositionFeedback(b, p.Slice(0, 10));
                    break;
                case 26:  // MULTIPLE_POSITION_VIBRATION: frequency, then ten zone amplitudes
                    ok = p.Length >= 11 && MultiplePositionVibration(b, p[0], p.Slice(1, 10));
                    break;
                default:
                    if (Presets.TryGetValue(mode, out var preset))
                    {
                        preset.CopyTo(b);
                        ok = true;
                    }
                    else
                    {
                        ok = false;
                    }
                    break;
            }
            if (ok) b.CopyTo(block);
            return ok;
        }

        /// <summary>CustomTriggerValue: the custom mode, then up to seven
        /// values. Values one to six land in bytes 1 to 6 and the seventh in
        /// byte 9. The Wujek Dualsense API writes its seventh force there
        /// (Controller.cs:352, report byte 20 on USB, trigger byte 9), and so
        /// does the DS4Windows fork (DSXUdpServer.cs:545). DualSense4Rockstar
        /// names that value ab_actuationFrequency (DSX_Base/Client/iO.cs:227),
        /// and byte 9 is where mode 0x26 keeps its frequency. DualSenseY's
        /// current version and DSZ write it to byte 7. A missing value is 0,
        /// as in DualSenseY.</summary>
        private static bool CustomValue(Span<byte> b, ReadOnlySpan<byte> p)
        {
            if (p.Length < 1 || p[0] >= CustomModeBytes.Length) return false;
            b[0] = CustomModeBytes[p[0]];
            for (int i = 1; i <= 6; i++) b[i] = i < p.Length ? p[i] : (byte)0;
            b[9] = p.Length > 7 ? p[7] : (byte)0;
            return true;
        }

        // The encoders below follow Nielk1's TriggerEffectGenerator (MIT,
        // Copyright (c) 2021-2022 John "Nielk1" Klein) function for
        // function: the same range checks, the same Off when a strength or a
        // frequency is zero, the same packing. DualSenseY, DSZ and the
        // DS4Windows fork encode these modes identically.

        private static bool Off(Span<byte> b)
        {
            b.Clear();
            b[0] = ModeOff;
            return true;
        }

        private static bool Feedback(Span<byte> b, byte position, byte strength)
        {
            if (position > 9 || strength > 8) return false;
            if (strength == 0) return Off(b);
            byte force = (byte)((strength - 1) & 0x07);
            uint forceZones = 0;
            ushort activeZones = 0;
            for (int i = position; i < 10; i++)
            {
                forceZones |= (uint)(force << (3 * i));
                activeZones |= (ushort)(1 << i);
            }
            WriteZones(b, ModeFeedback, activeZones, forceZones);
            return true;
        }

        private static bool Weapon(Span<byte> b, byte start, byte end, byte strength)
        {
            if (start > 7 || start < 2 || end > 8 || end <= start || strength > 8) return false;
            if (strength == 0) return Off(b);
            ushort startAndStop = (ushort)((1 << start) | (1 << end));
            b.Clear();
            b[0] = ModeWeapon;
            b[1] = (byte)(startAndStop & 0xff);
            b[2] = (byte)(startAndStop >> 8);
            b[3] = (byte)(strength - 1);
            return true;
        }

        private static bool Vibration(Span<byte> b, byte position, byte amplitude, byte frequency)
        {
            if (position > 9 || amplitude > 8) return false;
            if (amplitude == 0 || frequency == 0) return Off(b);
            byte strength = (byte)((amplitude - 1) & 0x07);
            uint amplitudeZones = 0;
            ushort activeZones = 0;
            for (int i = position; i < 10; i++)
            {
                amplitudeZones |= (uint)(strength << (3 * i));
                activeZones |= (ushort)(1 << i);
            }
            WriteZones(b, ModeVibration, activeZones, amplitudeZones);
            b[9] = frequency;
            return true;
        }

        private static bool MultiplePositionFeedback(Span<byte> b, ReadOnlySpan<byte> strength)
        {
            uint forceZones = 0;
            ushort activeZones = 0;
            for (int i = 0; i < 10; i++)
            {
                if (strength[i] > 8) return false;
                if (strength[i] == 0) continue;
                forceZones |= (uint)(((strength[i] - 1) & 0x07) << (3 * i));
                activeZones |= (ushort)(1 << i);
            }
            if (activeZones == 0) return Off(b);
            WriteZones(b, ModeFeedback, activeZones, forceZones);
            return true;
        }

        private static bool SlopeFeedback(Span<byte> b, byte start, byte end, byte startStrength, byte endStrength)
        {
            if (start > 8 || end > 9 || end <= start) return false;
            if (startStrength < 1 || startStrength > 8 || endStrength < 1 || endStrength > 8) return false;
            Span<byte> strength = stackalloc byte[10];
            strength.Clear();
            // Nielk1 rounds a float with Math.Round's default, to even.
            float slope = 1.0f * (endStrength - startStrength) / (end - start);
            for (int i = start; i < 10; i++)
                strength[i] = i <= end
                    ? (byte)Math.Round(startStrength + slope * (i - start))
                    : endStrength;
            return MultiplePositionFeedback(b, strength);
        }

        private static bool MultiplePositionVibration(Span<byte> b, byte frequency, ReadOnlySpan<byte> amplitude)
        {
            uint amplitudeZones = 0;
            ushort activeZones = 0;
            for (int i = 0; i < 10; i++)
            {
                if (amplitude[i] > 8) return false;
                if (amplitude[i] == 0) continue;
                amplitudeZones |= (uint)(((amplitude[i] - 1) & 0x07) << (3 * i));
                activeZones |= (ushort)(1 << i);
            }
            if (frequency == 0 || activeZones == 0) return Off(b);
            WriteZones(b, ModeVibration, activeZones, amplitudeZones);
            b[9] = frequency;
            return true;
        }

        private static bool Bow(Span<byte> b, byte start, byte end, byte strength, byte snapForce)
        {
            if (start > 8 || end > 8 || start >= end || strength > 8 || snapForce > 8) return false;
            if (end == 0 || strength == 0 || snapForce == 0) return Off(b);
            ushort startAndStop = (ushort)((1 << start) | (1 << end));
            uint forcePair = (uint)(((strength - 1) & 0x07) | (((snapForce - 1) & 0x07) << 3));
            b.Clear();
            b[0] = ModeBow;
            b[1] = (byte)(startAndStop & 0xff);
            b[2] = (byte)(startAndStop >> 8);
            b[3] = (byte)(forcePair & 0xff);
            b[4] = (byte)(forcePair >> 8);
            return true;
        }

        private static bool Galloping(Span<byte> b, byte start, byte end, byte firstFoot, byte secondFoot, byte frequency)
        {
            if (start > 8 || end > 9 || start >= end) return false;
            if (secondFoot > 7 || firstFoot > 6 || firstFoot >= secondFoot) return false;
            if (frequency == 0) return Off(b);
            ushort startAndStop = (ushort)((1 << start) | (1 << end));
            uint timeAndRatio = (uint)((secondFoot & 0x07) | ((firstFoot & 0x07) << 3));
            b.Clear();
            b[0] = ModeGalloping;
            b[1] = (byte)(startAndStop & 0xff);
            b[2] = (byte)(startAndStop >> 8);
            b[3] = (byte)(timeAndRatio & 0xff);
            b[4] = frequency;
            return true;
        }

        private static bool Machine(Span<byte> b, byte start, byte end, byte amplitudeA, byte amplitudeB, byte frequency, byte period)
        {
            if (start > 8 || end > 9 || end <= start || amplitudeA > 7 || amplitudeB > 7) return false;
            if (frequency == 0) return Off(b);
            ushort startAndStop = (ushort)((1 << start) | (1 << end));
            uint strengthPair = (uint)((amplitudeA & 0x07) | ((amplitudeB & 0x07) << 3));
            b.Clear();
            b[0] = ModeMachine;
            b[1] = (byte)(startAndStop & 0xff);
            b[2] = (byte)(startAndStop >> 8);
            b[3] = (byte)(strengthPair & 0xff);
            b[4] = frequency;
            b[5] = period;
            return true;
        }

        private static void WriteZones(Span<byte> b, byte mode, ushort activeZones, uint zoneValues)
        {
            b.Clear();
            b[0] = mode;
            b[1] = (byte)(activeZones & 0xff);
            b[2] = (byte)(activeZones >> 8);
            b[3] = (byte)(zoneValues & 0xff);
            b[4] = (byte)((zoneValues >> 8) & 0xff);
            b[5] = (byte)((zoneValues >> 16) & 0xff);
            b[6] = (byte)((zoneValues >> 24) & 0xff);
        }

        // ─────────────────────────────────────────────
        //  Lights
        // ─────────────────────────────────────────────

        /// <summary>RGBUpdate after the controller index: red, green, blue
        /// and, from DSX v3, a brightness that scales all three, as DSZ
        /// applies it (udp.rs, type 2). Values clamp to 0-255.</summary>
        internal static bool TryLightbar(ReadOnlySpan<int> p, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (p.Length < 3) return false;
            int brightness = p.Length > 3 ? ToByte(p[3]) : 255;
            r = (byte)(ToByte(p[0]) * brightness / 255);
            g = (byte)(ToByte(p[1]) * brightness / 255);
            b = (byte)(ToByte(p[2]) * brightness / 255);
            return true;
        }

        /// <summary>The DualSense's no-fade bit, which PadForge sets on its
        /// own pips too (Ds5EffectSynthesizer.PlayerIndicatorNoFade).</summary>
        internal const byte PlayerLightsNoFade = 0x20;

        /// <summary>PlayerLED after the controller index: five lights, left
        /// to right, as bits 0 to 4 (DSZ, udp.rs type 3). A light the packet
        /// leaves out is off.</summary>
        internal static bool TryPlayerLights(ReadOnlySpan<int> p, out byte bits)
        {
            bits = 0;
            if (p.Length < 1) return false;
            for (int i = 0; i < 5 && i < p.Length; i++)
                if (p[i] != 0) bits |= (byte)(1 << i);
            bits |= PlayerLightsNoFade;
            return true;
        }

        /// <summary>The player 1 to 5 patterns of PlayerLEDNewRevision, then
        /// all off: PadForge's own PlayerLedBits, the DS4Windows fork's table
        /// (DSXUdpServer.cs:459) and the layout the v3.1 example draws.</summary>
        private static readonly byte[] NewRevisionPatterns = { 0x04, 0x0A, 0x15, 0x1B, 0x1F, 0x00 };

        internal static bool TryPlayerLightsNewRevision(ReadOnlySpan<int> p, out byte bits)
        {
            bits = 0;
            if (p.Length < 1 || p[0] < 0 || p[0] >= NewRevisionPatterns.Length) return false;
            bits = (byte)(NewRevisionPatterns[p[0]] | PlayerLightsNoFade);
            return true;
        }

        /// <summary>MicLED after the controller index: On, Pulse and Off
        /// (0, 1, 2) to the report's 1, 2 and 0, as DSZ maps them.</summary>
        internal static bool TryMuteLight(ReadOnlySpan<int> p, out byte value)
        {
            value = 0;
            if (p.Length < 1) return false;
            switch (p[0])
            {
                case 0: value = 1; return true;
                case 1: value = 2; return true;
                case 2: value = 0; return true;
                default: return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Reply
        // ─────────────────────────────────────────────

        /// <summary>One entry of the reply's v3 device list.</summary>
        internal sealed class ReplyDevice
        {
            public int Index;
            public string MacAddress = "";
            /// <summary>0 DualSense, 1 DualSense Edge, 2 DualShock 4 v1,
            /// 3 DualShock 4 v2, 4 the wireless adapter (DSX DeviceType).</summary>
            public int DeviceType;
            /// <summary>0 USB, 1 Bluetooth, 2 the adapter (DSX ConnectionType).</summary>
            public int ConnectionType;
            public int BatteryLevel;
            public bool IsSupportAT;
            public bool IsSupportLightBar;
            public bool IsSupportPlayerLED;
            public bool IsSupportMicLED;
        }

        /// <summary>The reply every packet gets, with the field names and
        /// casing of the examples' ServerResponse and Device classes. The
        /// connection and battery fields describe the controller the packet's
        /// first instruction named, or the first one listed.</summary>
        internal static byte[] BuildReply(IReadOnlyList<ReplyDevice> devices, int firstIndex, DateTime now)
        {
            ReplyDevice first = null;
            if (devices != null)
            {
                foreach (var d in devices)
                    if (d != null && d.Index == firstIndex) { first = d; break; }
                if (first == null && devices.Count > 0) first = devices[0];
            }

            var buffer = new ArrayBufferWriter<byte>(512);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("Status", ReplyStatus);
                w.WriteString("TimeReceived", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                w.WriteBoolean("isControllerConnected", first != null);
                w.WriteNumber("BatteryLevel", first?.BatteryLevel ?? 0);
                w.WriteStartArray("Devices");
                if (devices != null)
                {
                    foreach (var d in devices)
                    {
                        if (d == null) continue;
                        w.WriteStartObject();
                        w.WriteNumber("Index", d.Index);
                        w.WriteString("MacAddress", d.MacAddress ?? "");
                        w.WriteNumber("DeviceType", d.DeviceType);
                        w.WriteNumber("ConnectionType", d.ConnectionType);
                        w.WriteNumber("BatteryLevel", d.BatteryLevel);
                        w.WriteBoolean("IsSupportAT", d.IsSupportAT);
                        w.WriteBoolean("IsSupportLightBar", d.IsSupportLightBar);
                        w.WriteBoolean("IsSupportPlayerLED", d.IsSupportPlayerLED);
                        w.WriteBoolean("IsSupportMicLED", d.IsSupportMicLED);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
    }
}
