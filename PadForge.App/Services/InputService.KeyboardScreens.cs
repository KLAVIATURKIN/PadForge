using System;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;

namespace PadForge.Services
{
    public partial class InputService
    {
        private KeyboardScreenService _keyboardScreens;
        private string _keyboardScreensStatusLast;

        /// <summary>The keyboard screens (#519), made on first use.</summary>
        internal KeyboardScreenService KeyboardScreens
            => _keyboardScreens ??= new KeyboardScreenService(_mainVm.Settings,
                () => _settingsService?.MarkDirty(),
                m => PadForge.Engine.SdlDiagLog.WriteLine(m));

        /// <summary>Every UI tick while the engine runs, ungated by focus: a
        /// clock or a show on a keyboard screen keeps running while PadForge
        /// sits behind a game, the Dreamcast screens' rule. The service holds
        /// itself to four passes a second. With the feature off, a service
        /// that ran is reset, which closes every screen's session.</summary>
        private void TickKeyboardScreens()
        {
            if (!_mainVm.Settings.KeyboardScreensEnabled)
            {
                if (_keyboardScreens?.Running == true) _keyboardScreens.Reset();
                else KeyboardScreenService.DropRequests();
                return;
            }
            try { KeyboardScreens.Tick(); }
            catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: tick failed: " + ex.Message); }
        }

        /// <summary>The switch went off: the screens' sessions close, and a
        /// G19 screen interface PadForge bound to WinUSB goes back to Windows.
        /// The restore takes seconds, so it runs off the UI thread.</summary>
        internal void KeyboardScreensSwitched(bool on)
        {
            if (on) return;
            _keyboardScreens?.Reset();
            KeyboardScreenService.DropRequests();
            Task.Run(() =>
            {
                try { G19UsbScreen.Restore(m => PadForge.Engine.SdlDiagLog.WriteLine(m)); }
                catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Keyboard screens: G19 restore failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// The keyboard screens' lines under the Settings switch (#519): one
        /// per kind, naming what draws it or why nothing does. Empty while
        /// the feature is off or the engine is stopped, which collapses the
        /// lines, the Bliss-Box shape.
        /// </summary>
        private void UpdateKeyboardScreensStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            if (im != null && im.IsRunning && settings.KeyboardScreensEnabled && _keyboardScreens?.Running == true)
                status = string.Join(Environment.NewLine,
                    KeyboardScreenFormats.Kinds.Select(kind => _keyboardScreens.StatusLine(kind)));
            if (string.Equals(_keyboardScreensStatusLast, status, StringComparison.Ordinal)) return;
            _keyboardScreensStatusLast = status;
            settings.KeyboardScreensStatus = status;
        }
    }
}
