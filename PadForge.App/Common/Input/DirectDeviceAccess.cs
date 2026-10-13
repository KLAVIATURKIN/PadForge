using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Whether this process may open a device through the writers that make
    /// their own handles: the PlayStation effect writer, the haptic tone,
    /// Wii speaker and audio passthrough sinks, the Xbox impulse and guide
    /// LED writers, the DualShock 3 and PS Move direct services, the Sony
    /// headset's motion collection, the analog keyboard channel, the NFC
    /// reader and SDL's own device layer. Always yes in the app, where
    /// nothing sets the flag.
    ///
    /// <para>The test host says no for its whole life (DeviceOutputOffLimits
    /// in PadForge.Tests). A test hands a writer a file or a named pipe of
    /// its own, and those still open: only a path in the device namespace is
    /// refused, the kind a HID interface, a WinUSB interface or a driver's
    /// control device has. So a test, or a build of the code with one of its
    /// checks removed, that reaches a writer with the path of a device on
    /// the PC it runs on gets a failed open in place of a write.</para>
    /// </summary>
    internal static class DirectDeviceAccess
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every open is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>True in the test host for a path that names a
        /// device.</summary>
        internal static bool Refuses(string path) => OffLimitsForTest && IsDevicePath(path);

        /// <summary>True in the test host, for the device layers that take
        /// no path: SDL's, which opens every controller it lists, and the
        /// smart card service's readers.</summary>
        internal static bool RefusesAll => OffLimitsForTest;

        /// <summary>Whether a path is in the device namespace and names
        /// neither a named pipe nor a file by its full path: \\?\hid#...,
        /// \\?\usb#..., \\.\XboxGIP and the like.</summary>
        internal static bool IsDevicePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 5) return false;
            if (!path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return false;
            string rest = path.Substring(4);
            if (rest.StartsWith(@"pipe\", StringComparison.OrdinalIgnoreCase)) return false;
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return false;
            // A drive letter: a file by its full path.
            if (rest.Length >= 2 && rest[1] == ':' && char.IsAsciiLetter(rest[0])) return false;
            return true;
        }
    }
}
