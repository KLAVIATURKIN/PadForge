using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  Game mods (#529)
        // ─────────────────────────────────────────────

        private GameModServer _gameModServer;
        private long _gameModLinesVersion = -1;
        private long _gameModLinesMs;

        /// <summary>Starts the server for DSX game mods when its switch is
        /// on and the engine runs, the motion server's rule.</summary>
        private void StartGameModServerIfEnabled()
        {
            if (!_mainVm.Dashboard.EnableGameModServer || _inputManager == null) return;
            if (_gameModServer != null) return;

            var server = new GameModServer();
            server.StatusChanged += (_, _) => _dispatcher.BeginInvoke(RefreshGameModStatus);
            _gameModServer = server;

            int port = _mainVm.Dashboard.GameModServerPort;
            if (port < 1024 || port > 65535) port = GameModServer.DefaultPort;
            if (!server.Start(port, _mainVm.Dashboard.GameModDsxStandIn))
            {
                // The status the failed start raised stays on the card: the
                // port in use or the bind that failed.
                _gameModServer = null;
                string failed = server.FormatStatus();
                server.Dispose();
                _dispatcher.BeginInvoke(() =>
                {
                    _mainVm.Dashboard.GameModServerStatus = failed;
                    _mainVm.Dashboard.IsGameModServerRunning = false;
                });
            }
        }

        private void StopGameModServer()
        {
            var server = _gameModServer;
            if (server == null) return;
            _gameModServer = null;
            server.Dispose();
            _dispatcher.BeginInvoke(() =>
            {
                _mainVm.Dashboard.GameModServerStatus = Strings.Instance.Common_Stopped;
                _mainVm.Dashboard.IsGameModServerRunning = false;
                _gameModLinesVersion = -1;
            });
        }

        /// <summary>The card's line and flame from the server's state.</summary>
        private void RefreshGameModStatus()
        {
            var server = _gameModServer;
            var dash = _mainVm.Dashboard;
            if (server == null)
            {
                dash.IsGameModServerRunning = false;
                return;
            }
            dash.GameModServerStatus = server.FormatStatus();
            dash.IsGameModServerRunning = server.State == GameModServer.ServerState.Listening;
        }

        /// <summary>The Dashboard line and the pad page lines, on the
        /// dashboard cadence. Rebuilt when what the mods hold changed, and
        /// once a second while they hold anything, since the virtual
        /// controller a mod's index names moves with the pads. One read
        /// when nothing moved.</summary>
        private void UpdateGameModLines()
        {
            long version = GameModEffects.Version;
            long now = Environment.TickCount64;
            bool held = GameModEffects.AnyHeld;
            if (version == _gameModLinesVersion && !(held && now - _gameModLinesMs >= 1000)) return;
            _gameModLinesVersion = version;
            _gameModLinesMs = now;

            if (_gameModServer != null) RefreshGameModStatus();
            int[] order = held ? GameModEffects.OrderedSlots() : Array.Empty<int>();
            foreach (var pad in _mainVm.Pads)
            {
                var hold = held ? GameModEffects.ForController(Array.IndexOf(order, pad.PadIndex)) : null;
                SetGameModPadLines(pad, hold);
            }
        }

        /// <summary>A slot's three pad page lines from what a mod holds on
        /// it: the Adaptive Triggers and Impulse Triggers tabs while it holds
        /// a trigger program, the Lighting tab while it holds a light.</summary>
        internal static void SetGameModPadLines(PadViewModel pad, GameModEffects.Hold hold)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string name = hold?.SenderName ?? s.GameMods_UnknownSender;
            bool triggers = hold != null && hold.HasTriggers;
            bool lights = hold != null && hold.HasLights;
            pad.GameModTriggersLine = triggers ? string.Format(culture, s.Pad_GameModTriggers_Format, name) : string.Empty;
            pad.GameModImpulseLine = triggers ? string.Format(culture, s.Pad_GameModImpulse_Format, name) : string.Empty;
            pad.GameModLightsLine = lights ? string.Format(culture, s.Pad_GameModLights_Format, name) : string.Empty;
        }

        /// <summary>The selected row's game mod line (#529), on the Devices
        /// page's tick, shown with Clear Mod Effects. A row no mod reaches
        /// shows neither.</summary>
        private void UpdateGameModDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            row.GameModLine = GameModDeviceLine(ud);
        }

        /// <summary>What the mods hold on the virtual controllers this device
        /// feeds, as this device carries it: a DualSense's triggers and
        /// lights, a DualShock 4's lightbar, or the vibrations an Xbox pad's
        /// impulse triggers play with Translate Adaptive Trigger Vibrations
        /// on. Empty when none of that reaches it.</summary>
        internal static string GameModDeviceLine(UserDevice ud)
        {
            if (ud == null || !GameModEffects.AnyHeld) return string.Empty;
            var settings = SettingsManager.UserSettings;
            if (settings == null) return string.Empty;

            var fed = new List<(int Slot, bool Impulse)>(1);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.InstanceGuid != ud.InstanceGuid || us.MapTo < 0) continue;
                    var ps = us.GetPadSetting();
                    fed.Add((us.MapTo, ps != null && ps.AtVibrationToImpulseEnabled == "1"));
                }
            }
            if (fed.Count == 0) return string.Empty;

            var order = GameModEffects.OrderedSlots();
            bool sony = GameModEffects.IsSonyPad(ud.VendorId, ud.ProdId);
            bool dualSense = GameModEffects.IsDualSense(ud.VendorId, ud.ProdId);
            bool impulse = !sony && ud.Device != null && ud.Device.HasRumbleTriggers;
            bool triggers = false, lights = false, impulseVibrations = false;
            string name = null;
            foreach (var (slot, translate) in fed)
            {
                var hold = GameModEffects.ForController(Array.IndexOf(order, slot));
                if (hold == null) continue;
                bool reached = false;
                if (dualSense)
                {
                    if (hold.HasTriggers) { triggers = true; reached = true; }
                    if (hold.HasLights) { lights = true; reached = true; }
                }
                else if (sony && hold.Lightbar != null)
                {
                    lights = true;
                    reached = true;
                }
                else if (impulse && translate && hold.HasTriggers)
                {
                    impulseVibrations = true;
                    reached = true;
                }
                if (reached) name ??= hold.SenderName;
            }

            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            name ??= s.GameMods_UnknownSender;
            if (triggers && lights) return string.Format(culture, s.Devices_GameModTriggersAndLights_Format, name);
            if (triggers) return string.Format(culture, s.Devices_GameModTriggers_Format, name);
            if (lights) return string.Format(culture, s.Devices_GameModLights_Format, name);
            if (impulseVibrations) return string.Format(culture, s.Devices_GameModImpulse_Format, name);
            return string.Empty;
        }
    }
}
