using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Motion Shake and Motion Lean pairs on a trigger row. The picker
    /// offers both for every target and the button and axis reads handle
    /// them, but the trigger read had no arm for either. Both fell through to
    /// the numeric-descriptor parser and read 0, so a shake or a lean mapped
    /// to LT or RT never pulled, and Invert turned that 0 into a trigger held
    /// fully down.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionTriggerReadTests
    {
        private const string Dev = "11111111-2222-3333-4444-555555555555";

        private static float Trigger(MappingSource src)
            => SourceEvaluator.EvaluateForTriggerTarget(
                new CustomInputState(), src, 0, "RightTrigger", 0,
                new SourceKindRuntime(), 0.016, Dev);

        private static MappingSource Src(string descriptor, bool invert = false, bool half = false) => new()
        {
            Kind = "Direct",
            Descriptor = descriptor,
            DeviceGuid = Dev,
            Invert = invert,
            HalfAxis = half,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Shake_PullsWithTheEnvelope(bool aux)
        {
            var oldP = SourceCoercion.ShakeEnvelopeProvider;
            var oldA = SourceCoercion.ShakeEnvelopeProviderAux;
            try
            {
                float env = 0f;
                SourceCoercion.ShakeEnvelopeProvider = _ => aux ? 0f : env;
                SourceCoercion.ShakeEnvelopeProviderAux = _ => aux ? env : 0f;
                var src = Src(aux ? SourceCoercion.MotionShakeAuxDescriptor : SourceCoercion.MotionShakeDescriptor);

                Assert.Equal(0f, Trigger(src), 3);
                env = 0.62f;
                // Positive control in the same window: the axis read of the
                // same source sees the envelope.
                Assert.Equal(0.62f, SourceEvaluator.EvaluateForBipolarAxisTarget(
                    new CustomInputState(), src, 0, "RawAxis2", 0,
                    new SourceKindRuntime(), 0.016, Dev), 3);
                Assert.Equal(0.62f, Trigger(src), 3);
            }
            finally
            {
                SourceCoercion.ShakeEnvelopeProvider = oldP;
                SourceCoercion.ShakeEnvelopeProviderAux = oldA;
            }
        }

        /// <summary>An envelope has no direction, so Invert has nothing to
        /// select. Applied as 1 - v it would hold the trigger down whenever
        /// the pad is still, the shape the rumble family already refuses.</summary>
        [Fact]
        public void Shake_InvertDoesNotHoldTheTriggerDown()
        {
            var oldP = SourceCoercion.ShakeEnvelopeProvider;
            try
            {
                float env = 0f;
                SourceCoercion.ShakeEnvelopeProvider = _ => env;
                var src = Src(SourceCoercion.MotionShakeDescriptor, invert: true);

                Assert.Equal(0f, Trigger(src), 3);
                env = 0.5f;
                Assert.Equal(0.5f, Trigger(src), 3);
            }
            finally
            {
                SourceCoercion.ShakeEnvelopeProvider = oldP;
            }
        }

        // Aux gravity samples in the provider's convention (reaction force,
        // +1 g up at rest). A 60 degree side tilt is past the default
        // 15 / 135 tilt deadzones' full scale either way.
        private static readonly (float gx, float gy, float gz) Rest = (0f, 9.8f, 0f);
        private static readonly (float gx, float gy, float gz) TiltA = (-8.49f, 4.9f, 0f);
        private static readonly (float gx, float gy, float gz) TiltB = (8.49f, 4.9f, 0f);

        private static void WithAuxGravity(System.Action<System.Action<(float, float, float)>> body)
        {
            var oldA = SourceCoercion.GravityProviderAux;
            try
            {
                var grav = Rest;
                SourceCoercion.GravityProviderAux = _ => grav;
                SourceCoercion.ResetGyroLeanNeutral();
                body(g => grav = g);
            }
            finally
            {
                SourceCoercion.GravityProviderAux = oldA;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void Lean_PullsWithTiltEitherWay_AndRestsAtZero()
        {
            WithAuxGravity(set =>
            {
                var src = Src(SourceCoercion.MotionLeanAuxDescriptor);
                // The first read latches the level grip.
                Assert.Equal(0f, Trigger(src), 3);

                set(TiltA);
                // Positive control in the same window: the button read of
                // the same source fires on this tilt.
                Assert.True(SourceCoercion.EvaluateForButtonTarget(new CustomInputState(), src, 30, 0, Dev));
                Assert.Equal(1f, Trigger(src), 3);

                set(TiltB);
                Assert.Equal(1f, Trigger(src), 3);

                set(Rest);
                Assert.Equal(0f, Trigger(src), 3);
            });
        }

        /// <summary>Without Half, Invert on a lean trigger is the ordinary
        /// trigger flip, the gravity-lean pair's behavior: full pull level,
        /// released at full tilt.</summary>
        [Fact]
        public void Lean_Invert_ReleasesAsTheTiltGrows()
        {
            WithAuxGravity(set =>
            {
                var src = Src(SourceCoercion.MotionLeanAuxDescriptor, invert: true);
                Assert.Equal(1f, Trigger(src), 3);
                set(TiltA);
                Assert.Equal(0f, Trigger(src), 3);
            });
        }

        /// <summary>Half picks one tilt direction and Invert picks which, the
        /// grammar the button read of this pair and the gravity-lean trigger
        /// read already use. Invert is spent on the selection, so a level pad
        /// reads released either way.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Lean_Half_PullsOneWay_AndInvertPicksTheWay(bool invert)
        {
            WithAuxGravity(set =>
            {
                var src = Src(SourceCoercion.MotionLeanAuxDescriptor, invert: invert, half: true);
                Assert.Equal(0f, Trigger(src), 3);

                set(TiltA);
                Assert.Equal(invert ? 0f : 1f, Trigger(src), 3);

                set(TiltB);
                Assert.Equal(invert ? 1f : 0f, Trigger(src), 3);

                set(Rest);
                Assert.Equal(0f, Trigger(src), 3);
            });
        }
    }
}
