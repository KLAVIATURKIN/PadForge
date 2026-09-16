using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A formula variable bound to the slot's own output reads that output on
    /// an Extended slot, the same as on an Xbox-shape one.
    ///
    /// <para>The Extended reader returned a flat zero for every output
    /// channel, on the grounds that an Extended slot has no Xbox-shape
    /// combined state. The raw state IS that slot's combined output, and the
    /// editor offers these channels there, so every formula built on one was
    /// inert: the condition could never come true and the macro never fired,
    /// with a picker entry that looked like it worked.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ExtendedOutputChannelExpressionTests
    {
        private const short Fire = 24000;

        private static MacroItem OutputChannelMacro(MacroOutputChannel channel)
        {
            var m = new MacroItem
            {
                Name = "formula",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = MacroTriggerMode.CustomExpression,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.TriggerExpressionVariables.Clear();
            m.TriggerExpressionVariables.Add(new MacroExpressionVariable
            {
                Source = MacroTriggerSource.OutputController,
                OutputChannel = channel,
            });
            m.TriggerExpression = "a";
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.RightStickY,
                AxisValue = Fire,
            });
            return m;
        }

        /// <summary>Runs one tick over a raw state the caller has set up, and
        /// reports what the macro wrote to its own separate channel.</summary>
        private static short Tick(InputManager im, MacroItem[] macros, Action<RawHidState> setup)
        {
            var raw = RawHidState.Create(8, 32, 1);
            for (int i = 0; i < raw.Povs.Length; i++) raw.Povs[i] = -1;
            // Axis rest: sticks centered, triggers at the bottom of the word.
            raw.Axes[2] = short.MinValue;
            raw.Axes[5] = short.MinValue;
            setup?.Invoke(raw);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            return raw.Axes[4];
        }

        /// <summary>Button 1 on an Extended slot is raw button index 0, the
        /// numbering the picker prints.</summary>
        [Fact]
        public void AButtonChannelReadsTheSlotsOwnRawButton()
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(MacroOutputChannel.A) };

            // Positive control first: with the button clear the formula is
            // false and nothing fires, so the assert below is about the
            // button and not about the macro firing unconditionally.
            Assert.Equal(0, Tick(im, macros, null));

            Assert.Equal(Fire, Tick(im, macros, raw => raw.Buttons[0] |= 1u));
        }

        /// <summary>Each button channel reads its own index. Reading a
        /// neighbor would pass the test above and still be wrong.</summary>
        [Theory]
        [InlineData(MacroOutputChannel.B, 1)]
        [InlineData(MacroOutputChannel.Y, 3)]
        [InlineData(MacroOutputChannel.Guide, 10)]
        public void EachButtonChannelReadsItsOwnIndex(MacroOutputChannel channel, int rawIndex)
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(channel) };

            // The neighbor below leaves it false.
            if (rawIndex > 0)
                Assert.Equal(0, Tick(im, macros, raw => raw.Buttons[0] |= 1u << (rawIndex - 1)));

            Assert.Equal(Fire, Tick(im, macros, raw => raw.Buttons[0] |= 1u << rawIndex));
        }

        /// <summary>A trigger channel rests at the bottom of the signed word
        /// on this surface, so a resting trigger must read as released. The
        /// formula fires at 0.5, so a trigger past half pull crosses it.</summary>
        [Fact]
        public void ATriggerChannelRestsReleasedAndFiresWhenPulled()
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(MacroOutputChannel.LT) };

            Assert.Equal(0, Tick(im, macros, null));
            Assert.Equal(Fire, Tick(im, macros, raw => raw.Axes[2] = 20000));
        }

        /// <summary>A stick channel rests at the middle of the word, which
        /// reads 0.5. The formula's threshold is "at least 0.5", so a
        /// centered stick is already true and only the negative half is
        /// false. This pins the scale rather than a preference.</summary>
        [Fact]
        public void AStickChannelReadsCenteredAsAHalf()
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(MacroOutputChannel.LX) };

            Assert.Equal(Fire, Tick(im, macros, raw => raw.Axes[0] = 0));
            Assert.Equal(0, Tick(im, macros, raw => raw.Axes[0] = short.MinValue));
        }

        /// <summary>The D-pad lives on the first POV hat here. Each case
        /// starts from a centered hat, so the formula goes false and the
        /// direction under test is a fresh rising edge rather than the
        /// middle of a run already going.</summary>
        private static short TapPov(InputManager im, MacroItem[] macros, int angle)
        {
            Tick(im, macros, null);   // centered: the run ends
            return Tick(im, macros, raw => raw.Povs[0] = angle);
        }

        /// <summary>A centered hat is released. A diagonal answers for both
        /// of its directions, matching the two D-pad bits an Xbox-shape
        /// diagonal sets. A neighbor direction does not answer.</summary>
        [Fact]
        public void ADpadChannelReadsTheFirstPovHat()
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(MacroOutputChannel.DpadUp) };

            Assert.Equal(0, Tick(im, macros, null));                  // centered
            Assert.Equal(Fire, TapPov(im, macros, 0));                // up
            Assert.Equal(Fire, TapPov(im, macros, 4500));             // up and right
            Assert.Equal(0, TapPov(im, macros, 9000));                // right only
            Assert.Equal(0, TapPov(im, macros, 18000));               // down
        }

        /// <summary>Each direction reads its own quarter of the hat.</summary>
        [Theory]
        [InlineData(MacroOutputChannel.DpadUp, 0)]
        [InlineData(MacroOutputChannel.DpadRight, 9000)]
        [InlineData(MacroOutputChannel.DpadDown, 18000)]
        [InlineData(MacroOutputChannel.DpadLeft, 27000)]
        public void EachDpadDirectionReadsItsOwnAngle(MacroOutputChannel channel, int angle)
        {
            var im = new InputManager();
            var macros = new[] { OutputChannelMacro(channel) };

            Assert.Equal(Fire, TapPov(im, macros, angle));
            // The opposite direction never answers.
            Assert.Equal(0, TapPov(im, macros, (angle + 18000) % 36000));
        }
    }
}
