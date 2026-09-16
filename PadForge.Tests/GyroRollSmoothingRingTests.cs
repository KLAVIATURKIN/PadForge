using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Gyro Roll row and a Gyro Yaw row on one device and slot keep separate
    /// smoothing windows.
    ///
    /// <para>In Local space a Roll row carries roll in the same variable a Yaw
    /// row carries yaw, so one shared window holds both rows' samples. The
    /// window advances once per poll and later reads refresh its head in
    /// place, so the row that reads SECOND overwrites the head and comes out
    /// looking right, while the row that reads FIRST averages a window whose
    /// earlier heads belong to its sibling. That is what this pins, and it is
    /// why a test must watch the first-read row.</para>
    ///
    /// <para>The legacy filter gives roll its own lane for this exact reason
    /// and says so, and the passthrough gives it its own channel. The default
    /// filter did not, and it is the one a stock profile gets: the two
    /// thresholds that select it are nonzero by default and the space
    /// defaults to Local.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroRollSmoothingRingTests
    {
        private const string Dev = "beefbeef-1111-2222-3333-444455556666";

        /// <summary>The dual-threshold filter, which is what a stock profile
        /// gets. A deep window makes a leaked sample easy to see.</summary>
        private static SourceCoercion.GyroTuning Tuning() => new SourceCoercion.GyroTuning
        {
            SensH = 1f,
            SensV = 1f,
            OutputCurve = "Linear",
            TighteningRadPerSec = 100f,          // every rate here stays below,
            SmoothingThresholdRadPerSec = 200f,  // so the window is fully applied
            SmoothingWindowSeconds = 0.05f,      // five samples at 100 Hz
        };

        // The roll row negates the roll rate on its way into the shared
        // variable, so these two rates put clearly different numbers there:
        // the yaw row contributes 1, the roll row contributes 5.
        private const float YawRate = 1f;
        private const float RollRate = -5f;

        private static CustomInputState Sample()
        {
            var st = new CustomInputState();
            st.Gyro[1] = YawRate;
            st.Gyro[2] = RollRate;
            return st;
        }

        private static float Read(CustomInputState st, string descriptor)
        {
            var src = new MappingSource { Kind = "Direct", DeviceGuid = Dev, Descriptor = descriptor };
            return SourceEvaluator.EvaluateForBipolarAxisTarget(
                st, src, 0, "LeftThumbAxisX", 0, null, 0.01, Dev);
        }

        /// <summary>Runs the given rows every poll and reports what the FIRST
        /// one read on the last poll.</summary>
        private static float RunFirstRowOver(params string[] rows)
        {
            float first = 0f;
            for (int frame = 0; frame < 12; frame++)
            {
                SourceCoercion.BeginPollFrame();
                var st = Sample();
                for (int i = 0; i < rows.Length; i++)
                {
                    float v = Read(st, rows[i]);
                    if (i == 0) first = v;
                }
            }
            return first;
        }

        private static void WithTuning(Action body)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroTuningProvider = (g, s) => Tuning();
                SourceCoercion.PollHzProvider = () => 100f;
                body();
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.PollHzProvider = null;
            }
        }

        /// <summary>Adding a Roll row beside a Yaw row must not change what
        /// the Yaw row reports. With one shared window the roll sample sits in
        /// the average the yaw row takes, and a five-to-one difference in the
        /// two rates makes that plain.</summary>
        [Fact]
        public void AddingARollRowDoesNotChangeWhatTheYawRowReports()
        {
            WithTuning(() =>
            {
                float yawAlone = RunFirstRowOver("Gyro Yaw");
                float yawBesideRoll = RunFirstRowOver("Gyro Yaw", "Gyro Roll");

                Assert.NotEqual(0f, yawAlone);
                Assert.Equal(yawAlone, yawBesideRoll, 4);
            });
        }

        /// <summary>The mirror case, so neither ordering is privileged.</summary>
        [Fact]
        public void AddingAYawRowDoesNotChangeWhatTheRollRowReports()
        {
            WithTuning(() =>
            {
                float rollAlone = RunFirstRowOver("Gyro Roll");
                float rollBesideYaw = RunFirstRowOver("Gyro Roll", "Gyro Yaw");

                Assert.NotEqual(0f, rollAlone);
                Assert.Equal(rollAlone, rollBesideYaw, 4);
            });
        }

        /// <summary>Positive control: the two rows really do carry different
        /// numbers, so the comparisons above are not two equal values agreeing
        /// by accident.</summary>
        [Fact]
        public void TheTwoRowsCarryDifferentValues()
        {
            WithTuning(() =>
            {
                float yaw = RunFirstRowOver("Gyro Yaw");
                float roll = RunFirstRowOver("Gyro Roll");
                Assert.True(Math.Abs(roll) > Math.Abs(yaw) * 2f,
                    $"the rates chosen do not separate the rows: yaw {yaw}, roll {roll}");
            });
        }

        /// <summary>Horizontal stays on the yaw window on purpose: it is the
        /// yaw-equivalent blend, meant to replace a yaw row rather than sit
        /// beside one. The legacy lane says so, and this keeps the fix from
        /// quietly splitting it too.</summary>
        [Fact]
        public void HorizontalKeepsSharingTheYawWindow()
        {
            WithTuning(() =>
            {
                // Pure yaw, no roll, so the blend and the yaw row agree on the
                // value and sharing is the correct, intended outcome.
                var oldTuning = SourceCoercion.GyroTuningProvider;
                try
                {
                    float a = 0f, b = 0f;
                    for (int frame = 0; frame < 12; frame++)
                    {
                        SourceCoercion.BeginPollFrame();
                        var st = new CustomInputState();
                        st.Gyro[1] = 1f;
                        a = Read(st, "Gyro Yaw");
                        b = Read(st, "Gyro Horizontal");
                    }
                    Assert.Equal(a, b, 4);
                }
                finally { SourceCoercion.GyroTuningProvider = oldTuning; }
            });
        }
    }
}
