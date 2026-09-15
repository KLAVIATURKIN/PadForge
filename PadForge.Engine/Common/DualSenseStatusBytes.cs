using System;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>The eight bytes a physical DualSense reports at payload
    /// offsets 40..47 of its full input report (USB report 0x01, BT report
    /// 0x31): the touch timestamp (40), the right and left adaptive-trigger
    /// feedback bytes (41, 42: a stop-location nibble and a status nibble
    /// each), the host timestamp echo (43..46) and the active trigger-effect
    /// mode nibbles (47). Games that drive adaptive triggers read 41, 42 and
    /// 47 back to decide whether a trigger is being held against its effect,
    /// which is why a virtual DualSense that leaves them zero cannot hold L2
    /// in such a game (#433).
    ///
    /// <para>SDL's PS5 driver keeps the range as <c>rgucUnknown1[8]</c> and
    /// parses nothing from it. PadForge's SDL fork publishes the eight bytes
    /// of the most recent full report, packed little-endian (payload byte 40
    /// in bits 0..7, byte 47 in bits 56..63), as the joystick number property
    /// named by <see cref="Property"/>. Until that fork build is bundled the
    /// property is absent and <see cref="Read"/> returns zero, which leaves
    /// the virtual report exactly as it was.</para></summary>
    public static class DualSenseStatusBytes
    {
        /// <summary>Joystick number property the SDL fork sets from the PS5
        /// driver's full-report handler.</summary>
        public const string Property = "SDL.joystick.hidapi.ps5.status_bytes";

        public const ushort SonyVendorId = 0x054C;
        public const ushort DualSensePid = 0x0CE6;
        public const ushort DualSenseEdgePid = 0x0DF2;

        /// <summary>True for the two pads that report the feedback bytes.</summary>
        public static bool IsDualSense(ushort vendorId, ushort productId)
            => vendorId == SonyVendorId
            && (productId == DualSensePid || productId == DualSenseEdgePid);

        /// <summary>The packed bytes for an open SDL gamepad, or zero when the
        /// handle is empty, closed, or the property is not published.</summary>
        public static ulong Read(IntPtr gamepadHandle)
        {
            if (gamepadHandle == IntPtr.Zero) return 0;
            IntPtr joystick = SDL_GetGamepadJoystick(gamepadHandle);
            if (joystick == IntPtr.Zero) return 0;
            uint props = SDL_GetJoystickProperties(joystick);
            if (props == 0) return 0;
            return unchecked((ulong)SDL_GetNumberProperty(props, Property, 0));
        }
    }
}
