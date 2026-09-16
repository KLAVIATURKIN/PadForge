using System;
using System.IO;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Steam splits one physical trackpad into a left and a right virtual pad
    /// (DualShock 4 and DualSense register exactly one touchpad), and an
    /// imported config binds each half separately. A gesture whose name carried
    /// no half answered both halves' bindings, so a swipe anywhere on the pad
    /// fired what one side alone was meant to fire. The recognizer now names
    /// the half the gesture STARTED on, alongside the whole-pad name that every
    /// hand-authored row already binds.
    /// </summary>
    public class GestureOriginHalfTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static TouchpadGestureSettings Settings() => new TouchpadGestureSettings
        {
            Enabled = true,
            EnableFourWaySwipes = true,
            EnableTaps = true,
            SwipeDistanceThreshold = 0.10f,
            SwipeTimeWindowMs = 1000,
            TapTimeWindowMs = 200,
            TapMaxMotion = 0.02f,
            CooldownMs = 0,
        };

        private static void Finger(TouchpadInputState pad, bool down, float x, float y)
        {
            pad.FingerDown[0] = down;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            pad.FingerContactId[0] = down ? 1 : -1;
        }

        /// <summary>Drags one finger from (x0, y0) to (x1, y1) and lifts,
        /// returning the gestures that fired on the lift.</summary>
        private static string[] Swipe(float x0, float y0, float x1, float y1)
        {
            var ctx = new TouchpadGestureContext();
            var s = Settings();
            var pad = new TouchpadInputState(2);

            Finger(pad, true, x0, y0);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            Finger(pad, true, x1, y1);
            GestureRecognizer.Update(0, ctx, pad, s, 150);
            Finger(pad, false, x1, y1);
            GestureRecognizer.Update(0, ctx, pad, s, 200);

            return ctx.FiredGesturesThisFrame.ToArray();
        }

        /// <summary>A swipe that starts on the left half names the left half,
        /// and still fires the whole-pad name a hand-authored row binds.
        /// </summary>
        [Fact]
        public void ASwipeStartingLeftNamesTheLeftHalf()
        {
            var fired = Swipe(0.20f, 0.50f, 0.20f, 0.10f);

            Assert.Contains("Touchpad 0 SwipeUp", fired);
            Assert.Contains("Touchpad 0 SwipeUp Left", fired);
            Assert.DoesNotContain("Touchpad 0 SwipeUp Right", fired);
        }

        [Fact]
        public void ASwipeStartingRightNamesTheRightHalf()
        {
            var fired = Swipe(0.80f, 0.50f, 0.80f, 0.10f);

            Assert.Contains("Touchpad 0 SwipeUp", fired);
            Assert.Contains("Touchpad 0 SwipeUp Right", fired);
            Assert.DoesNotContain("Touchpad 0 SwipeUp Left", fired);
        }

        /// <summary>The ORIGIN decides, not where the finger ended. A swipe
        /// that crosses the midline belongs to the half it began on, which is
        /// the half whose binding the user reached for.</summary>
        [Fact]
        public void TheOriginDecidesEvenWhenTheSwipeCrossesOver()
        {
            var fired = Swipe(0.20f, 0.50f, 0.90f, 0.50f);

            Assert.Contains("Touchpad 0 SwipeRight", fired);
            Assert.Contains("Touchpad 0 SwipeRight Left", fired);
            Assert.DoesNotContain("Touchpad 0 SwipeRight Right", fired);
        }

        /// <summary>The split is the midline, the same one every windowed
        /// descriptor reads. The touch spots' own 0.4 split belongs to a
        /// different feature and must not have been borrowed here.</summary>
        [Fact]
        public void TheSplitIsTheMidlineNotTheTouchSpotSplit()
        {
            // 0.45 is right of the touch-spot split (0.4) and left of the
            // window split (0.5).
            var fired = Swipe(0.45f, 0.50f, 0.45f, 0.10f);
            Assert.Contains("Touchpad 0 SwipeUp Left", fired);

            var justRight = Swipe(0.55f, 0.50f, 0.55f, 0.10f);
            Assert.Contains("Touchpad 0 SwipeUp Right", justRight);
        }

        /// <summary>Taps carry the half too. A half-hosted mouse-mode group's
        /// Double Tap member is one of the five import sites that needed
        /// it.</summary>
        [Fact]
        public void ATapNamesTheHalfItLandedOn()
        {
            var ctx = new TouchpadGestureContext();
            var s = Settings();
            var pad = new TouchpadInputState(2);

            Finger(pad, true, 0.85f, 0.5f);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            Finger(pad, false, 0.85f, 0.5f);
            GestureRecognizer.Update(0, ctx, pad, s, 140);

            Assert.Contains("Touchpad 0 Tap", ctx.FiredGesturesThisFrame);
            Assert.Contains("Touchpad 0 Tap Right", ctx.FiredGesturesThisFrame);
        }

        /// <summary>A half-qualified name has to arm its own family, or the
        /// imported row reads dead no matter how well the descriptor is
        /// spelled.</summary>
        [Fact]
        public void AHalfQualifiedNameArmsItsFamily()
        {
            var set = new PadForge.Engine.Data.MappingSet { Authoritative = true };
            set.Rows.Add(new PadForge.Engine.Data.MappingRow
            {
                Target = "ButtonA",
                Sources = { new PadForge.Engine.Data.MappingSource
                    { Descriptor = "Touchpad 0 SwipeUp Left" } },
            });

            var armed = TouchpadGestureAutoArm.Apply(TouchpadGestureSettings.Default(), set);

            Assert.True(armed.Enabled);
            Assert.True(armed.EnableFourWaySwipes);
            Assert.False(armed.EnableTaps);
        }

        /// <summary>The picker advertises what the recognizer fires, on pad 0
        /// where the halves live, and labels each with the same half phrase
        /// every other windowed source uses.</summary>
        [Fact]
        public void ThePickerOffersTheHalfVariants()
        {
            string src = File.ReadAllText(Path.Combine(
                RepoRoot(), "PadForge.App", "Common", "MappingDisplayResolver.cs"));

            Assert.Contains("IsHalfQualifiableGesture", src);
            Assert.Contains("Descriptor = $\"Touchpad {padIdx} {name} {half}\"", src);
            Assert.Contains("TouchpadWindowPhrase(half)", src);
        }

        /// <summary>All five import emitters that host a gesture on a
        /// half-hosted group carry the half now. Before, the two halves of one
        /// DualSense pad emitted byte-identical descriptors.</summary>
        [Fact]
        public void EveryHalfHostedEmitterCarriesTheHalf()
        {
            string ct = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.SteamWorkshop", "Translation", "ConfigTranslator.cs"));
            string ps = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.SteamWorkshop", "Translation", "PhysicalSlotResolver.cs"));

            // The swipe group.
            Assert.Contains("Descriptor = $\"Touchpad {p} {dir}{swipeSfx}\"", ct);
            // The double-press lowering, which recovers the half from the
            // source it re-hosts.
            Assert.Contains("TouchpadHalfSuffixOf(source)", ct);
            // The mouse-mode Double Tap member.
            Assert.Contains("Descriptor = $\"Touchpad {p} DoubleTap{HalfSuffix(half)}\"", ps);

            // The scrollwheel tap and the CycleList detent both pick the half
            // up from the same helper pair.
            int wholePadSwipes = System.Text.RegularExpressions.Regex.Matches(
                ct, @"Descriptor = \$""Touchpad \{PhysicalSlotResolver\.TrackpadIndex\([^""]*\}""\s*,").Count;
            Assert.Equal(0, wholePadSwipes);
        }
    }
}
