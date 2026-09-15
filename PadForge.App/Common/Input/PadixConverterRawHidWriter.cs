using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes rumble to a Padix PSX/USB converter (Buffalo BSGC101 / BSGC201)
    /// through the converter's 9-byte HID output report, bypassing SDL and the
    /// vendor's DirectInput effect plug-in. The PS1 or PS2 pad plugged into the
    /// converter has a big motor in the left grip (amplitude) and a small motor
    /// in the right grip (on or off), and the converter exposes exactly those
    /// two controls.
    ///
    /// <para>Wire format read from the vendor effect plug-in that ships in
    /// Buffalo's bsgc101_201-110.exe (RFVib_C264.dll 8.13.4.8, "Rockfire
    /// Vibration Driver"), the only public implementation that drives these
    /// motors. The plug-in opens the HID interface path from
    /// DIPROP_GUIDANDPATH and calls WriteFile with nine bytes:</para>
    /// <code>
    /// byte 0   0x00                report ID
    /// byte 1   0 or 1              right (small) motor off / on
    /// byte 2   0, or 0x7F + (L/2)  left (big) motor, L = 1..127
    /// byte 3-8 0x00
    /// </code>
    /// <para>The motor sides come from the plug-in's direction handling: a
    /// DirectInput force aimed left drives byte 2 only, one aimed right drives
    /// byte 1 only, and one with no direction drives both. Buffalo's control
    /// panel "left / both / right" buttons are those three cases. The level
    /// byte is the plug-in's encoding of a signed 8-bit force (127 at full
    /// magnitude), and positive levels 1..127 encode as 0x7F..0xBE, monotonic
    /// in level. Bytes are hardware facts, the code is original.</para>
    ///
    /// <para>Motor mapping follows SDL's DualShock 3 driver
    /// (<c>SDL_hidapi_ps3.c</c>, <c>effects[2] = rumble_right ? 1 : 0</c> with
    /// <c>rumble_right = high_frequency_rumble >> 8</c>): the low-frequency
    /// channel scales to the big motor's level, the high-frequency channel
    /// turns the small motor on whenever its high byte is nonzero.</para>
    ///
    /// <para>Uses the shared <see cref="RawHidOutput"/> write path, which
    /// keeps the handle open, pads the report to the collection's
    /// OutputReportByteLength, and is reset on unplug.</para>
    /// </summary>
    internal static class PadixConverterRawHidWriter
    {
        public const int ReportLength = 9;

        /// <summary>The plug-in's zero point for the big-motor level byte.</summary>
        internal const byte LevelBase = 0x7F;

        /// <summary>Full big-motor level, the plug-in's scale of a full-magnitude force.</summary>
        internal const int MaxLevel = 127;

        public static bool IsPlayStationConverter(ushort vid, ushort pid)
            => PadixConverterIdentity.IsPlayStationConverter(vid, pid);

        /// <summary>Builds the 9-byte motor report. <paramref name="left16"/> is
        /// PadForge's low-frequency motor (0..65535), <paramref name="right16"/>
        /// the high-frequency motor. Both zero gives the all-zero report the
        /// plug-in itself sends to stop.</summary>
        public static byte[] BuildReport(ushort left16, ushort right16)
        {
            var buf = new byte[ReportLength];
            buf[0] = 0x00;
            buf[1] = (byte)((right16 >> 8) != 0 ? 1 : 0);
            int level = left16 >> 9; // 0..127, the plug-in's force scale
            buf[2] = level == 0 ? (byte)0 : (byte)(LevelBase + (level >> 1));
            return buf;
        }

        /// <summary>Writes the two motor magnitudes (PadForge's 0..65535 range)
        /// to the converter at <paramref name="devicePath"/>, the HID interface
        /// path SDL reports for the device.</summary>
        public static bool Write(string devicePath, ushort left16, ushort right16)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            return RawHidOutput.Write(devicePath, BuildReport(left16, right16));
        }
    }
}
