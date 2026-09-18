using System;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Turns an OpenXR headset pose into the pose convention the Head Tracker
    /// row already speaks (issue #403). Pure, so the convention is settled by
    /// tests rather than by a headset.
    ///
    /// <para>OpenXR reports a position in meters and an orientation quaternion
    /// in a right-handed space with +X right, +Y up and forward at -Z
    /// (OpenXR 1.1, "Coordinate System"). <see cref="HeadPose"/> wants
    /// OpenTrack's convention: translation in centimeters, rotation in
    /// degrees, yaw positive turning right and pitch positive looking
    /// up.</para>
    ///
    /// <para>The angle extraction is deliberately the same arithmetic the
    /// SteamVR path uses (OpenVrConsumerService.EulerFromPoseMatrix), reached
    /// through the rotation matrix the quaternion describes. Two head-tracking
    /// backends that disagreed about which way is positive would be a defect
    /// no test in either one would catch.</para>
    ///
    /// <para>Position is relative to a captured baseline and yaw is too,
    /// because neither has a meaningful absolute zero: where a user sits and
    /// which way their room faces are not the neutral the mappings want.
    /// Pitch and roll are absolute, since a level head is their natural
    /// zero.</para>
    /// </summary>
    public static class OpenXrHeadPose
    {
        /// <summary>Meters to centimeters, the unit OpenTrack poses use.</summary>
        public const double MetersToCentimeters = 100.0;

        /// <summary>Where a session's neutral sits. Position and yaw only,
        /// for the reason in the type remarks.</summary>
        public struct Baseline
        {
            public bool Captured;
            public double X, Y, Z, YawDeg;

            public void Clear() => this = default;
        }

        /// <summary>Yaw, pitch and roll in degrees from an orientation
        /// quaternion, in the convention the SteamVR path established: yaw
        /// positive turning RIGHT, pitch positive looking UP, roll positive
        /// tilting the head RIGHT, and the identity orientation reading
        /// (0, 0, 0).</summary>
        public static (double YawDeg, double PitchDeg, double RollDeg) EulerFromQuaternion(
            double x, double y, double z, double w)
        {
            // Basis vectors of the rotation the quaternion describes. Only the
            // three components the angle formulas read are built.
            double zAxisX = 2.0 * (x * z + w * y);
            double zAxisY = 2.0 * (y * z - w * x);
            double zAxisZ = 1.0 - 2.0 * (x * x + y * y);
            double xAxisY = 2.0 * (x * y + w * z);
            double yAxisY = 1.0 - 2.0 * (x * x + z * z);

            // Forward is the negated Z basis vector.
            double fx = -zAxisX, fy = -zAxisY, fz = -zAxisZ;

            double yaw = Math.Atan2(fx, -fz) * (180.0 / Math.PI);
            double pitch = Math.Asin(Math.Clamp(fy, -1.0, 1.0)) * (180.0 / Math.PI);
            double roll = Math.Atan2(-xAxisY, yAxisY) * (180.0 / Math.PI);
            return (yaw, pitch, roll);
        }

        /// <summary>Wraps a degree delta into (-180, 180], so subtracting a
        /// yaw baseline can never read as a 359 degree lean.</summary>
        public static double WrapDegrees(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg <= -180.0) deg += 360.0;
            return deg;
        }

        /// <summary>
        /// Fills a six-value pose in <see cref="HeadPose"/>'s order and units
        /// from an OpenXR position in meters and orientation quaternion,
        /// capturing <paramref name="baseline"/> on the first call and after
        /// it has been cleared.
        ///
        /// <para>Returns false without touching <paramref name="pose"/> when
        /// any component is not finite. A runtime that loses tracking can
        /// report a pose it has marked invalid, and a NaN reaching the axis
        /// scaler would read as centered rather than as absent, which is the
        /// difference between a stick at rest and a stick nobody is
        /// driving.</para>
        /// </summary>
        public static bool TryFillPose(
            double posX, double posY, double posZ,
            double quatX, double quatY, double quatZ, double quatW,
            ref Baseline baseline, Span<double> pose)
        {
            if (pose.Length < HeadPose.PoseCount) return false;
            if (!IsFinite(posX) || !IsFinite(posY) || !IsFinite(posZ)
                || !IsFinite(quatX) || !IsFinite(quatY) || !IsFinite(quatZ) || !IsFinite(quatW))
                return false;

            var (yaw, pitch, roll) = EulerFromQuaternion(quatX, quatY, quatZ, quatW);
            if (!IsFinite(yaw) || !IsFinite(pitch) || !IsFinite(roll)) return false;

            if (!baseline.Captured)
            {
                baseline.Captured = true;
                baseline.X = posX;
                baseline.Y = posY;
                baseline.Z = posZ;
                baseline.YawDeg = yaw;
            }

            // Forward is -Z in OpenXR, so leaning forward has to read as a
            // positive Z the way OpenTrack's does.
            pose[HeadPose.TX] = (posX - baseline.X) * MetersToCentimeters;
            pose[HeadPose.TY] = (posY - baseline.Y) * MetersToCentimeters;
            pose[HeadPose.TZ] = -(posZ - baseline.Z) * MetersToCentimeters;
            pose[HeadPose.Yaw] = WrapDegrees(yaw - baseline.YawDeg);
            pose[HeadPose.Pitch] = pitch;
            pose[HeadPose.Roll] = roll;
            return true;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
