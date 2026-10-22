using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Names Flydigi's Space Station processes when they run (discussion
    /// #395). A Vader 5 Pro whose owner had whitelisted
    /// <c>SpaceStationService.exe</c> in HidHide re-enumerated at the USB
    /// level every few seconds to every few minutes while PadForge ran, and
    /// stopped the moment the service was stopped or lost sight of the pad.
    /// SDL's Flydigi driver and the service both drive the controller's
    /// vendor interface (Get Info, Get Status and the Acquire heartbeat on
    /// SDL's side), and the service is the party PadForge cannot see, so
    /// the Devices page names it while it runs and the arrival log records
    /// whether it could reach the pad. Modeled on
    /// <see cref="HandheldDaemonWatch"/>: process names as Flydigi ships
    /// them, refreshed by a worker the device sweep starts, never on the
    /// polling thread (InputManager.FlydigiWatchTick says why).
    /// </summary>
    internal static class FlydigiServiceWatch
    {
        // Process image names without .exe, as installed by
        // FlydigiSpaceStation_setup (C:\Program Files\Flydigi Space Station\).
        private static readonly string[] Names =
        {
            "SpaceStationService",   // the background service, as the user's 4.x-era install names it
            "GameControllerService", // the same service in the 3.4.x installer (Service\GameControllerService.exe)
            "Flydigi Space Station", // the desktop app
        };

        private static volatile string _running = string.Empty;
        private static volatile string _detail = "none";

        /// <summary>Comma-joined image names of the Flydigi processes
        /// currently running, empty when none.</summary>
        public static string Running => _running;

        /// <summary>Log detail: each running process with its pid, file
        /// version and path, or "none". Built once per refresh so the arrival
        /// log never walks the process list itself.</summary>
        public static string Detail => _detail;

        /// <summary>Flydigi controllers as SDL knows them
        /// (SDL_IsJoystickFlydigiController): every 37D7 product, plus the
        /// first-generation 04B4:2412 gamepad on a Cypress vendor id.</summary>
        public static bool IsFlydigiDevice(ushort vendorId, ushort productId)
            => vendorId == 0x37D7 || (vendorId == 0x04B4 && productId == 0x2412);

        /// <summary>Re-scans the process list. Worker thread only. Returns
        /// whether <see cref="Running"/> changed, so the device sweep can
        /// republish rows when the service starts or stops between arrivals.
        /// Every matching pid lands in <see cref="Detail"/>. A failed scan
        /// keeps the last names and says so in the detail.</summary>
        public static bool Refresh()
        {
            string before = _running;
            try
            {
                var found = new List<string>();
                var details = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var running = RunningProcessList.Read();
                if (running == null)
                {
                    _detail = "scan-failed";
                    return false;
                }
                foreach (var p in running)
                {
                    string n = p.Name;
                    string canonical = null;
                    foreach (var name in Names)
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { canonical = name; break; }
                    if (canonical == null) continue;
                    // The canonical spelling from Names, so two spellings of one
                    // image cannot make Running differ between scans.
                    if (seen.Add(canonical)) found.Add(canonical);
                    details.Add(Describe(p, canonical));
                }
                found.Sort(StringComparer.OrdinalIgnoreCase);
                _running = string.Join(", ", found);
                _detail = details.Count == 0 ? "none" : string.Join(" | ", details);
            }
            catch (Exception ex)
            {
                _detail = "scan-failed " + ex.GetType().Name;
            }
            return !string.Equals(before, _running, StringComparison.Ordinal);
        }

        /// <summary>The one FLYDIGI arrival record, built here so a test reads
        /// the emitted line rather than the production source. The caller
        /// passes the service detail it read, so the line is one snapshot
        /// and nothing reads the static twice around a parallel scan. The
        /// HidHide snapshot is a configuration read, not proof of an open
        /// handle.</summary>
        public static string DescribeArrival(ushort vendorId, ushort productId, uint sdlInstanceId,
            string backend, string devicePath, string serviceDetail, string hidHideSnapshot)
            => $"FLYDIGI arrival {vendorId:X4}:{productId:X4} sdl={sdlInstanceId} backend={backend} path={devicePath} service=[{serviceDetail}] {hidHideSnapshot}";

        private static string Describe(RunningProcessList.Entry p, string name)
        {
            // The path comes from the limited query that named the process,
            // which a SYSTEM service answers. The version is read from the
            // file. The name and pid always print.
            string path = p.Path ?? "", version = "";
            try
            {
                if (path.Length > 0) version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
            }
            catch { }
            return path.Length > 0
                ? $"{name}.exe pid={p.Id} v={version} path={path}"
                : $"{name}.exe pid={p.Id}";
        }
    }
}
