using System;
using System.Linq;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two state-pipeline contracts that dropped an authored choice.
    ///
    /// <para>An incoming input-reactive overlay of Off means "no overlay". The
    /// apply resolved it only inside the non-Off arm, so a current-format
    /// config landing on a device that already carried an overlay matched none
    /// of the legacy cases and kept the previous one.</para>
    ///
    /// <para>Keyboard and mouse settings stay parked on a slot that is not
    /// currently Keyboard and Mouse. The reader restores them for any created
    /// slot, so a profile snapshot that captured only current KeyboardMouse
    /// slots lost them on save.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class LightingOverlayAndKbmSnapshotTests : IDisposable
    {
        private readonly bool[] savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly SettingsCollection savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
            SettingsManager.UserSettings = savedSettings;
            SettingsManager.UserDevices = savedDevices;
        }

        [Fact]
        public void AnIncomingOffOverlayClearsTheDestinationOverlay()
        {
            var live = new DeviceSlotConfig { InputReactiveMode = InputReactiveMode.Cycle };
            SettingsService.ApplyDeviceSlotConfigData(live, new DeviceSlotConfigData
            {
                LightingRev = 1,
                LightbarMode = LightbarMode.PlayerNumber,
                InputReactiveMode = InputReactiveMode.Off,
            });
            Assert.Equal(InputReactiveMode.Off, live.InputReactiveMode);
        }

        [Fact]
        public void AnIncomingOverlayStillLands()
        {
            var live = new DeviceSlotConfig { InputReactiveMode = InputReactiveMode.Off };
            SettingsService.ApplyDeviceSlotConfigData(live, new DeviceSlotConfigData
            {
                LightingRev = 1,
                LightbarMode = LightbarMode.PlayerNumber,
                InputReactiveMode = InputReactiveMode.Fixed,
            });
            Assert.Equal(InputReactiveMode.Fixed, live.InputReactiveMode);
        }

        /// <summary>Positive control for the arm the fix moved: a legacy
        /// reactive base still becomes an overlay over a dark base.</summary>
        [Fact]
        public void ALegacyReactiveBaseStillMigratesToAnOverlay()
        {
            var live = new DeviceSlotConfig();
            SettingsService.ApplyDeviceSlotConfigData(live, new DeviceSlotConfigData
            {
                LightingRev = 1,
                LightbarMode = LightbarMode.InputReactiveCycle,
                InputReactiveMode = InputReactiveMode.Off,
            });
            Assert.Equal(InputReactiveMode.Cycle, live.InputReactiveMode);
            Assert.Equal(LightbarMode.Off, live.LightbarMode);
        }

        /// <summary>Both profile snapshot producers keep a created slot's
        /// parked keyboard and mouse settings even when that slot currently
        /// forges some other controller.</summary>
        [Theory]
        [InlineData("BuildKbmConfigSnapshot")]
        [InlineData("SnapshotKbmConfigs")]
        public void ProfileSnapshotKeepsParkedKeyboardAndMouseSettings(string producer)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;

            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            var pad = vm.Pads[0];
            // Parked: the slot forges an Xbox pad right now, but still holds
            // the keyboard and mouse settings the user configured earlier.
            pad.OutputType = VirtualControllerType.Xbox;
            pad.KbmConfig.SocdMode = "Neutral";

            object host = producer == "BuildKbmConfigSnapshot" ? settings : new InputService(vm);
            var m = host.GetType().GetMethod(producer, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(m != null, producer + " is gone");
            var result = (KbmSlotConfigData[])m.Invoke(host, null);

            Assert.True(result != null, producer + " dropped a created slot's parked settings");
            var row = result.FirstOrDefault(r => r.SlotIndex == 0);
            Assert.True(row != null, producer + " omitted slot 0");
            Assert.Equal("Neutral", row.SocdMode);
        }
    }
}
