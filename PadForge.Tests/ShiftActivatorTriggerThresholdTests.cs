using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #443: an "Axis Past Threshold" shift activator on a gamepad
    /// trigger engaged the moment it was saved. A gamepad trigger rests at 0
    /// on the shared axis array (SdlDeviceWrapper fills Axis 2 and Axis 5 as
    /// 0..65535, released = 0), and the activator read it on the bipolar
    /// stick scale, where 0 raw is -1.0, so |axis| was 1.0 at rest and past
    /// every threshold. Trigger-class sources now read on the trigger scale
    /// (0 at rest, 1 fully pulled). Centered axes keep the bipolar test.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ShiftActivatorTriggerThresholdTests : IDisposable
    {
        private const int Slot = 3;
        private static readonly Guid PadGuid = new("a4430001-0000-4000-8000-000000000001");
        private static readonly Guid StickGuid = new("a4430002-0000-4000-8000-000000000002");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();

        private readonly CustomInputState _padState = RestState();
        private readonly CustomInputState _stickState = CenteredState();
        private readonly MappingSet _set = new();

        public ShiftActivatorTriggerThresholdTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            SettingsManager.SlotMappingSets[Slot] = _set;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            AddDevice(PadGuid, InputDeviceType.Gamepad, _padState);
            AddDevice(StickGuid, InputDeviceType.Joystick, _stickState);
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
        }

        /// <summary>A gamepad at rest: sticks centered, triggers released.</summary>
        private static CustomInputState RestState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            return state;
        }

        /// <summary>A joystick at rest: every axis centered, sliders released.</summary>
        private static CustomInputState CenteredState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            Array.Fill(state.Povs, -1);
            return state;
        }

        private static void AddDevice(Guid id, int capType, CustomInputState state)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = capType, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        private Gamepad Pass(CustomInputState state, Guid device)
        {
            Assert.NotNull(BeginFrame);
            Assert.NotNull(Apply);
            BeginFrame.Invoke(null, null);
            object[] args = { state, _set, device.ToString(), 50, Slot, new Gamepad() };
            Apply.Invoke(null, args);
            return (Gamepad)args[5];
        }

        /// <summary>The reporter's layer: Axis Past Threshold on the left
        /// trigger at 0.50, Hold, plus one row on that layer so engagement
        /// is visible on the output.</summary>
        private void ArmTriggerLayer(Guid device, string descriptor, CustomInputState state)
        {
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = device.ToString(), Descriptor = descriptor, Kind = "Axis",
                AxisThreshold = 0.5, Mode = "Hold", LayerMask = "Shift", LayerName = "Shift",
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "ButtonB", LayerMask = "Shift",
                Sources = new[] { new MappingSource { DeviceGuid = device.ToString(), Descriptor = "Button 1" } }.ToList(),
            });
            state.Buttons[1] = true; // the layer's row would fire the moment the layer engages
        }

        [Fact]
        public void TriggerLayerRestsReleasedAndEngagesPastHalfPull()
        {
            ArmTriggerLayer(PadGuid, "Gamepad LeftTrigger", _padState);

            // Saved and untouched: the layer must not be active.
            Assert.False(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));

            _padState.Axis[2] = 26214; // 40 % pull, under the 0.50 threshold
            Assert.False(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));

            _padState.Axis[2] = 39321; // 60 % pull
            Assert.True(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));

            _padState.Axis[2] = 65535; // full pull stays engaged (the old read released here)
            Assert.True(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));

            _padState.Axis[2] = 0; // released: Hold lets go
            Assert.False(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void TheCanonicalTriggerAxisOnAGamepadReadsTheSameWay()
        {
            ArmTriggerLayer(PadGuid, "Axis 5", _padState);
            Assert.False(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));
            _padState.Axis[5] = 65535;
            Assert.True(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void ACenteredJoystickAxisKeepsTheBipolarTest()
        {
            // Axis 2 on a joystick is a centered axis, not a trigger: rest is
            // the middle and either direction past half deflection engages.
            ArmTriggerLayer(StickGuid, "Axis 2", _stickState);
            Assert.False(Pass(_stickState, StickGuid).IsButtonPressed(Gamepad.B));
            _stickState.Axis[2] = 65535;
            Assert.True(Pass(_stickState, StickGuid).IsButtonPressed(Gamepad.B));
            _stickState.Axis[2] = 0;
            Assert.True(Pass(_stickState, StickGuid).IsButtonPressed(Gamepad.B));
        }

        [Fact]
        public void ASliderRestsReleasedLikeATrigger()
        {
            ArmTriggerLayer(StickGuid, "Slider 0", _stickState);
            Assert.False(Pass(_stickState, StickGuid).IsButtonPressed(Gamepad.B));
            _stickState.Sliders[0] = 65535;
            Assert.True(Pass(_stickState, StickGuid).IsButtonPressed(Gamepad.B));
        }
    }
}
