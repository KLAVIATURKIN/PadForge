using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A macro's slot-axis trigger compares the read against the threshold the
    /// card shows. A stick rests at the middle of its 0..1 read, so its
    /// direction is measured from there. A trigger rests at 0 and only moves
    /// one way, so a direction has no meaning on it and the threshold is the
    /// pull: the #443 rule, applied to the macro trigger in both evaluators.
    ///
    /// <para>Before this, a recorded trigger (stamped Positive) needed 75% pull
    /// to pass a 50% threshold, Negative on a trigger read true at rest, and a
    /// stick set to Any read true at rest while a push to the left never
    /// counted.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroAxisDirectionTests
    {
        private const ushort SeventyPercentPull = 45875;

        private static MacroItem AxisMacro(MacroAxisTarget target, MacroAxisDirection dir)
        {
            var m = new MacroItem
            {
                Name = "axis",
                IsEnabled = true,
                PadIndex = 0,
                TriggerAxisTargets = new[] { target },
                TriggerAxisDirections = new[] { dir },
                TriggerAxisThreshold = 50,
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DurationMs = 60000 });
            return m;
        }

        private static bool Fires(MacroItem m, Gamepad gp)
        {
            new InputManager().EvaluateSlotMacros(ref gp, new[] { m });
            return m.IsExecuting;
        }

        private static bool FiresExtended(MacroItem m, Action<RawHidState> setup)
        {
            var raw = RawHidState.Create(8, 32, 1);
            for (int i = 0; i < raw.Povs.Length; i++) raw.Povs[i] = -1;
            raw.Axes[2] = short.MinValue;   // triggers at rest
            raw.Axes[5] = short.MinValue;
            setup(raw);
            new InputManager().EvaluateSlotMacrosExtended(ref raw, new[] { m });
            return m.IsExecuting;
        }

        [Theory]
        [InlineData(MacroAxisDirection.Positive)]
        [InlineData(MacroAxisDirection.Negative)]
        [InlineData(MacroAxisDirection.Any)]
        public void ATrigger_EngagesAtTheThresholdPull_WhateverTheDirection(MacroAxisDirection dir)
        {
            Assert.True(Fires(AxisMacro(MacroAxisTarget.LeftTrigger, dir), new Gamepad { LeftTrigger = SeventyPercentPull }));
            Assert.False(Fires(AxisMacro(MacroAxisTarget.LeftTrigger, dir), new Gamepad()));
            Assert.True(FiresExtended(AxisMacro(MacroAxisTarget.LeftTrigger, dir),
                r => r.Axes[2] = (short)(short.MinValue + SeventyPercentPull)));
            Assert.False(FiresExtended(AxisMacro(MacroAxisTarget.LeftTrigger, dir), r => { }));
        }

        [Fact]
        public void AStickSetToAny_RestsIdle_AndEngagesPastTheThresholdEitherWay()
        {
            var any = MacroAxisDirection.Any;
            Assert.False(Fires(AxisMacro(MacroAxisTarget.LeftStickX, any), new Gamepad()));
            Assert.True(Fires(AxisMacro(MacroAxisTarget.LeftStickX, any), new Gamepad { ThumbLX = -30000 }));
            Assert.True(Fires(AxisMacro(MacroAxisTarget.LeftStickX, any), new Gamepad { ThumbLX = 30000 }));
            Assert.False(Fires(AxisMacro(MacroAxisTarget.LeftStickX, any), new Gamepad { ThumbLX = 8000 }));
            Assert.False(FiresExtended(AxisMacro(MacroAxisTarget.LeftStickX, any), r => r.Axes[0] = 0));
            Assert.True(FiresExtended(AxisMacro(MacroAxisTarget.LeftStickX, any), r => r.Axes[0] = -30000));
        }

        /// <summary>A turbo scaled by the trigger's own pull reads the pull, not
        /// a half measured from the middle: a recorded trigger (Positive) read
        /// no pressure until half pull, and a Negative one read full pressure
        /// at rest.</summary>
        [Theory]
        [InlineData(MacroAxisDirection.Positive)]
        [InlineData(MacroAxisDirection.Negative)]
        [InlineData(MacroAxisDirection.Any)]
        public void ATurboOnTheTriggersOwnAxis_ReadsThePull(MacroAxisDirection dir)
        {
            var m = new MacroItem
            {
                TriggerAxisTargets = new[] { MacroAxisTarget.LeftTrigger },
                TriggerAxisDirections = new[] { dir },
            };
            var source = new MacroAction
            {
                Type = MacroActionType.RepeatKeyWhileHeld,
                PressureScaledRate = true,
                SourceDeviceGuid = Guid.NewGuid(),
                SourceDeviceAxisIndex = 2,
            };
            Assert.Equal(0f, InputManager.ResolveTurboPressure01(m, source, 0), 3);
            Assert.Equal(49152f / 65535f, InputManager.ResolveTurboPressure01(m, source, 49152), 3);
        }

        /// <summary>Positive control: a stick's Positive and Negative halves
        /// keep their center-measured behavior.</summary>
        [Fact]
        public void AStickDirection_StillMeasuresFromCenter()
        {
            Assert.False(Fires(AxisMacro(MacroAxisTarget.LeftStickX, MacroAxisDirection.Positive), new Gamepad()));
            Assert.True(Fires(AxisMacro(MacroAxisTarget.LeftStickX, MacroAxisDirection.Positive), new Gamepad { ThumbLX = 30000 }));
            Assert.False(Fires(AxisMacro(MacroAxisTarget.LeftStickX, MacroAxisDirection.Positive), new Gamepad { ThumbLX = -30000 }));
            Assert.True(Fires(AxisMacro(MacroAxisTarget.LeftStickX, MacroAxisDirection.Negative), new Gamepad { ThumbLX = -30000 }));
        }
    }
}
