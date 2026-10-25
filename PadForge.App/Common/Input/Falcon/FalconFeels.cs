using System;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>The small filters both feels share (issue #521).</summary>
    internal static class FalconMath
    {
        public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        public static double Smoothstep(double t)
        {
            t = Clamp01(t);
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>The one-pole low-pass step both programs use,
        /// <c>1 - exp(-2 pi fc dt)</c>.</summary>
        public static double Alpha(double hz, double dt) => 1.0 - Math.Exp(-2.0 * Math.PI * hz * dt);

        /// <summary>A per-millisecond retention factor over a step of
        /// <paramref name="dt"/> seconds, FalconJoyForce's dt-based decay.</summary>
        public static double Retain(double perMs, double dt) => Math.Pow(perMs, dt * 1000.0);
    }

    /// <summary>
    /// The windowed handle velocity both programs estimate: position change
    /// over the last six samples' elapsed time (FalconJoyForce.cpp
    /// <c>AxisState::UpdateVelocity</c>, FalconJoystick.cpp the same, window
    /// <c>VEL_WINDOW_SAMPLES</c> 6). Meters per second.
    /// </summary>
    internal sealed class FalconVelocityWindow
    {
        public const int Samples = 6;

        private readonly double[] _pos = new double[Samples];
        private readonly double[] _dt = new double[Samples];
        private int _index;
        private bool _seeded;

        /// <summary>The window's average velocity and the two-sample
        /// velocity FalconJoyForce prefers when it is the faster.</summary>
        public void Push(double pos, double dt, out double average, out double shortTerm)
        {
            double step = dt > 0.0 ? dt : 0.001;
            if (!_seeded)
            {
                for (int i = 0; i < Samples; i++) { _pos[i] = pos; _dt[i] = step; }
                _seeded = true;
            }
            _pos[_index] = pos;
            _dt[_index] = step;
            _index = (_index + 1) % Samples;

            double oldest = _pos[_index];
            double elapsed = 0;
            for (int i = 0; i < Samples; i++) elapsed += _dt[i];
            average = elapsed > 0.0001 ? (pos - oldest) / elapsed : 0.0;

            int prev2 = (_index + Samples - 2) % Samples;
            double t2 = _dt[(_index + Samples - 1) % Samples] + _dt[prev2];
            shortTerm = t2 > 0.0001 ? (pos - _pos[prev2]) / t2 : 0.0;
        }

        public void Reset() => _seeded = false;
    }

    /// <summary>
    /// FalconJoystick's feel (issue #521): the stick is the handle's
    /// position, full at 85% of the reach, over a centering spring of 3 N at
    /// full deflection with 4 N per m/s of radial damping both ways, and an
    /// end stop past full deflection that builds to 8 N
    /// (FalconJoystick.cpp:58-71, <c>MapJoystick</c> :652-662,
    /// <c>ApplyForces</c> :664-703). FalconJoystick's own radial deadzone
    /// and expo are left out: PadForge's stick deadzone and response curve
    /// apply to the row as to any stick. The center is Force Dimension's
    /// origin (FalconJoystick.cpp:12-14).
    /// </summary>
    internal sealed class FalconJoystickFeel
    {
        public const double FullDeflection = 0.85;   // JOY_FULL_RAD
        public const double SpringN = 3.0;           // JOY_SPRING_MAX_N
        public const double DampingNsPerM = 4.0;     // JOY_SPRING_DAMPING
        public const double EndStopN = 8.0;          // FORCE_MAX_N
        public const double EndStopRadius = 1.0;     // FORCE_MAX_RAD
        public const double EndStopExponent = 2.3;   // FORCE_EXPONENT
        public const double VelocityCutoffHz = 30.0; // VEL_SMOOTH_HZ

        private readonly FalconVelocityWindow _wy = new(), _wz = new();
        private double _vy, _vz;

        public double VelocityY => _vy;
        public double VelocityZ => _vz;

        /// <summary>Velocity for the damping alone: the window, then one
        /// low-pass at 30 Hz (FalconJoystick.cpp <c>UpdateVelocity</c>).</summary>
        public void Update(double y, double z, double dt)
        {
            double a = FalconMath.Alpha(VelocityCutoffHz, dt);
            _wy.Push(y, dt, out double ay, out _);
            _wz.Push(z, dt, out double az, out _);
            _vy += a * (ay - _vy);
            _vz += a * (az - _vz);
        }

        public void Reset()
        {
            _wy.Reset();
            _wz.Reset();
            _vy = _vz = 0;
        }

        /// <summary>The stick for normalized offsets: the direction kept, the
        /// length full at <see cref="FullDeflection"/>.</summary>
        public static (double X, double Y) Stick(double offY, double offZ)
        {
            double r = Math.Sqrt(offY * offY + offZ * offZ);
            if (r < 1e-9) return (0, 0);
            double mag = Math.Min(r / FullDeflection, 1.0);
            return (offY / r * mag, offZ / r * mag);
        }

        /// <summary>The spring, end stop and damping, in newtons on the
        /// left/right and up/down axes. <paramref name="springScale"/> is the
        /// slot's Spring Strength, 1 for 3 N. A game's spring takes the
        /// centering spring's place while it plays, so
        /// <paramref name="centering"/> false leaves the end stop and the
        /// damping alone.</summary>
        public static (double Fy, double Fz) Force(double offY, double offZ, double velY, double velZ,
            double springScale, bool centering)
        {
            double r = Math.Sqrt(offY * offY + offZ * offZ);
            if (r <= 0.0001) return (0, 0);
            double dirY = offY / r, dirZ = offZ / r;
            double springN = SpringN * springScale;
            double t = Math.Min(r / FullDeflection, 1.0);
            double mag = centering ? t * springN : 0.0;
            if (r > FullDeflection)
            {
                double tw = FalconMath.Clamp01((r - FullDeflection) / (EndStopRadius - FullDeflection));
                mag += Math.Pow(tw, EndStopExponent) * Math.Max(EndStopN - springN, 0.0);
            }
            // Bidirectional radial damping. It may reduce the pull, never
            // turn it into an outward push.
            double vOut = velY * dirY + velZ * dirZ;
            mag += vOut * DampingNsPerM;
            if (mag < 0.0) mag = 0.0;
            return (-dirY * mag, -dirZ * mag);
        }
    }

    /// <summary>
    /// One axis of FalconJoyForce's aim (issue #521): the bracketed velocity
    /// estimator, the slow position reference behind the low-speed assist,
    /// and the flick carry (FalconJoyForce.cpp <c>AxisState</c> :887-1018,
    /// constants :65-91 and :181-184). Positions are meters, velocities
    /// meters per second.
    /// </summary>
    internal sealed class FalconAimAxis
    {
        public const double VelDeadzone = 0.0001;     // VEL_DEADZONE
        public const double BlendVlow = 0.004;        // VEL_BLEND_VLOW
        public const double BlendLow = 0.025;         // VEL_BLEND_LOW
        public const double BlendHigh = 0.15;         // VEL_BLEND_HIGH
        public const double ShortVelNoise = 0.0005;   // SHORT_VEL_NOISE
        public const double FcVlow = 9.0, FcLow = 18.0, FcHigh = 30.0, FcMax = 55.0; // VEL_FC_*
        public const double FcSpeedCeil = 0.06;       // VEL_FC_SPEED_CEIL
        public const double ReleaseMult = 35.0;       // VEL_RELEASE_MULT
        public const double SlowRefHz = 1.2;          // SLOWREF_HZ
        public const int ZeroFrames = 10;             // VEL_ZERO_FRAMES
        public const double FlickVelOn = 0.01;        // FLICK_VEL_ON
        public const double FlickGain = 4.0;          // FLICK_GAIN
        public const double FlickDecay = 0.92;        // FLICK_DECAY, per ms
        public const double FlickCarryMax = 0.9;      // FLICK_CARRY_MAX

        private readonly FalconVelocityWindow _window = new();
        private double _slowRef;
        private bool _slowRefSeeded;
        private int _zeroCount;

        public double SmoothVel { get; set; }
        public double FlickCarry { get; private set; }

        public void Reset()
        {
            _window.Reset();
            _slowRefSeeded = false;
            _zeroCount = 0;
            SmoothVel = 0;
            FlickCarry = 0;
        }

        public void UpdateVelocity(double pos, double dt)
        {
            _window.Push(pos, dt, out double avgVel, out double shortVel);
            double rawVel = Math.Abs(shortVel) > ShortVelNoise && Math.Abs(shortVel) > Math.Abs(avgVel)
                ? shortVel : avgVel;

            double speed = Math.Abs(rawVel);
            double fc;
            if (speed < BlendVlow) fc = FcVlow;
            else if (speed < BlendLow) fc = FcVlow + (FcLow - FcVlow) * FalconMath.Smoothstep((speed - BlendVlow) / (BlendLow - BlendVlow));
            else if (speed < BlendHigh) fc = FcLow + (FcHigh - FcLow) * FalconMath.Smoothstep((speed - BlendLow) / (BlendHigh - BlendLow));
            else fc = FcHigh + (FcMax - FcHigh) * FalconMath.Clamp01((speed - BlendHigh) / (FcSpeedCeil - BlendHigh));
            // Decelerating: collapse faster.
            double fcRel = Math.Abs(rawVel) < Math.Abs(SmoothVel) ? fc * ReleaseMult : fc;
            SmoothVel += FalconMath.Alpha(fcRel, dt) * (rawVel - SmoothVel);

            if (Math.Abs(rawVel) < VelDeadzone && Math.Abs(SmoothVel) < VelDeadzone * 2.0)
            {
                if (++_zeroCount > ZeroFrames) SmoothVel = 0.0;
            }
            else _zeroCount = 0;
        }

        public void UpdateSlowRef(double pos, double dt)
        {
            if (!_slowRefSeeded) { _slowRef = pos; _slowRefSeeded = true; return; }
            _slowRef += FalconMath.Alpha(SlowRefHz, dt) * (pos - _slowRef);
        }

        /// <summary>Position against the slow reference, in reaches, -1 to 1.</summary>
        public double PosError(double pos, double reach)
            => reach < 0.001 ? 0.0 : Math.Clamp((pos - _slowRef) / reach, -1.0, 1.0);

        /// <summary>Banks the speed past where the stick saturates and pays
        /// it out as a decaying carry. A reversal cancels it.</summary>
        public void UpdateFlick(double dt)
        {
            double excess = Math.Max(Math.Abs(SmoothVel) - FlickVelOn, 0.0);
            double target = (SmoothVel >= 0.0 ? 1.0 : -1.0) * excess * FlickGain;
            double carry = FlickCarry;
            if (carry != 0.0 && Math.Abs(SmoothVel) > VelDeadzone)
            {
                double velSign = SmoothVel >= 0.0 ? 1.0 : -1.0;
                if (velSign * carry < 0.0) carry = 0.0;
            }
            if (Math.Abs(target) > Math.Abs(carry)) carry = target;
            else carry *= FalconMath.Retain(FlickDecay, dt);
            carry = Math.Clamp(carry, -FlickCarryMax, FlickCarryMax);
            if (Math.Abs(carry) < 0.001) carry = 0.0;
            FlickCarry = carry;
        }
    }

    /// <summary>
    /// FalconJoyForce's push zone (issue #521): past 81% of the reach the
    /// stick keeps turning in the direction the handle crossed, snapped to
    /// up, down, left or right, faster the deeper the handle goes
    /// (FalconJoyForce.cpp <c>PushState2D</c> :1036-1112, constants
    /// :97-111). The exit sits 0.05 inside the entry, the deadband
    /// <c>ApplyDerivedConfig</c> enforces, and the latch holds at least
    /// 150 ms.
    /// </summary>
    internal sealed class FalconAimPush
    {
        public const double EnterRadius = 0.81;       // PUSH_ENTER_RAD
        public const double ExitRadius = 0.76;        // PUSH_EXIT_RAD less PUSH_EXIT_MIN_HYST
        public const double SpeedBase = 0.64;         // PUSH_SPEED_BASE
        public const double SpeedMax = 6.0;           // PUSH_SPEED_MAX
        public const double VerticalScale = 0.5;      // PUSH_VERTICAL_SCALE
        public const double EntrySmoothHz = 6.0;      // PUSH_ENTRY_SMOOTH_HZ
        public const double EntryMinTravel = 0.005;   // PUSH_ENTRY_MIN_TRAVEL
        public const double MinHoldMs = 150;          // PUSH_MIN_MS

        private double _entryMag, _heldMs, _dirY, _dirZ, _trailY, _trailZ;
        private bool _trailSeeded;

        public bool InPush { get; private set; }

        public void Reset()
        {
            InPush = false;
            _entryMag = _heldMs = 0;
            _trailSeeded = false;
        }

        /// <summary>The push's stick output, replacing
        /// <paramref name="outX"/> and <paramref name="outY"/> (which carry
        /// the velocity zone's output, read as the push's starting speed).
        /// True while the push holds.</summary>
        public bool Update(double offY, double offZ, double dt, ref double outX, ref double outY)
        {
            double r = Math.Sqrt(offY * offY + offZ * offZ);
            double radY = 0, radZ = 0;
            if (r > 1e-6) { radY = offY / r; radZ = offZ / r; }

            if (!_trailSeeded) { _trailY = offY; _trailZ = offZ; _trailSeeded = true; }
            double appY = offY - _trailY, appZ = offZ - _trailZ;
            double a = FalconMath.Alpha(EntrySmoothHz, dt);
            _trailY += a * (offY - _trailY);
            _trailZ += a * (offZ - _trailZ);

            if (InPush)
            {
                _heldMs += dt * 1000.0;
                if (r < ExitRadius && _heldMs >= MinHoldMs) { InPush = false; _entryMag = 0; }
            }
            else if (r >= EnterRadius)
            {
                InPush = true;
                _heldMs = 0;
                _entryMag = Math.Sqrt(outX * outX + outY * outY);
                // The direction is read once, from position alone, and held.
                if (Math.Sqrt(appY * appY + appZ * appZ) > EntryMinTravel) Cardinal(appY, appZ);
                else Cardinal(radY, radZ);
            }

            outX = outY = 0;
            if (!InPush || r < 1e-6) return InPush;

            double excess = FalconMath.Clamp01((r - EnterRadius) / (1.0 - EnterRadius));
            double baseSpeed = _entryMag > 0.01 ? _entryMag : SpeedBase;
            double mag = Math.Min(baseSpeed + excess * (SpeedMax - baseSpeed), SpeedMax);
            outX = _dirY * mag;
            outY = _dirZ * mag * VerticalScale;
            return true;
        }

        private void Cardinal(double vy, double vz)
        {
            if (Math.Abs(vy) >= Math.Abs(vz)) { _dirY = vy >= 0 ? 1 : -1; _dirZ = 0; }
            else { _dirY = 0; _dirZ = vz >= 0 ? 1 : -1; }
        }
    }

    /// <summary>
    /// FalconJoyForce's aim feel (issue #521): the stick follows the
    /// handle's speed through three zones, a low-speed position assist and a
    /// flick carry, the push zone at the edge with its reversal damping, and
    /// the forces that mark the edge: a ridge before the push border, a wall
    /// from 78% to 95% of the reach, and a nudge on the way back in, with
    /// FalconJoyForce's friction and stiction assists
    /// (FalconJoyForce.cpp :1501-1601 for the stick, <c>ApplyForces</c>
    /// :1120-1272 for the forces, constants :48-178).
    /// </summary>
    internal sealed class FalconAimFeel
    {
        // Velocity zones (VEL_*).
        public const double VlowSens = 22.0, VlowCurve = 0.80;
        public const double LowSens = 18.0, LowCurve = 0.88;
        public const double HighSens = 12.0, HighCurve = 0.95;
        public const double SoftRamp = 0.004;          // SOFT_RAMP
        public const double VlowPosScale = 4.0;        // VEL_VLOW_POS_SCALE
        public const double PosBlendMax = FalconAimAxis.BlendLow * 2.0; // posBlendMax = VEL_BLEND_LOW * POSBLEND_MAX_MULT
        public const double PosBlendSlewHz = 6.0;      // POSBLEND_SLEW_HZ
        public const double PushDampCoeff = 0.9;       // PUSH_DAMP_COEFF
        public const double PushDampDecay = 3.0;       // PUSH_DAMP_DECAY

        // Brace (BRACE_VEL_SCALE, BRACE_PUSH_SCALE).
        public const double BraceVelScale = 0.3, BracePushScale = 0.3;

        // The wall (FORCE_*), the ridge (DETENT_*), the nudge (PUMP_*).
        public const double WallStart = 0.78;          // FORCE_SPRING_START
        public const double WallFull = 0.95;           // FORCE_MAX_RAD
        public const double WallN = 8.0;               // FORCE_MAX_N
        public const double WallExponent = 2.3;        // FORCE_EXPONENT
        public const double WallDamping = 1.0;         // FORCE_DAMPING
        public const double DampVelMin = 0.010, DampVelMax = 0.020; // DAMP_VEL_MIN/MAX
        public const double RidgeWidth = 0.12;         // DETENT_WIDTH
        public const double RidgePeakN = 1.4;          // DETENT_PEAK_N
        public const double EdgeHysteresis = 0.02;     // EDGE_HYST
        public const double NudgePeakN = 1.3;          // PUMP_PEAK_N
        public const double NudgeDecay = 0.90;         // PUMP_DECAY, per ms
        public const double PlaneCapN = 7.8;           // FORCE_YZ_CAP_N

        // Friction and stiction assists (FRICTION_*, STICTION_*).
        public const double FrictionCancelN = 0.18;
        public const double FrictionVelMin = 0.003, FrictionVelFull = 0.004, FrictionVelMax = 0.08;
        public const double StictionBreakN = 0.09, StictionVelOn = 0.0005;

        public readonly FalconAimAxis AxisY = new(), AxisZ = new();
        public readonly FalconAimPush Push = new();
        private double _pushDamp, _posBlend, _nudge;
        private bool _wasInPush, _edgePopped, _edgePoppedPrev;

        public void Reset()
        {
            AxisY.Reset();
            AxisZ.Reset();
            Push.Reset();
            _pushDamp = _posBlend = _nudge = 0;
            _wasInPush = _edgePopped = _edgePoppedPrev = false;
        }

        /// <summary>One velocity zone: speed times sensitivity, capped at 1,
        /// raised to the curve.</summary>
        private static double Zone(double speed, double sens, double curve) => Math.Pow(Math.Min(speed * sens, 1.0), curve);

        /// <summary>The three zones blended across 4, 25 and 150 mm/s, with
        /// the soft ramp out of the deadzone.</summary>
        public static double ZoneCurve(double v)
        {
            double speed = Math.Abs(v);
            if (speed < FalconAimAxis.VelDeadzone) return 0.0;
            double sign = v > 0 ? 1.0 : -1.0;
            double ramp = FalconMath.Smoothstep((speed - FalconAimAxis.VelDeadzone) / SoftRamp);
            double vlow = Zone(speed, VlowSens, VlowCurve);
            double lo = Zone(speed, LowSens, LowCurve);
            double hi = Zone(speed, HighSens, HighCurve);
            double tVlow = FalconMath.Smoothstep((speed - FalconAimAxis.BlendVlow) / (FalconAimAxis.BlendLow - FalconAimAxis.BlendVlow));
            double lowBlended = vlow + (lo - vlow) * tVlow;
            double tHi = FalconMath.Smoothstep((speed - FalconAimAxis.BlendLow) / (FalconAimAxis.BlendHigh - FalconAimAxis.BlendLow));
            return sign * (lowBlended * (1.0 - tHi) + hi * tHi) * ramp;
        }

        /// <summary>
        /// One pass. Positions in meters, reaches in meters, the kick's
        /// velocity bleed (0 to 1, how hard a firing kick holds the aim's
        /// velocity) and whether the brace is held. Returns the stick, -1 to
        /// 1 on each axis before the clamp, and whether the push holds.
        /// </summary>
        public (double X, double Y, bool InPush) Stick(double y, double z, double reachY, double reachZ,
            double dt, double bleed, bool braced)
        {
            AxisY.UpdateVelocity(y, dt);
            AxisZ.UpdateVelocity(z, dt);
            AxisY.UpdateSlowRef(y, dt);
            AxisZ.UpdateSlowRef(z, dt);

            double offY = Offset(y, reachY), offZ = Offset(z, reachZ);
            double stickX = 0, stickY = 0;
            bool inPush = Push.Update(offY, offZ, dt, ref stickX, ref stickY);

            // Reversal damping in the push zone, and the blank that follows
            // an exit, so the stick does not jump on the way back in.
            if (inPush)
            {
                double toCenter = -(offY * AxisY.SmoothVel) - (offZ * AxisZ.SmoothVel);
                if (toCenter > 0.0)
                {
                    double trigger = toCenter * PushDampCoeff;
                    if (trigger > _pushDamp) _pushDamp = trigger;
                }
            }
            else if (_wasInPush) _pushDamp = 1.0;
            _wasInPush = inPush;
            _pushDamp -= _pushDamp * PushDampDecay * dt;
            _pushDamp = FalconMath.Clamp01(_pushDamp);

            // A firing kick moves the handle, which is not aim. Downward
            // motion is held harder, since gravity drives the tail
            // (BLEED_Y_SCALE, BLEED_Z_DOWN, BLEED_Z_UP).
            if (bleed > 0.0)
            {
                AxisY.SmoothVel *= 1.0 - bleed * 0.5;
                AxisZ.SmoothVel *= AxisZ.SmoothVel < 0.0 ? 1.0 - bleed * 0.8 : 1.0 - bleed * 0.4;
            }

            AxisY.UpdateFlick(dt);
            AxisZ.UpdateFlick(dt);

            if (!inPush)
            {
                stickX = ZoneCurve(AxisY.SmoothVel);
                stickY = ZoneCurve(AxisZ.SmoothVel);

                double velMag = Math.Sqrt(AxisY.SmoothVel * AxisY.SmoothVel + AxisZ.SmoothVel * AxisZ.SmoothVel);
                double blendRaw = FalconMath.Smoothstep(1.0 - velMag / PosBlendMax);
                _posBlend += FalconMath.Alpha(PosBlendSlewHz, dt) * (blendRaw - _posBlend);
                if (_posBlend > 0.0)
                {
                    double pcX = Math.Clamp(AxisY.PosError(y, reachY) * VlowPosScale, -1.0, 1.0);
                    double pcY = Math.Clamp(AxisZ.PosError(z, reachZ) * VlowPosScale, -1.0, 1.0);
                    stickX = stickX * (1.0 - _posBlend) + pcX * _posBlend;
                    stickY = stickY * (1.0 - _posBlend) + pcY * _posBlend;
                }
                stickX += AxisY.FlickCarry;
                stickY += AxisZ.FlickCarry;
            }

            if (braced)
            {
                double s = inPush ? BracePushScale : BraceVelScale;
                stickX *= s;
                stickY *= s;
            }
            double scale = Math.Max(1.0 - _pushDamp, 0.0);
            return (stickX * scale, stickY * scale, inPush);
        }

        public static double Offset(double pos, double reach) => reach < 0.001 ? 0.0 : Math.Clamp(pos / reach, -1.0, 1.0);

        /// <summary>The wall, the ridge, the nudge and the assists, in
        /// newtons on the left/right and up/down axes, for normalized
        /// offsets and the aim's velocities (m/s).</summary>
        public (double Fy, double Fz) Force(double offY, double offZ, double dt)
        {
            double velY = AxisY.SmoothVel, velZ = AxisZ.SmoothVel;
            double r = Math.Sqrt(offY * offY + offZ * offZ);
            double fy = 0, fz = 0;

            if (r > WallStart)
            {
                double dirY = offY / r, dirZ = offZ / r;
                double t = FalconMath.Clamp01((r - WallStart) / (WallFull - WallStart));
                double mag = Math.Pow(t, WallExponent) * WallN;
                double vOut = velY * dirY + velZ * dirZ;
                double damp = WallDamping;
                if (vOut > 0.0)
                {
                    double proximity = FalconMath.Clamp01((r - WallStart) / (FalconAimPush.EnterRadius - WallStart));
                    double velMag = Math.Sqrt(velY * velY + velZ * velZ);
                    double gate = FalconMath.Smoothstep((velMag - DampVelMin) / (DampVelMax - DampVelMin));
                    damp = WallDamping * (1.0 + proximity * 2.0 * gate);
                }
                mag += vOut * damp;
                if (mag < 0.0) mag = 0.0;
                fy = -dirY * mag;
                fz = -dirZ * mag;
            }

            // The ridge, a function of position alone, latched released once
            // popped until the handle is back inside by the hysteresis.
            if (r >= FalconAimPush.EnterRadius) _edgePopped = true;
            else if (r < FalconAimPush.EnterRadius - EdgeHysteresis) _edgePopped = false;
            double ridgeStart = FalconAimPush.EnterRadius - RidgeWidth;
            if (!_edgePopped && r > ridgeStart && r < FalconAimPush.EnterRadius && r > 0.0001)
            {
                double lip = FalconMath.Smoothstep((r - ridgeStart) / RidgeWidth) * RidgePeakN;
                fy += -offY / r * lip;
                fz += -offZ / r * lip;
            }

            // The nudge, once per genuine exit from the push border.
            if (_edgePoppedPrev && !_edgePopped) _nudge = 1.0;
            _edgePoppedPrev = _edgePopped;
            if (_nudge > 0.01 && r > 0.0001)
            {
                fy += -offY / r * NudgePeakN * _nudge;
                fz += -offZ / r * NudgePeakN * _nudge;
            }
            _nudge *= FalconMath.Retain(NudgeDecay, dt);
            if (_nudge < 0.01) _nudge = 0.0;

            double speed = Math.Sqrt(velY * velY + velZ * velZ);
            if (speed > FrictionVelMin)
            {
                double fadeIn = Math.Min((speed - FrictionVelMin) / (FrictionVelFull - FrictionVelMin), 1.0);
                double fadeOut = Math.Max(1.0 - (speed - FrictionVelFull) / (FrictionVelMax - FrictionVelFull), 0.0);
                fy += velY / speed * FrictionCancelN * fadeIn * fadeOut;
                fz += velZ / speed * FrictionCancelN * fadeIn * fadeOut;
            }
            if (speed > StictionVelOn)
            {
                fy += velY / speed * StictionBreakN;
                fz += velZ / speed * StictionBreakN;
            }
            return (fy, fz);
        }
    }
}
