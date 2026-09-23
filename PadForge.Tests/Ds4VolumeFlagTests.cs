using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// validFlag0 bits 4 to 7 of a DualShock 4 output report enable the
    /// headphone, mic and speaker volume bytes. The effect packet supplies none
    /// of those values, so the encoder zero-fills them, and an asserted bit
    /// writes volume 0 over whatever the pad had, including the level the
    /// Bluetooth audio lane arms once at start. Every DS4 writer on disk (SDL,
    /// Linux hid-playstation, DS4Windows, OpenRGB, RPCS3) asserts bits 0 to 2
    /// only, and DS4Windows 3.1.2 dropped the speaker flag. The DualSense twin
    /// is Ds5LightbarAuthorityTests.HeadphoneVolume_EnableIsNeverAsserted_BecauseWeSupplyNoValue.
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
