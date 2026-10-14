using System;
using System.Collections.Generic;
using HIDMaestro;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The devices a Keyboard + Mouse slot can send through (HIDMaestro
    /// 1.12.0, HIDMaestro#67), for the slot's Keyboard and Mouse dropdowns and
    /// for the engine. A keyboard profile with a mouse collection, such as a
    /// keyboard with a TrackPoint, can also be the slot's mouse, and a mouse
    /// with a keyboard collection its keyboard. A slot that picks one such
    /// device for both halves gets one device, as the hardware is.
    /// </summary>
    internal static class KbmDeviceCatalog
    {
        /// <summary>The profile behind a device value, or null for Windows
        /// input and for an id the catalog does not have.</summary>
        internal static HMProfile ProfileOf(string deviceId)
            => string.IsNullOrEmpty(deviceId) ? null : HMaestroProfileCatalog.GetProfileById(deviceId);

        /// <summary>What the device can send, or null for Windows input and
        /// for an id the catalog does not have.</summary>
        internal static KbmDeviceCaps CapsOf(string deviceId) => KbmDeviceCaps.For(ProfileOf(deviceId));

        /// <summary>True when the device has a keyboard collection.</summary>
        internal static bool HasKeyboard(string deviceId) => CapsOf(deviceId)?.HasKeyboard == true;

        /// <summary>True when the device has a mouse collection.</summary>
        internal static bool HasMouse(string deviceId) => CapsOf(deviceId)?.HasMouse == true;

        private static bool IsType(string deviceId, string type)
            => string.Equals(ProfileOf(deviceId)?.Type, type, StringComparison.OrdinalIgnoreCase);

        private static KbmDeviceOption[] s_keyboards, s_mice;
        private static string s_culture;
        private static IReadOnlyList<HMProfile> s_keyboardSource, s_mouseSource;

        /// <summary>The keyboard dropdown for a slot whose mouse half is on
        /// <paramref name="mouseDevice"/>.</summary>
        internal static IReadOnlyList<KbmDeviceOption> KeyboardOptions(string mouseDevice)
        {
            Refresh();
            var shared = IsType(mouseDevice, "mouse") && HasKeyboard(mouseDevice)
                ? new KbmDeviceOption { Value = mouseDevice, Name = PadForge.Resources.Strings.Strings.Instance.Pad_Kbm_Device_SameAsMouse }
                : null;
            return With(s_keyboards, shared);
        }

        /// <summary>The mouse dropdown for a slot whose keyboard half is on
        /// <paramref name="keyboardDevice"/>.</summary>
        internal static IReadOnlyList<KbmDeviceOption> MouseOptions(string keyboardDevice)
        {
            Refresh();
            var shared = IsType(keyboardDevice, "keyboard") && HasMouse(keyboardDevice)
                ? new KbmDeviceOption { Value = keyboardDevice, Name = PadForge.Resources.Strings.Strings.Instance.Pad_Kbm_Device_SameAsKeyboard }
                : null;
            return With(s_mice, shared);
        }

        /// <summary>Windows input first, the other half's device next when it
        /// carries this half, then the catalog's devices.</summary>
        private static IReadOnlyList<KbmDeviceOption> With(KbmDeviceOption[] devices, KbmDeviceOption shared)
        {
            var list = new List<KbmDeviceOption>(devices.Length + 2)
            {
                new KbmDeviceOption
                {
                    Value = KbmSlotConfig.WindowsInput,
                    Name = PadForge.Resources.Strings.Strings.Instance.Pad_Kbm_Device_WindowsInput,
                },
            };
            if (shared != null) list.Add(shared);
            list.AddRange(devices);
            return list;
        }

        /// <summary>Rebuilds the device lists when the UI language or the
        /// catalog changed. The catalog sorts by name already.</summary>
        private static void Refresh()
        {
            string culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            var keyboards = HMaestroProfileCatalog.KeyboardProfiles;
            var mice = HMaestroProfileCatalog.MouseProfiles;
            if (s_keyboards != null && s_culture == culture
                && ReferenceEquals(s_keyboardSource, keyboards) && ReferenceEquals(s_mouseSource, mice))
                return;
            s_keyboards = Options(keyboards);
            s_mice = Options(mice);
            s_keyboardSource = keyboards;
            s_mouseSource = mice;
            s_culture = culture;
        }

        private static KbmDeviceOption[] Options(IReadOnlyList<HMProfile> profiles)
        {
            var arr = new KbmDeviceOption[profiles.Count];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = new KbmDeviceOption { Value = profiles[i].Id, Name = profiles[i].Name };
            return arr;
        }
    }
}
