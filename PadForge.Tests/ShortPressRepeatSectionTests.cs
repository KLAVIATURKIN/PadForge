using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A ShortPress macro honors a fixed repeat count, so the editor has to
    /// show the Repeat section for it.
    ///
    /// <para>The 2026-07-25 audit hid the whole section for ShortPress on the
    /// grounds that the repeat is dead there. Only one of the three repeat
    /// modes is dead: a ShortPress run starts with the trigger already
    /// released, which sets the deferred-completion flag, and that flag gates
    /// the until-release branch alone. The fixed-count branch is a separate
    /// test on a counter the executor arms for every trigger mode. Hiding the
    /// section therefore hid a live setting, and a macro that arrived with a
    /// fixed count kept repeating with no way to see or edit it.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ShortPressRepeatSectionTests
    {
        private const int Fire = 30000;
        // Set Axis on a trigger writes the pull scale: the stored value doubled, plus one.
        private const int FirePull = Fire * 2 + 1;

        private static MacroItem Macro(MacroRepeatMode repeat, int count, int repeatDelayMs)
        {
            var m = new MacroItem
            {
                Name = "SP",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.ShortPress,
                RepeatMode = repeat,
                RepeatCount = count,
                RepeatDelayMs = repeatDelayMs,
                ConsumeTriggerButtons = false,
                TriggerHoldMs = 500,
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

        /// <summary>Tap the button: an idle tick so the rising edge is
        /// observed, a press to arm the window, then a release inside it.
        /// The release tick is the one that fires.</summary>
        private static ushort Tap(InputManager im, MacroItem[] macros)
        {
            Tick(im, macros, held: false);
            Tick(im, macros, held: true);
            return Tick(im, macros, held: false);
        }

        /// <summary>The live setting: a count of two runs the actions twice.</summary>
        [Fact]
        public void ShortPress_WithAFixedCount_RunsThePassesItWasGiven()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroRepeatMode.FixedCount, count: 2, repeatDelayMs: 40) };

            Assert.Equal(FirePull, Tap(im, macros));
            // Inside the interval the run idles between passes.
            Assert.Equal(0, Tick(im, macros, held: false));
            // Past the interval the second pass fires, with the trigger
            // already released the whole time.
            Thread.Sleep(70);
            Assert.Equal(FirePull, Tick(im, macros, held: false));
            // The count bounds it: no third pass, ever.
            Assert.Equal(0, Tick(im, macros, held: false));
            Thread.Sleep(70);
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        /// <summary>Positive control for the test above. A count of one is
        /// one pass, so the second pass there came from the count and not
        /// from some other repeat path.</summary>
        [Fact]
        public void ShortPress_WithACountOfOne_RunsOnePass()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroRepeatMode.FixedCount, count: 1, repeatDelayMs: 40) };

            Assert.Equal(FirePull, Tap(im, macros));
            Thread.Sleep(70);
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        /// <summary>The one dead mode, and the reason the picker disables it
        /// here. Until Release repeats while the trigger is down, and a
        /// ShortPress run begins after the release.</summary>
        [Fact]
        public void ShortPress_WithUntilRelease_RunsOnePass()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroRepeatMode.UntilRelease, count: 5, repeatDelayMs: 40) };

            Assert.Equal(FirePull, Tap(im, macros));
            Thread.Sleep(70);
            Assert.Equal(0, Tick(im, macros, held: false));
            Thread.Sleep(70);
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        /// <summary>The editor matches the engine: the section shows, and
        /// only the dead option is closed off.</summary>
        [Fact]
        public void TheEditorShowsTheSectionAndDisablesOnlyUntilRelease()
        {
            var m = Macro(MacroRepeatMode.FixedCount, 2, 0);
            Assert.True(m.ShowsRepeatSection, "the count is live for ShortPress, so its controls must show");
            Assert.False(m.SupportsUntilReleaseRepeat);

            // Positive control: an ordinary mode supports all three options,
            // and Turbo still hides the whole section.
            m.TriggerMode = MacroTriggerMode.OnPress;
            Assert.True(m.ShowsRepeatSection);
            Assert.True(m.SupportsUntilReleaseRepeat);
            m.TriggerMode = MacroTriggerMode.Turbo;
            Assert.False(m.ShowsRepeatSection);
        }

        /// <summary>The editor rebinds when the mode changes, so switching
        /// into ShortPress closes the option off without a reload.</summary>
        [Fact]
        public void ChangingTheModeRaisesTheGateForTheView()
        {
            var m = Macro(MacroRepeatMode.FixedCount, 2, 0);
            m.TriggerMode = MacroTriggerMode.OnPress;

            var raised = new System.Collections.Generic.List<string>();
            m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            m.TriggerMode = MacroTriggerMode.ShortPress;

            Assert.Contains(nameof(MacroItem.ShowsRepeatSection), raised);
            Assert.Contains(nameof(MacroItem.SupportsUntilReleaseRepeat), raised);
        }
    }
}
