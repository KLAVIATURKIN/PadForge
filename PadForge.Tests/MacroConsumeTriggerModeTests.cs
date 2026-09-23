using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Always and Custom Expression macros never read their trigger button list,
    /// so they must not consume it either. A macro recorded with a trigger and
    /// then switched to one of these modes keeps the old bits, and the consume
    /// checkbox is hidden in both modes, so a stale list used to strip that
    /// button from the game for as long as the macro ran: forever, for Always.
    /// Step 3's source-read consume already skipped these modes (C30). These
    /// tests pin the Step 4b strip to the same rule on both evaluators.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroConsumeTriggerModeTests
    {
        private static MacroItem Macro(MacroTriggerMode mode)
        {
            var m = new MacroItem
            {
                Name = "stale",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = true,
            };
            if (mode == MacroTriggerMode.CustomExpression)
            {
                m.TriggerExpressionVariables.Clear();
                m.TriggerExpression = "1";
            }
            m.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DurationMs = 60000 });
            return m;
        }

        private static ushort GamepadTick(InputManager im, MacroItem m)
        {
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, new[] { m });
            return gp.Buttons;
        }

        private static uint ExtendedTick(InputManager im, MacroItem m)
        {
            var raw = RawHidState.Create(8, 32, 1);
            for (int i = 0; i < raw.Povs.Length; i++) raw.Povs[i] = -1;
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, new[] { m });
            return raw.Buttons[0];
        }

        [Theory]
        [InlineData(MacroTriggerMode.Always)]
        [InlineData(MacroTriggerMode.CustomExpression)]
        public void AModeThatIgnoresItsTriggerList_LeavesThoseButtonsToTheGame(MacroTriggerMode mode)
        {
            var im = new InputManager();
            var m = Macro(mode);
            Assert.Equal(Gamepad.A, GamepadTick(im, m));
            Assert.True(m.IsExecuting, "the macro must be running for the strip to have applied");
            Assert.Equal(Gamepad.A, GamepadTick(im, m));
        }

        [Theory]
        [InlineData(MacroTriggerMode.Always)]
        [InlineData(MacroTriggerMode.CustomExpression)]
        public void AModeThatIgnoresItsTriggerList_LeavesThoseButtonsToTheGame_OnAnExtendedSlot(MacroTriggerMode mode)
        {
            var im = new InputManager();
            var m = Macro(mode);
            Assert.Equal(1u, ExtendedTick(im, m));
            Assert.True(m.IsExecuting, "the macro must be running for the strip to have applied");
            Assert.Equal(1u, ExtendedTick(im, m));
        }

        /// <summary>Positive control, same harness: a mode that reads its
        /// trigger list still eats the trigger while it runs.</summary>
        [Fact]
        public void WhileHeldStillConsumesItsTrigger()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.WhileHeld);
            Assert.Equal(0, GamepadTick(im, m));
            Assert.Equal(0u, ExtendedTick(new InputManager(), Macro(MacroTriggerMode.WhileHeld)));
        }
    }
}
