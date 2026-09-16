using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the source-coercion findings in the 2026-09-15 audit.
    /// Each test names the defect it forbids.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SourceCoercionAuditFixTests
    {
        private const float G = 9.81f;

        // C217: a tilt range past 90 degrees cannot be expressed.

        private static (float, float, float) TiltedRight(double deg)
        {
            double r = deg * Math.PI / 180.0;
            return (-(float)(G * Math.Sin(r)), (float)(G * Math.Cos(r)), 0f);
        }

        private static float ReadTilt(MappingSource src, string guid)
            => SourceCoercion.EvaluateForBipolarAxisTarget(new CustomInputState(), src,
                evaluatedDeviceGuid: guid);

        private static MappingSource TiltX(string guid, double range)
            => new MappingSource
            {
                Descriptor = SourceCoercion.GyroTiltXDescriptor,
                DeviceGuid = guid,
                ParamTiltRangeDeg = range,
            };

        /// <summary>The tilt angle comes from an arc sine of one gravity
        /// component, so it tops out at 90 degrees. A range past that could
        /// never reach full deflection, and the reading folded back: past 90
        /// degrees of physical tilt the derived angle falls again, so leaning
        /// further read as leaning less and 180 degrees read as upright.</summary>
        [Theory]
        [InlineData(120.0)]
        [InlineData(150.0)]
        [InlineData(180.0)]
        public void ATiltRangePast90StillReachesFullDeflection(double range)
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = (0f, G, 0f);
                SourceCoercion.GravityProvider = _ => sample;
                Assert.Equal(0f, ReadTilt(TiltX(guid, range), guid), 3);

                sample = TiltedRight(90);
                Assert.Equal(1f, ReadTilt(TiltX(guid, range), guid), 2);
            }
            finally { SourceCoercion.GravityProvider = old; SourceCoercion.ResetGyroLeanNeutral(); }
        }

        /// <summary>Positive control: a range inside the expressible band is
        /// untouched, so the clamp did not flatten the whole envelope.</summary>
        [Fact]
        public void ARangeInsideTheBandIsUnchanged()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = (0f, G, 0f);
                SourceCoercion.GravityProvider = _ => sample;
                Assert.Equal(0f, ReadTilt(TiltX(guid, 25), guid), 3);

                // Full deflection lands at the authored range, not at 90.
                sample = TiltedRight(25);
                Assert.Equal(1f, ReadTilt(TiltX(guid, 25), guid), 2);
                sample = TiltedRight(12.5);
                Assert.Equal(0.5f, ReadTilt(TiltX(guid, 25), guid), 2);
            }
            finally { SourceCoercion.GravityProvider = old; SourceCoercion.ResetGyroLeanNeutral(); }
        }

        // C219: the right gyro family had lanes the array could not hold.

        /// <summary>The lane math reaches index 8 for the explicit right
        /// family, and the state array held six. The bounds guard then handed
        /// back the raw rate, so those rows read UNSMOOTHED while the setting
        /// looked wired.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void EveryGyroLaneActuallySmooths(int lane)
        {
            string guid = "lane-array-" + Guid.NewGuid();

            // A lane starts from zero, so one step at alpha 0.5 lands halfway.
            // A lane the array cannot hold returns the raw rate untouched.
            SourceCoercion.BeginPollFrame();
            float smoothed = SourceCoercion.ApplyGyroSmoothing(guid, 0, lane, 10f, 0.5f);

            Assert.Equal(5f, smoothed, 3);
        }

        // C224: the bend button ignored its own deadzone.

        private static CustomInputState MidiState(int pitchBend)
        {
            var st = new CustomInputState();
            st.Midi = new MidiInputState();
            st.Midi.PitchBend = pitchBend;
            return st;
        }

        private static bool ReadBendButton(int pitchBend, int deadZone)
            => SourceCoercion.EvaluateForButtonTarget(
                MidiState(pitchBend),
                new MappingSource { Descriptor = "Midi Pitch", DeadZone = deadZone },
                globalThresholdPercent: 50);

        /// <summary>Every other derived button read in this switch consults
        /// the source deadzone, including the CC arm directly above. The bend
        /// arm hardcoded half scale, so the row's own value did nothing.</summary>
        [Fact]
        public void TheBendButtonHonorsItsOwnDeadzone()
        {
            // A quarter-scale bend is below the default half-scale gate.
            Assert.False(ReadBendButton(MidiInputState.PitchBendCenter + 8000, 50));
            // With a low deadzone the same bend answers.
            Assert.True(ReadBendButton(MidiInputState.PitchBendCenter + 8000, 10));
            // And a high one refuses a bend that the default would pass.
            Assert.True(ReadBendButton(MidiInputState.PitchBendCenter + 20000, 50));
            Assert.False(ReadBendButton(MidiInputState.PitchBendCenter + 20000, 90));
        }

        /// <summary>The default is bit-identical to the half scale this used
        /// to hardcode, so an untouched row did not move. Both the unset
        /// spellings the threshold helper treats as unset are checked.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        public void AnUntouchedBendRowKeepsItsHalfScaleGate(int deadZone)
        {
            Assert.False(ReadBendButton(MidiInputState.PitchBendCenter + 16000, deadZone));
            Assert.True(ReadBendButton(MidiInputState.PitchBendCenter + 16500, deadZone));
            // Symmetric: the arm takes the magnitude either way.
            Assert.True(ReadBendButton(MidiInputState.PitchBendCenter - 16500, deadZone));
        }

        // C225: a negative pad index indexed the array at that value.

        /// <summary>Two of this reader's three click arms route through the
        /// bounds-safe lookup. The third checked only the upper bound, so a
        /// descriptor carrying a negative pad index threw instead of reading
        /// unavailable.</summary>
        [Theory]
        [InlineData("Touchpad -1 Click")]
        [InlineData("Touchpad -3 Click")]
        public void ANegativePadIndexReadsUnavailableInsteadOfThrowing(string descriptor)
        {
            var st = new CustomInputState();
            st.Touchpads = new[] { new TouchpadInputState(1), new TouchpadInputState(1) };
            st.Touchpads[1].Clicked = true;

            bool read = SourceCoercion.EvaluateForButtonTarget(
                st, new MappingSource { Descriptor = descriptor }, globalThresholdPercent: 50);

            Assert.False(read);
        }

        /// <summary>Positive control: a real second pad still answers, so the
        /// guard did not silence the case the arm exists for.</summary>
        [Fact]
        public void ASecondPadClickStillAnswers()
        {
            var st = new CustomInputState();
            st.Touchpads = new[] { new TouchpadInputState(1), new TouchpadInputState(1) };
            st.Touchpads[1].Clicked = true;

            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                st, new MappingSource { Descriptor = "Touchpad 1 Click" }, globalThresholdPercent: 50));

            st.Touchpads[1].Clicked = false;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                st, new MappingSource { Descriptor = "Touchpad 1 Click" }, globalThresholdPercent: 50));
        }
        // C221: the cursor lane ignored the window, the axis, and one row's scale.

        private const float PollDt = 0.004f;
        private static readonly long Freq = System.Diagnostics.Stopwatch.Frequency;
        private static int _slotSeq = 9000;
        private static int NewSlot() => _slotSeq++;

        private static CustomInputState PadAt(float x, float y)
        {
            var st = new CustomInputState();
            st.Touchpads = new[]
            {
                new TouchpadInputState
                {
                    MaxFingers = 2,
                    FingerX = new[] { x, 0f },
                    FingerY = new[] { y, 0f },
                    FingerPressure = new[] { 1f, 0f },
                    FingerDown = new[] { true, false },
                },
            };
            return st;
        }

        /// <summary>One poll: seed the contact, then drag. Returns what the
        /// named source delivers to the named mouse axis.</summary>
        private static float Drag(MappingSource src, bool forX,
            float x0, float y0, float x1, float y1)
        {
            int slot = NewSlot();
            SourceCoercion.BeginPollFrame();
            SourceCoercion.ReadTouchpadMouseCounts(PadAt(x0, y0), src, slot, "",
                PollDt, forX, 0L, Freq);
            SourceCoercion.BeginPollFrame();
            var (cx, cy) = SourceCoercion.ReadTouchpadMouseCounts(PadAt(x1, y1), src, slot, "",
                PollDt, forX, (long)(PollDt * Freq), Freq);
            return forX ? cx : cy;
        }

        /// <summary>A windowed source only answers while the finger is inside
        /// its window. The window was parsed and discarded here, so a row
        /// authored as the left half drove the cursor from anywhere on the pad
        /// and the half the user picked did nothing.</summary>
        [Fact]
        public void AWindowedSourceOnlyAnswersInsideItsWindow()
        {
            var left = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Left" };

            // Inside the left half: the drag answers.
            Assert.NotEqual(0f, Drag(left, forX: true, 0.20f, 0.50f, 0.35f, 0.50f));
            // The same drag on the right half does not.
            Assert.Equal(0f, Drag(left, forX: true, 0.70f, 0.50f, 0.85f, 0.50f));
        }

        /// <summary>Positive control: an unwindowed source answers from both
        /// halves, so the gate above is the window and not the geometry.</summary>
        [Fact]
        public void AnUnwindowedSourceAnswersFromEitherHalf()
        {
            var whole = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X" };

            Assert.NotEqual(0f, Drag(whole, forX: true, 0.20f, 0.50f, 0.35f, 0.50f));
            Assert.NotEqual(0f, Drag(whole, forX: true, 0.70f, 0.50f, 0.85f, 0.50f));
        }

        /// <summary>The source names the component and the row names where it
        /// lands. Reading by the target row alone meant a source bound across
        /// the axes quietly delivered the other one, so the axis the user
        /// picked was ignored and the row looked like it worked.</summary>
        [Fact]
        public void TheSourceAxisPicksTheComponentNotTheTargetRow()
        {
            var ySrc = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Y" };

            // A Y source on the Mouse X row: only vertical finger motion
            // answers, and it lands on X.
            Assert.Equal(0f, Drag(ySrc, forX: true, 0.20f, 0.50f, 0.60f, 0.50f));
            Assert.NotEqual(0f, Drag(ySrc, forX: true, 0.50f, 0.20f, 0.50f, 0.60f));
        }

        /// <summary>Each row carries its own Sensitivity. The ball integrates
        /// once per poll for whichever row reaches it first, so folding a
        /// row's scale into that shared step handed the first row's value to
        /// both axes and left the other row's inert.</summary>
        [Fact]
        public void EachRowAppliesItsOwnSensitivity()
        {
            var plain = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X" };
            var doubled = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X", Sensitivity = 2.0 };

            float a = Drag(plain, forX: true, 0.30f, 0.50f, 0.50f, 0.50f);
            float b = Drag(doubled, forX: true, 0.30f, 0.50f, 0.50f, 0.50f);

            Assert.NotEqual(0f, a);
            Assert.Equal(2f * a, b, 3);
        }
        // C223: the motion lean pair reads Invert itself.

        private static bool ReadLeanButton(MappingSource src, string guid)
            => SourceCoercion.EvaluateForButtonTarget(
                new CustomInputState(), src, globalThresholdPercent: 50,
                slotIndex: 0, evaluatedDeviceGuid: guid);

        /// <summary>The lean arm consumes Invert as its direction selector,
        /// the way the gravity-tilt family beside it does and the way its own
        /// comment says. The outer flip applied it a second time, so an
        /// inverted row sat PRESSED at rest and released on the very
        /// direction the user asked for. Without the half-axis flag it was
        /// worse: the any-direction test is false at rest, so the flip held
        /// the button down whenever the pad was still.</summary>
        [Theory]
        [InlineData(true)]   // half-axis: Invert picks the direction
        [InlineData(false)]  // any-direction: Invert is not meaningful
        public void AnInvertedMotionLeanButtonIsReleasedAtRest(bool halfAxis)
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                SourceCoercion.GravityProvider = _ => (0f, G, 0f);
                var src = new MappingSource
                {
                    Descriptor = SourceCoercion.MotionLeanDescriptor,
                    DeviceGuid = guid,
                    HalfAxis = halfAxis,
                    Invert = true,
                };

                // Upright and still. Nothing is being asked for, so nothing
                // may be pressed.
                Assert.False(ReadLeanButton(src, guid));
            }
            finally { SourceCoercion.GravityProvider = old; SourceCoercion.ResetGyroLeanNeutral(); }
        }

        /// <summary>Positive control: an uninverted row is released at rest
        /// too, so the test above is about Invert and not about the lean read
        /// being dead.</summary>
        [Fact]
        public void AnUninvertedMotionLeanButtonIsAlsoReleasedAtRest()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                SourceCoercion.GravityProvider = _ => (0f, G, 0f);
                var src = new MappingSource
                {
                    Descriptor = SourceCoercion.MotionLeanDescriptor,
                    DeviceGuid = guid,
                    HalfAxis = true,
                };
                Assert.False(ReadLeanButton(src, guid));
            }
            finally { SourceCoercion.GravityProvider = old; SourceCoercion.ResetGyroLeanNeutral(); }
        }
    }
}
