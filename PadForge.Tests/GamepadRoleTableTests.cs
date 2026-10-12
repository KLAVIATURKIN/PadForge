using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// What the automap binds onto the lettered wires, the two Switch pads and
    /// the three Valve pads, for three pads. Each expected line is what the
    /// automap bound before it read its roles from one table, so moving to
    /// the table changed no binding.
    /// </summary>
    public class GamepadRoleTableTests
    {
        private readonly ITestOutputHelper _output;

        public GamepadRoleTableTests(ITestOutputHelper output) => _output = output;

        private static DeviceObjectItem Obj(int index, DeviceObjectTypeFlags kind) => new()
        {
            InputIndex = index,
            ObjectType = kind,
        };

        /// <summary>A pad with every position of SDL's layout: six axes,
        /// buttons 0 to 17, a hat, two touchpads, a gyro and an
        /// accelerometer.</summary>
        private static UserDevice Full()
        {
            var objects = new List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++) objects.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
            for (int i = 0; i < 18; i++) objects.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
            objects.Add(Obj(0, DeviceObjectTypeFlags.PointOfViewController));
            return new UserDevice
            {
                CapType = InputDeviceType.Gamepad,
                DeviceObjects = objects.ToArray(),
                HasTouchpad = true,
                CapTouchpadCount = 2,
                HasGyro = true,
                HasAccel = true,
            };
        }

        /// <summary>A DualSense as PadForge sees it: six axes, buttons 0 to 11
        /// and the touchpad click at 16, a hat, one touchpad, a gyro and an
        /// accelerometer.</summary>
        private static UserDevice DualSense()
        {
            var objects = new List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++) objects.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
            for (int i = 0; i < 12; i++) objects.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
            objects.Add(Obj(16, DeviceObjectTypeFlags.PushButton));
            objects.Add(Obj(0, DeviceObjectTypeFlags.PointOfViewController));
            return new UserDevice
            {
                CapType = InputDeviceType.Gamepad,
                DeviceObjects = objects.ToArray(),
                HasTouchpad = true,
                CapTouchpadCount = 1,
                HasGyro = true,
                HasAccel = true,
            };
        }

        /// <summary>A gamepad that lists no objects, read as SDL's full
        /// layout without the extras.</summary>
        private static UserDevice Unlisted() => new() { CapType = InputDeviceType.Gamepad };

        private static readonly string[] Fields =
        {
            "TouchpadX1", "TouchpadY1", "TouchpadX2", "TouchpadY2",
            "TouchpadContact1", "TouchpadContact2", "TouchpadClick", "MotionGyro", "MotionAccel",
            "ButtonA", "ButtonB", "ButtonX", "ButtonY", "LeftShoulder", "RightShoulder", "ButtonBack",
            "ButtonStart", "ButtonGuide", "ButtonShare", "ButtonMute", "LeftThumbButton", "RightThumbButton",
            "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "LeftTrigger", "RightTrigger",
            "LeftThumbAxisX", "LeftThumbAxisY", "RightThumbAxisX", "RightThumbAxisY",
        };

        /// <summary>Every raw binding and every named field the automap set,
        /// in one line.</summary>
        private static string Dump(PadSetting ps)
        {
            var parts = new List<string>();
            foreach (var entry in (ps.RawMappingEntries ?? Array.Empty<RawMappingEntry>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.Value))
                .OrderBy(e => e.Key, StringComparer.Ordinal))
                parts.Add(entry.Key + "=" + entry.Value);
            foreach (string field in Fields)
            {
                string value = typeof(PadSetting).GetProperty(field)?.GetValue(ps) as string;
                if (!string.IsNullOrEmpty(value)) parts.Add(field + "=" + value);
            }
            return string.Join("; ", parts);
        }

        private static UserDevice Pad(string pad) => pad switch
        {
            "full" => Full(),
            "dualsense" => DualSense(),
            _ => Unlisted(),
        };

        [Theory]
        [InlineData(VirtualControllerType.Nintendo, "switch-pro", "full")]
        [InlineData(VirtualControllerType.Nintendo, "switch-pro", "dualsense")]
        [InlineData(VirtualControllerType.Nintendo, "switch-pro", "unlisted")]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller", "full")]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller", "dualsense")]
        [InlineData(VirtualControllerType.Nintendo, "switch2-pro-controller", "unlisted")]
        [InlineData(VirtualControllerType.Extended, "steam-deck", "full")]
        [InlineData(VirtualControllerType.Extended, "steam-deck", "dualsense")]
        [InlineData(VirtualControllerType.Extended, "steam-deck", "unlisted")]
        [InlineData(VirtualControllerType.Extended, "steam-controller", "full")]
        [InlineData(VirtualControllerType.Extended, "steam-controller", "dualsense")]
        [InlineData(VirtualControllerType.Extended, "steam-controller", "unlisted")]
        [InlineData(VirtualControllerType.Extended, "steam-controller-2", "full")]
        [InlineData(VirtualControllerType.Extended, "steam-controller-2", "dualsense")]
        [InlineData(VirtualControllerType.Extended, "steam-controller-2", "unlisted")]
        public void TheAutomapBindsWhatItBoundBefore(VirtualControllerType type, string profile, string pad)
        {
            string dump = Dump(SettingsManager.CreateDefaultPadSetting(Pad(pad), type, profile));
            _output.WriteLine(profile + " " + pad + ": " + dump);
            Assert.Equal(Before[profile + " " + pad], dump);
        }

        private static readonly Dictionary<string, string> Before = new()
        {
            ["switch-pro full"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 8; RawBtn11=Button 9; RawBtn12=Button 10; RawBtn13=Button 11; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Axis 2; RawBtn7=Axis 5; RawBtn8=Button 6; RawBtn9=Button 7; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["switch-pro dualsense"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 8; RawBtn11=Button 9; RawBtn12=Button 10; RawBtn13=Button 11; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Axis 2; RawBtn7=Axis 5; RawBtn8=Button 6; RawBtn9=Button 7; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["switch-pro unlisted"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 8; RawBtn11=Button 9; RawBtn12=Button 10; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Axis 2; RawBtn7=Axis 5; RawBtn8=Button 6; RawBtn9=Button 7; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up",
            ["switch2-pro-controller full"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=POV 0 Left; RawBtn11=POV 0 Up; RawBtn12=Button 4; RawBtn13=Axis 2; RawBtn14=Button 6; RawBtn15=Button 8; RawBtn16=Button 10; RawBtn17=Button 11; RawBtn18=Button 12; RawBtn19=Button 13; RawBtn2=Button 2; RawBtn20=Button 17; RawBtn3=Button 3; RawBtn4=Button 5; RawBtn5=Axis 5; RawBtn6=Button 7; RawBtn7=Button 9; RawBtn8=POV 0 Down; RawBtn9=POV 0 Right; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["switch2-pro-controller dualsense"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=POV 0 Left; RawBtn11=POV 0 Up; RawBtn12=Button 4; RawBtn13=Axis 2; RawBtn14=Button 6; RawBtn15=Button 8; RawBtn16=Button 10; RawBtn17=Button 11; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 5; RawBtn5=Axis 5; RawBtn6=Button 7; RawBtn7=Button 9; RawBtn8=POV 0 Down; RawBtn9=POV 0 Right; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["switch2-pro-controller unlisted"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 3; RawAxis3=Axis 4; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=POV 0 Left; RawBtn11=POV 0 Up; RawBtn12=Button 4; RawBtn13=Axis 2; RawBtn14=Button 6; RawBtn15=Button 8; RawBtn16=Button 10; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 5; RawBtn5=Axis 5; RawBtn6=Button 7; RawBtn7=Button 9; RawBtn8=POV 0 Down; RawBtn9=POV 0 Right",
            ["steam-deck full"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn11=Button 11; RawBtn12=Button 12; RawBtn13=Button 13; RawBtn14=Button 14; RawBtn15=Button 15; RawBtn16=Button 16; RawBtn17=Button 17; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadX2=Touchpad 1 Finger 0 X; TouchpadY2=Touchpad 1 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; TouchpadContact2=Touchpad 1 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-deck dualsense"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn11=Button 11; RawBtn16=Button 16; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-deck unlisted"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up",
            ["steam-controller full"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 13; RawBtn11=Button 12; RawBtn12=Button 16; RawBtn13=Button 17; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 10; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadX2=Touchpad 1 Finger 0 X; TouchpadY2=Touchpad 1 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; TouchpadContact2=Touchpad 1 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-controller dualsense"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn12=Button 16; RawBtn13=Button 9; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 10; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-controller unlisted"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn13=Button 9; RawBtn2=Button 2; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 10; RawPov0Down=POV 0 Down; RawPov0Left=POV 0 Left; RawPov0Right=POV 0 Right; RawPov0Up=POV 0 Up",
            ["steam-controller-2 full"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn11=Button 11; RawBtn12=Button 12; RawBtn13=Button 13; RawBtn14=Button 14; RawBtn15=Button 15; RawBtn16=Button 16; RawBtn17=Button 17; RawBtn18=POV 0 Up; RawBtn19=POV 0 Down; RawBtn2=Button 2; RawBtn20=POV 0 Left; RawBtn21=POV 0 Right; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadX2=Touchpad 1 Finger 0 X; TouchpadY2=Touchpad 1 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; TouchpadContact2=Touchpad 1 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-controller-2 dualsense"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn11=Button 11; RawBtn16=Button 16; RawBtn18=POV 0 Up; RawBtn19=POV 0 Down; RawBtn2=Button 2; RawBtn20=POV 0 Left; RawBtn21=POV 0 Right; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9; TouchpadX1=Touchpad 0 Finger 0 X; TouchpadY1=Touchpad 0 Finger 0 Y; TouchpadContact1=Touchpad 0 Finger 0 Down; MotionGyro=Motion Gyro; MotionAccel=Motion Accel",
            ["steam-controller-2 unlisted"] =
                "RawAxis0=Axis 0; RawAxis1=Axis 1; RawAxis2=Axis 2; RawAxis3=Axis 3; RawAxis4=Axis 4; RawAxis5=Axis 5; RawBtn0=Button 0; RawBtn1=Button 1; RawBtn10=Button 10; RawBtn18=POV 0 Up; RawBtn19=POV 0 Down; RawBtn2=Button 2; RawBtn20=POV 0 Left; RawBtn21=POV 0 Right; RawBtn3=Button 3; RawBtn4=Button 4; RawBtn5=Button 5; RawBtn6=Button 6; RawBtn7=Button 7; RawBtn8=Button 8; RawBtn9=Button 9",
        };
    }
}
