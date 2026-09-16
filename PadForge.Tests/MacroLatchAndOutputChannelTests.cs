using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two macro-editor findings that were fixed without a guard.
    ///
    /// <para>A Toggle latch is runtime state owned by the action's TYPE, and
    /// the latch pass dispatches on that type. Retyping a latched action away
    /// stopped its output while leaving the bit set, so retyping back resumed
    /// the output with no trigger press behind it.</para>
    ///
    /// <para>An output-controller variable on an Extended slot read a flat
    /// zero for every channel, because the raw surface had no reader. The
    /// editor offers those channels there, so every formula bound to one could
    /// never come true.</para>
    /// </summary>
    public class MacroLatchAndOutputChannelTests
    {
        // ── Retyping an action drops the latches it was holding ──────────

        private static readonly string[] LatchNames =
        {
            "VcToggleLatched", "KeyToggleLatched", "MouseToggleLatched",
            "VcAxisToggleLatched", "WheelToggleLatched",
        };

        private static void SetAllLatches(MacroAction a, bool value)
        {
            foreach (string n in LatchNames)
                typeof(MacroAction).GetProperty(n,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(a, value);
        }

        private static bool AnyLatchHeld(MacroAction a)
        {
            foreach (string n in LatchNames)
                if ((bool)typeof(MacroAction).GetProperty(n,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(a))
                    return true;
            return false;
        }

        [Fact]
        public void RetypingALatchedActionDropsEveryLatch()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton };
            SetAllLatches(action, true);
            Assert.True(AnyLatchHeld(action));

            action.Type = MacroActionType.KeyPress;

            Assert.False(AnyLatchHeld(action));
        }

        /// <summary>The round trip is the case that shipped the bug: away and
        /// back must not restore the output.</summary>
        [Fact]
        public void RetypingAwayAndBackDoesNotResurrectTheLatch()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton };
            SetAllLatches(action, true);

            action.Type = MacroActionType.KeyPress;
            action.Type = MacroActionType.ToggleVcButton;

            Assert.False(AnyLatchHeld(action));
        }

        /// <summary>Assigning the same type is not a retype, so it must not
        /// disturb a latch the action legitimately holds.</summary>
        [Fact]
        public void AssigningTheSameTypeLeavesTheLatchAlone()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton };
            SetAllLatches(action, true);

            action.Type = MacroActionType.ToggleVcButton;

            Assert.True(AnyLatchHeld(action));
        }

        /// <summary>A hidden pulse flag must not survive a retype either. The
        /// editor only offers the checkbox for pulse-capable types, so a
        /// surviving flag has no control that can clear it.</summary>
        [Fact]
        public void ARetypeClearsAPulseFlagTheNewTypeCannotShow()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.ToggleVcAxis,
                PulseWhileLatched = true,
            };

            action.Type = MacroActionType.Delay;

            Assert.False(action.PulseWhileLatched);
        }

        // ── An Extended slot's output channels read its raw surface ──────

        private static float ReadChannel(MacroOutputChannel ch, RawHidState raw)
        {
            var m = typeof(InputManager).GetMethod("ReadOutputChannelRaw",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (float)m.Invoke(null, new object[] { ch, raw });
        }

        [Fact]
        public void ButtonChannelsReadTheRawButtonBits()
        {
            var raw = RawHidState.Create(6, 16, 1);
            raw.SetButton(0, true);    // A
            raw.SetButton(10, true);   // Guide

            Assert.Equal(1f, ReadChannel(MacroOutputChannel.A, raw));
            Assert.Equal(1f, ReadChannel(MacroOutputChannel.Guide, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.B, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.Start, raw));
        }

        /// <summary>The whole point of the finding: these used to be zero
        /// whatever the pad was doing.</summary>
        [Fact]
        public void APressedButtonIsNoLongerReadAsZero()
        {
            var raw = RawHidState.Create(6, 16, 1);
            raw.SetButton(3, true);    // Y

            Assert.NotEqual(0f, ReadChannel(MacroOutputChannel.Y, raw));
        }

        [Fact]
        public void TheDpadChannelsReadTheFirstHat()
        {
            var raw = RawHidState.Create(6, 16, 1);
            raw.Povs[0] = 0;
            Assert.Equal(1f, ReadChannel(MacroOutputChannel.DpadUp, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.DpadDown, raw));

            raw.Povs[0] = 18000;
            Assert.Equal(1f, ReadChannel(MacroOutputChannel.DpadDown, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.DpadUp, raw));

            // A released hat answers no direction.
            raw.Povs[0] = -1;
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.DpadUp, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.DpadDown, raw));
        }

        /// <summary>A diagonal answers both of its directions, matching the
        /// two D-pad bits an Xbox-shape diagonal sets.</summary>
        [Fact]
        public void ADiagonalHatAnswersBothOfItsDirections()
        {
            var raw = RawHidState.Create(6, 16, 1);
            raw.Povs[0] = 4500;   // north-east

            Assert.Equal(1f, ReadChannel(MacroOutputChannel.DpadUp, raw));
            Assert.Equal(1f, ReadChannel(MacroOutputChannel.DpadRight, raw));
            Assert.Equal(0f, ReadChannel(MacroOutputChannel.DpadDown, raw));
        }

        /// <summary>A trigger rests at the low end of the raw surface and a
        /// stick rests at center, and both normalize to the same 0..1 scale
        /// the Xbox-shape twin uses.</summary>
        [Fact]
        public void TriggersAndSticksNormalizeTheSameWayTheirTwinDoes()
        {
            var raw = RawHidState.Create(6, 16, 1);
            raw.Axes[2] = short.MinValue;   // left trigger released
            raw.Axes[5] = short.MaxValue;   // right trigger fully pulled
            raw.Axes[0] = 0;                // left stick X centered

            Assert.Equal(0f, ReadChannel(MacroOutputChannel.LT, raw), 3);
            Assert.Equal(1f, ReadChannel(MacroOutputChannel.RT, raw), 3);
            Assert.Equal(0.5f, ReadChannel(MacroOutputChannel.LX, raw), 2);
        }

        /// <summary>The reader has to be WIRED, not merely present. The
        /// defect was an arm that returned a flat zero for every
        /// output-controller variable, so a guard that calls the reader
        /// directly stays green while the arm is still dead.</summary>
        [Fact]
        public void AnOutputControllerVariableReadsThroughToTheRawSurface()
        {
            var variable = new MacroExpressionVariable
            {
                Source = MacroTriggerSource.OutputController,
                OutputChannel = MacroOutputChannel.Y,
            };
            Assert.True(variable.IsBound);

            var raw = RawHidState.Create(6, 16, 1);
            raw.SetButton(3, true);   // Y

            // The OutputController arm returns before touching any instance
            // state, so an uninitialized instance is enough to exercise it.
            var mgr = (InputManager)System.Runtime.CompilerServices
                .RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
            // It never ran its constructor, so it owns nothing to finalize and
            // its finalizer would run Dispose against null fields. Left to the
            // collector that killed the whole test host partway through a run,
            // which reads as an unrelated intermittent somewhere else entirely.
            GC.SuppressFinalize(mgr);
            var m = typeof(InputManager).GetMethod("ReadExpressionVariableRaw",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);

            float v = (float)m.Invoke(mgr, new object[] { variable, raw, 0 });

            Assert.Equal(1f, v);
        }
    }
}
