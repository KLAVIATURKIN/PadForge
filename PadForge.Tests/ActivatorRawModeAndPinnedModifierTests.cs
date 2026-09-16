using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two device-identity contracts in the mapping evaluator.
    ///
    /// <para>Force Raw Joystick Mode bypasses SDL's gamepad remapping, so a
    /// gamepad in that mode does not report the trigger layout and its Axis 2
    /// can rest centered. Classifying it as a unipolar trigger made a centered
    /// axis read as half pulled, which is the same shape as the rest-engaged
    /// bug the trigger-scale change fixed for ordinary gamepads.</para>
    ///
    /// <para>A pinned modifier whose device is offline must read released. The
    /// cycle and chord companions already use the all-rest sentinel for this;
    /// the invert-on-hold modifier fell back to the state of whatever device
    /// was being processed and could borrow its matching button.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ActivatorRawModeAndPinnedModifierTests : IDisposable
    {
        private readonly DeviceCollection savedDevices = SettingsManager.UserDevices;

        public void Dispose() => SettingsManager.UserDevices = savedDevices;

        private static bool IsUnipolar(string descriptor, string deviceGuid)
        {
            var m = typeof(InputManager).GetMethod("IsUnipolarActivatorSource",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(m != null, "IsUnipolarActivatorSource is gone");
            return (bool)m.Invoke(null, new object[] { descriptor, deviceGuid });
        }

        private static Guid SeedGamepad(bool forceRaw)
        {
            var id = Guid.NewGuid();
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = id,
                ProductGuid = id,
                InstanceName = "Test pad",
                ProductName = "Test pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                IsEnabled = true,
                ForceRawJoystickMode = forceRaw,
            });
            SettingsManager.UserDevices = devices;
            return id;
        }

        [Theory]
        [InlineData("Axis 2")]
        [InlineData("Axis 5")]
        public void AMappedGamepadTriggerAxisStaysUnipolar(string descriptor)
        {
            var id = SeedGamepad(forceRaw: false);
            Assert.True(IsUnipolar(descriptor, id.ToString()));
        }

        [Theory]
        [InlineData("Axis 2")]
        [InlineData("Axis 5")]
        public void ARawModeGamepadAxisIsBipolar(string descriptor)
        {
            var id = SeedGamepad(forceRaw: true);
            Assert.False(IsUnipolar(descriptor, id.ToString()),
                "raw joystick indices do not carry SDL's trigger layout");
        }

        /// <summary>A slider rests at zero whatever the device does, so raw
        /// mode must not change it. Positive control for the change above.</summary>
        [Fact]
        public void ASliderStaysUnipolarInRawMode()
        {
            var id = SeedGamepad(forceRaw: true);
            Assert.True(IsUnipolar("Slider 0", id.ToString()));
        }

        /// <summary>The invert-on-hold modifier now uses the same all-rest
        /// sentinel its cycle and chord companions use for an offline pinned
        /// device, rather than the state of the device being processed.</summary>
        [Fact]
        public void AnOfflinePinnedInvertModifierUsesTheRestSentinel()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs");
            int at = src.IndexOf("private static bool IsInvertOnHoldActive", StringComparison.Ordinal);
            Assert.True(at > 0, "IsInvertOnHoldActive is gone");
            int end = src.IndexOf("\n        private static", at + 40, StringComparison.Ordinal);
            Assert.True(end > at);
            string body = src.Substring(at, end - at);
            Assert.Contains("LookupDeviceState(src.DeviceGuid) ?? OfflinePinnedRestState", body);
            Assert.DoesNotContain("LookupDeviceState(src.DeviceGuid) ?? fallbackState", body);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(
                new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
