using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the macro editor findings in the 2026-09-15 audit.
    /// Each test names the defect it forbids.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroEditorAuditFixTests
    {
        // C178: a mouse gesture trigger survives its string round trip.

        /// <summary>The picker writes mouse gestures to the same field and
        /// tag as touchpad gestures, so the parser has to read them back.
        /// It admitted the touchpad prefix alone, so a saved mouse-gesture
        /// trigger was dropped on the next load and the macro lost its
        /// trigger with nothing to show the user why.</summary>
        [Theory]
        [InlineData("Mouse Gesture Left")]
        [InlineData("Mouse Gesture Up")]
        [InlineData("Mouse Gesture Click")]
        [InlineData("Touchpad 0 Swipe Left")]
        public void AGestureTriggerSurvivesTheSpecRoundTrip(string descriptor)
        {
            var guid = Guid.NewGuid();
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = descriptor, DeviceGuid = guid.ToString() },
                out var built), "the picker refused a descriptor it offers");
            Assert.Equal(descriptor, built.GestureDescriptor);

            var reloaded = MacroItem.TriggerInputEntry.Parse(built.Spec);

            Assert.NotNull(reloaded);
            Assert.Equal(descriptor, reloaded.GestureDescriptor);
            Assert.Equal(guid, reloaded.DeviceGuid);
        }

        /// <summary>Positive control: the tail is still gated, so an
        /// unrelated descriptor under the gesture tag is still refused.</summary>
        [Fact]
        public void AnUnrelatedGestureTailIsStillRejected()
        {
            var spec = "in:" + Guid.NewGuid() + ":tg:Keyboard A";
            Assert.Null(MacroItem.TriggerInputEntry.Parse(spec));
        }

        // C181: a zero-button layout offers no buttons.

        /// <summary>An axis-only Extended layout has no buttons. Clamping the
        /// count up to one offered a Button 1 the layout can never emit, the
        /// same defect the menu editor's RawButtonCount already fixed.</summary>
        [Fact]
        public void AZeroButtonLayoutKeepsItsCountAndOffersNoButtons()
        {
            var macro = new MacroItem { Name = "axis only", CustomButtonCount = 0 };
            Assert.Equal(0, macro.CustomButtonCount);

            var action = new MacroAction
            {
                Type = MacroActionType.ButtonPress,
                ButtonStyle = MacroButtonStyle.Numbered,
                CustomButtonCount = 0,
            };
            Assert.Equal(0, action.CustomButtonCount);
            Assert.Empty(action.ButtonOptions);
        }

        /// <summary>Positive control: a layout with buttons still offers
        /// exactly that many.</summary>
        [Fact]
        public void ALayoutWithButtonsOffersExactlyThatMany()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.ButtonPress,
                ButtonStyle = MacroButtonStyle.Numbered,
                CustomButtonCount = 3,
            };
            Assert.Equal(3, action.ButtonOptions.Count);
        }

        // C184: retyping an action drops the latch it no longer owns.

        /// <summary>The latch pass dispatches on the action type, so retyping
        /// a latched action away stopped its output while leaving the bit
        /// set. Retyping back resumed the output with no trigger behind it.</summary>
        [Fact]
        public void RetypingAnActionClearsItsVolatileLatch()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton };
            action.VcToggleLatched = true;

            action.Type = MacroActionType.KeyPress;

            Assert.False(action.VcToggleLatched,
                "the latch outlived the type that owned it");

            // And it stays clear on the way back, so the output needs a
            // fresh trigger press.
            action.Type = MacroActionType.ToggleVcButton;
            Assert.False(action.VcToggleLatched);
        }

        [Fact]
        public void RetypingClearsEveryLatchFamily()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleKey };
            action.VcToggleLatched = true;
            action.KeyToggleLatched = true;
            action.MouseToggleLatched = true;
            action.VcAxisToggleLatched = true;
            action.WheelToggleLatched = true;

            action.Type = MacroActionType.Delay;

            Assert.False(action.VcToggleLatched);
            Assert.False(action.KeyToggleLatched);
            Assert.False(action.MouseToggleLatched);
            Assert.False(action.VcAxisToggleLatched);
            Assert.False(action.WheelToggleLatched);
        }

        /// <summary>Positive control: setting the same type again is not a
        /// change, so nothing is disturbed.</summary>
        [Fact]
        public void SettingTheSameTypeLeavesTheLatchAlone()
        {
            var action = new MacroAction { Type = MacroActionType.ToggleVcButton };
            action.VcToggleLatched = true;

            action.Type = MacroActionType.ToggleVcButton;

            Assert.True(action.VcToggleLatched);
        }

        // C186: Clear stops the recorder that would refill it.

        /// <summary>A running recorder writes its result back every polling
        /// tick, so clearing under it put the value straight back and the
        /// user had to press Clear twice. The trigger Clear already stopped
        /// its recorder. These two did not.</summary>
        [Fact]
        public void ClearingAnActionSourceStopsItsRecorder()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisSource = MacroAxisSource.InputDevice,
                SourceDeviceGuid = Guid.NewGuid(),
                SourceDeviceAxisIndex = 3,
            };
            int stopRequests = 0;
            action.RecordSourceRequested += (_, _) => stopRequests++;
            action.IsRecordingSource = true;

            action.ClearSourceCommand.Execute(null);

            Assert.False(action.IsRecordingSource, "the recorder kept running under the clear");
            Assert.Equal(1, stopRequests);
            Assert.Equal(Guid.Empty, action.SourceDeviceGuid);
            Assert.Equal(-1, action.SourceDeviceAxisIndex);
        }

        [Fact]
        public void ClearingAFormulaVariableBindingStopsItsRecorder()
        {
            var variable = new MacroExpressionVariable
            {
                DeviceGuid = Guid.NewGuid(),
                RawButton = 4,
            };
            int stopRequests = 0;
            variable.RecordRequested += (_, _) => stopRequests++;
            variable.IsRecording = true;

            variable.ClearBindingCommand.Execute(null);

            Assert.False(variable.IsRecording);
            Assert.Equal(1, stopRequests);
            Assert.Equal(Guid.Empty, variable.DeviceGuid);
            Assert.Equal(-1, variable.RawButton);
        }

        /// <summary>Positive control: with no recording running, Clear does
        /// not raise a spurious stop.</summary>
        [Fact]
        public void ClearingWithoutARecordingRaisesNoStop()
        {
            var action = new MacroAction { Type = MacroActionType.AxisSet, SourceDeviceAxisIndex = 3 };
            int stopRequests = 0;
            action.RecordSourceRequested += (_, _) => stopRequests++;

            action.ClearSourceCommand.Execute(null);

            Assert.Equal(0, stopRequests);
            Assert.Equal(-1, action.SourceDeviceAxisIndex);
        }

        // C187: the action caption follows what it prints.

        /// <summary>The caption prints the volume, the loop mode and the
        /// source axis. None of those setters told it they had changed, so
        /// the list kept showing the previous text until something else
        /// happened to refresh it.</summary>
        [Theory]
        [InlineData("SoundVolume")]
        [InlineData("SoundLoop")]
        [InlineData("SourceDeviceAxisIndex")]
        public void ChangingAFieldTheCaptionPrintsRefreshesTheCaption(string field)
        {
            var action = new MacroAction
            {
                Type = MacroActionType.PlaySound,
                SoundFilePath = "C:\\sounds\\beep.wav",
                SoundVolume = 100,
            };
            bool captionChanged = false;
            action.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MacroAction.DisplayText)) captionChanged = true;
            };

            switch (field)
            {
                case "SoundVolume": action.SoundVolume = 40; break;
                case "SoundLoop": action.SoundLoop = true; break;
                case "SourceDeviceAxisIndex": action.SourceDeviceAxisIndex = 2; break;
            }

            Assert.True(captionChanged, field + " changed without refreshing the caption");
        }

        /// <summary>The caption really does carry the volume, so the
        /// notification above is not decorative.</summary>
        [Fact]
        public void TheCaptionPrintsTheVolumeItWasGiven()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.PlaySound,
                SoundFilePath = "C:\\sounds\\beep.wav",
                SoundVolume = 40,
            };
            Assert.Contains("40", action.DisplayText);
        }

        // C189: one button, one name.

        /// <summary>The compact helper resolved Nintendo lettering but not
        /// Valve, so a Valve pad named the same button two ways in one
        /// profile: the role name in the mapping grid, Btn N on the macro
        /// chip beside it.</summary>
        [Theory]
        [InlineData("steam-deck")]
        [InlineData("steam-controller")]
        [InlineData("steam-controller-2")]
        public void ValveButtonsReadTheSameOnBothLabelHelpers(string profileId)
        {
            for (int n = 1; n <= 11; n++)
            {
                string longLabel = MacroButtonNames.RawButtonLabel(profileId, n);
                string shortLabel = MacroButtonNames.RawButtonShortLabel(profileId, n);
                Assert.Equal(longLabel, shortLabel);
            }
        }

        /// <summary>Positive control: an unlettered profile still falls back
        /// to the compact numbered form, which is the only thing that should
        /// differ between the two helpers.</summary>
        [Fact]
        public void AnUnletteredProfileKeepsTheCompactFallback()
        {
            string longLabel = MacroButtonNames.RawButtonLabel(null, 7);
            string shortLabel = MacroButtonNames.RawButtonShortLabel(null, 7);
            Assert.Contains("7", longLabel);
            Assert.Contains("7", shortLabel);
            Assert.NotEqual(longLabel, shortLabel);
        }
    }
}
