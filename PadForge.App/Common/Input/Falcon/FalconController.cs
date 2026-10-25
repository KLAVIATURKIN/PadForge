using System;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>One reading of the handle (issue #521): position in meters
    /// on Force Dimension's axes (X toward the player, Y right, Z up) and the
    /// button mask.</summary>
    internal readonly record struct FalconSample(double X, double Y, double Z, uint Buttons);

    /// <summary>A Falcon's reach on each axis in meters, the distance from
    /// the center that reads full (issue #521).</summary>
    internal readonly record struct FalconReach(double X, double Y, double Z)
    {
        /// <summary>60 mm, FalconJoyForce's <c>HALF_RANGE_MAX</c>
        /// (FalconJoyForce.cpp:118), until a calibration measures it.</summary>
        public const double DefaultMeters = 0.060;

        public static readonly FalconReach Default = new(DefaultMeters, DefaultMeters, DefaultMeters);
    }

    /// <summary>What one pass produces: the stick, -1 to 1 with up and right
    /// positive, the In/Out position, -1 to 1 with out (toward the player)
    /// positive, and the force on the handle in newtons.</summary>
    internal readonly record struct FalconOutput(double StickX, double StickY, double InOut, double Fx, double Fy, double Fz);

    /// <summary>
    /// The force loop's body for one Falcon (issue #521), free of I/O so the
    /// tests can run it: the feel, the game's forces on top, the caps and
    /// the fade. The haptic thread owns it.
    /// </summary>
    internal sealed class FalconController
    {
        /// <summary>Forces fade in over this long when a Falcon opens, its
        /// row is assigned, or its feel changes, so a spring never snaps a
        /// handle nobody is holding.</summary>
        public const double FadeSeconds = 0.5;

        /// <summary>Preload's push along In/Out while its button is held,
        /// FalconJoyForce's <c>BTN2_X_FORCE</c> (FalconJoyForce.cpp:49).</summary>
        public const double PreloadN = 1.6;

        /// <summary>The acceleration a game's inertia reads is low-passed at
        /// this cutoff.</summary>
        public const double AccelCutoffHz = 30.0;

        private readonly FalconJoystickFeel _joystick = new();
        private readonly FalconAimFeel _aim = new();
        private readonly FalconRumble _rumble;
        private double _fade;
        private bool _assigned;
        private FalconFeel _feel;
        private double _prevVelY, _prevVelZ, _accY, _accZ;
        private bool _velSeeded;

        public FalconController(int seed) => _rumble = new FalconRumble(seed);

        /// <summary>0 to 1, how far the forces have faded in.</summary>
        public double Fade => _fade;

        public static double Offset(double pos, double reach) => FalconAimFeel.Offset(pos, reach);

        public FalconOutput Step(FalconSample s, FalconReach reach, FalconSettings set, FalconGameForce game,
            bool assigned, double dt, double nowMs)
        {
            set ??= FalconSettings.Unassigned;
            game ??= FalconGameForce.Silent;
            if (set.Feel != _feel)
            {
                _joystick.Reset();
                _aim.Reset();
                _fade = 0;
                _feel = set.Feel;
            }
            if (assigned != _assigned)
            {
                if (assigned) _fade = 0;
                else _rumble.Reset();
                _assigned = assigned;
            }
            if (assigned) _fade = Math.Min(_fade + dt / FadeSeconds, 1.0);

            double offY = Offset(s.Y, reach.Y), offZ = Offset(s.Z, reach.Z), offX = Offset(s.X, reach.X);
            bool Held(int button) => button >= 1 && button <= FalconDevice.ButtonCount
                && (s.Buttons & (1u << (button - 1))) != 0;

            double kickX = 0, shakeY = 0, shakeZ = 0;
            if (assigned)
                (kickX, shakeY, shakeZ) = _rumble.Step(game.Heavy, game.Light, Held(set.KickButton), nowMs, dt);

            double stickX, stickY, fy, fz, velY, velZ;
            if (set.Feel == FalconFeel.Aim)
            {
                var (sx, sy, _) = _aim.Stick(s.Y, s.Z, reach.Y, reach.Z, dt,
                    assigned ? _rumble.Bleed : 0.0, assigned && Held(set.BraceButton));
                double aimScale = assigned ? _rumble.AimScale : 1.0;
                stickX = sx * aimScale;
                stickY = sy * aimScale;
                (fy, fz) = _aim.Force(offY, offZ, dt);
                velY = _aim.AxisY.SmoothVel;
                velZ = _aim.AxisZ.SmoothVel;
            }
            else
            {
                _joystick.Update(s.Y, s.Z, dt);
                (stickX, stickY) = FalconJoystickFeel.Stick(offY, offZ);
                velY = _joystick.VelocityY;
                velZ = _joystick.VelocityZ;
                (fy, fz) = FalconJoystickFeel.Force(offY, offZ, velY, velZ, set.Spring, !game.GameSpring);
            }

            // The acceleration a game's inertia reads.
            if (!_velSeeded) { _prevVelY = velY; _prevVelZ = velZ; _velSeeded = true; }
            double step = dt > 0 ? dt : 0.001;
            double aAcc = FalconMath.Alpha(AccelCutoffHz, step);
            _accY += aAcc * ((velY - _prevVelY) / step - _accY);
            _accZ += aAcc * ((velZ - _prevVelZ) / step - _accZ);
            _prevVelY = velY;
            _prevVelZ = velZ;

            double fx = 0;
            if (assigned)
            {
                double kick = set.Kick * set.Overall, shake = set.Shake * set.Overall;
                fx = kickX * kick;
                fy += shakeY * shake;
                fz += shakeZ * shake;
                if (set.Feel == FalconFeel.Aim && Held(set.PreloadButton)) fx += PreloadN;
                var (ey, ez) = FalconEffects.Render(game, offY, offZ, velY, velZ, _accY, _accZ,
                    set.ForceCap * set.Overall, nowMs);
                fy += ey;
                fz += ez;
            }
            else
            {
                // A Falcon no slot is assigned holds itself up and does
                // nothing else.
                fy = fz = 0;
            }

            // The plane and In/Out are capped apart, FalconJoyForce's rule,
            // then the whole force to the cap.
            double cap = set.ForceCap;
            double planeCap = Math.Min(FalconAimFeel.PlaneCapN, cap);
            double plane = Math.Sqrt(fy * fy + fz * fz);
            if (plane > planeCap) { double k = planeCap / plane; fy *= k; fz *= k; }
            fx = Math.Clamp(fx, -cap, cap);
            double total = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (total > cap) { double k = cap / total; fx *= k; fy *= k; fz *= k; }

            fx *= _fade;
            fy *= _fade;
            fz *= _fade;
            return new FalconOutput(Math.Clamp(stickX, -1, 1), Math.Clamp(stickY, -1, 1), offX, fx, fy, fz);
        }
    }
}
