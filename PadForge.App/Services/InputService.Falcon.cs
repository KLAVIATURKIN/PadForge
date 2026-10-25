using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Falcon;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        /// <summary>How long a Falcon's line asks for homing after it opens.
        /// Force Dimension's library names no status that says a Falcon is
        /// homed, so the line asks for a while and then gives the rate.</summary>
        private const int FalconHomingHintMs = 20000;

        private string _falconStatusLast;

        /// <summary>The Falcon section of a device's settings into the pad's
        /// view model (#521), the wheel fields' neighbor.</summary>
        internal static void LoadFalconSettings(PadViewModel padVm, PadSetting ps)
        {
            static int Int(string s, int fallback)
                => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
            padVm.FalconFeel = Int(ps.FalconFeel, 0);
            padVm.FalconSpring = Int(ps.FalconSpring, 100);
            padVm.FalconKickButton = Int(ps.FalconKickButton, 1);
            padVm.FalconKick = Int(ps.FalconKick, 100);
            padVm.FalconShake = Int(ps.FalconShake, 100);
            padVm.FalconBraceButton = Int(ps.FalconBraceButton, 0);
            padVm.FalconPreloadButton = Int(ps.FalconPreloadButton, 0);
            padVm.FalconGravity = Int(ps.FalconGravity, 100);
            padVm.FalconForceCap = Int(ps.FalconForceCap, 8);
        }

        /// <summary>The pad's Falcon section back into a device's settings.</summary>
        internal static void SaveFalconSettings(PadViewModel padVm, PadSetting ps)
        {
            var ic = CultureInfo.InvariantCulture;
            ps.FalconFeel = padVm.FalconFeel.ToString(ic);
            ps.FalconSpring = padVm.FalconSpring.ToString(ic);
            ps.FalconKickButton = padVm.FalconKickButton.ToString(ic);
            ps.FalconKick = padVm.FalconKick.ToString(ic);
            ps.FalconShake = padVm.FalconShake.ToString(ic);
            ps.FalconBraceButton = padVm.FalconBraceButton.ToString(ic);
            ps.FalconPreloadButton = padVm.FalconPreloadButton.ToString(ic);
            ps.FalconGravity = padVm.FalconGravity.ToString(ic);
            ps.FalconForceCap = padVm.FalconForceCap.ToString(ic);
        }

        /// <summary>The open Falcon with this row's identity, or null.</summary>
        internal FalconDevice FindFalcon(Guid instanceGuid)
            => _inputManager?.FalconRows.FirstOrDefault(f => f.InstanceGuid == instanceGuid);

        /// <summary>The Settings card's lines (#521): the library, then each
        /// open Falcon, or why none opened. Empty while the switch is off or
        /// the engine is stopped.</summary>
        private void UpdateFalconStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            bool canInstall = false;
            if (im != null && im.IsRunning && settings.NovintFalconEnabled)
            {
                var s = Strings.Instance;
                var culture = CultureInfo.CurrentCulture;
                var sweep = FalconRuntime.Status;
                var lines = new List<string>();
                switch (sweep.LibraryReason)
                {
                    case "":
                        lines.Add(string.Format(culture, s.Falcon_Status_Library_Format,
                            string.IsNullOrEmpty(sweep.SdkVersion) ? "?" : sweep.SdkVersion, sweep.LibraryPath));
                        break;
                    case "platform":
                        lines.Add(s.Falcon_Status_Arm64);
                        break;
                    case "load":
                        lines.Add(s.Falcon_Status_LoadFailed);
                        break;
                    case "exports":
                        lines.Add(s.Falcon_Status_NotLibrary);
                        break;
                    default:
                        lines.Add(s.Falcon_Status_NoSdk);
                        break;
                }

                var rows = im.FalconRows;
                long now = Environment.TickCount64;
                foreach (var falcon in rows.OrderBy(f => f.Index))
                {
                    string name = falcon.Serial.HasValue
                        ? string.Format(culture, s.Falcon_Name_Format, falcon.Serial.Value)
                        : string.Format(culture, s.Falcon_Name_Format, falcon.Index + 1);
                    string state = now - falcon.OpenedAtTicks < FalconHomingHintMs ? s.Falcon_Status_Home
                        : falcon.RateKhz > 0 ? string.Format(culture, s.Falcon_Status_Rate_Format, falcon.RateKhz)
                        : s.Falcon_Status_Open;
                    lines.Add(string.Format(culture, s.Falcon_Status_Line_Format, name, state));
                }
                // The Install button: a Falcon with no driver, and an SDK
                // folder that holds Force Dimension's package, which the
                // sweep found on its worker.
                canInstall = rows.Count == 0 && sweep.HasDriverlessNode && sweep.OtherDrivers.Length == 0
                    && sweep.DriverPackage != null;
                if (rows.Count == 0 && sweep.LibraryReason.Length == 0)
                {
                    if (sweep.InUse > 0) lines.Add(s.Falcon_Status_InUse);
                    else if (sweep.OtherDrivers.Length > 0)
                        lines.Add(string.Format(culture, s.Falcon_Status_OtherDriver_Format, sweep.OtherDrivers));
                    else if (sweep.HasDriverlessNode)
                        lines.Add(canInstall ? s.Falcon_Status_NoDriver : s.Falcon_Status_NoDriverNoPackage);
                    else lines.Add(s.Falcon_Status_NotFound);
                }
                status = string.Join(Environment.NewLine, lines);
            }
            settings.FalconCanInstallDriver = canInstall;
            if (string.Equals(_falconStatusLast, status, StringComparison.Ordinal)) return;
            _falconStatusLast = status;
            settings.FalconStatus = status;
        }

        /// <summary>The Calibrate row's line for the selected device: the
        /// reach it uses, in millimeters.</summary>
        internal static string FalconReachLine(FalconReach reach)
            => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Falcon_Reach_Format,
                reach.Y * 1000.0, reach.Z * 1000.0, reach.X * 1000.0);
    }
}
