using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>
    /// The Dashboard mirror the engine reads for game telemetry (issue #531),
    /// the shape of <see cref="HeadTrackingRuntime"/>: static, written by the
    /// Dashboard view model on the UI thread, read by the poll thread's
    /// device sweep and by the row.
    ///
    /// <para><see cref="Version"/> bumps on a change that needs the row
    /// reopened: the switch, the port, the game, and the custom formats,
    /// whose variables are axes on the row. The forward port and Full Speed
    /// are read live, so an edit to either takes effect at once.</para>
    /// </summary>
    internal static class GameTelemetryRuntime
    {
        /// <summary>The port F1, the Codemasters games and EA SPORTS WRC send
        /// to out of the box.</summary>
        public const int DefaultPort = 20777;

        /// <summary>Project CARS 2 and Automobilista 2 send to this port and
        /// no other (PCars2UdpNet PCars2UDPClient.cs).</summary>
        public const int ProjectCars2Port = 5606;

        /// <summary>MrHokss's ETS2 and ATS plugin sends here and only here
        /// (ets2-telemetry-udp telemetry.cpp, udp_open).</summary>
        public const int TruckSimulatorPort = 49001;

        public const int DefaultFullSpeedKmh = 300;

        /// <summary>The row's standard axes. The custom variables follow them.</summary>
        public const int StandardAxisCount = 7;

        /// <summary>Custom variables past this many have no axis, since the
        /// input state holds 24, and still read as expression channels.</summary>
        public static int MaxVariableAxes => PadForge.Engine.CustomInputState.MaxAxis - StandardAxisCount;

        private static volatile bool _enabled;
        private static volatile int _port = DefaultPort;
        private static volatile GameTelemetryFormat _format = GameTelemetryFormat.Auto;
        private static volatile string _customFormatName = string.Empty;
        private static volatile int _forwardPort;
        private static volatile int _fullSpeedKmh = DefaultFullSpeedKmh;
        private static volatile int _version;

        private static readonly object _formatsLock = new();
        private static CustomTelemetryFormat[] _formats = Array.Empty<CustomTelemetryFormat>();
        private static volatile TelemetryVariable[] _variables = Array.Empty<TelemetryVariable>();
        private static volatile CompiledTelemetryFormat[] _compiled = Array.Empty<CompiledTelemetryFormat>();

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _version++;
            }
        }

        /// <summary>The UDP port the row listens on.</summary>
        public static int Port
        {
            get => _port;
            set
            {
                int v = Math.Clamp(value, 1, 65535);
                if (_port == v) return;
                _port = v;
                if (_enabled) _version++;
            }
        }

        public static GameTelemetryFormat Format
        {
            get => _format;
            set
            {
                if (_format == value) return;
                _format = value;
                if (_enabled) _version++;
            }
        }

        /// <summary>The custom format read while <see cref="Format"/> is
        /// Custom.</summary>
        public static string CustomFormatName
        {
            get => _customFormatName ?? string.Empty;
            set
            {
                string v = value ?? string.Empty;
                if (string.Equals(_customFormatName, v, StringComparison.Ordinal)) return;
                _customFormatName = v;
                if (_enabled && _format == GameTelemetryFormat.Custom) _version++;
            }
        }

        /// <summary>The port every arriving packet is copied to on 127.0.0.1,
        /// or 0 for none. Read by the receive thread for each packet.</summary>
        public static int ForwardPort
        {
            get => _forwardPort;
            set => _forwardPort = Math.Clamp(value, 0, 65535);
        }

        /// <summary>The speed, in km/h, that moves the Speed axis all the way.</summary>
        public static int FullSpeedKmh
        {
            get => _fullSpeedKmh;
            set => _fullSpeedKmh = Math.Clamp(value, 10, 1000);
        }

        /// <summary>The custom formats, as saved. Setting them rebuilds the
        /// variable list and the compiled readers and reopens the row.</summary>
        public static CustomTelemetryFormat[] Formats
        {
            get
            {
                lock (_formatsLock)
                {
                    var copy = new CustomTelemetryFormat[_formats.Length];
                    for (int i = 0; i < copy.Length; i++) copy[i] = _formats[i].Clone();
                    return copy;
                }
            }
            set
            {
                var formats = new List<CustomTelemetryFormat>();
                if (value != null)
                    foreach (var format in value)
                        if (format != null && CustomTelemetryFormat.IsValidName(format.Name))
                            formats.Add(format.Clone());
                var variables = BuildVariables(formats);
                var compiled = new CompiledTelemetryFormat[formats.Count];
                for (int i = 0; i < compiled.Length; i++) compiled[i] = new CompiledTelemetryFormat(formats[i], variables);
                lock (_formatsLock)
                {
                    _formats = formats.ToArray();
                    _variables = variables;
                    _compiled = compiled;
                }
                _version++;
            }
        }

        /// <summary>Every custom variable across the formats, by first use
        /// and without repeats, with the span its first definition gives.</summary>
        public static TelemetryVariable[] Variables => _variables;

        public static CompiledTelemetryFormat[] CompiledFormats => _compiled;

        /// <summary>The compiled format the Custom choice names, or null.</summary>
        public static CompiledTelemetryFormat PinnedFormat
        {
            get
            {
                string name = CustomFormatName;
                foreach (var format in _compiled)
                    if (string.Equals(format.Name, name, StringComparison.OrdinalIgnoreCase))
                        return format;
                return null;
            }
        }

        public static int Version => _version;

        internal static TelemetryVariable[] BuildVariables(IReadOnlyList<CustomTelemetryFormat> formats)
        {
            var list = new List<TelemetryVariable>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var format in formats)
            {
                foreach (var field in format.Fields)
                {
                    if (field == null || field.Target != CustomTelemetryTarget.Variable) continue;
                    string name = field.Name?.Trim() ?? string.Empty;
                    if (!CustomTelemetryFormat.IsValidName(name) || !seen.Add(name)) continue;
                    list.Add(new TelemetryVariable(name, field.Min, field.Max));
                }
            }
            return list.ToArray();
        }

        // ─────────────────────────────────────────────
        //  The values Custom Expression variables read
        // ─────────────────────────────────────────────

        private static readonly TelemetryFrame _values = new();
        private static bool _live;

        /// <summary>An engine value published longer ago than this is not
        /// read. The row publishes on every poll, so a gap this long means
        /// the engine stopped reading the row.</summary>
        public const long EngineStaleMs = 1000;

        // The copy the wheel RPM lights read. TelemetryHub polls on its own
        // thread, so these fields alone take a lock.
        private static readonly object _engineLock = new();
        private static float _engineRpm, _engineMaxRpm;
        private static bool _engineLive, _engineMaxLearned;
        private static long _engineAtMs;

        /// <summary>Written by the row on every read (Step 2, the poll
        /// thread) and read by the macro evaluator later in the same pass, so
        /// the values take no lock: both run on the poll thread.</summary>
        internal static void Publish(TelemetryFrame frame, bool live, long nowMs)
        {
            _live = live;
            if (live) frame.CopyTo(_values);
            else _values.Reset(_variables.Length);
            lock (_engineLock)
            {
                // A game in a menu or paused lights nothing, the rule the
                // hub's Forza source keeps with IsRaceOn.
                _engineLive = live && (!frame.DrivingReported || frame.Driving);
                _engineRpm = frame.Rpm;
                _engineMaxRpm = frame.MaxRpm;
                _engineMaxLearned = frame.MaxRpmLearned;
                _engineAtMs = nowMs;
            }
        }

        /// <summary>Back to rest, when the row closes.</summary>
        internal static void ClearValues()
        {
            _live = false;
            _values.Reset(_variables.Length);
            lock (_engineLock) _engineLive = false;
        }

        internal static bool Live => _live;

        /// <summary>The engine's RPM and max RPM for the wheel RPM lights
        /// (<see cref="PadForge.Common.Telemetry.GameTelemetryRowSource"/>).
        /// True while the row is live, the game is driving, a max RPM is
        /// known, and the row published within <see cref="EngineStaleMs"/>
        /// of <paramref name="nowMs"/>. <paramref name="maxLearned"/> is set
        /// when the max RPM is the highest RPM seen, because the game sent
        /// none.</summary>
        internal static bool TryGetEngine(long nowMs, out float rpm, out float maxRpm, out bool maxLearned)
        {
            lock (_engineLock)
            {
                rpm = _engineRpm;
                maxRpm = _engineMaxRpm;
                maxLearned = _engineMaxLearned;
                return _engineLive && maxRpm > 0f && nowMs - _engineAtMs <= EngineStaleMs;
            }
        }

        /// <summary>A channel in its own unit, 0 while nothing is live.</summary>
        internal static float Read(TelemetryChannel channel, string variable)
        {
            if (!_live) return 0f;
            var f = _values;
            switch (channel)
            {
                case TelemetryChannel.SpeedKmh: return f.SpeedMs * 3.6f;
                case TelemetryChannel.SpeedMph: return f.SpeedMs * 2.2369363f;
                case TelemetryChannel.Rpm: return f.Rpm;
                case TelemetryChannel.MaxRpm: return f.MaxRpm;
                case TelemetryChannel.RpmPercent: return f.MaxRpm > 0f ? Math.Min(100f, f.Rpm / f.MaxRpm * 100f) : 0f;
                case TelemetryChannel.Gear: return f.Gear;
                case TelemetryChannel.ThrottlePercent: return f.Throttle * 100f;
                case TelemetryChannel.BrakePercent: return f.Brake * 100f;
                case TelemetryChannel.ClutchPercent: return f.Clutch * 100f;
                case TelemetryChannel.HandbrakePercent: return f.Handbrake * 100f;
                case TelemetryChannel.SteeringPercent: return f.Steering * 100f;
                case TelemetryChannel.Driving: return !f.DrivingReported || f.Driving ? 1f : 0f;
                case TelemetryChannel.Variable:
                    var variables = _variables;
                    for (int i = 0; i < variables.Length && i < f.Variables.Length; i++)
                        if (string.Equals(variables[i].Name, variable, StringComparison.OrdinalIgnoreCase))
                            return f.Variables[i];
                    return 0f;
                default:
                    return 0f;
            }
        }

        /// <summary>Test seam: the defaults, no formats, nothing live.</summary>
        internal static void ResetForTest()
        {
            _enabled = false;
            _port = DefaultPort;
            _format = GameTelemetryFormat.Auto;
            _customFormatName = string.Empty;
            _forwardPort = 0;
            _fullSpeedKmh = DefaultFullSpeedKmh;
            Formats = Array.Empty<CustomTelemetryFormat>();
            ClearValues();
        }
    }

    /// <summary>A Custom Expression variable's telemetry channel (issue #531).
    /// Saved by name in the variable's spec. APPEND-ONLY.</summary>
    public enum TelemetryChannel
    {
        None = 0,
        SpeedKmh = 1,
        SpeedMph = 2,
        Rpm = 3,
        MaxRpm = 4,
        RpmPercent = 5,
        Gear = 6,
        ThrottlePercent = 7,
        BrakePercent = 8,
        ClutchPercent = 9,
        HandbrakePercent = 10,
        SteeringPercent = 11,
        Driving = 12,
        /// <summary>A custom variable, named beside the channel.</summary>
        Variable = 13,
    }
}
