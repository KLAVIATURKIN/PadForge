using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A headset read through OpenXR drives the Head Tracker row (issue #403).
    ///
    /// <para>The reporter on #403 runs Virtual Desktop's runtime with no
    /// SteamVR. The SteamVR consumer cannot serve them, because it loads its
    /// native library out of a SteamVR install, so the poses have to arrive
    /// some other way. They arrive here, on the row that already has six
    /// named head axes, thresholds and range settings, so nothing about the
    /// mapping side is new.</para>
    ///
    /// <para>What is worth pinning is that the pose survives the whole way
    /// from an OpenXR position and orientation to the axis a mapping reads.
    /// A sign flipped anywhere along that chain leaves a rider leaning the
    /// wrong way, which is not a failure any single step would report.</para>
    /// </summary>
    // These read HeadTrackingRuntime's per-axis ranges live, so they
    // share the statics collection rather than racing a test that pins one.
    [Collection("SettingsManagerStatics")]
    public class OpenXrHeadTrackerRowTests
    {
        private static HeadTrackerDevice OpenXrRow(Func<long> now = null)
        {
            var device = new HeadTrackerDevice(
                udp: false, port: 4242, freeTrack: false, configVersion: 0,
                now: now, freeTrackFactory: null, configureFirewall: _ => { },
                openXr: true, openXrManifest: null);
            device.AttachForTest();
            return device;
        }

        /// <summary>The row identifies itself as OpenXR when that is the only
        /// backend on, so a user who turned SteamVR off can see which one is
        /// feeding it.</summary>
        [Fact]
        public void TheRowSaysWhichBackendItIsOn()
        {
            using var openXr = OpenXrRow();
            Assert.Equal("Head Tracker (OpenXR)", openXr.Name);
            Assert.True(openXr.OpenXrEnabled);

            using var openTrack = new HeadTrackerDevice(
                udp: true, port: 4242, freeTrack: false, configVersion: 0, now: null,
                freeTrackFactory: null, configureFirewall: _ => { });
            Assert.Equal("Head Tracker (OpenTrack)", openTrack.Name);
            Assert.False(openTrack.OpenXrEnabled);
        }

        /// <summary>Turning a backend on or off must not change the row's
        /// saved identity, or every mapping a user made against it would
        /// point at a device that no longer exists.</summary>
        [Fact]
        public void TheRowKeepsOneIdentityAcrossBackends()
        {
            using var openXr = OpenXrRow();
            using var openTrack = new HeadTrackerDevice(
                udp: true, port: 4242, freeTrack: false, configVersion: 0, now: null,
                freeTrackFactory: null, configureFirewall: _ => { });

            Assert.Equal(openTrack.InstanceGuid, openXr.InstanceGuid);
            Assert.Equal(openTrack.ProductGuid, openXr.ProductGuid);
            Assert.Equal(openTrack.DevicePath, openXr.DevicePath);
            Assert.Equal(openTrack.SdlInstanceId, openXr.SdlInstanceId);
            Assert.Equal(InputDeviceType.HeadTracker, openXr.GetInputDeviceType());
        }

        /// <summary>The row still exposes exactly six named head axes.</summary>
        [Fact]
        public void TheRowStillHasItsSixNamedAxes()
        {
            using var device = OpenXrRow();
            var objects = device.GetDeviceObjects();
            Assert.Equal(HeadPose.AxisCount, objects.Length);
            Assert.Equal("Head Yaw", objects[HeadPose.AxisYaw].Name);
            Assert.Equal("Head Y", objects[HeadPose.AxisY].Name);
        }

        /// <summary>
        /// Standing up reads as a stick pushed up and tucking as a stick
        /// pushed down, all the way through to the axis value, from an
        /// OpenXR position in meters.
        ///
        /// <para>This is the mapping the reporter asked for first, and the
        /// one where a sign error is invisible until someone is on a bike in
        /// a game.</para>
        /// </summary>
        [Fact]
        public void HeadElevationReachesTheAxisTheRightWayRound()
        {
            long now = 1000;
            using var device = OpenXrRow(() => now);

            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];

            OpenXrHeadPose.TryFillPose(0, 1.60, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.Equal(HeadPose.AxisCenter, device.GetCurrentState().Axis[HeadPose.AxisY]);

            OpenXrHeadPose.TryFillPose(0, 1.75, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.True(device.GetCurrentState().Axis[HeadPose.AxisY] < HeadPose.AxisCenter,
                "sitting up should read as a stick pushed up");

            OpenXrHeadPose.TryFillPose(0, 1.42, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.True(device.GetCurrentState().Axis[HeadPose.AxisY] > HeadPose.AxisCenter,
                "tucking should read as a stick pushed down");
        }

        /// <summary>Leaning right drives the lateral axis right, the other
        /// mapping #403 named.</summary>
        [Fact]
        public void LateralLeanReachesTheAxisTheRightWayRound()
        {
            long now = 1000;
            using var device = OpenXrRow(() => now);
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];

            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);

            OpenXrHeadPose.TryFillPose(0.15, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.True(device.GetCurrentState().Axis[HeadPose.AxisX] > HeadPose.AxisCenter);

            OpenXrHeadPose.TryFillPose(-0.15, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.True(device.GetCurrentState().Axis[HeadPose.AxisX] < HeadPose.AxisCenter);
        }

        /// <summary>A headset that stops reporting returns the axes to
        /// center, the same failsafe the other two backends get. A stick left
        /// pinned by a headset someone took off is the worst of the failure
        /// modes here, because the game keeps steering.</summary>
        [Fact]
        public void AHeadsetThatGoesQuietReleasesTheAxes()
        {
            long now = 1000;
            using var device = OpenXrRow(() => now);
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];

            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            OpenXrHeadPose.TryFillPose(0.25, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            device.InjectOpenXrPose(pose);
            Assert.NotEqual(HeadPose.AxisCenter, device.GetCurrentState().Axis[HeadPose.AxisX]);

            now += HeadTrackerDevice.SilenceMs + 1;
            var state = device.GetCurrentState();
            Assert.Equal(HeadPose.AxisCenter, state.Axis[HeadPose.AxisX]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[HeadPose.AxisY]);
        }

        /// <summary>The backend is named in the status so a user can tell an
        /// OpenXR pose from an OpenTrack one.</summary>
        [Fact]
        public void TheSourceIsReportedAsOpenXr()
        {
            long now = 1000;
            using var device = OpenXrRow(() => now);
            var pose = new double[HeadPose.PoseCount];
            device.InjectOpenXrPose(pose);
            device.GetCurrentState();
            Assert.Equal(HeadTrackerSource.OpenXr, device.Source);
        }

        /// <summary>A row with OpenXR switched off starts nothing.</summary>
        [Fact]
        public void TheBackendStaysOffWhenItIsNotAskedFor()
        {
            using var device = new HeadTrackerDevice(
                udp: true, port: 4242, freeTrack: false, configVersion: 0, now: null,
                freeTrackFactory: null, configureFirewall: _ => { });
            Assert.Equal(OpenXrSourceState.Stopped, device.OpenXrState);
            Assert.Equal(string.Empty, device.OpenXrRuntimeName);
        }
    }
}
