using System;
using static PadForge.Engine.Common.Logitech.LogitechGKeyInterop;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>
    /// Maps the SDK's coordinates onto flat button indices (issue #454).
    ///
    /// <para>Each (key, M-state) pair is its own button rather than one button
    /// per key, because that is what the hardware reports and what makes M1,
    /// M2 and M3 worth having: one physical G-key carries three bindings.</para>
    ///
    /// <para>The layout is fixed, not derived from what a device happens to
    /// expose, so a saved mapping keeps pointing at the same key when a
    /// different keyboard is plugged in.</para>
    /// </summary>
    public static class LogitechGKeyMap
    {
        /// <summary>Keyboard buttons, 29 keys in each of 3 modes.</summary>
        public const int KeyboardCount = MaxGKeys * MaxMStates;   // 87

        /// <summary>Mouse buttons 6 through 20.</summary>
        public const int MouseCount = MaxMouseButton - MinMouseButton + 1;   // 15

        /// <summary>Every button the SDK can report.</summary>
        public const int TotalCount = KeyboardCount + MouseCount;   // 102

        /// <summary>The button index for a G-key in a mode, or -1 when either
        /// is out of the SDK's range.</summary>
        public static int KeyboardButton(int key, int mode)
        {
            if (key < 1 || key > MaxGKeys) return -1;
            if (mode < 1 || mode > MaxMStates) return -1;
            return (mode - 1) * MaxGKeys + (key - 1);
        }

        /// <summary>The button index for an extra mouse button, or -1.</summary>
        public static int MouseButton(int button)
        {
            if (button < MinMouseButton || button > MaxMouseButton) return -1;
            return KeyboardCount + (button - MinMouseButton);
        }

        /// <summary>The button index for an event, whichever kind it is, or
        /// -1 when the event names something outside the SDK's ranges.</summary>
        public static int ButtonFor(in GkeyCode code)
            => code.IsMouse ? MouseButton(code.KeyIndex)
                            : KeyboardButton(code.KeyIndex, code.MState);

        /// <summary>Reads a button index back into its coordinates. False when
        /// the index is outside the layout.</summary>
        public static bool Describe(int button, out bool isMouse, out int keyOrButton, out int mode)
        {
            isMouse = false;
            keyOrButton = 0;
            mode = 0;
            if (button < 0 || button >= TotalCount) return false;

            if (button >= KeyboardCount)
            {
                isMouse = true;
                keyOrButton = MinMouseButton + (button - KeyboardCount);
                return true;
            }

            mode = (button / MaxGKeys) + 1;
            keyOrButton = (button % MaxGKeys) + 1;
            return true;
        }

        /// <summary>
        /// The name to show when the SDK gives none.
        ///
        /// <para>The SDK's own names are preferred wherever it returns one,
        /// since they match what the Logitech software calls the key. This is
        /// the fallback, and it still says which key in which mode so a stack
        /// of 87 rows stays readable.</para>
        /// </summary>
        public static string FallbackName(int button)
        {
            if (!Describe(button, out bool isMouse, out int keyOrButton, out int mode))
                return string.Empty;
            return isMouse
                ? "Mouse Button " + keyOrButton.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "G" + keyOrButton.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " (M" + mode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}
