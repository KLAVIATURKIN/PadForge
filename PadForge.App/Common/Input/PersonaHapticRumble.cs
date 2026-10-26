using System;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Haptics a game plays as audio on a DualSense persona, turned into
    /// rumble (discussion #522). The DualSense (PS5): Full and DualSense Edge
    /// (PS5): Full profiles carry the pad's two voice-coil actuators as
    /// channels 3 and 4 of their audio stream. A game that drives them that
    /// way sends no rumble: its output frames leave the rumble emulation bits
    /// clear, which is how audio haptics stay on (SDL_hidapi_ps5.c: "Leaving
    /// emulated rumble bits off will restore audio haptics"), so the slot's
    /// vibration state reads 0.
    ///
    /// <para>The loudness of each haptic channel stands in for that rumble.
    /// The left actuator drives the low (left) motor and the right actuator
    /// the high (right) motor, the pairing SDL and hid-playstation use when
    /// they play rumble on the actuators: SDL's rumble_left takes the
    /// low-frequency motor, hid-playstation's motor_left the strong
    /// magnitude. Every reader of the slot's rumble layers the level on under
    /// max(), the way macro rumble joins, ahead of the Force Feedback tab's
    /// gain, motor strengths and swap, and Bass Shakers plays it too.</para>
    ///
    /// <para>A device that plays the haptic track itself takes none of it: a
    /// DualSense being fed the persona's haptic channels, whose rumble bits
    /// would switch it out of audio haptics, and a Steam or Switch pad with
    /// Play DualSense Haptics on This Controller on. chiaki-ng makes the same
    /// reduction for controllers without voice coils and skips it while a
    /// DualSense plays the haptics (gui/src/streamsession.cpp,
    /// PushHapticsFrame).</para>
    /// </summary>
    internal static class PersonaHapticRumble
    {
        /// <summary>The loudness follower's time constant, in seconds.</summary>
        internal const double TimeConstant = 0.020;

        /// <summary>A level older than this is not merged: the host closed
        /// the stream or the persona went away.</summary>
        internal const long StaleMs = 100;

        /// <summary>The follower publishes its level at most this often, the
        /// 10 ms tick the haptic-tone sinks run on. The level is a 20 ms
        /// envelope, so a faster publish carries nothing new, and it caps
        /// motor writes at a game's own rumble rate whatever window size the
        /// host's audio arrives in.</summary>
        internal const long PublishIntervalMs = 10;

        /// <summary>Follows the loudness of one interleaved s16 window's two
        /// haptic channels and returns it as a pack with the left channel on
        /// the low motor and the right on the high motor. The loudness is a
        /// running mean square with a <see cref="TimeConstant"/> time
        /// constant, kept in <paramref name="meanSquareLeft"/> and
        /// <paramref name="meanSquareRight"/> between windows, read the way
        /// <see cref="Engine.Haptics.HapticToneReducer"/> reads PCM loudness:
        /// RMS times 1.4, so a full-scale sine reads full, and nothing below
        /// its silence floor. Pure over its arguments.</summary>
        internal static long Follow(ref double meanSquareLeft, ref double meanSquareRight,
            ReadOnlySpan<byte> pcm, int stride, int leftOffset, int rightOffset, int rateHz)
        {
            if (stride <= 0 || rateHz <= 0 || leftOffset < 0 || rightOffset < 0
                || Math.Max(leftOffset, rightOffset) + 2 > stride)
                return 0L;
            double a = 1.0 - Math.Exp(-1.0 / (TimeConstant * rateHz));
            double l = meanSquareLeft, r = meanSquareRight;
            for (int i = 0; i + stride <= pcm.Length; i += stride)
            {
                double xl = (short)(pcm[i + leftOffset] | (pcm[i + leftOffset + 1] << 8)) / 32768.0;
                double xr = (short)(pcm[i + rightOffset] | (pcm[i + rightOffset + 1] << 8)) / 32768.0;
                l += a * (xl * xl - l);
                r += a * (xr * xr - r);
            }
            meanSquareLeft = l;
            meanSquareRight = r;
            return LfeOutputState.Pack(Level(l), Level(r), 0, 0);
        }

        private static ushort Level(double meanSquare)
        {
            double amp = Math.Min(Math.Sqrt(meanSquare) * 1.4, 1.0);
            return amp < Engine.Haptics.HapticToneReducer.SilenceRms ? (ushort)0 : (ushort)Math.Round(amp * 65535);
        }

        /// <summary><paramref name="level"/>, or 0 once it is more than
        /// <see cref="StaleMs"/> old. Pure.</summary>
        internal static long Fresh(long level, long levelMs, long nowMs)
            => level == 0L || nowMs - levelMs > StaleMs ? 0L : level;

        /// <summary>Layers a level onto <paramref name="raw"/>'s two body
        /// motors under max(). Returns <paramref name="raw"/> unchanged when
        /// the level is 0, or <paramref name="scratch"/> with the merged
        /// motors and every other field copied from <paramref name="raw"/>,
        /// the shape of <see cref="MacroRumbleOverride.Merge"/>.</summary>
        internal static Vibration Merge(Vibration raw, long level, Vibration scratch)
        {
            if (raw == null || scratch == null) return raw;
            ushort low = LfeOutputState.Low(level), high = LfeOutputState.High(level);
            if (low == 0 && high == 0) return raw;
            scratch.LeftMotorSpeed = Math.Max(raw.LeftMotorSpeed, low);
            scratch.RightMotorSpeed = Math.Max(raw.RightMotorSpeed, high);
            scratch.LeftTriggerMotorSpeed = raw.LeftTriggerMotorSpeed;
            scratch.RightTriggerMotorSpeed = raw.RightTriggerMotorSpeed;
            scratch.HasDirectionalData = raw.HasDirectionalData;
            scratch.HasConditionData = raw.HasConditionData;
            scratch.EffectType = raw.EffectType;
            scratch.SignedMagnitude = raw.SignedMagnitude;
            scratch.Direction = raw.Direction;
            scratch.Period = raw.Period;
            scratch.DeviceGain = raw.DeviceGain;
            scratch.ConditionAxisCount = raw.ConditionAxisCount;
            scratch.ConditionAxes = raw.ConditionAxes;
            return scratch;
        }

        /// <summary>Whether <paramref name="device"/> takes the slot's
        /// persona haptics as rumble. Every rumble device does, except one
        /// that plays the haptic track itself. Lock-free, any thread.</summary>
        internal static bool TakesRumble(int slot, Guid device, long nowMs)
            => !AudioPassthroughService.RendersPersonaHaptics(device, nowMs)
            && !HapticToneService.RendersPersonaHaptics(slot, device);
    }
}
