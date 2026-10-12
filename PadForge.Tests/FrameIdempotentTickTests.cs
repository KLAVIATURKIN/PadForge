using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard: Incremental and Ramped advance once per FRAME, not
    /// once per device pass.
    ///
    /// <para>The Extended/KBM/MIDI evaluators run once per assigned DEVICE
    /// per frame, and a source with an empty device GUID matches every
    /// device, so a slot with two assigned devices reaches the same
    /// (slot, target, sourceIndex) accumulator twice in a single frame.
    /// StickTrim and FlickStick were both gated against exactly this pass
    /// model. These two were not, and each pass advanced them by the full
    /// frame dt: a 2 s sweep finished in 1 s on a two-device slot and
    /// 0.66 s on three, and a Ramped attack time was wrong by the same
    /// factor.</para>
    ///
    /// <para>An Incremental or Ramp key with no controller of its own is
    /// read through SourceCoercion.KeyHeldProvider when the app has
    /// installed it, and other test classes install it. So this class runs
    /// in their collection and clears the hook for each test: with it left
    /// in place by a class running beside this one, every key here read
    /// released.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class FrameIdempotentTickTests : System.IDisposable
    {
        private const string Up = "Button 0";
        private const string Down = "Button 1";

        private readonly System.Func<string, string, int, bool> _savedKeyProvider = SourceCoercion.KeyHeldProvider;
        private readonly System.Func<int, string, string, bool> _savedHeldBack = SourceCoercion.KeyHeldBackProvider;

        public FrameIdempotentTickTests()
        {
            SourceCoercion.KeyHeldProvider = null;
            SourceCoercion.KeyHeldBackProvider = null;
        }

        public void Dispose()
        {
            SourceCoercion.KeyHeldProvider = _savedKeyProvider;
            SourceCoercion.KeyHeldBackProvider = _savedHeldBack;
        }

        private static CustomInputState StateWith(bool up, bool down)
        {
            var s = new CustomInputState();
            s.Buttons[0] = up;
            s.Buttons[1] = down;
            return s;
        }

        private static MappingSource Incremental() => new MappingSource
        {
            Kind = "Incremental",
            Descriptor = "LeftTrigger",
            ParamUp = Up,
            ParamDown = Down,
            ParamRate = 0.5,     // full range in 2 s
            ParamMin = 0,
            ParamMax = 1,
            ParamSticky = true,
        };

        private static MappingSource Ramped() => new MappingSource
        {
            Kind = "Ramped",
            Descriptor = "LeftStickX",
            ParamUp = Up,
            ParamDown = Down,
            ParamAttackTime = 1.0,
            ParamReleaseTime = 1.0,
        };

        [Fact]
        public void Incremental_TwoDevicePassesInOneFrame_AdvanceOnce()
        {
            var oneDevice = new SourceKindRuntime();
            var twoDevices = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);

            double a = 0, b = 0;
            for (int frame = 0; frame < 20; frame++)
            {
                oneDevice.FrameSeq++;
                a = oneDevice.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);

                twoDevices.FrameSeq++;
                b = twoDevices.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
                b = twoDevices.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            }

            // 20 frames x 50 ms = 1 s at half range per second = 0.5.
            Assert.Equal(0.5, a, 3);
            Assert.Equal(a, b, 6);   // pre-fix b was 1.0: double rate
        }

        [Fact]
        public void Ramped_TwoDevicePassesInOneFrame_AdvanceOnce()
        {
            var oneDevice = new SourceKindRuntime();
            var twoDevices = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);

            double a = 0, b = 0;
            for (int frame = 0; frame < 10; frame++)
            {
                oneDevice.FrameSeq++;
                a = oneDevice.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);

                twoDevices.FrameSeq++;
                b = twoDevices.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);
                b = twoDevices.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);
            }

            Assert.Equal(0.5, a, 3);   // 0.5 s of a 1 s attack
            Assert.Equal(a, b, 6);
        }

        [Fact]
        public void SecondPassReplaysTheSameValue_ItDoesNotReturnZero()
        {
            // The replay must return the frame's real output. Returning 0
            // (or the pre-tick accumulator) would make a two-device slot
            // flicker between the true value and a stale one, depending on
            // which pass wrote the target last.
            var rt = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);
            rt.FrameSeq++;
            double first = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double second = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double third = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            Assert.True(first > 0);
            Assert.Equal(first, second, 6);
            Assert.Equal(first, third, 6);
        }

        [Fact]
        public void DifferentSourceIndexesKeepIndependentAccumulators()
        {
            // The gate is per (slot, target, sourceIndex): a genuinely
            // different source on the same target must still tick in the
            // same frame.
            var rt = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);
            rt.FrameSeq++;
            double s0 = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double s1 = rt.TickIncremental(0, "LeftTrigger", 1, Incremental(), held, 0.05);
            Assert.True(s0 > 0);
            Assert.Equal(s0, s1, 6);   // same math, separate accumulators

            rt.FrameSeq++;
            double s0b = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            Assert.True(s0b > s0);     // index 0 advanced on the new frame
        }

        [Fact]
        public void ReleaseEdgeIsNotDoubleProcessed()
        {
            // Non-sticky Incremental snaps to ParamMin when neither key is
            // held. Two passes in one frame must not re-run that.
            var src = Incremental();
            src.ParamSticky = false;
            var rt = new SourceKindRuntime();

            var held = StateWith(up: true, down: false);
            for (int f = 0; f < 10; f++) { rt.FrameSeq++; rt.TickIncremental(0, "T", 0, src, held, 0.05); }

            var released = StateWith(up: false, down: false);
            rt.FrameSeq++;
            double first = rt.TickIncremental(0, "T", 0, src, released, 0.05);
            double second = rt.TickIncremental(0, "T", 0, src, released, 0.05);
            Assert.Equal(src.ParamMin, first, 6);
            Assert.Equal(first, second, 6);
        }

        /// <summary>A menu commit made by leaving a layer is read on that
        /// layer's row in a frame where the engaged layer's row for the same
        /// target has ticked already, and the two share the value. Read as
        /// that commit (SourceKindRuntime.UseCommitLayer), it is a reader of
        /// its own in the frame: its key steps the value, where its tick
        /// once replayed the engaged row's. The value is still stepped once
        /// a frame, so a second layer's commit holding the same key moves it
        /// no further. A second pass of a reader replays its first, as a
        /// second device pass does, and outside a commit a second row for
        /// the same place still replays.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFrameStepsTheValueOnceWhoeverReadsIt(bool ramped)
        {
            var rt = new SourceKindRuntime();
            var rest = StateWith(up: false, down: false);
            var held = StateWith(up: true, down: false);
            double Tick(CustomInputState s) => ramped
                ? rt.TickRamped(0, "T", 0, Ramped(), s, 0.05)
                : rt.TickIncremental(0, "T", 0, Incremental(), s, 0.05);
            double step = ramped ? 0.05 : 0.025;

            rt.FrameSeq++;
            Assert.Equal(0, Tick(rest), 6);                 // the engaged row, its keys at rest
            Assert.Equal(0, Tick(held), 6);                 // another row in the same frame replays it
            using (SourceKindRuntime.UseCommitLayer("L1"))
            {
                Assert.Equal(step, Tick(held), 6);          // the commit's key steps the value they share
                Assert.Equal(step, Tick(rest), 6);          // and its second pass replays, whatever it reads
                using (SourceKindRuntime.UseCommitLayer("L2"))
                    Assert.Equal(step, Tick(held), 6);      // another layer's commit, the same key: still one step
                Assert.Equal(step, Tick(held), 6);          // back in the first commit's scope
            }
            Assert.Equal(0, Tick(held), 6);                 // the scope ended: the engaged row's own replay

            // The next frame starts from the one step the frame made.
            rt.FrameSeq++;
            Assert.Equal(2 * step, Tick(held), 6);

            // A reset of the row drops the frame's start and keys with the
            // value, and every reader's replay: a commit's tick in the same
            // frame starts over, where it would have returned two steps.
            rt.ResetForRow(0, "T");
            using (SourceKindRuntime.UseCommitLayer("L2"))
                Assert.Equal(step, Tick(held), 6);
        }

        // ── One reader a frame: the values the engine gave before ──

        /// <summary>The Incremental tick as it stood while each reader
        /// stepped the value on its own (SourceKindRuntime before an
        /// accumulator was stepped once a frame for all its readers), with
        /// the two keys handed in. One reader a frame must still give these
        /// values to the bit.</summary>
        private static double IncrementalBefore(ref double accumulator, MappingSource src, bool up, bool down, double dt)
        {
            double current = accumulator;
            if (current < src.ParamMin) current = src.ParamMin;
            if (current > src.ParamMax) current = src.ParamMax;

            double rate = src.ParamRate;
            if (rate < 0) rate = 0;

            double range = src.ParamMax - src.ParamMin;
            if (range <= 0) range = 1.0;

            double step = rate * range * dt;

            if (up && !down)
            {
                current += step;
                if (current > src.ParamMax) current = src.ParamMax;
            }
            else if (down && !up)
            {
                current -= step;
                if (current < src.ParamMin) current = src.ParamMin;
            }
            else if (!up && !down && !src.ParamSticky)
            {
                current = src.ParamMin;
            }

            accumulator = current;
            return current;
        }

        /// <summary>The Ramp tick of the same vintage.</summary>
        private static double RampedBefore(ref double accumulator, MappingSource src, bool up, bool down, double dt)
        {
            double v = accumulator;
            double attack = src.ParamAttackTime; if (attack < 0) attack = 0;
            double release = src.ParamReleaseTime; if (release < 0) release = 0;
            double rev = src.ParamReverseMultiplier; if (rev < 1) rev = 1;

            double attackStep = attack > 0 ? dt / attack : 1.0;
            double releaseStep = release > 0 ? dt / release : 1.0;

            if (up && !down)
            {
                if (v < 0)
                {
                    v += releaseStep * (src.ParamAutocenter ? rev : 1.0);
                    if (v > 0) v = 0;
                }
                else
                {
                    v += attackStep;
                    if (v > 1) v = 1;
                }
            }
            else if (down && !up)
            {
                if (v > 0)
                {
                    v -= releaseStep * (src.ParamAutocenter ? rev : 1.0);
                    if (v < 0) v = 0;
                }
                else
                {
                    v -= attackStep;
                    if (v < -1) v = -1;
                }
            }
            else if (src.ParamAutocenter)
            {
                if (v > 0) { v -= releaseStep; if (v < 0) v = 0; }
                else if (v < 0) { v += releaseStep; if (v > 0) v = 0; }
            }

            if (v < -1) v = -1; else if (v > 1) v = 1;
            accumulator = v;
            return v;
        }

        /// <summary>Both kinds, sticky and not, autocenter on and off, with
        /// attack and release times of zero and of a new source's 0.30 s.</summary>
        public static TheoryData<string, bool, bool, double> KindCases()
        {
            var cases = new TheoryData<string, bool, bool, double>();
            foreach (string kind in new[] { "Incremental", "Ramped" })
                foreach (bool sticky in new[] { true, false })
                    foreach (bool autocenter in new[] { true, false })
                        foreach (double time in new[] { 0.0, 0.30 })
                            cases.Add(kind, sticky, autocenter, time);
            return cases;
        }

        /// <summary>A key source as the editor makes one, with the settings
        /// a case varies.</summary>
        private static MappingSource KeySource(string kind, string up, string down,
            bool sticky, bool autocenter, double time) => new()
        {
            Kind = kind, ParamUp = up, ParamDown = down,
            ParamSticky = sticky, ParamAutocenter = autocenter,
            ParamAttackTime = time, ParamReleaseTime = time,
        };

        private static readonly double[] FrameTimes = { 0.001, 0.004, 1.0 / 60.0, 0.05 };

        private static double Tick(SourceKindRuntime rt, MappingSource src, CustomInputState state, double dt)
            => src.Kind == "Ramped"
                ? rt.TickRamped(0, "T", 0, src, state, dt)
                : rt.TickIncremental(0, "T", 0, src, state, dt);

        private static double Before(ref double accumulator, MappingSource src, bool up, bool down, double dt)
            => src.Kind == "Ramped"
                ? RampedBefore(ref accumulator, src, up, down, dt)
                : IncrementalBefore(ref accumulator, src, up, down, dt);

        /// <summary>A frame with one reader gives the value it gave before
        /// accumulators were stepped once a frame for all their readers, to
        /// the bit: 3000 frames of keys held, released, crossed and held
        /// together for runs of frames, at frame times from 1 ms to 50 ms,
        /// with the Incremental range narrowed and widened on the way. A
        /// second pass in a frame replays the first.</summary>
        [Theory]
        [MemberData(nameof(KindCases))]
        public void OneReaderAFrameGivesTheValuesItGaveBefore(string kind, bool sticky, bool autocenter, double time)
        {
            var rt = new SourceKindRuntime();
            var src = KeySource(kind, Up, Down, sticky, autocenter, time);
            var random = new System.Random(20261011);
            var state = new CustomInputState();
            double before = 0;
            bool up = false, down = false;
            int run = 0;
            for (int frame = 0; frame < 3000; frame++)
            {
                if (run-- <= 0)
                {
                    int pattern = random.Next(4);
                    up = pattern is 1 or 3;
                    down = pattern is 2 or 3;
                    run = random.Next(1, 60);
                }
                if (frame == 1000) { src.ParamMin = 0.2; src.ParamMax = 0.6; }
                if (frame == 2000) { src.ParamMin = -1; src.ParamMax = 1; }
                double dt = FrameTimes[random.Next(FrameTimes.Length)];
                state.Buttons[0] = up;
                state.Buttons[1] = down;

                rt.FrameSeq++;
                double expected = Before(ref before, src, up, down, dt);
                double actual = Tick(rt, src, state, dt);
                Assert.True(System.BitConverter.DoubleToInt64Bits(expected) == System.BitConverter.DoubleToInt64Bits(actual),
                    $"frame {frame}: expected {expected:R}, got {actual:R}");
                double again = Tick(rt, src, state, dt);
                Assert.True(System.BitConverter.DoubleToInt64Bits(actual) == System.BitConverter.DoubleToInt64Bits(again),
                    $"frame {frame}: a second pass gave {again:R} after {actual:R}");
            }
        }

        // ── Two readers of one value in a frame ──
        //
        // A menu commit made by leaving a layer is read on that layer's row
        // (SourceKindRuntime.UseCommitLayer) in a frame where the engaged
        // layer's row for the same target has ticked already, and the two
        // share the value where each has a key source in the same place.

        /// <summary>A state with the keys of the three rows these tests
        /// read: the commit's on buttons 0 and 1, the engaged row's on 2 and
        /// 3, and on 4 and 5 the keys of one row that holds whatever either
        /// of the two holds.</summary>
        private static CustomInputState TwoRowState(bool commitUp, bool commitDown, bool engagedUp, bool engagedDown)
        {
            var s = new CustomInputState();
            s.Buttons[0] = commitUp;
            s.Buttons[1] = commitDown;
            s.Buttons[2] = engagedUp;
            s.Buttons[3] = engagedDown;
            s.Buttons[4] = commitUp || engagedUp;
            s.Buttons[5] = commitDown || engagedDown;
            return s;
        }

        private static bool SameBits(double a, double b)
            => System.BitConverter.DoubleToInt64Bits(a) == System.BitConverter.DoubleToInt64Bits(b);

        /// <summary>The value is stepped once a frame, from where the frame
        /// started, with the keys every reader has read so far. So two
        /// readers leave it where one row holding all their keys would, to
        /// the bit, whichever of them ticks first, and each reader's own
        /// tick returns one step from the frame's start with the keys read
        /// up to it. The keys are held, released, crossed and held together
        /// on both rows for runs of frames.</summary>
        [Theory]
        [MemberData(nameof(KindCases))]
        public void TwoReadersStepTheValueAsOneRowHoldingAllTheirKeysWould(string kind, bool sticky, bool autocenter, double time)
        {
            var engagedFirst = new SourceKindRuntime();
            var commitFirst = new SourceKindRuntime();
            var oneRow = new SourceKindRuntime();
            var commit = KeySource(kind, "Button 0", "Button 1", sticky, autocenter, time);
            var engaged = KeySource(kind, "Button 2", "Button 3", sticky, autocenter, time);
            var all = KeySource(kind, "Button 4", "Button 5", sticky, autocenter, time);
            var random = new System.Random(20261012);
            bool cUp = false, cDown = false, eUp = false, eDown = false;
            int run = 0;
            double start = 0;
            for (int frame = 0; frame < 3000; frame++)
            {
                if (run-- <= 0)
                {
                    int pattern = random.Next(16);
                    cUp = (pattern & 1) != 0;
                    cDown = (pattern & 2) != 0;
                    eUp = (pattern & 4) != 0;
                    eDown = (pattern & 8) != 0;
                    run = random.Next(1, 40);
                }
                double dt = FrameTimes[random.Next(FrameTimes.Length)];
                var state = TwoRowState(cUp, cDown, eUp, eDown);

                oneRow.FrameSeq++;
                double expected = Tick(oneRow, all, state, dt);

                double engagedAlone = start, commitAlone = start;
                Before(ref engagedAlone, engaged, eUp, eDown, dt);
                Before(ref commitAlone, commit, cUp, cDown, dt);

                engagedFirst.FrameSeq++;
                double first = Tick(engagedFirst, engaged, state, dt);
                double second;
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    second = Tick(engagedFirst, commit, state, dt);
                Assert.True(SameBits(engagedAlone, first), $"frame {frame}: the engaged row's own tick gave {first:R}, not {engagedAlone:R}");
                Assert.True(SameBits(expected, second), $"frame {frame}: engaged row first, {second:R}, not {expected:R}");
                Assert.True(SameBits(first, Tick(engagedFirst, engaged, state, dt)), $"frame {frame}: the engaged row's second pass");

                commitFirst.FrameSeq++;
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    first = Tick(commitFirst, commit, state, dt);
                second = Tick(commitFirst, engaged, state, dt);
                Assert.True(SameBits(commitAlone, first), $"frame {frame}: the commit's own tick gave {first:R}, not {commitAlone:R}");
                Assert.True(SameBits(expected, second), $"frame {frame}: commit first, {second:R}, not {expected:R}");

                start = expected;
            }
        }

        /// <summary>The engaged row's tick with its keys at rest does not
        /// undo the step of a key the commit holds. It did, every frame: a
        /// Ramp key at a new source's 0.30 s times took one release step off
        /// and put one attack step on, and an Incremental key beside a row
        /// that is not sticky was put back on its floor first, so neither
        /// got past one step. The value climbs a step a frame for as long as
        /// the commit reads, and the engaged row's own rule takes over once
        /// it reads alone.</summary>
        [Theory]
        [InlineData("Ramped")]
        [InlineData("Incremental")]
        public void TheEngagedRowsRestTickLeavesTheCommitsStepStanding(string kind)
        {
            var rt = new SourceKindRuntime();
            // 0.01 a frame: a Ramp at 0.30 s over a 3 ms frame, an Incremental
            // at half its range a second over a 20 ms one.
            double dt = kind == "Ramped" ? 0.003 : 0.02;
            var engaged = KeySource(kind, "Button 2", "Button 3", sticky: false, autocenter: true, time: 0.30);
            var commit = KeySource(kind, "Button 0", "Button 1", sticky: true, autocenter: true, time: 0.30);
            var state = TwoRowState(commitUp: true, commitDown: false, engagedUp: false, engagedDown: false);

            for (int frame = 1; frame <= 5; frame++)
            {
                rt.FrameSeq++;
                double atRest = Tick(rt, engaged, state, dt);
                // The engaged row's own output: a Ramp one release step below
                // where the frame started, an Incremental on its floor.
                Assert.Equal(kind == "Ramped" ? System.Math.Max(0, (frame - 2) * 0.01) : 0, atRest, 9);
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    Assert.Equal(frame * 0.01, Tick(rt, commit, state, dt), 9);
            }

            // The commit is over. The engaged row reads alone from the value
            // the commit left: a Ramp releases from it, and a row that is not
            // sticky goes back to its floor.
            rt.FrameSeq++;
            Assert.Equal(kind == "Ramped" ? 0.04 : 0, Tick(rt, engaged, state, dt), 9);
        }

        /// <summary>A key both readers hold steps the value once a frame. A
        /// cell that the engaged layer's row and the departed layer's row
        /// both name as a key (a layer copied from Base) stepped it twice,
        /// so one commit raised it twice as far made by leaving the layer
        /// as made inside it.</summary>
        [Theory]
        [InlineData("Ramped")]
        [InlineData("Incremental")]
        public void AKeyBothReadersHoldStepsTheValueOnce(string kind)
        {
            var rt = new SourceKindRuntime();
            double dt = kind == "Ramped" ? 0.003 : 0.02;
            var engaged = KeySource(kind, "Button 2", "Button 3", sticky: true, autocenter: true, time: 0.30);
            var commit = KeySource(kind, "Button 0", "Button 1", sticky: true, autocenter: true, time: 0.30);
            var state = TwoRowState(commitUp: true, commitDown: false, engagedUp: true, engagedDown: false);

            for (int frame = 1; frame <= 5; frame++)
            {
                rt.FrameSeq++;
                Assert.Equal(frame * 0.01, Tick(rt, engaged, state, dt), 9);
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    Assert.Equal(frame * 0.01, Tick(rt, commit, state, dt), 9);
            }
        }

        /// <summary>Opposite keys on the two readers read as both held, as
        /// they would on one row: an Incremental value holds, and a Ramp
        /// releases toward its center.</summary>
        [Theory]
        [InlineData("Ramped")]
        [InlineData("Incremental")]
        public void OppositeKeysOfTwoReadersReadAsBothHeld(string kind)
        {
            var rt = new SourceKindRuntime();
            double dt = kind == "Ramped" ? 0.003 : 0.02;
            var engaged = KeySource(kind, "Button 2", "Button 3", sticky: true, autocenter: true, time: 0.30);
            var commit = KeySource(kind, "Button 0", "Button 1", sticky: true, autocenter: true, time: 0.30);

            var up = TwoRowState(commitUp: false, commitDown: false, engagedUp: true, engagedDown: false);
            for (int frame = 1; frame <= 10; frame++)
            {
                rt.FrameSeq++;
                Tick(rt, engaged, up, dt);
            }

            var crossed = TwoRowState(commitUp: true, commitDown: false, engagedUp: false, engagedDown: true);
            for (int frame = 1; frame <= 3; frame++)
            {
                rt.FrameSeq++;
                Tick(rt, engaged, crossed, dt);
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    Assert.Equal(kind == "Ramped" ? 0.10 - frame * 0.01 : 0.10, Tick(rt, commit, crossed, dt), 9);
            }
        }

        /// <summary>Clear, ResetForSlot and ResetForRow drop the frame's
        /// keys and its starting value with the accumulator, and each
        /// reader's replay with them. After one, in the same frame, a commit
        /// that reads rest steps nothing with the key the engaged row read
        /// before it, and the engaged row's next tick is a tick of its own
        /// and no replay.</summary>
        [Theory]
        [InlineData("Ramped", "clear")]
        [InlineData("Ramped", "slot")]
        [InlineData("Ramped", "row")]
        [InlineData("Incremental", "clear")]
        [InlineData("Incremental", "slot")]
        [InlineData("Incremental", "row")]
        public void EveryResetDropsTheFramesKeysWithTheValue(string kind, string reset)
        {
            var rt = new SourceKindRuntime();
            double dt = kind == "Ramped" ? 0.003 : 0.02;
            var engaged = KeySource(kind, "Button 2", "Button 3", sticky: true, autocenter: false, time: 0.30);
            var commit = KeySource(kind, "Button 0", "Button 1", sticky: true, autocenter: false, time: 0.30);
            var held = TwoRowState(commitUp: false, commitDown: false, engagedUp: true, engagedDown: false);
            var rest = TwoRowState(false, false, false, false);

            for (int frame = 1; frame <= 3; frame++)
            {
                rt.FrameSeq++;
                Assert.Equal(frame * 0.01, Tick(rt, engaged, held, dt), 9);
            }

            // Positive control: with no reset, a commit that reads rest in
            // this frame returns the step the engaged row's key made.
            using (SourceKindRuntime.UseCommitLayer("L0"))
                Assert.Equal(0.03, Tick(rt, commit, rest, dt), 9);

            if (reset == "clear") rt.Clear();
            else if (reset == "slot") rt.ResetForSlot(0);
            else rt.ResetForRow(0, "T");

            using (SourceKindRuntime.UseCommitLayer("L1"))
                Assert.Equal(0, Tick(rt, commit, rest, dt), 9);
            Assert.Equal(0, Tick(rt, engaged, rest, dt), 9);
        }

        /// <summary>A tick allocates nothing, with one reader or two, on a
        /// second pass, and whatever its keys name: a button, a hat
        /// direction, a "Gamepad ..." name for either, a stick ring, a touch
        /// sensor, or nothing, with the app's hold-back test wired or not.
        /// The frame's record is made once, when the value is first read, the
        /// keys read so far are two flags on it, and a key is parsed where it
        /// stands. A "Button N" key cost a string on every read, a hat key two
        /// arrays and three or four strings, and a ring or a touch sensor a
        /// string for each time its name was looked up. The count is taken in
        /// windows the runtime left alone (<see cref="AllocationWindow"/>): a
        /// pause of the runtime with no collection counted grew the thread's
        /// count by a few kilobytes the code never allocated, 7,728 bytes in
        /// the window that once failed this test. Two positive controls run
        /// in the same kind of window: an allocation beside the ticks is
        /// counted, and so is one inside them, made by the hold-back test a
        /// held key's read asks.</summary>
        [Theory]
        [InlineData("Ramped", "Button 0", false)]
        [InlineData("Incremental", "Button 0", false)]
        [InlineData("Incremental", "Button 0", true)]
        [InlineData("Ramped", "POV 0 Up", false)]
        [InlineData("Incremental", "POV 0 Up", true)]
        [InlineData("Incremental", "Gamepad ButtonA", false)]
        [InlineData("Ramped", "Gamepad DPadUp", true)]
        [InlineData("Incremental", "Gamepad LeftStickRing", true)]
        [InlineData("Ramped", "Gamepad RightStickTouch", false)]
        [InlineData("Ramped", "", false)]
        [InlineData("Incremental", "", true)]
        public void ATickAllocatesNothing(string kind, string key, bool heldBackWired)
        {
            var rt = new SourceKindRuntime();
            var src = KeySource(kind, key, key, sticky: true, autocenter: true, time: 0.30);
            var state = StateWith(up: true, down: false);
            state.Povs[0] = 0;
            bool keyHeld = key.Length != 0 && SourceKindRuntime.ReadKeyOn(state, key, null, 0);
            if (heldBackWired) SourceCoercion.KeyHeldBackProvider = PadForge.Common.Input.InputManager.KeyHeldBack;

            // Each frame: the engaged row twice (a second device pass) and a
            // commit once, so two ticks that read both keys and one replay.
            void Frame()
            {
                rt.FrameSeq++;
                Tick(rt, src, state, 0.001);
                Tick(rt, src, state, 0.001);
                using (SourceKindRuntime.UseCommitLayer("L1"))
                    Tick(rt, src, state, 0.001);
            }
            Assert.Equal(new long[] { 0, 0, 0 }, AllocationWindow.Clean(Frame, 1000));

            foreach (long beside in AllocationWindow.Clean(() => { Frame(); AllocationWindow.Kept = new object(); }, 1000))
                Assert.True(beside >= 1000 * 24, "an allocation beside the ticks went uncounted: " + beside);
            if (keyHeld)
            {
                SourceCoercion.KeyHeldBackProvider = AllocationWindow.AllocatingHoldBack;
                foreach (long inside in AllocationWindow.Clean(Frame, 1000))
                    Assert.True(inside >= 1000 * 24, "an allocation inside the ticks went uncounted: " + inside);
            }
        }

        [Fact]
        public void ResetForSlot_DropsTheAccumulator_NotJustTheReplayStamp()
        {
            // A wholesale row replacement invalidates every state family
            // keyed on (slot, target, srcIdx). Clearing the frame-replay
            // stamp while leaving the accumulated value just meant the
            // re-authored row resumed the previous occupant's cruise
            // position on the very next tick.
            var rt = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);
            for (int f = 0; f < 20; f++) { rt.FrameSeq++; rt.TickIncremental(0, "T", 0, Incremental(), held, 0.05); }

            rt.FrameSeq++;
            double before = rt.TickIncremental(0, "T", 0, Incremental(), held, 0.05);
            Assert.True(before > 0.4, $"fixture never accumulated (v={before})");

            rt.ResetForSlot(0);
            rt.FrameSeq++;
            double after = rt.TickIncremental(0, "T", 0, Incremental(), held, 0.05);
            Assert.True(after < before,
                $"ResetForSlot left the Incremental accumulator behind ({before} -> {after})");
        }
    }
}
