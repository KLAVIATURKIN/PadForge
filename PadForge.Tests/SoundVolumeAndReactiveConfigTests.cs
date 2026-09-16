using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two settings round trips that used to lose a deliberate choice.
    ///
    /// <para>A macro sound volume of zero means muted. The save side writes it
    /// unconditionally and the view model clamps to 0..100, so zero is a value
    /// a user can author, yet the load side rewrote it to 100. Absent legacy
    /// values still arrive as 100 from ActionData's own initializer, so nothing
    /// needs to re-supply it.</para>
    ///
    /// <para>An input-reactive overlay rides OVER the base lighting mode, so a
    /// device carrying only an overlay above a default base is configured. Both
    /// "is this configured" predicates omitted it, which let Copy, Paste and
    /// Copy From treat such a device as empty when picking a representative.</para>
    /// </summary>
    public sealed class SoundVolumeAndReactiveConfigTests
    {
        [Fact]
        public void AMutedMacroSoundSurvivesTheLoad()
        {
            var muted = SettingsService.BuildMacroAction(new ActionData { SoundVolume = 0 });
            Assert.Equal(0, muted.SoundVolume);
        }

        [Fact]
        public void AnAbsentLegacyVolumeStillArrivesAtFull()
        {
            // A legacy action that never stored the attribute keeps the
            // initializer, so the load must not need its own fallback.
            var legacy = SettingsService.BuildMacroAction(new ActionData());
            Assert.Equal(100, legacy.SoundVolume);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(55)]
        [InlineData(100)]
        public void MacroSoundVolumeRoundTripsBothWays(int volume)
        {
            var action = SettingsService.BuildMacroAction(new ActionData { SoundVolume = volume });
            Assert.Equal(volume, action.SoundVolume);
            Assert.Equal(volume, SettingsService.BuildActionData(action).SoundVolume);
        }

        /// <summary>An overlay alone, over an otherwise untouched device, must
        /// read as configured in both the stored and the live shape.</summary>
        [Fact]
        public void AnInputReactiveOverlayAloneCountsAsConfigured()
        {
            // A rev-0 stored config spells every unset lighting value as Off,
            // so a default-constructed one is the neutral baseline.
            var stored = new DeviceSlotConfigData();
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(stored),
                "positive control: an untouched stored config must read as unconfigured");
            stored.InputReactiveMode = InputReactiveMode.Fixed;
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(stored));

            var live = new DeviceSlotConfig();
            Assert.False(SettingsService.IsDeviceConfigConfigured(live),
                "positive control: an untouched live config must read as unconfigured");
            live.InputReactiveMode = InputReactiveMode.Cycle;
            Assert.True(SettingsService.IsDeviceConfigConfigured(live));
        }
    }
}
