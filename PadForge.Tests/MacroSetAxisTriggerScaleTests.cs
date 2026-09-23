using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Set Axis on a trigger takes the percent the editor shows, on the pull
    /// scale every other trigger write uses: 100% is a full pull on every slot
    /// type. The Gamepad path wrote the stored 0..32767 value raw into the
    /// 0..65535 trigger, so 100% reached half pull on Xbox and PlayStation slots
    /// while the same action reached full pull on an Extended slot.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroSetAxisTriggerScaleTests
    {
        private static MacroItem SetTrigger(double percent)
        {
            var m = new MacroItem
            {
                Name = "set",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            var action = new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger };
            action.AxisValuePercent = percent;
            m.Actions.Add(action);
            return m;
        }

        private static ushort GamepadPull(double percent)
        {
            var gp = new Gamepad { Buttons = Gamepad.A };
            new InputManager().EvaluateSlotMacros(ref gp, new[] { SetTrigger(percent) });
            return gp.LeftTrigger;
        }

        private static float ExtendedFraction(double percent)
        {
            var raw = RawHidState.Create(8, 32, 1);
            for (int i = 0; i < raw.Povs.Length; i++) raw.Povs[i] = -1;
            raw.Axes[2] = short.MinValue;
            raw.Axes[5] = short.MinValue;
            raw.Buttons[0] = 1;
            new InputManager().EvaluateSlotMacrosExtended(ref raw, new[] { SetTrigger(percent) });
            return (raw.Axes[2] - (float)short.MinValue) / 65535f;
        }

        [Fact]
        public void HundredPercent_IsAFullPull()
        {
            Assert.Equal(65535, GamepadPull(100));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(25)]
        [InlineData(50)]
        [InlineData(100)]
        public void BothEvaluators_LandTheSamePull(double percent)
        {
            float gamepad = GamepadPull(percent) / 65535f;
            Assert.InRange(gamepad - ExtendedFraction(percent), -2f / 65535f, 2f / 65535f);
        }
    }
}
