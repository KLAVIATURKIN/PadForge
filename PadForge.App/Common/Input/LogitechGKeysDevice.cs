using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Logitech;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Logitech G-keys and extra mouse buttons as one mappable device row
    /// (issue #454, asked for in discussion #449).
    ///
    /// <para><see cref="HandheldButtonsDevice"/>'s shape, for the same reason:
    /// extra hardware buttons that are not a gamepad, surfaced as a synthetic
    /// <see cref="ISdlInputDevice"/> so they bind through the grid every other
    /// device already uses. Nothing about mapping or macros is new here.</para>
    ///
    /// <para>Without this, a G-key has to be programmed in the Logitech
    /// software to send some real key, which consumes a keycode, fires in
    /// every application, and puts two remappers in series.</para>
    ///
    /// <para>Events arrive on the SDK's thread. A press asserts its button for
    /// at least <see cref="PulseMs"/>, because a tap can begin and end between
    /// two polls and a macro still has to see the edge. That is the handheld
    /// row's rule and it is here for the same reason.</para>
    /// </summary>
    internal sealed class LogitechGKeysDevice : ISdlInputDevice
    {
        // "LG" and "GK", the synthetic identity convention the handheld row set.
        private const ushort LogitechVendorId = 0x4C47;
        private const ushort GKeysProductId = 0x474B;

        /// <summary>How long a press is held asserted. A G-key tap can be
        /// shorter than one poll, and a dropped edge is a macro that never
        /// fires.</summary>
        public const int PulseMs = 175;

        private readonly object _stateLock = new();
        private readonly bool[] _down = new bool[LogitechGKeyMap.TotalCount];
        private readonly long[] _pulseUntil = new long[LogitechGKeyMap.TotalCount];

        private LogitechGKeySource _source;
        private volatile bool _attached;
        private volatile bool _disposed;

        /// <summary>The open row, for the status line. Null while the feature
        /// is off, the NFC and handheld rows' Active pattern.</summary>
        public static LogitechGKeysDevice Active { get; private set; }

        public LogitechGKeysDevice()
        {
            Name = "Logitech G-Keys";
            DevicePath = "logigkeys://local";
            InstanceGuid = Md5Guid("pflogigkeys");
            ProductGuid = Md5Guid("pflogigkeys-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => LogitechGKeyMap.TotalCount;
        public int RawButtonCount => LogitechGKeyMap.TotalCount;
        public int NumHats => 0;
        /// <summary>Null, not an empty array: every index below the count is a
        /// real button the SDK can report. The SDK has no way to ask how many
        /// G-keys a given keyboard has, so gating here would be a guess.</summary>
        public int[] SupportedButtonIndices => null;
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => LogitechVendorId;
        public ushort ProductId => GKeysProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.LogitechGKeys;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>What the source is doing, for the status line.</summary>
        public LogitechGKeyState SourceState => _source?.State ?? LogitechGKeyState.Stopped;

        /// <summary>How many events have arrived, which is how a user tells
        /// "connected" from "connected and hearing the keys".</summary>
        public long EventCount => _source?.EventCount ?? 0;

        /// <summary>
        /// Every button the SDK can report, named by the SDK where it gives a
        /// name.
        ///
        /// <para>All 29 keys are listed even on a keyboard with six, because
        /// the SDK cannot be asked how many a device has. Finding the right
        /// one is what the Record button is for: press the key and it binds.</para>
        /// </summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[LogitechGKeyMap.TotalCount];
            for (int i = 0; i < items.Length; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = NameFor(i),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = i,
                };
            return items;
        }

        private string NameFor(int button)
        {
            var src = _source;
            if (src != null && LogitechGKeyMap.Describe(button, out bool isMouse, out int keyOrButton, out int mode))
            {
                string sdkName = isMouse ? src.MouseName(keyOrButton) : src.KeyName(keyOrButton, mode);
                // The SDK's name has no mode in it, so M2 and M3 would arrive
                // as three rows all reading "G5". The mode is appended rather
                // than dropped.
                if (!string.IsNullOrWhiteSpace(sdkName))
                    return isMouse ? sdkName : sdkName + " (M" + mode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            }
            return LogitechGKeyMap.FallbackName(button);
        }

        // ─── Lifecycle ───

        public bool Open()
        {
            if (_disposed) return false;
            _source = new LogitechGKeySource(OnGkeyEvent, PadForge.Engine.SdlDiagLog.WriteLine);
            // The row attaches whether or not the SDK starts, so the Devices
            // list can carry the reason. A row that vanishes tells the user
            // nothing about why.
            _source.Start();
            _attached = true;
            Active = this;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            if (ReferenceEquals(Active, this)) Active = null;
            var src = _source;
            _source = null;
            try { src?.Dispose(); } catch (Exception) { }
        }

        /// <summary>An event from the SDK's thread. Nothing here may block:
        /// this call is on Logitech's dispatch path.</summary>
        private void OnGkeyEvent(LogitechGKeyInterop.GkeyCode code)
        {
            int button = LogitechGKeyMap.ButtonFor(code);
            if (button < 0) return;
            lock (_stateLock)
            {
                _down[button] = code.KeyDown;
                if (code.KeyDown) _pulseUntil[button] = Environment.TickCount64 + PulseMs;
            }
        }

        // ─── State read (poll thread) ───

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                long now = Environment.TickCount64;
                var s = _statePool.Next();
                int n = Math.Min(_down.Length, s.Buttons.Length);
                for (int b = 0; b < s.Buttons.Length; b++)
                {
                    bool pressed = b < n
                        && (_down[b] || (_pulseUntil[b] != 0 && now < _pulseUntil[b]));
                    if (!pressed && b < n) _pulseUntil[b] = 0;
                    // Written EVERY poll, pressed or not, so a released key
                    // produces its falling edge. The NFC lane's lesson.
                    s.Buttons[b] = pressed;
                }
                return s;
            }
        }

        /// <summary>Test seam: an event as the SDK would deliver it.</summary>
        internal void InjectForTest(int keyOrButton, int mode, bool down, bool mouse)
        {
            uint raw = (uint)(keyOrButton & 0xFF)
                     | (down ? 1u << 8 : 0u)
                     | ((uint)(mode & 3) << 9)
                     | (mouse ? 1u << 11 : 0u);
            OnGkeyEvent(new LogitechGKeyInterop.GkeyCode { Raw = raw });
        }

        /// <summary>Test seam: mark live without loading the SDK.</summary>
        internal void AttachForTest() => _attached = true;

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
