using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the "is this PlayStation config configured?" check that decides
    /// which controller's Lighting / Adaptive Trigger / Audio config a Copy / Paste /
    /// Copy From uses as the fallback. The old check ignored audio, so an audio-only
    /// setup read as empty and was skipped.</summary>
    public class DeviceSlotConfigCopyTests
    {
        [Fact]
        public void Configured_FalseForAllDefault()
        {
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(new DeviceSlotConfigData()));
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(null));
        }

        [Fact]
        public void Configured_TrueForAudioOnly()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioPassthroughEnabled = true }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioLightbarEnabled = true }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioMirrorSourceId = "some-output" }));
        }

        [Fact]
        public void Configured_TrueForLightingOrTriggers()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { LightbarMode = LightbarMode.Static }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { LeftTriggerMode = AdaptiveTriggerMode.Weapon }));
        }

        /// <summary>#347: a tuned custom crossfeed is deliberate configuration.
        /// The two knobs only do anything at the custom level, but the keep-alive
        /// rule is what lets a user park the level back at Off without losing the
        /// tuning, so each knob keeps the config alive on its own.</summary>
        [Fact]
        public void Configured_TrueForCustomCrossfeedKnobsAlone()
        {
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedCutHz = 450 }));
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedFeedDb = 9.0d }));
            // The defaults are the library's own, so they must NOT read as tuning.
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(
                new DeviceSlotConfigData { AudioCrossfeedCutHz = 700, AudioCrossfeedFeedDb = 4.5d }));
        }

        /// <summary>The knobs clamp to libbs2b's accepted window on the way in,
        /// and the custom level survives the level clamp. A level of 9 that
        /// clamped to 8 would silently reinterpret a saved custom setting as the
        /// bs2b default preset.</summary>
        [Fact]
        public void CustomCrossfeed_ClampsToTheLibraryRange()
        {
            var cfg = new DeviceSlotConfig();
            Assert.Equal(700, cfg.AudioCrossfeedCutHz);
            Assert.Equal(4.5d, cfg.AudioCrossfeedFeedDb);
            Assert.False(cfg.AudioCrossfeedIsCustom);

            cfg.AudioCrossfeedLevel = 9;
            Assert.Equal(9, cfg.AudioCrossfeedLevel);
            Assert.True(cfg.AudioCrossfeedIsCustom);

            cfg.AudioCrossfeedCutHz = 10;
            Assert.Equal(300, cfg.AudioCrossfeedCutHz);
            cfg.AudioCrossfeedCutHz = 96000;
            Assert.Equal(2000, cfg.AudioCrossfeedCutHz);

            cfg.AudioCrossfeedFeedDb = 0d;
            Assert.Equal(1.0d, cfg.AudioCrossfeedFeedDb);
            cfg.AudioCrossfeedFeedDb = 200d;
            Assert.Equal(15.0d, cfg.AudioCrossfeedFeedDb);

            cfg.AudioCrossfeedLevel = 99;
            Assert.Equal(9, cfg.AudioCrossfeedLevel);
        }
    }

    /// <summary>
    /// Copy From and Paste land each source device's settings on the same
    /// device, and a destination device the source slot did not have on a
    /// stand-in source device's (#494). The stand-in is another device, so
    /// its Control This Device's Lighting switch never travels: a mouse's
    /// switch would make the destination controller take over a keyboard
    /// assigned only for its keys, and a DualSense's would let go of a
    /// keyboard the user had lit. The same device on both slots carries its
    /// switch.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DeviceSlotConfigStandInTests : IDisposable
    {
        private static readonly System.Guid Mouse = new("00000000-0000-0000-0000-00000000c0a1");
        private static readonly System.Guid Keyboard = new("00000000-0000-0000-0000-00000000c0b2");
        private static readonly System.Guid DualSense = new("00000000-0000-0000-0000-00000000c0c3");
        private readonly PadForge.Common.Input.SettingsCollection _settings = PadForge.Common.Input.SettingsManager.UserSettings;
        private readonly PadForge.Common.Input.DeviceCollection _devices = PadForge.Common.Input.SettingsManager.UserDevices;
        private readonly bool[] _created = PadForge.Common.Input.SettingsManager.SlotCreated;
        private readonly bool[] _enabled = PadForge.Common.Input.SettingsManager.SlotEnabled;
        private readonly System.Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public DeviceSlotConfigStandInTests()
        {
            PadForge.Common.Input.SettingsManager.UserSettings = new PadForge.Common.Input.SettingsCollection();
            PadForge.Common.Input.SettingsManager.UserDevices = new PadForge.Common.Input.DeviceCollection();
            PadForge.Common.Input.SettingsManager.SlotCreated = new bool[PadForge.Common.Input.InputManager.MaxPads];
            PadForge.Common.Input.SettingsManager.SlotEnabled = new bool[PadForge.Common.Input.InputManager.MaxPads];
        }

        public void Dispose()
        {
            PadForge.Common.Input.SettingsManager.UserSettings = _settings;
            PadForge.Common.Input.SettingsManager.UserDevices = _devices;
            PadForge.Common.Input.SettingsManager.SlotCreated = _created;
            PadForge.Common.Input.SettingsManager.SlotEnabled = _enabled;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            var vm = new MainViewModel();
            return (vm, new SettingsService(vm));
        }

        [Fact]
        public void CopyFrom_KeepsAnUnmatchedDevicesOwnSwitch()
        {
            var (vm, settings) = Arrange();
            var mouse = vm.Pads[0].GetOrCreateDeviceConfig(Mouse);
            mouse.PeripheralLightingEnabled = true;
            mouse.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.True(mouse.PeripheralLightingEnabled);
        }

        [Fact]
        public void CopyFrom_NeverTurnsOffAnUnmatchedDevicesSwitch()
        {
            var (vm, settings) = Arrange();
            var pad = vm.Pads[0].GetOrCreateDeviceConfig(DualSense);
            pad.LightbarMode = LightbarMode.Static;
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            keyboard.PeripheralLightingEnabled = true;

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.True(keyboard.PeripheralLightingEnabled);
        }

        [Fact]
        public void CopyFrom_CarriesTheSwitchForTheSameDevice()
        {
            var (vm, settings) = Arrange();
            vm.Pads[0].GetOrCreateDeviceConfig(Mouse).PeripheralLightingEnabled = true;
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);

            settings.CopySlotConfigsAcrossSlots(0, 1);

            Assert.True(mouse.PeripheralLightingEnabled);
        }

        [Fact]
        public void Paste_KeepsAnUnmatchedDevicesOwnSwitch_AndCarriesTheSameDevicesOwn()
        {
            var (vm, settings) = Arrange();
            var keyboard = vm.Pads[1].GetOrCreateDeviceConfig(Keyboard);
            var mouse = vm.Pads[1].GetOrCreateDeviceConfig(Mouse);
            var standIn = new DeviceSlotConfigData
            {
                DeviceGuid = DualSense, LightingRev = 1, LightbarMode = LightbarMode.Static, PeripheralLightingEnabled = true,
            };
            var own = new DeviceSlotConfigData
            {
                DeviceGuid = Mouse, LightingRev = 1, LightbarMode = LightbarMode.Static, PeripheralLightingEnabled = true,
            };

            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn, own });

            Assert.Equal(LightbarMode.Static, keyboard.LightbarMode);
            Assert.False(keyboard.PeripheralLightingEnabled);
            Assert.True(mouse.PeripheralLightingEnabled);
            // The clipboard's own entry is left as it came.
            Assert.True(standIn.PeripheralLightingEnabled);

            keyboard.PeripheralLightingEnabled = true;
            standIn.PeripheralLightingEnabled = false;
            settings.ApplyDeviceSlotConfigsToSlot(1, new[] { standIn });
            Assert.True(keyboard.PeripheralLightingEnabled);
        }
    }
}
