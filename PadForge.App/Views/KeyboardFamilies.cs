using System;
using System.Collections.Generic;

namespace PadForge.Views
{
    /// <summary>One key of a keyboard drawing: its position and size in key
    /// units (1u is one letter key), the HID usage it sends as
    /// page &lt;&lt; 16 | usage, and, for a key that sends nothing on the
    /// board's base layer (Fn, a lighting key, an empty position), the QMK
    /// keycode its source named.</summary>
    internal readonly struct LayoutKey
    {
        public readonly double X, Y, W, H;
        public readonly int Usage;
        public readonly string Token;

        public LayoutKey(double x, double y, double w, double h, int usage, string token = null)
        {
            X = x; Y = y; W = w; H = h; Usage = usage; Token = token;
        }

        public ushort Page => (ushort)(Usage >> 16);
        public ushort Id => (ushort)Usage;
    }

    /// <summary>
    /// The drawings the Keyboard + Mouse preview shows for a virtual keyboard
    /// (HIDMaestro 1.12.0). The key sets come from QMK's layout data
    /// (KeyboardFamilies.g.cs, tools/gen_kbm_layouts.py). Each keyboard
    /// profile draws as the set its board has. A board whose own extra keys
    /// (macro columns, G keys, volume wheels) send through vendor interfaces
    /// the profile does not carry draws as the standard set beside them,
    /// since the virtual keyboard sends only its keyboard report. A board no
    /// QMK layout matches draws as the nearest set and is marked
    /// approximate.
    /// </summary>
    internal static partial class KeyboardFamilies
    {
        /// <summary>The legend set a board prints on its modifiers.</summary>
        internal enum Legends { Pc, Mac }

        internal sealed class Family
        {
            public Family(string name, LayoutKey[] keys, bool approximate, Legends legends)
            {
                Name = name; Keys = keys; Approximate = approximate; Legends = legends;
            }

            public string Name { get; }
            public LayoutKey[] Keys { get; }

            /// <summary>True when the board is drawn as the nearest standard
            /// set rather than its own.</summary>
            public bool Approximate { get; }

            public Legends Legends { get; }
        }

        private static Family F(string name, LayoutKey[] keys, bool approximate = false, Legends legends = Legends.Pc)
            => new(name, keys, approximate, legends);

        /// <summary>Every keyboard profile HIDMaestro 1.12.0 ships, by id. A
        /// keyboards-and-mice test holds this list to the catalog, so a
        /// profile a later SDK adds cannot go undrawn. Built on first use:
        /// the key arrays live in the generated half of this class, and C#
        /// runs the static initializers of a partial class's files in no
        /// promised order, so a table built at type load could read them
        /// before they exist.</summary>
        private static readonly Lazy<Dictionary<string, Family>> s_byProfile = new(BuildTable);

        private static Dictionary<string, Family> BuildTable() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Full size, ANSI. The Corsair, Logitech, Razer, HyperX and
                // Genius boards add macro or media keys of their own beside it.
                ["cherry-g83-rs6000"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["cherry-kc-6000-slim"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["cherry-strait-3"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["cooler-master-ck352"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["corsair-k55-rgb-pro-xt"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["corsair-k70-rgb-mk2-low-profile"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["corsair-k70-rgb-rapidfire"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["corsair-k95-rgb-platinum-xt"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["corsair-strafe-rgb"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["genius-imperator"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["hyperx-alloy-elite-rgb"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["lenovo-ultraslim-wireless-combo"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-bolt-receiver-keyboard"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-g710"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-g810"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-g815"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-unifying-receiver-keyboard"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["logitech-usb-receiver-c52e-keyboard"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["microsoft-wired-keyboard-600"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-blackwidow-v4-pro"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-blackwidow-v4-x"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-cynosa-chroma-pro"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-deathstalker-v2-pro"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-huntsman-v3-pro"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-ornata-v3"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["razer-pro-type-ultra"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                ["realforce-rgb"] = F(nameof(FullsizeAnsi), FullsizeAnsi),
                // The Natural Ergonomic 4000 splits and curves the same keys.
                ["microsoft-natural-ergonomic-4000"] = F(nameof(FullsizeAnsi), FullsizeAnsi, approximate: true),
                // Apple's full-size boards: the same blocks with Apple's
                // modifiers, F13 to F19 and an fn key the profiles do not send.
                ["apple-keyboard-a1243"] = F(nameof(FullsizeAnsi), FullsizeAnsi, approximate: true, Legends.Mac),
                ["apple-magic-keyboard-touch-id-numeric"] = F(nameof(FullsizeAnsi), FullsizeAnsi, approximate: true, Legends.Mac),

                // Full size with four keys over the number pad.
                ["ducky-dk2108"] = F(nameof(FullsizeExtended), FullsizeExtended),
                ["realforce-108us"] = F(nameof(FullsizeExtended), FullsizeExtended),
                ["varmilo-ma109c"] = F(nameof(FullsizeExtended), FullsizeExtended),

                // Full size, ISO: the HP board is the French AZERTY 803181-051.
                ["hp-usb-keyboard"] = F(nameof(FullsizeIso), FullsizeIso),

                // Tenkeyless.
                ["8bitdo-retro-mechanical-keyboard"] = F(nameof(Tkl), Tkl),
                ["drevo-blademaster-te"] = F(nameof(Tkl), Tkl),
                ["truly-ergonomic-cleave"] = F(nameof(Tkl), Tkl, approximate: true),

                // 75 percent, and the compact laptop-style boards nearest it.
                ["glorious-gmmk-pro"] = F(nameof(SeventyFive), SeventyFive),
                ["apple-magic-keyboard-a1644"] = F(nameof(SeventyFive), SeventyFive, approximate: true, Legends.Mac),
                ["apple-wireless-keyboard"] = F(nameof(SeventyFive), SeventyFive, approximate: true, Legends.Mac),
                ["lenovo-thinkpad-compact-bluetooth-keyboard"] = F(nameof(SeventyFive), SeventyFive, approximate: true),
                ["microsoft-designer-keyboard"] = F(nameof(SeventyFive), SeventyFive, approximate: true),
                ["raspberry-pi-400-keyboard"] = F(nameof(SeventyFive), SeventyFive, approximate: true),

                // 65 and 60 percent.
                ["razer-blackwidow-v3-mini"] = F(nameof(SixtyFive), SixtyFive),
                ["royuan-gamakay-lk67"] = F(nameof(SixtyFiveBlocker), SixtyFiveBlocker),
                ["obins-anne-pro-2"] = F(nameof(Sixty), Sixty),

                // Happy Hacking.
                ["hhkb-classic"] = F(nameof(Hhkb), Hhkb),
                ["hhkb-professional"] = F(nameof(Hhkb), Hhkb),
                ["hhkb-studio"] = F(nameof(Hhkb), Hhkb),
                ["hhkb-professional-jp"] = F(nameof(HhkbJis), HhkbJis),

                ["keychron-q6"] = F(nameof(KeychronQ6), KeychronQ6),
            };

        /// <summary>The drawing for a keyboard profile, or null for an id
        /// this table does not have (a user-imported profile), which draws
        /// as the generic keyboard.</summary>
        internal static Family For(string profileId)
            => !string.IsNullOrEmpty(profileId) && s_byProfile.Value.TryGetValue(profileId, out var f) ? f : null;

        /// <summary>The ids the table draws, for the catalog test.</summary>
        internal static IReadOnlyCollection<string> ProfileIds => s_byProfile.Value.Keys;

        /// <summary>The legend a board prints on a key whose meaning no
        /// layout changes, or null to let the caller use the generic one:
        /// Apple's modifier symbols, the Japanese keys' own legends, and the
        /// name of a key that sends nothing.</summary>
        internal static string FixedLegend(in LayoutKey key, Legends legends)
        {
            if (key.Usage == 0)
                return key.Token != null && key.Token.StartsWith("MO(", StringComparison.Ordinal) ? "Fn" : "";
            if (key.Page != 0x07) return null;
            if (legends == Legends.Mac)
            {
                switch (key.Id)
                {
                    case 0xE0: case 0xE4: return "⌃";   // control
                    case 0xE2: case 0xE6: return "⌥";   // option
                    case 0xE3: case 0xE7: return "⌘";   // command
                    case 0x2A: return "⌫";              // delete
                    case 0x28: return "↩";              // return
                }
            }
            return key.Id switch
            {
                0x88 => "かな",          // Kana
                0x8A => "変換",          // Henkan
                0x8B => "無変換",    // Muhenkan
                0x87 => "ろ",                // Ro
                0x89 => "¥",                // Yen
                _ => null,
            };
        }
    }
}
