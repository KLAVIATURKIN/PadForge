using PadForge.Resources.Strings;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Keyboard + Mouse key targets past the original grid, added with the
    /// virtual HID keyboards (HIDMaestro 1.12.0): F13 to F24, the key a
    /// non-US keyboard has beside Left Shift, the Japanese keys, and the
    /// media, browser and launch keys. One table, read by the engine's key
    /// list (InputManager.KbmKeyVkCodes), the mapping grid
    /// (PadViewModel.InitializeKeyboardMouseMappings) and the SOCD key list
    /// (KbmSlotConfig.GetKeyOptions), so a key one of them offers the others
    /// cannot leave out. Each is a Windows virtual-key code, and Windows
    /// input sends it as one (SendInput). A virtual HID keyboard sends the
    /// usage KbmHidKeyMap gives it.
    /// </summary>
    internal static class KbmExtraKeys
    {
        public static readonly byte[] Vks = BuildVks();

        private static byte[] BuildVks()
        {
            var list = new System.Collections.Generic.List<byte>(40);
            for (int i = 0; i < 12; i++) list.Add((byte)(0x7C + i));   // F13-F24
            list.Add(0xE2);   // VK_OEM_102, the non-US \ | key
            list.Add(0xF3);   // Hankaku/Zenkaku (VK_OEM_AUTO)
            list.Add(0x1D);   // No Convert (VK_NONCONVERT, Muhenkan)
            list.Add(0x1C);   // Convert (VK_CONVERT, Henkan)
            list.Add(0x15);   // Kana (VK_KANA)
            list.Add(0xAD);   // Mute
            list.Add(0xAE);   // Volume Down
            list.Add(0xAF);   // Volume Up
            list.Add(0xB3);   // Play/Pause
            list.Add(0xB2);   // Stop
            list.Add(0xB1);   // Previous Track
            list.Add(0xB0);   // Next Track
            list.Add(0xA6);   // Browser Back
            list.Add(0xA7);   // Browser Forward
            list.Add(0xA8);   // Browser Refresh
            list.Add(0xA9);   // Browser Stop
            list.Add(0xAA);   // Browser Search
            list.Add(0xAB);   // Browser Favorites
            list.Add(0xAC);   // Browser Home
            list.Add(0xB4);   // Mail
            list.Add(0xB5);   // Media Select
            list.Add(0xB6);   // Launch App 1
            list.Add(0xB7);   // Launch App 2
            return list.ToArray();
        }

        /// <summary>The name the grid, the SOCD list and the preview show for
        /// one of <see cref="Vks"/>. The media and launch keys reuse the
        /// consumer-control names PadForge already shows for the same usages
        /// on a real device's input side.</summary>
        public static string Label(byte vk)
        {
            var s = Strings.Instance;
            if (vk >= 0x7C && vk <= 0x87) return "F" + (vk - 0x7C + 13).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return vk switch
            {
                0xE2 => s.Key_NonUsBackslash,
                0xF3 => s.Key_HankakuZenkaku,
                0x1D => s.Key_NonConvert,
                0x1C => s.Key_Convert,
                0x15 => s.Key_Kana,
                0xAD => s.DevObj_ConsumerMute,
                0xAE => s.DevObj_ConsumerVolumeDown,
                0xAF => s.DevObj_ConsumerVolumeUp,
                0xB3 => s.DevObj_ConsumerPlayPause,
                0xB2 => s.DevObj_ConsumerMediaStop,
                0xB1 => s.DevObj_ConsumerPreviousTrack,
                0xB0 => s.DevObj_ConsumerNextTrack,
                0xA6 => s.DevObj_ConsumerBrowserBack,
                0xA7 => s.DevObj_ConsumerBrowserForward,
                0xA8 => s.DevObj_ConsumerBrowserRefresh,
                0xA9 => s.DevObj_ConsumerBrowserStop,
                0xAA => s.DevObj_ConsumerBrowserSearch,
                0xAB => s.DevObj_ConsumerBrowserBookmarks,
                0xAC => s.DevObj_ConsumerBrowserHome,
                0xB4 => s.DevObj_ConsumerEmail,
                0xB5 => s.DevObj_ConsumerMediaPlayer,
                0xB6 => s.DevObj_ConsumerFileBrowser,
                0xB7 => s.DevObj_ConsumerCalculator,
                _ => $"Key 0x{vk:X2}",
            };
        }
    }
}
