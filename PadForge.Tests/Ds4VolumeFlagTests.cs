using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// validFlag0 bits 4 to 7 of a DualShock 4 output report enable the
    /// headphone, mic and speaker volume bytes. An asserted bit with no value
    /// behind it writes volume 0 over whatever the pad had, including the level
    /// the Bluetooth audio lane arms once at start. Every DS4 writer on disk (SDL,
    /// Linux hid-playstation, DS4Windows, OpenRGB, RPCS3) asserts bits 0 to 2
    /// for effects alone, and DS4Windows 3.1.2 dropped the speaker flag. Since
    /// #528 a Bluetooth DualShock 4's packet carries its route's headphone and
    /// speaker levels and asserts their bits only when a level or the route
    /// changes, the DualSense rule
    /// (Ds4BtAudioTests.EffectsPacket_CarriesTheModeAndOnlyTheChangedLevels).
    /// </summary>
    public class Ds4VolumeFlagTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void VolumeEnables_AreNeverAsserted_BecauseThePacketSuppliesNoVolume(bool rumble)
        {
            var fields = Ds4EffectSynthesizer.BuildFields(
                new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0, assertRumbleEnable: rumble);
            byte validFlag0 = (byte)fields["validFlag0"];
            Assert.Equal(0, validFlag0 & 0xF0);
            // Rumble, lightbar color and flash stay enabled as before.
            Assert.Equal(rumble ? 0x07 : 0x06, validFlag0);
        }
    }
}
