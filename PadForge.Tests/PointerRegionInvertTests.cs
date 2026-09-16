using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Invert on an absolute pointer flips the DIRECTION inside its region, it
    /// does not move the region.
    ///
    /// <para>The region maps the finger's reading onto a rectangle by adding
    /// the rectangle's offset, and the public wrapper then negates the whole
    /// result. The offset went with it, so an inverted off-center region
    /// mirrored to the other side of the screen: a rectangle authored on the
    /// left answered on the right, at the opposite end of the travel from
    /// where the user put it.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PointerRegionInvertTests
    {
        private const string PointerX = "Touchpad 0 Pointer X";

        private static CustomInputState PadAt(float x)
        {
            var st = new CustomInputState();
            st.Touchpads = new[]
            {
                new TouchpadInputState
                {
                    MaxFingers = 2,
                    FingerX = new[] { x, 0f },
                    FingerY = new[] { 0.5f, 0f },
                    FingerPressure = new[] { 1f, 0f },
                    FingerDown = new[] { true, false },
                },
            };
            return st;
        }

        private static MappingSource Region(double center, double extent, bool invert) =>
            new MappingSource
            {
                Kind = "Direct",
                Descriptor = PointerX,
                ParamPointerCenter = center,
                ParamPointerExtent = extent,
                Invert = invert,
            };

        private static float Read(MappingSource src, float fingerX)
            => SourceCoercion.EvaluateForBipolarAxisTarget(PadAt(fingerX), src, evaluatedDeviceGuid: "");

        /// <summary>A region on the left of the screen stays on the left when
        /// inverted. Only the direction inside it reverses.</summary>
        [Fact]
        public void AnInvertedOffCenterRegionStaysOnItsSideOfTheScreen()
        {
            // A quarter-width region centered a quarter of the way across, so
            // its whole span sits well left of screen center.
            var plain = Region(center: 0.25, extent: 0.25, invert: false);
            var inverted = Region(center: 0.25, extent: 0.25, invert: true);

            // Every reading through the plain region is on the left half.
            float plainLow = Read(plain, 0f);
            float plainHigh = Read(plain, 1f);
            Assert.True(plainLow < 0f && plainHigh < 0f,
                $"the fixture region is not off-center: {plainLow}..{plainHigh}");

            // Inverted, it must still be on the left half.
            float invLow = Read(inverted, 0f);
            float invHigh = Read(inverted, 1f);
            Assert.True(invLow < 0f && invHigh < 0f,
                $"the inverted region mirrored to the other side: {invLow}..{invHigh}");
        }

        /// <summary>The direction really does reverse, so the test above is
        /// not passing because Invert stopped doing anything.</summary>
        [Fact]
        public void InvertStillReversesTheDirectionInsideTheRegion()
        {
            var plain = Region(center: 0.25, extent: 0.25, invert: false);
            var inverted = Region(center: 0.25, extent: 0.25, invert: true);

            Assert.True(Read(plain, 1f) > Read(plain, 0f),
                "the plain region does not increase with the finger");
            Assert.True(Read(inverted, 1f) < Read(inverted, 0f),
                "Invert no longer reverses the direction");
        }

        /// <summary>The region keeps its span, so inverting scales nothing.
        /// A mirrored region would have the same span too, which is why the
        /// side test above is the one that catches it.</summary>
        [Fact]
        public void InvertingKeepsTheRegionsSpan()
        {
            var plain = Region(center: 0.25, extent: 0.25, invert: false);
            var inverted = Region(center: 0.25, extent: 0.25, invert: true);

            float plainSpan = Math.Abs(Read(plain, 1f) - Read(plain, 0f));
            float invSpan = Math.Abs(Read(inverted, 1f) - Read(inverted, 0f));
            Assert.Equal(plainSpan, invSpan, 4);
        }

        /// <summary>A centered region is the one case where mirroring and
        /// flipping agree, so it must be unchanged either way. This is the
        /// control that proves the fix did not move the common case.</summary>
        [Fact]
        public void ACenteredRegionIsUnaffected()
        {
            var plain = Region(center: 0.5, extent: 1.0, invert: false);
            var inverted = Region(center: 0.5, extent: 1.0, invert: true);

            Assert.Equal(-Read(plain, 0.2f), Read(inverted, 0.2f), 4);
            Assert.Equal(-Read(plain, 0.8f), Read(inverted, 0.8f), 4);
        }
    }
}
