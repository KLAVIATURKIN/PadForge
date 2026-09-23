using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A fixed repeat count counts passes, not ticks.
    ///
    /// <para>A finished sequence leaves the action index past the end, so the
    /// evaluator re-entered its completion block on every later tick. The
    /// count was decremented there before the repeat interval was checked, so
    /// each tick spent waiting out the interval consumed one repeat. A macro
    /// set to repeat three times at 200 ms fired once and burned the other two
    /// repeats standing still. Only a count paired with a zero interval
    /// behaved, because there was nothing to wait for.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroFixedRepeatPacingTests
    {
        private const int Fire = 30000;
        // Set Axis on a trigger writes the pull scale: the stored value doubled, plus one.
        private const int FirePull = Fire * 2 + 1;
        private const int Interval = 40;

        private static MacroItem Macro(MacroTriggerMode mode, MacroRepeatMode repeat,
            int count, int repeatDelayMs)
        {
            var m = new MacroItem
            {
                Name = "RP",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                RepeatCount = count,
                RepeatDelayMs = repeatDelayMs,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = (short)Fire,
            });
            return m;
        }

        private static ushort Tick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        /// <summary>Counts the passes over a run long enough for the count to
        /// finish, sampling the way the engine ticks.</summary>
        private static int CountPasses(InputManager im, MacroItem[] macros, int ticks, bool held)
        {
            int passes = 0;
            for (int i = 0; i < ticks; i++)
            {
                if (Tick(im, macros, held) == FirePull) passes++;
                Thread.Sleep(Interval / 2);
            }
            return passes;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AFixedCountFiresThatManyPasses(int count)
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, MacroRepeatMode.FixedCount, count, Interval) };

            // The press starts the run, and the hold gives the interval room
            // to elapse between passes.
            Assert.Equal(count, CountPasses(im, macros, ticks: 20, held: true));
        }

        /// <summary>A zero interval is the case that always worked. It must
        /// keep working after the pacing moved ahead of the count.</summary>
        [Fact]
        public void AFixedCountWithNoIntervalStillFiresEveryPass()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, MacroRepeatMode.FixedCount, 3, 0) };

            // With no interval every tick carries a pass, so the three land
            // back to back and the run is over well inside the loop.
            int passes = 0;
            for (int i = 0; i < 12; i++)
                if (Tick(im, macros, held: true) == FirePull) passes++;

            Assert.Equal(3, passes);
            Assert.False(macros[0].IsExecuting);
        }

        /// <summary>Until Release is paced by the interval and bounded by the
        /// release, not by the count. It repeats past the authored count.</summary>
        [Fact]
        public void UntilReleaseRepeatsPastTheCountAndStopsOnRelease()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, 2, Interval) };

            Assert.True(CountPasses(im, macros, ticks: 20, held: true) > 3,
                "until-release stopped at the authored count");

            Tick(im, macros, held: false);
            Assert.Equal(0, Tick(im, macros, held: false));
            Assert.Equal(0, CountPasses(im, macros, ticks: 4, held: false));
        }

        /// <summary>The last pass ends the run at once. Waiting is for the gap
        /// BETWEEN passes, and after the final one there is no next pass to
        /// pace, so a long interval must not hold the run open.
        ///
        /// <para>A run that stays open swallows every later press, because a
        /// macro already executing does not start again. A single-pass macro
        /// on a one-second interval would answer one press per second.</para></summary>
        [Fact]
        public void TheLastPassEndsTheRunWithoutWaitingOutTheInterval()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, MacroRepeatMode.FixedCount, 1, 1000) };

            Assert.Equal(FirePull, Tick(im, macros, held: true));
            Assert.Equal(0, Tick(im, macros, held: false));
            Assert.False(macros[0].IsExecuting, "the run held itself open past its only pass");

            // The very next press answers, with no part of the interval waited.
            Assert.Equal(FirePull, Tick(im, macros, held: true));
            Assert.Equal(0, Tick(im, macros, held: false));
            Assert.Equal(FirePull, Tick(im, macros, held: true));
        }

        /// <summary>The interval still paces the passes. Without pacing the
        /// count would empty in one tick and this would read one pass.</summary>
        [Fact]
        public void TheIntervalSpreadsThePassesAcrossTicks()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, MacroRepeatMode.FixedCount, 3, 1000) };

            Assert.Equal(FirePull, Tick(im, macros, held: true));
            // A second pass is due only after a full second, so nothing fires
            // in the ticks right behind the first.
            for (int i = 0; i < 5; i++)
                Assert.Equal(0, Tick(im, macros, held: true));
            Assert.True(macros[0].IsExecuting, "the run ended instead of waiting out the interval");
            Assert.Equal(3, macros[0].RemainingRepeats);
        }
    }
}
