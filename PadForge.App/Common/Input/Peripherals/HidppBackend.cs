using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What the HID++ worker has found, swapped whole so the linker
    /// never reads half a scan.</summary>
    internal sealed class HidppSnapshot
    {
        public static readonly HidppSnapshot Empty = new(Array.Empty<HidppUnit>(), Array.Empty<Guid>(), scanned: false);

        public IReadOnlyList<HidppUnit> Units { get; }

        /// <summary>Containers with a slot still to be asked: a device there
        /// may yet answer, so a row there keeps the outputs it had.</summary>
        public IReadOnlyCollection<Guid> PendingContainers { get; }

        /// <summary>False until the worker's first scan has run, so nothing
        /// it has not looked for reads as missing.</summary>
        public bool Scanned { get; }

        public HidppSnapshot(IReadOnlyList<HidppUnit> units, IReadOnlyCollection<Guid> pending, bool scanned = true)
        {
            Units = units;
            PendingContainers = pending;
            Scanned = scanned;
        }
    }

    /// <summary>
    /// The Logitech HID++ worker (#494). One thread owns every HID++
    /// collection, so a probe, a haptic pulse and a color never interleave
    /// on one channel. The first build's worker shape (6b0eeb3c removed its
    /// global lane), with the targets taken from the link table instead of
    /// every device found:
    ///
    /// <para>A unit plays rumble when a row linked to it has a level, the
    /// strongest when two rows link it. The shaper picks the waveform and
    /// the repeat interval (<see cref="HapticRumbleShaper"/>), and silence
    /// resets the interval so the first pulse of new rumble plays at once.</para>
    ///
    /// <para>A unit lit directly through feature 0x8070 shows the color its
    /// path resolves to (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int)"/>).
    /// The first color takes the lighting from the device's own effect with
    /// SetSWControl [01 01], each zone gets the static effect in that color,
    /// and both are sent again every <see cref="ReassertMs"/>, since a device
    /// that comes back from sleep boots its onboard profile and drops the
    /// claim without a word (OpenRGB LogitechHIDPP20Controller.cpp:6102-6110).
    /// When nothing claims the path any more, [00 00] hands the lighting back
    /// (OpenRGB LogitechHIDPP20Controller.cpp:3935-3946 and 4058-4078, Solaar
    /// settings_templates.py:3204-3293). The linker gives these paths only
    /// while G HUB is not running, which drives the same devices through the
    /// LED SDK. When G HUB starts while a unit is lit, its path is withdrawn
    /// and the worker forgets the claim without a frame, the one case with
    /// no hand-back (<see cref="LeftToLogitechProgram"/>).</para>
    ///
    /// <para>While a unit is linked for rumble, discovery waits for a second
    /// with nothing playing, so a probe does not hold up rumble that comes in
    /// bursts. Rumble that starts during a probe still waits for it: for a
    /// receiver's pairing read that goes unanswered
    /// (<see cref="HidppReceiverProtocol.RegisterTimeoutMs"/>), then for one
    /// request timeout at each slot that stays silent, or up to two where a
    /// device stops answering partway. Discovery finds any device that
    /// answers, with no device list, and asks a silent slot again after 5,
    /// 10, then 15 seconds. On a Bolt, Unifying or LIGHTSPEED receiver it
    /// first reads the receiver's pairing table, so a slot with no device is
    /// never asked and never holds its rows' outputs
    /// (<see cref="HidppReceiverProtocol"/>).
    /// A found device is asked for its configuration every 30 seconds while
    /// quiet: one that does not answer went to sleep, and its slot reopens so
    /// the next scan finds it when it wakes.</para>
    /// </summary>
    internal sealed class HidppBackend : IDisposable
    {
        internal const int FirstScanMs = 5000;
        internal const int RescanMs = 30000;
        internal const int CheckMs = 30000;
        internal const int QuietMs = 1000;

        /// <summary>The worker's cadence while a row linked to a unit for
        /// rumble holds a level above zero, and otherwise. A silent linked
        /// unit waits the idle tick: <see cref="PeripheralOutputs.HapticsChanged"/>
        /// wakes the worker when its rumble starts.</summary>
        internal const int ActiveTickMs = 10;
        internal const int IdleTickMs = 100;

        /// <summary>How long a new worker waits for the one a timed-out Stop
        /// left inside a probe, before it opens the collections anyway.</summary>
        internal const int PredecessorJoinMs = 5000;

        /// <summary>How often a lit unit's claim and color are sent again.</summary>
        internal const int ReassertMs = 5000;

        /// <summary>The fastest a unit's color is repainted, the effects
        /// dispatcher's own animation tick.</summary>
        internal const int PaintMs = 33;

        private sealed class Channel
        {
            public IHidppChannel Io;
            public HidppPathState State;
            public Guid Container;
            public readonly List<HidppUnit> Units = new();
            public readonly Dictionary<byte, (int Level, long NextPlay)> Play = new();
            public readonly Dictionary<byte, LightState> Light = new();
        }

        /// <summary>What a lit unit was last sent: whether the lighting is
        /// claimed and when the claim last went out, the color, and when.
        /// The claim keeps its own time, so a color that changes faster than
        /// the re-assert interval never holds the claim back.</summary>
        private struct LightState
        {
            public bool Claimed;
            public long ClaimedAt;
            public int Rgb;
            public long SentAt;
        }

        /// <summary>The units holding a claim now, for a hand-back from the
        /// crash path, which cannot wait on this worker. Swapped whole.</summary>
        private (string Path, byte DeviceIndex, byte RgbIndex)[] _claimed = Array.Empty<(string, byte, byte)>();

        private readonly Func<IReadOnlyList<VendorHidCollection>> _enumerate;
        private readonly Func<VendorHidCollection, VendorHidCollection, IHidppChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly int _reassertMs;
        private readonly Func<string, byte[], int, bool> _writeOnce;
        private readonly int _predecessorJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>Each present path's container ID, looked up once per
        /// appearance that answers it. Worker thread only.</summary>
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private HidppSnapshot _snapshot = HidppSnapshot.Empty;
        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _scans;
        private int _passes;
        private int _waitMs;
        private long _scanQuietMs;

        /// <summary>The newest worker thread of any instance, which the next
        /// one waits for. Static because the collections it holds are the
        /// machine's.</summary>
        private static Thread s_lastWorker;

        public HidppBackend()
            : this(null, null, null, ActiveTickMs, IdleTickMs) { }

        /// <summary>Test seam: the enumeration (every vendor collection, the
        /// long ones and their short siblings), the channel opener (a long
        /// collection and its receiver's short one, or null), the container
        /// lookup, the lighting re-assert interval, the one-shot writer the
        /// crash path uses and the wait for a predecessor worker, so a bench
        /// drives the worker against scripted channels.</summary>
        internal HidppBackend(Func<IReadOnlyList<VendorHidCollection>> enumerate,
            Func<VendorHidCollection, VendorHidCollection, IHidppChannel> open, Func<string, Guid> containerOf,
            int activeTickMs, int idleTickMs, int reassertMs = ReassertMs,
            Func<string, byte[], int, bool> writeOnce = null, int predecessorJoinMs = PredecessorJoinMs)
        {
            _enumerate = enumerate ?? VendorHidRuntime.Enumerate;
            _open = open ?? ((c, s) => HidppChannel.Open(c, s));
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _activeTickMs = activeTickMs;
            _idleTickMs = idleTickMs;
            _reassertMs = reassertMs;
            _writeOnce = writeOnce ?? RawHidOutput.WriteOnce;
            _predecessorJoinMs = predecessorJoinMs;
        }

        public HidppSnapshot Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>Raised from the worker after it publishes a new snapshot,
        /// so the host's link pass picks up a unit or a charge at once. Any
        /// thread. Subscribers must not block.</summary>
        public event Action SnapshotChanged;

        /// <summary>Scans this worker ran.</summary>
        internal int ScanCount => Volatile.Read(ref _scans);

        /// <summary>How long nothing had played when the last scan started,
        /// so the test of the quiet second reads what the worker measured
        /// and times nothing.</summary>
        internal long ScanQuietMs => Volatile.Read(ref _scanQuietMs);

        /// <summary>Passes the worker made, for the test of its cadence.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker asked for at the end of its last
        /// pass, so the test of its cadence reads the tick it chose and
        /// times nothing.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>A HID++ long collection with the 20-byte report every
        /// frame here is, on a usage OpenRGB's detector drives. Mice and
        /// receivers sit on usage page 0xFF00 usage 2, keyboards on 0xFF43
        /// usage 0x0602 (LogitechControllerDetect.cpp:162-166 and 1530), and
        /// on 0xFF43 usage 0x0202 are the headsets OpenRGB registers over USB
        /// (1553 and 1763-1768) and the devices paired over Bluetooth LE
        /// (OpenLogi openlogi-hid transport.rs:181-183 and 198-202,
        /// HIDPP_LONG_COLLECTIONS), a page OpenRGB's HID++ 2.0 detector takes
        /// over Bluetooth with any usage (1783-1784 and 1795). The 64-byte
        /// usage 0x0604 and the Centurion page 0xFFA0, whose frames this
        /// channel does not speak, stay out.</summary>
        internal static bool IsHidppLong(VendorHidCollection c)
            => c.VendorId == HidppHapticProtocol.VendorId
               && c.InputReportLength == HidppHapticProtocol.LongReportLength
               && ((c.UsagePage == HidppHapticProtocol.UsagePage && c.Usage == HidppHapticProtocol.LongUsage)
                   || (c.UsagePage == 0xFF43 && (c.Usage == 0x0602 || c.Usage == 0x0202)));

        public void Start()
        {
            if (_thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralHidpp" };
            _thread.Start();
            PeripheralOutputs.HapticsChanged += Wake;
            PeripheralOutputs.LightingChanged += Wake;
        }

        public void Stop()
        {
            PeripheralOutputs.HapticsChanged -= Wake;
            PeripheralOutputs.LightingChanged -= Wake;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(3000); } catch { }
            _thread = null;
            _run = null;
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Worker(WorkerRun run)
        {
            // A worker a timed-out Stop left inside a probe still holds the
            // collections, and its finally hands its units back, so a new
            // worker waits for it before it opens anything: the bounded join
            // SensaHapticsService makes for its predecessor and the orphan
            // wait LedSdkBackend makes. A predecessor that never comes back
            // holds the new worker only for that bound.
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL HID++ previous worker still running after {_predecessorJoinMs} ms, starting anyway");
            var channels = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, HidppPathState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                long start = Environment.TickCount64;
                long nextScan = start, nextCheck = start + CheckMs, quietSince = start;
                while (!run.Stop)
                {
                    Interlocked.Increment(ref _passes);
                    long now = Environment.TickCount64;
                    var links = PeripheralOutputs.Links;
                    bool active = PlayHaptics(channels, states, links, now, out bool playing, out bool linked, ref nextScan);
                    if (playing) quietSince = now;
                    PaintLighting(run, channels, states, links, now, ref nextScan);
                    bool quiet = now - quietSince >= QuietMs;

                    // While a unit is linked for rumble, a scan waits for a
                    // second with nothing playing, so a probe does not hold
                    // up rumble that comes in bursts.
                    if (now >= nextScan && (!linked || quiet))
                    {
                        Volatile.Write(ref _scanQuietMs, now - quietSince);
                        Scan(run, channels, states, now);
                        // A slot still waiting for its device, one asleep at
                        // launch or one that went to sleep, is asked again on
                        // its own backoff, so the scan comes as often as that
                        // can matter. With nothing waiting, a slow rescan finds
                        // what gets plugged in or paired.
                        nextScan = Environment.TickCount64 + (AnyWaiting(states) ? FirstScanMs : RescanMs);
                    }
                    if (now >= nextCheck && quiet)
                    {
                        // A device that went to sleep is asked for again on
                        // the fast cadence, so it is found soon after it wakes.
                        if (Check(run, channels, states))
                            nextScan = Math.Min(nextScan, Environment.TickCount64 + FirstScanMs);
                        nextCheck = Environment.TickCount64 + CheckMs;
                    }

                    int wait = active ? _activeTickMs : _idleTickMs;
                    Volatile.Write(ref _waitMs, wait);
                    // A Dispose whose Stop gave up its join releases the wake
                    // under a worker still finishing a probe, which ends here.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ worker fault: " + ex.GetType().Name);
            }
            finally
            {
                foreach (var channel in channels.Values)
                {
                    // Lighting goes back to the device's own effect before
                    // the channel closes, so stopping the engine leaves no
                    // mouse frozen on the last color.
                    try { ReleaseLighting(channel); } catch { }
                    try { channel.Io.Dispose(); } catch { }
                }
                Volatile.Write(ref _claimed, Array.Empty<(string, byte, byte)>());
                Volatile.Write(ref _snapshot, HidppSnapshot.Empty);
            }
        }

        /// <summary>One pass of colors over every unit lit directly.</summary>
        private void PaintLighting(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            LinkTable links, long now, ref long nextScan)
        {
            List<string> dead = null;
            bool claims = false;
            foreach (var pair in channels)
            {
                var channel = pair.Value;
                foreach (var unit in channel.Units)
                {
                    if (!unit.DirectRgb) continue;
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    channel.Light.TryGetValue(unit.DeviceIndex, out var light);
                    int rgb = 0;
                    bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb);
                    bool ok = true;
                    if (wanted)
                    {
                        // A device that comes back from sleep boots its
                        // onboard profile and drops the claim without a word
                        // (OpenRGB ReclaimSWControl,
                        // LogitechHIDPP20Controller.cpp:6102-6110), so the
                        // claim goes out again on its own interval, whatever
                        // the colors are doing.
                        bool reassert = light.Claimed && now - light.ClaimedAt >= _reassertMs;
                        bool repaint = !light.Claimed || reassert
                                       || (rgb != light.Rgb && now - light.SentAt >= PaintMs);
                        if (!repaint) continue;
                        long claimedAt = light.ClaimedAt;
                        // The crash path learns of a claim before the claim
                        // goes out, so no moment exists in which the device
                        // is claimed and the hand-back does not know it. A
                        // claim that then fails is handed back for nothing.
                        // A stopping worker claims nothing more.
                        if (run.Stop) return;
                        if (!light.Claimed) NoteClaimed(channel.Io.Path, unit.DeviceIndex, unit.RgbIndex);
                        if (!light.Claimed || reassert)
                        {
                            ok = channel.Io.Write(HidppUnitProtocol.SetSwControl(unit.DeviceIndex, unit.RgbIndex, claim: true));
                            claimedAt = now;
                        }
                        foreach (var zone in unit.Zones)
                        {
                            if (!ok) break;
                            ok = channel.Io.Write(HidppUnitProtocol.SetStaticColor(unit.DeviceIndex, unit.RgbIndex, zone,
                                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                        }
                        if (ok)
                        {
                            if (!light.Claimed)
                            {
                                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' lighting claimed");
                                claims = true;
                            }
                            channel.Light[unit.DeviceIndex] = new LightState
                            {
                                Claimed = true, ClaimedAt = claimedAt, Rgb = rgb, SentAt = now,
                            };
                        }
                    }
                    else if (light.Claimed && LeftToLogitechProgram(path))
                    {
                        // G HUB or Logitech Gaming Software started and the
                        // linker took the unit's path away. The program
                        // lights the device from here on, and a hand-back
                        // that landed after its own claim would clear that
                        // claim, so the worker lets go without a frame.
                        channel.Light.Remove(unit.DeviceIndex);
                        claims = true;
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' lighting left to the Logitech program that started");
                    }
                    else if (light.Claimed)
                    {
                        ok = channel.Io.Write(HidppUnitProtocol.SetSwControl(unit.DeviceIndex, unit.RgbIndex, claim: false));
                        channel.Light.Remove(unit.DeviceIndex);
                        claims = true;
                        if (ok) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' lighting handed back");
                    }
                    if (!ok)
                    {
                        (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                }
            }
            if (dead != null)
            {
                foreach (var path in dead)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ write failed, dropping " + path);
                    Close(channels, states, path, forget: true);
                }
                nextScan = now;
                Publish(channels, states);
                claims = true;
            }
            if (claims) PublishClaimed(channels);
        }

        /// <summary>Whether a claimed unit's lighting path is gone because a
        /// Logitech program started: no row is linked to the path for
        /// lighting any more, and G HUB or Logitech Gaming Software runs. A
        /// path that is still linked and merely unclaimed, a switch turned
        /// off or a device unassigned, is handed back as ever. The table is
        /// read before the presence: the link pass publishes the presence
        /// first, so a table that shows the path withdrawn is never paired
        /// with a presence older than it.</summary>
        private static bool LeftToLogitechProgram(OutputPath path)
        {
            var links = PeripheralOutputs.Links;
            if (links.ByPath.TryGetValue(path, out var devices))
            {
                foreach (var device in devices)
                {
                    var row = links.For(device);
                    if (row != null && Array.IndexOf(row.Lighting, path) >= 0) return false;
                }
            }
            return PeripheralOutputs.Presence.LogitechGHubRunning;
        }

        /// <summary>Adds one unit to the crash path's record ahead of its
        /// claim. Worker thread only, swapped whole.</summary>
        private void NoteClaimed(string path, byte deviceIndex, byte rgbIndex)
        {
            var current = Volatile.Read(ref _claimed);
            foreach (var entry in current)
                if (entry.DeviceIndex == deviceIndex && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    return;
            var next = new (string, byte, byte)[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = (path, deviceIndex, rgbIndex);
            Volatile.Write(ref _claimed, next);
        }

        /// <summary>Records which units hold a claim, for the crash path.</summary>
        private void PublishClaimed(Dictionary<string, Channel> channels)
        {
            var claimed = new List<(string, byte, byte)>();
            foreach (var channel in channels.Values)
                foreach (var unit in channel.Units)
                    if (channel.Light.TryGetValue(unit.DeviceIndex, out var light) && light.Claimed)
                        claimed.Add((channel.Io.Path, unit.DeviceIndex, unit.RgbIndex));
            Volatile.Write(ref _claimed, claimed.ToArray());
        }

        /// <summary>For the crash path and the process exit: hands every
        /// claimed unit back to its own effect. A crash dialog keeps the
        /// process, and this worker, alive, and a worker left running would
        /// claim the device again on its next re-assert, so the worker stops
        /// first and its own finally hands back what it holds, OpenRGB's
        /// teardown order (its sender stops before SetSWControl(0, 0),
        /// LogitechHIDPP20Controller.cpp:3843-3856). A worker that does not
        /// stop within <see cref="PanicJoinMs"/> claims nothing more, and each
        /// unit still recorded goes back through a one-shot write that opens
        /// its own handle with a short timeout, so nothing here waits on the
        /// worker's channel. The frame and the path are the ones the channel's
        /// own write sends, HidppChannel.Write going through
        /// RawHidOutput.Write.</summary>
        public void ReleaseClaimedNow()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            try { _thread?.Join(PanicJoinMs); } catch { }
            foreach (var (path, deviceIndex, rgbIndex) in Interlocked.Exchange(ref _claimed, Array.Empty<(string, byte, byte)>()))
            {
                try { _writeOnce(path, HidppUnitProtocol.SetSwControl(deviceIndex, rgbIndex, claim: false), 200); }
                catch { }
            }
        }

        /// <summary>How long the crash path waits for the worker to stop and
        /// hand its devices back on its own channel.</summary>
        internal const int PanicJoinMs = 250;

        /// <summary>Hands every claimed unit on a channel back to its own
        /// effect.</summary>
        private static void ReleaseLighting(Channel channel)
        {
            foreach (var unit in channel.Units)
            {
                if (!channel.Light.TryGetValue(unit.DeviceIndex, out var light) || !light.Claimed) continue;
                channel.Io.Write(HidppUnitProtocol.SetSwControl(unit.DeviceIndex, unit.RgbIndex, claim: false));
            }
            channel.Light.Clear();
        }

        /// <summary>One pass of pulses. <paramref name="linked"/> is set when
        /// any unit is linked for rumble, which makes a scan wait for a quiet
        /// second, and <paramref name="playing"/> when any unit plays a
        /// pulse. True when a row linked to a unit holds a level above zero,
        /// which keeps the worker on its fast tick: a level below the shaper's
        /// first step plays nothing yet, and when it climbs into a pulse it
        /// raises no <see cref="PeripheralOutputs.HapticsChanged"/>, so the
        /// tick has to be fast there too.</summary>
        private bool PlayHaptics(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            LinkTable links, long now, out bool playing, out bool linked, ref long nextScan)
        {
            playing = false;
            linked = false;
            bool active = false;
            List<string> dead = null;
            foreach (var pair in channels)
            {
                var channel = pair.Value;
                foreach (var unit in channel.Units)
                {
                    if (!unit.HasHaptics) continue;
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    if (!links.ByPath.TryGetValue(path, out var devices)) continue;
                    linked = true;
                    float amplitude = 0f;
                    foreach (var device in devices)
                    {
                        var row = links.For(device);
                        if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                        float a = PeripheralOutputs.AmplitudeOf(device);
                        if (a > amplitude) amplitude = a;
                    }
                    if (amplitude > 0f) active = true;
                    channel.Play.TryGetValue(unit.DeviceIndex, out var play);
                    int level = HapticRumbleShaper.Level(amplitude, play.Level);
                    if (level == 0)
                    {
                        channel.Play[unit.DeviceIndex] = (0, 0);
                        continue;
                    }
                    playing = true;
                    if (now < play.NextPlay)
                    {
                        channel.Play[unit.DeviceIndex] = (level, play.NextPlay);
                        continue;
                    }
                    byte? waveform = HapticRumbleShaper.Waveform(level, unit.WaveformMask);
                    if (waveform == null) continue;
                    if (!channel.Io.Write(HidppHapticProtocol.Play(unit.DeviceIndex, unit.HapticIndex, waveform.Value)))
                    {
                        (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                    channel.Play[unit.DeviceIndex] = (level, now + HapticRumbleShaper.IntervalMs(amplitude));
                }
            }
            if (dead != null)
            {
                foreach (var path in dead)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ write failed, dropping " + path);
                    Close(channels, states, path, forget: true);
                }
                nextScan = now;
                Publish(channels, states);
            }
            return active;
        }

        private void Scan(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states, long now)
        {
            Interlocked.Increment(ref _scans);
            var all = _enumerate();
            if (all == null) return;
            var collections = all.Where(IsHidppLong).ToList();

            var present = new HashSet<string>(collections.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in states.Keys.Where(p => !present.Contains(p)).ToList())
            {
                Close(channels, states, path, forget: true);
                _containers.Remove(path);
            }

            foreach (var collection in collections)
            {
                if (run.Stop) return;
                if (!states.TryGetValue(collection.Path, out var state))
                {
                    states[collection.Path] = state = new HidppPathState
                    {
                        ReceiverKind = collection.UsagePage == HidppHapticProtocol.UsagePage
                            ? HidppReceiverProtocol.KindOf(collection.ProductId)
                            : HidppReceiverKind.None,
                    };
                }
                if (!_containers.TryGetValue(collection.Path, out var container))
                {
                    try { container = _containerOf(collection.Path); }
                    catch { container = Guid.Empty; }
                    // A failed lookup is not kept: the next scan asks again,
                    // and the units already found on the path take the
                    // container once it answers. Kept, it would leave every
                    // unit there unmatched for as long as the path stays.
                    if (container != Guid.Empty)
                    {
                        _containers[collection.Path] = container;
                        if (channels.TryGetValue(collection.Path, out var known) && known.Container == Guid.Empty)
                        {
                            known.Container = container;
                            for (int i = 0; i < known.Units.Count; i++)
                                known.Units[i] = known.Units[i] with { ContainerId = container };
                        }
                    }
                }
                // A known receiver's pairing table is read at every scan, so a
                // device paired since the last one is found.
                bool pairing = state.ReceiverKind != HidppReceiverKind.None
                               && state.PairingMisses < HidppUnitProbe.PairingGiveUp;
                if (!pairing && !HasQuestions(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path))) continue;

                bool open = channels.TryGetValue(collection.Path, out var channel);
                var io = open ? channel.Io
                    : _open(collection, state.ReceiverKind != HidppReceiverKind.None
                        ? HidppReceiverProtocol.ShortSibling(collection, all)
                        : null);
                if (io == null)
                {
                    HidppUnitProbe.MissOpenSlots(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path));
                    continue;
                }
                var found = HidppUnitProbe.Probe(io, state, container, now);
                // The charge for the Battery lighting mode, read once here and
                // again with each liveness check.
                for (int i = 0; i < found.Count; i++)
                {
                    if (found[i].BatteryFeatureId == 0) continue;
                    found[i] = found[i] with { BatteryPercent = HidppUnitProbe.ReadBattery(io, found[i]) };
                }
                if (found.Count > 0)
                {
                    if (!open)
                    {
                        channel = new Channel { Io = io, State = state, Container = container };
                        channels[collection.Path] = channel;
                    }
                    channel.Units.AddRange(found);
                    foreach (var unit in found)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ found '{unit.Name}' index=0x{unit.DeviceIndex:X2} type={unit.DeviceType}"
                            + $" haptic=0x{unit.HapticIndex:X2} mask=0x{unit.WaveformMask:X8}"
                            + $" rgb={unit.RgbFeatureId:X4} zones={unit.Zones.Length} battery={unit.BatteryFeatureId:X4}"
                            + $" charge={unit.BatteryPercent}");
                }
                else if (!open)
                {
                    io.Dispose();
                }

                if (state.Dead) Close(channels, states, collection.Path, forget: true);
            }
            // A slot that settled changes what the linker may conclude even
            // with nothing found, so every scan publishes.
            Publish(channels, states);
        }

        /// <summary>Whether a scan has anything to ask this path: a slot not
        /// yet answered whose backoff is over.</summary>
        private static bool HasQuestions(HidppPathState state, long now, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Open(slot) && now >= state.RetryAt[slot]) return true;
            return false;
        }

        /// <summary>Whether any present path has a slot still waiting for
        /// its device.</summary>
        private static bool AnyWaiting(Dictionary<string, HidppPathState> states)
        {
            foreach (var pair in states)
                if (!pair.Value.Dead && HidppUnitProbe.Pending(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key)))
                    return true;
            return false;
        }

        /// <summary>Asks each found unit something cheap: a haptic unit its
        /// configuration, any other its name feature. One that does not
        /// answer went to sleep or away. It leaves the list and its slot
        /// reopens, so the next scan finds it again once it answers. A unit
        /// that answers and reports a charge has it read again. True when a
        /// slot reopened.</summary>
        private bool Check(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            bool changed = false;
            bool reopened = false;
            foreach (var path in channels.Keys.ToList())
            {
                if (run.Stop) return reopened;
                var channel = channels[path];
                for (int i = channel.Units.Count - 1; i >= 0; i--)
                {
                    var unit = channel.Units[i];
                    bool? alive;
                    if (unit.HapticIndex != 0)
                    {
                        bool? enabled = HidppHapticProbe.ReadFeedbackEnabled(channel.Io, unit.DeviceIndex, unit.HapticIndex);
                        alive = enabled != null;
                        if (enabled != null && enabled.Value != unit.FeedbackEnabled)
                        {
                            channel.Units[i] = unit with { FeedbackEnabled = enabled.Value };
                            changed = true;
                        }
                    }
                    else
                    {
                        var reply = channel.Io.Request(unit.DeviceIndex, 0x00, 0,
                            new byte[] { 0x00, 0x05 }, HidppHapticProtocol.TimeoutMs(channel.Io.Bluetooth));
                        // A receiver's HID++ 1.0 error means the device is
                        // out of reach, as in ReadFeedbackEnabled.
                        alive = reply.Kind == HidppReplyKind.Answer
                                || (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10);
                    }
                    if (alive == true)
                    {
                        if (unit.BatteryFeatureId == 0) continue;
                        int percent = HidppUnitProbe.ReadBattery(channel.Io, channel.Units[i]);
                        if (percent < 0 || percent == channel.Units[i].BatteryPercent) continue;
                        channel.Units[i] = channel.Units[i] with { BatteryPercent = percent };
                        changed = true;
                        continue;
                    }
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' stopped answering");
                    channel.Units.RemoveAt(i);
                    channel.Play.Remove(unit.DeviceIndex);
                    channel.Light.Remove(unit.DeviceIndex);
                    channel.State.Reopen(unit.DeviceIndex);
                    changed = true;
                    reopened = true;
                }
                if (channel.Units.Count == 0)
                {
                    Close(channels, states, path, forget: false);
                    changed = true;
                }
            }
            if (changed) Publish(channels, states);
            return reopened;
        }

        /// <summary>Closes a path's channel. Forgetting also drops what the
        /// path answered, for one that vanished or failed.</summary>
        private static void Close(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            string path, bool forget)
        {
            if (channels.Remove(path, out var channel))
            {
                try { channel.Io.Dispose(); } catch { }
            }
            if (forget) states.Remove(path);
        }

        private void Publish(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            var units = new List<HidppUnit>();
            foreach (var channel in channels.Values) units.AddRange(channel.Units);
            var pending = new HashSet<Guid>();
            foreach (var pair in states)
            {
                if (pair.Value.Dead) continue;
                if (!HidppUnitProbe.HasOpenSlot(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key))) continue;
                if (_containers.TryGetValue(pair.Key, out var container) && container != Guid.Empty)
                    pending.Add(container);
            }
            Volatile.Write(ref _snapshot, new HidppSnapshot(units, pending));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
