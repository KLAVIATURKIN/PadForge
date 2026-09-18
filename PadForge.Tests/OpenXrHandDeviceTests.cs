using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// VR motion controllers as mappable rows (issue #403).
    ///
    /// <para>The reporter asked for "the controllers and headset position".
    /// The headset went first because it needs no action set at all. A
    /// controller needs one, and what it produces has two independent
    /// validity flags: the pose can be tracked while the controls are not
    /// ours to read, because another application holds focus. Publishing
    /// either when it is not valid pins whatever the user last did.</para>
    /// </summary>
    // These read HeadTrackingRuntime's per-axis ranges live, so they
    // share the statics collection rather than racing a test that pins one.
    [Collection("SettingsManagerStatics")]
    public class OpenXrHandDeviceTests
    {
        private static OpenXrHandDevice Row(OpenXrHand hand, Func<long> now = null)
        {
            var device = new OpenXrHandDevice(hand, now);
            device.Open();
            return device;
        }

        private static OpenXrHandState Tracked(double x = 0, double y = 0, double z = 0)
            => new() { PoseValid = true, TX = x, TY = y, TZ = z };

        [Fact]
        public void TheTwoHandsAreSeparateDevices()
        {
            using var left = Row(OpenXrHand.Left);
            using var right = Row(OpenXrHand.Right);

            Assert.NotEqual(left.InstanceGuid, right.InstanceGuid);
            Assert.NotEqual(left.DevicePath, right.DevicePath);
            Assert.NotEqual(left.SdlInstanceId, right.SdlInstanceId);
            Assert.Equal("VR Controller (Left)", left.Name);
            Assert.Equal("VR Controller (Right)", right.Name);
            Assert.Equal(InputDeviceType.VrController, left.GetInputDeviceType());
        }

        /// <summary>A resting controller must not answer the (Any Device)
        /// wildcard. Issue #431 was exactly this: a head tracker sitting
        /// still held both triggers at half pull on every slot.</summary>
        [Fact]
        public void AControllerNeverAnswersTheAnyDeviceWildcard()
        {
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.VrController));
        }

        [Fact]
        public void EveryControlIsNamedAndReachable()
        {
            using var device = Row(OpenXrHand.Right);
            var objects = device.GetDeviceObjects();
            Assert.Equal(OpenXrHandDevice.AxisCount + OpenXrHandDevice.ButtonCount, objects.Length);
            Assert.Equal("Trigger", objects[OpenXrHandDevice.AxisTrigger].Name);
            Assert.Equal("Thumbstick X", objects[OpenXrHandDevice.AxisStickX].Name);
            Assert.Equal("Menu Button",
                objects[OpenXrHandDevice.AxisCount + OpenXrHandDevice.ButtonMenu].Name);
        }

        /// <summary>A trigger rests at zero and a stick rests at center. The
        /// two conventions live in one state array, and mixing them up leaves
        /// a trigger reading half pulled with nothing touching it.</summary>
        [Fact]
        public void AFreshRowRestsCorrectlyForBothConventions()
        {
            using var device = Row(OpenXrHand.Left);
            var state = device.GetCurrentState();
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisStickX]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisYaw]);
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisSqueeze]);
        }

        [Fact]
        public void PoseAxesFollowTheHeadTrackerConvention()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);

            device.InjectForTest(Tracked(y: 10));       // 10 cm up
            Assert.True(device.GetCurrentState().Axis[OpenXrHandDevice.AxisY] < HeadPose.AxisCenter,
                "raising the hand should read as a stick pushed up");

            device.InjectForTest(Tracked(x: 10));       // 10 cm right
            Assert.True(device.GetCurrentState().Axis[OpenXrHandDevice.AxisX] > HeadPose.AxisCenter);
        }

        /// <summary>
        /// A tracked pose with unsynchronized controls publishes the pose and
        /// releases the controls.
        ///
        /// <para>xrSyncActions answers XR_SESSION_NOT_FOCUSED, a SUCCESS
        /// code, whenever another application holds focus. That is the
        /// ordinary case for a background client, and treating it as live
        /// would hold a trigger down for as long as the game had focus.</para>
        /// </summary>
        [Fact]
        public void AnUnsynchronizedActionSetReleasesTheControls()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);

            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = true, TX = 12,
                ControlsActive = true, Trigger = 1f, PrimaryButton = true,
            });
            var held = device.GetCurrentState();
            Assert.True(held.Axis[OpenXrHandDevice.AxisTrigger] > 60000);
            Assert.True(held.Buttons[OpenXrHandDevice.ButtonPrimary]);

            device.InjectForTest(new OpenXrHandState { PoseValid = true, TX = 12, ControlsActive = false });
            var released = device.GetCurrentState();
            Assert.Equal(0, released.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.False(released.Buttons[OpenXrHandDevice.ButtonPrimary]);
            // The pose is still good, so it keeps reporting.
            Assert.True(released.Axis[OpenXrHandDevice.AxisX] > HeadPose.AxisCenter);
        }

        /// <summary>Losing tracking centers the pose axes but leaves live
        /// controls alone. The two are independent on purpose.</summary>
        [Fact]
        public void LosingThePoseDoesNotReleaseTheButtons()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Left, () => now);
            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = false, ControlsActive = true, PrimaryButton = true, Trigger = 0.5f,
            });
            var state = device.GetCurrentState();
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisX]);
            Assert.True(state.Buttons[OpenXrHandDevice.ButtonPrimary]);
            Assert.True(state.Axis[OpenXrHandDevice.AxisTrigger] > 30000);
        }

        /// <summary>A controller set down mid-game releases everything.</summary>
        [Fact]
        public void AControllerThatGoesQuietReleasesEverything()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);
            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = true, TX = 20, ControlsActive = true, Trigger = 1f, MenuButton = true,
            });
            Assert.NotEqual(0, device.GetCurrentState().Axis[OpenXrHandDevice.AxisTrigger]);

            now += OpenXrHandDevice.SilenceMs + 1;
            var state = device.GetCurrentState();
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisX]);
            Assert.False(state.Buttons[OpenXrHandDevice.ButtonMenu]);
            Assert.False(device.IsLive);
        }

        /// <summary>An idle sample is not a sample. A sleeping controller
        /// publishing nothing must not hold the silence timer open, or its
        /// row would claim to be live forever.</summary>
        [Fact]
        public void AnIdleSampleDoesNotCountAsLiveness()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Left, () => now);
            device.InjectForTest(OpenXrHandState.Empty);
            Assert.False(device.IsLive);

            device.InjectForTest(Tracked(x: 5));
            Assert.True(device.IsLive);
        }
    }
}
