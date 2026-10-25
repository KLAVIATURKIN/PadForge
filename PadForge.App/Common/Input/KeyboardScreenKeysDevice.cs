using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Resources.Strings;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The keys beside the keyboard screens as one mappable device row (issue
    /// #519): the four keys under a Logitech monochrome screen and its round
    /// applet key, and the G19's display keys. <see cref="LogitechGKeysDevice"/>'s
    /// shape: a synthetic <see cref="ISdlInputDevice"/> that binds through the
    /// grid every other device uses. The engines report what they read into
    /// <see cref="KeyboardScreenKeys"/>, which holds a press for at least
    /// <see cref="KeyboardScreenKeys.PulseMs"/>.
    /// </summary>
    internal sealed class KeyboardScreenKeysDevice : ISdlInputDevice
    {
        // "KS" and "KK", the synthetic identity convention the handheld row set.
        private const ushort ScreenKeysVendorId = 0x4B53;
        private const ushort ScreenKeysProductId = 0x4B4B;

        private volatile bool _attached;
        private volatile bool _disposed;

        /// <summary>The open row, for the status line. Null while the feature
        /// is off, the G-key row's Active pattern.</summary>
        public static KeyboardScreenKeysDevice Active { get; private set; }

        public KeyboardScreenKeysDevice()
        {
            Name = "Keyboard Screen Keys";
            DevicePath = "keyboardscreenkeys://local";
            InstanceGuid = Md5Guid("pfkeyboardscreenkeys");
            ProductGuid = Md5Guid("pfkeyboardscreenkeys-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => KeyboardScreenFormats.KeyCount;
        public int RawButtonCount => KeyboardScreenFormats.KeyCount;
        public int NumHats => 0;
        /// <summary>Null: every index below the count is a key some screen
        /// has, and which ones a PC has depends on its keyboards.</summary>
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
        public ushort VendorId => ScreenKeysVendorId;
        public ushort ProductId => ScreenKeysProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.KeyboardScreenKeys;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>The 13 keys in the row's fixed order.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[KeyboardScreenFormats.KeyCount];
            for (int i = 0; i < items.Length; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = KeyName(i),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = i,
                };
            return items;
        }

        /// <summary>A key's name as the row lists it.</summary>
        internal static string KeyName(int button)
        {
            var s = Strings.Instance;
            return button switch
            {
                >= KeyboardScreenFormats.MonoKey1 and < KeyboardScreenFormats.MonoAppletKey => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture, s.KeyboardScreenKey_Mono_Format, button - KeyboardScreenFormats.MonoKey1 + 1),
                KeyboardScreenFormats.MonoAppletKey => s.KeyboardScreenKey_Applet,
                KeyboardScreenFormats.G19Left => s.KeyboardScreenKey_G19Left,
                KeyboardScreenFormats.G19Right => s.KeyboardScreenKey_G19Right,
                KeyboardScreenFormats.G19Ok => s.KeyboardScreenKey_G19Ok,
                KeyboardScreenFormats.G19Cancel => s.KeyboardScreenKey_G19Cancel,
                KeyboardScreenFormats.G19Up => s.KeyboardScreenKey_G19Up,
                KeyboardScreenFormats.G19Down => s.KeyboardScreenKey_G19Down,
                KeyboardScreenFormats.G19Menu => s.KeyboardScreenKey_G19Menu,
                KeyboardScreenFormats.G19Settings => s.KeyboardScreenKey_G19Settings,
                _ => string.Format(System.Globalization.CultureInfo.CurrentCulture, "Button {0}", button + 1),
            };
        }

        // ─── Lifecycle ───

        public bool Open()
        {
            if (_disposed) return false;
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
        }

        // ─── State read (poll thread) ───

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            var s = _statePool.Next();
            KeyboardScreenKeys.Read(s.Buttons, Environment.TickCount64);
            return s;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
