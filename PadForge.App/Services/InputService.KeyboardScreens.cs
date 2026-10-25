using System;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

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
        /// The keyboard screens' lines under the Settings switch (#519): only
        /// what belongs to the feature as a whole, the vendor libraries and no
        /// screen found. Each screen's own line is on its row's panel
        /// (<see cref="UpdateKeyboardScreenDeviceRow"/>). Empty while the
        /// feature is off or the engine is stopped, which collapses the lines,
        /// the Bliss-Box shape.
        /// </summary>
        private void UpdateKeyboardScreensStatus()
        {
            var settings = _mainVm.Settings;
            var im = _inputManager;
            string status = string.Empty;
            if (im != null && im.IsRunning && settings.KeyboardScreensEnabled && _keyboardScreens?.Running == true)
                status = _keyboardScreens.SubsystemLines();
            if (string.Equals(_keyboardScreensStatusLast, status, StringComparison.Ordinal)) return;
            _keyboardScreensStatusLast = status;
            settings.KeyboardScreensStatus = status;
        }

        /// <summary>
        /// The selected row's keyboard screen line and button (#519), on the
        /// Devices page's tick: what draws the screen or why nothing does,
        /// and the button that opens its dialog, the Dreamcast Screen
        /// button's place. A row that is not a present screen shows
        /// neither.
        /// </summary>
        private void UpdateKeyboardScreenDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            var screen = _mainVm.Settings.KeyboardScreensEnabled && _keyboardScreens?.Running == true
                         && ud.CapType == InputDeviceType.KeyboardScreenKeys
                ? _keyboardScreens.ScreenOf(ud.InstanceGuid) : null;
            row.KeyboardScreenLine = screen == null ? string.Empty : _keyboardScreens.StatusLine(screen);
            row.KeyboardScreenButtonText = screen == null ? string.Empty : KeyboardScreenService.ButtonText(screen.Kind);
            row.ShowKeyboardScreen = screen != null && ud.IsOnline;
        }
    }
}
