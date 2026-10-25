using System;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // The keys beside the keyboard screens (issue #519). The row reads a
        // static the screen engines fill and opens without any I/O, so the
        // poll thread runs its whole lifecycle, the G-key row's shape.
        private volatile KeyboardScreenKeysDevice _keyboardScreenKeysDevice;
        private readonly object _keyboardScreenKeysLock = new object();
        private volatile bool _keyboardScreenKeysInputsSuppressed;
        private int _keyboardScreenKeysVersion;

        /// <summary>The live Keyboard Screen Keys row, null while the feature
        /// is off.</summary>
        internal KeyboardScreenKeysDevice KeyboardScreenKeysRow => _keyboardScreenKeysDevice;

        /// <summary>Phase 1j2 (issue #519). One row while the feature is on,
        /// recreated when the user removes it from the Devices page, the
        /// G-key row's rule. Off with nothing to retire: two volatile reads
        /// and out.</summary>
        private bool UpdateKeyboardScreenKeysDevice()
        {
            if (_keyboardScreenKeysInputsSuppressed)
                return false;

            bool enabled = KeyboardScreensRuntime.Enabled;
            if (!enabled && _keyboardScreenKeysDevice == null)
                return false;

            bool changed = false;
            lock (_keyboardScreenKeysLock)
            {
                if (_keyboardScreenKeysInputsSuppressed)
                    return false;

                var dev = _keyboardScreenKeysDevice;
                if (dev != null)
                {
                    bool removedByUser = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid) == null;
                    bool reconfigured = _keyboardScreenKeysVersion != KeyboardScreensRuntime.Version;
                    if (!enabled || removedByUser || reconfigured)
                    {
                        RetireKeyboardScreenKeysRow();
                        MarkChanged(ref changed, "screenkeys", !enabled ? "- (feature off)"
                            : removedByUser ? "- (removed by user)" : "- (reconfigured)");
                    }
                }

                if (enabled && _keyboardScreenKeysDevice == null)
                {
                    try
                    {
                        var created = new KeyboardScreenKeysDevice();
                        if (created.Open())
                        {
                            UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                            ud.LoadFromExternalDevice(created);
                            ud.IsOnline = true;
                            _keyboardScreenKeysDevice = created;
                            _keyboardScreenKeysVersion = KeyboardScreensRuntime.Version;
                            MarkChanged(ref changed, "screenkeys", "+");
                        }
                        else created.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Error opening the Keyboard Screen Keys device", ex);
                    }
                }
            }
            return changed;
        }

        // Caller holds _keyboardScreenKeysLock.
        private void RetireKeyboardScreenKeysRow()
        {
            var dev = _keyboardScreenKeysDevice;
            if (dev == null) return;
            var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            dev.Dispose();
            _keyboardScreenKeysDevice = null;
        }

        public void ShutdownKeyboardScreenKeysInputs()
        {
            _keyboardScreenKeysInputsSuppressed = true;
            lock (_keyboardScreenKeysLock)
            {
                RetireKeyboardScreenKeysRow();
            }
        }
    }
}
