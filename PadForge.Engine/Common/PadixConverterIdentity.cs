using System.Collections.Generic;

namespace PadForge.Engine
{
    /// <summary>
    /// Single source of truth for "is this a Padix PSX/USB converter whose
    /// motors take the 9-byte output report PadForge writes itself."
    /// Used by <see cref="SdlDeviceWrapper"/> to force-enable
    /// <c>HasRumble</c> on these devices (SDL only reports rumble for them
    /// when Buffalo's DirectInput effect plug-in is installed, and PadForge
    /// never uses that plug-in), and by
    /// <c>PadForge.Common.Input.PadixConverterRawHidWriter</c> to gate the
    /// raw HID write path.
    ///
    /// Padix builds the converter boards that Buffalo, Elecom and others
    /// sell under their own names. Only the two Buffalo models whose vendor
    /// effect plug-in (RFVib_C2.dll) was read for the wire format are listed.
    /// The BGC-UPS103 (0xB00A) and BGC-UPS203 (0xB00C) use a different
    /// plug-in with a 4-byte report and are deliberately absent (#440).
    /// </summary>
    public static class PadixConverterIdentity
    {
        public const ushort PadixVid = 0x0583;

        private static readonly HashSet<ushort> PlayStationConverterPids = new()
        {
            0xB047, // Buffalo BSGC101 (one PlayStation port)
            0xB048, // Buffalo BSGC201 (two PlayStation ports)
        };

        public static bool IsPlayStationConverter(ushort vid, ushort pid)
            => vid == PadixVid && PlayStationConverterPids.Contains(pid);
    }
}
