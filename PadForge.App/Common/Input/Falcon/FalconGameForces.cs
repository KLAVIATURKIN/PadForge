using System;
using PadForge.Engine;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>
    /// Rumble as FalconJoyForce turns it into force (issue #521). While the
    /// Kick button is held, or for 250 ms after it is let go, rumble pushes
    /// the handle toward the player, sized by the level, at least 4.5 N, held
    /// while the rumble keeps coming, and fading faster after weak shots than
    /// after strong ones. Otherwise rumble shakes the handle in the
    /// left/right and up/down plane, in a direction that wanders every
    /// 140 ms, kept between 3.2 and 9 N.
    ///
    /// <para>FalconJoyForce.cpp: the constants :186-275, the drive mapping
    /// :284-302, the kick :1640-1770, the shake :1772-1829, the kick's
    /// lateral texture :1831-1862, the rumble decay :1864-1870. FalconJoyForce
    /// reads rumble as packets from its Pico. PadForge has the slot's motor
    /// levels at every poll instead, so a level above zero counts as rumble
    /// arriving, and a rise in the level counts as a new shot, which is what
    /// a packet's arrival does there.</para>
    /// </summary>
    internal sealed class FalconRumble
    {
        public const double AmbientLargeScale = 5.5, AmbientSmallScale = 5.5;  // AMBIENT_*_SCALE
        public const double RumbleDecay = 0.70;           // RUMBLE_DECAY, per ms
        public const double AmbientTailQuiet = 0.05;      // AMBIENT_TAIL_QUIET
        public const double AmbientDirChangeMs = 140;     // AMBIENT_DIR_CHANGE_MS
        public const double AmbientDirSlewHz = 5.0;       // AMBIENT_DIR_SLEW_HZ
        public const double AmbientAttackHz = 7.0, AmbientReleaseHz = 3.0;
        public const double RecoilLargeScale = 3.4, RecoilSmallScale = 3.4;    // RUMBLE_*_SCALE
        public const double RecoilWindowMs = 250;         // RECOIL_WINDOW_MS
        public const double SustainThreshold = 0.5;       // RECOIL_SUSTAIN_THRESHOLD
        public const double DecayMin = 0.25, DecayMax = 0.75;  // RECOIL_DECAY_MIN/MAX, per ms
        public const double Fullscale = 4.0;              // RECOIL_FULLSCALE
        public const double DriveCurve = 0.65;            // RECOIL_DRIVE_CURVE
        public const double TimeCurve = 1.0;              // RECOIL_TIME_CURVE
        public const double AimDamp = 0.4;                // RECOIL_AIM_DAMP
        public const double AimDampDecay = 8.0;           // RECOIL_DAMP_DECAY
        public const double ReleaseHz = 12.0;             // RECOIL_RELEASE_HZ
        public const double KickMinN = 4.5;               // RECOIL_TYPICAL_MIN_N
        public const double ShakeMinN = 3.2, ShakeMaxN = 9.0;  // AMBIENT_TYPICAL_MIN/MAX_N
        public const double FloorGate = 0.05;             // TYPICAL_FLOOR_GATE
        public const double ForceMaxN = 8.0;              // FORCE_MAX_N
        public const double FreshMs = 40;                 // RUMBLE_FRESH_MS
        public const double TextureDirChangeMs = 40;      // RECOIL_DIR_CHANGE_MS
        public const double TextureScale = 0.15;          // RECOIL_YZ_SCALE
        public const double TextureDirSlewHz = 6.0;       // RECOIL_YZ_DIR_SLEW_HZ
        public const double TextureGateHi = 0.8, TextureGateLo = 0.5;
        public const double TextureSmoothHz = 18.0, TextureCutHz = 45.0;
        /// <summary>A level rise smaller than this is the same shot.</summary>
        public const double ShotRise = 0.02;

        private readonly Random _random;
        private double _large, _small;              // decaying held levels
        private double _lastHeavy, _lastLight, _lastLevelMs = double.NegativeInfinity;
        private bool _wasHeld;
        private double _releasedMs = double.NegativeInfinity;
        private bool _wasActive;
        private double _dampEnv;
        private bool _firing;
        private double _force, _peak, _decay = 0.55;
        private int _rapidShots;
        private double _xOut;
        private bool _tailActive;
        private double _dirY = 1, _dirZ, _tgtY = 1, _tgtZ, _magSm, _lastDirMs = double.NegativeInfinity;
        private bool _textureOn;
        private double _textureLevel, _texDirY = 1, _texDirZ, _texTgtY = 1, _texTgtZ, _lastTexDirMs = double.NegativeInfinity;

        public FalconRumble(int seed) => _random = new Random(seed);

        /// <summary>How hard a firing kick holds the aim's velocity, 0 to 1
        /// (the recoil velocity bleed's strength).</summary>
        public double Bleed => _firing ? Norm(_force) : 0.0;

        /// <summary>The aim's stick scale while the kick window is open,
        /// <c>1 - (1 - 0.4) * envelope</c>.</summary>
        public double AimScale { get; private set; } = 1.0;

        public void Reset()
        {
            _large = _small = _lastHeavy = _lastLight = 0;
            _lastLevelMs = _releasedMs = double.NegativeInfinity;
            _wasHeld = _wasActive = _firing = _tailActive = _textureOn = false;
            _dampEnv = _force = _peak = _xOut = _magSm = _textureLevel = 0;
            _decay = 0.55;
            _rapidShots = 0;
            AimScale = 1.0;
        }

        private static double Norm(double mag) => FalconMath.Clamp01(mag / Fullscale);

        /// <summary>The kick in newtons at a level: lands on 8 N at full scale.</summary>
        public static double KickN(double mag) => ForceMaxN * Math.Pow(Norm(mag), DriveCurve);

        /// <summary>The per-millisecond decay after a shot of this level.</summary>
        public static double DecayFor(double mag) => DecayMin + Math.Pow(Norm(mag), TimeCurve) * (DecayMax - DecayMin);

        /// <summary>A force above the floor gate is held inside the band.
        /// A dying tail below the gate passes through so it can reach zero.</summary>
        public static double Band(double f, double min, double max)
        {
            if (f <= FloorGate) return f;
            if (max > 0 && f > max) f = max;
            if (min > 0 && f < min) f = min;
            return f;
        }

        /// <summary>
        /// One pass: the motors (0 to 1), whether the Kick button is down,
        /// a monotonic clock in milliseconds and the step in seconds. Returns
        /// the push along +X (toward the player) and the shake on the
        /// left/right (Y) and up/down (Z) axes, in newtons.
        /// </summary>
        public (double X, double Y, double Z) Step(double heavy, double light, bool kickHeld, double nowMs, double dt)
        {
            // Rumble arriving: a level above zero is live, a rise is a shot.
            bool shot = false;
            if (heavy > 0 || light > 0)
            {
                if (heavy > _lastHeavy + ShotRise || light > _lastLight + ShotRise
                    || (_lastHeavy <= 0 && _lastLight <= 0))
                    shot = true;
                _lastLevelMs = nowMs;
                if (heavy > _large) _large = heavy;
                if (light > _small) _small = light;
            }
            _lastHeavy = heavy;
            _lastLight = light;
            bool fresh = nowMs - _lastLevelMs < FreshMs;
            double rL = _large, rS = _small;

            if (_wasHeld && !kickHeld) _releasedMs = nowMs;
            _wasHeld = kickHeld;
            bool active = kickHeld || nowMs - _releasedMs < RecoilWindowMs;
            if (active && !_wasActive) _dampEnv = 1.0;
            _wasActive = active;
            _dampEnv = Math.Max(_dampEnv - _dampEnv * AimDampDecay * dt, 0.0);
            AimScale = active ? 1.0 - (1.0 - AimDamp) * _dampEnv : 1.0;

            double x = 0, y = 0, z = 0;
            double live = heavy * RecoilLargeScale + light * RecoilSmallScale;
            if (active)
            {
                if (!kickHeld && !fresh) live = 0.0;
                if (shot)
                {
                    if (!_firing)
                    {
                        _force = _peak = live;
                        _firing = true;
                        _rapidShots = 0;
                    }
                    else
                    {
                        _rapidShots++;
                        if (live > _force) _force = live;
                        _peak = _force;
                    }
                    _decay = DecayFor(live);
                }
                bool sustaining = live > SustainThreshold;
                if (sustaining)
                {
                    if (live > _force) { _force = live; _peak = live; }
                    if (!_firing && live > 0.01) { _force = _peak = live; _firing = true; }
                }
                if (_firing)
                {
                    x = KickN(_peak);
                    if (!sustaining)
                    {
                        double step = FalconMath.Retain(_decay, dt);
                        _force *= step;
                        _peak *= step;
                        if (_force < 0.01) { _force = 0; _firing = false; _rapidShots = 0; }
                    }
                }
            }
            else
            {
                if (!_firing) _force = _peak = 0;
                _rapidShots = 0;
                if (_firing)
                {
                    double step = FalconMath.Retain(_decay, dt);
                    _force *= step;
                    _peak *= step;
                    x = KickN(_peak);
                    if (_force < 0.01) { _force = 0; _firing = false; }
                }
            }

            // Up instantly so the kick stays sharp, down through a low-pass
            // so the release is one clean ramp.
            x = Band(x, KickMinN, 0);
            if (x >= _xOut) _xOut = x;
            else _xOut += FalconMath.Alpha(ReleaseHz, dt) * (x - _xOut);
            if (Math.Abs(_xOut) < 0.01) _xOut = 0;
            x = _xOut;

            // The shake stays off through a kick's whole tail.
            if (active || _firing) _tailActive = true;
            else if (!fresh || live < AmbientTailQuiet) _tailActive = false;

            if (x == 0.0)
            {
                double target = Band(rL * AmbientLargeScale + rS * AmbientSmallScale, ShakeMinN, ShakeMaxN);
                if (_tailActive) target = 0;
                if (nowMs - _lastDirMs > AmbientDirChangeMs)
                {
                    _lastDirMs = nowMs;
                    double angle = _random.NextDouble() * 2.0 * Math.PI;
                    _tgtY = Math.Cos(angle);
                    _tgtZ = Math.Sin(angle);
                }
                double aDir = FalconMath.Alpha(AmbientDirSlewHz, dt);
                _dirY += aDir * (_tgtY - _dirY);
                _dirZ += aDir * (_tgtZ - _dirZ);
                double len = Math.Sqrt(_dirY * _dirY + _dirZ * _dirZ);
                if (len > 1e-6) { _dirY /= len; _dirZ /= len; }
                _magSm += FalconMath.Alpha(target > _magSm ? AmbientAttackHz : AmbientReleaseHz, dt) * (target - _magSm);
                if (_magSm > 0.001) { y = _dirY * _magSm; z = _dirZ * _magSm; }
            }

            // The kick's lateral texture, on only while the rumble is strong.
            if (!active || !fresh) _textureOn = false;
            else if (live > TextureGateHi) _textureOn = true;
            else if (live < TextureGateLo) _textureOn = false;
            if (_textureOn && nowMs - _lastTexDirMs > TextureDirChangeMs)
            {
                _lastTexDirMs = nowMs;
                double angle = _random.NextDouble() * 2.0 * Math.PI;
                _texTgtY = Math.Cos(angle);
                _texTgtZ = Math.Sin(angle);
            }
            double texTarget = _textureOn ? live * TextureScale : 0.0;
            _textureLevel += FalconMath.Alpha(texTarget > _textureLevel ? TextureSmoothHz : TextureCutHz, dt) * (texTarget - _textureLevel);
            if (_textureLevel < 0.001) _textureLevel = 0;
            if (_textureLevel > 0)
            {
                double aTex = FalconMath.Alpha(TextureDirSlewHz, dt);
                _texDirY += aTex * (_texTgtY - _texDirY);
                _texDirZ += aTex * (_texTgtZ - _texDirZ);
                double len = Math.Sqrt(_texDirY * _texDirY + _texDirZ * _texDirZ);
                if (len > 1e-6) { _texDirY /= len; _texDirZ /= len; }
                y += _texDirY * _textureLevel;
                z += _texDirZ * _textureLevel;
            }

            double retain = FalconMath.Retain(RumbleDecay, dt);
            _large = rL * retain;
            _small = rS * retain;
            return (x, y, z);
        }
    }

    /// <summary>
    /// An Extended slot's force feedback effect as force on the handle
    /// (issue #521). A constant, ramp or periodic effect pushes along its
    /// direction, which names where the force comes from, as SDL
    /// (<c>SDL_haptic.h</c>, "the direction where the force comes from") and
    /// PadForge's decoder (<c>HMaestroFfbDecoder</c>) read it: an effect from
    /// the east pushes the handle left. Spring, damper, friction and inertia
    /// act on the left/right and up/down axes around their center, condition
    /// axis 0 on left/right and axis 1 on up/down, with DirectInput's Y
    /// running toward the player. A single condition axis acts on both.
    /// </summary>
    internal static class FalconEffects
    {
        /// <summary>Damping at a full coefficient, CHAI3D's maximum linear
        /// damping for the Falcon (<c>CDeltaDevices.cpp</c>).</summary>
        public const double FullDampingNsPerM = 20.0;
        /// <summary>Mass at a full inertia coefficient.</summary>
        public const double FullInertiaKg = 0.2;
        /// <summary>Below this speed a friction ramps to zero, so the handle
        /// does not chatter at rest.</summary>
        public const double FrictionRampMps = 0.005;

        /// <summary>The effect's force in newtons on the left/right (Y) and
        /// up/down (Z) axes. Offsets are normalized to the reach, velocities
        /// in m/s, accelerations in m/s². <paramref name="fullN"/> is the
        /// force at a full magnitude or coefficient, the cap.</summary>
        public static (double Fy, double Fz) Render(FalconGameForce game, double offY, double offZ,
            double velY, double velZ, double accY, double accZ, double fullN, double nowMs)
        {
            if (game == null || !game.HasEffect || fullN <= 0) return (0, 0);
            if (game.IsCondition)
            {
                var a0 = game.Axes[0];
                var a1 = game.Axes.Length > 1 ? game.Axes[1] : a0;
                double g = game.ConditionGain;
                // DirectInput's Y grows toward the player, which is down on
                // the handle, so axis 1 reads the handle's up/down reversed
                // and pushes back the same way.
                double fy = Condition(game.EffectType, a0, offY, velY, accY, g) * fullN;
                double fz = -Condition(game.EffectType, a1, -offZ, -velZ, -accZ, g) * fullN;
                return (fy, fz);
            }
            double wave = game.EffectType switch
            {
                FfbEffectTypes.Const or FfbEffectTypes.Ramp => 1.0,
                _ => Waveform(game.EffectType, game.PeriodMs, nowMs),
            };
            double mag = game.Magnitude * wave * fullN;
            // Toward is where it comes from turned half around.
            double toward = game.DirectionRad + Math.PI;
            return (mag * Math.Sin(toward), mag * Math.Cos(toward));
        }

        /// <summary>One condition axis as a fraction of full force, signed
        /// to push back toward center (spring) or against motion (damper,
        /// friction, inertia), held to the side's saturation.</summary>
        public static double Condition(uint type, ConditionAxisData axis, double d, double v, double acc, double gain)
        {
            double center = axis.Offset / 10000.0;
            double dead = axis.DeadBand / 10000.0;
            double input = type switch
            {
                FfbEffectTypes.Spring => d - center,
                FfbEffectTypes.Damper => v * FullDampingNsPerM / FalconSettings.RatedForceN,
                FfbEffectTypes.Friction => Math.Clamp(v / FrictionRampMps, -1.0, 1.0),
                FfbEffectTypes.Inertia => acc * FullInertiaKg / FalconSettings.RatedForceN,
                _ => 0.0,
            };
            if (type == FfbEffectTypes.Spring)
            {
                if (Math.Abs(input) <= dead) return 0.0;
                input -= Math.Sign(input) * dead;
            }
            bool positive = input > 0;
            double coef = (positive ? axis.PositiveCoefficient : axis.NegativeCoefficient) / 10000.0 * gain;
            double sat = (positive ? axis.PositiveSaturation : axis.NegativeSaturation) / 10000.0;
            double f = -coef * input;
            return Math.Clamp(f, -sat, sat);
        }

        /// <summary>A periodic effect's -1 to 1 multiplier now, the shapes
        /// <c>ForceFeedbackState.PeriodicWaveformAtPhase</c> samples for a
        /// wheel.</summary>
        public static double Waveform(uint type, uint periodMs, double nowMs)
        {
            if (periodMs == 0) return 1.0;
            double phase = (nowMs % periodMs) / periodMs;
            if (phase < 0) phase += 1.0;
            return type switch
            {
                FfbEffectTypes.Square => phase < 0.5 ? 1.0 : -1.0,
                FfbEffectTypes.Sine => Math.Sin(phase * 2.0 * Math.PI),
                FfbEffectTypes.Triangle => 4.0 * Math.Abs(phase - 0.5) - 1.0,
                FfbEffectTypes.SawUp => 2.0 * phase - 1.0,
                FfbEffectTypes.SawDown => 1.0 - 2.0 * phase,
                _ => 1.0,
            };
        }
    }
}
