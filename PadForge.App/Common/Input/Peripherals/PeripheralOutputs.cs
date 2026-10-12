using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What a device row can do besides input, persisted on the row
    /// (<see cref="PadForge.Engine.Data.UserDevice.PeripheralOutputs"/>) so
    /// its tabs stay up while it sleeps.</summary>
    [Flags]
    internal enum PeripheralOutputKinds
    {
        None = 0,
        Haptics = 1,
        Lighting = 2,
    }

    /// <summary>The protocol a peripheral output travels on. One unit is a
    /// single physical device. Every other family addresses a device class
    /// the vendor's software resolves, so several devices share it.</summary>
    internal enum OutputFamily
    {
        /// <summary>One Logitech device on its HID++ collection.</summary>
        HidppUnit,
        /// <summary>A Razer Chroma device category through Synapse.</summary>
        ChromaCategory,
        /// <summary>A Logitech LED SDK device type through G HUB.</summary>
        LedSdkType,
        /// <summary>A SteelSeries GameSense device type through GG.</summary>
        GameSenseColor,
        /// <summary>The GameSense tactile handler, every tactile Rival at once.</summary>
        GameSenseTactile,
        /// <summary>Razer Sensa HD devices through the Interhaptics engine.</summary>
        Interhaptics,
    }

    /// <summary>Where a backend stands, for the device tabs.</summary>
    internal enum BackendState
    {
        /// <summary>Nothing assigned uses it, so it holds nothing.</summary>
        Idle,
        Connected,
        /// <summary>Assigned, and the vendor's software is not answering.</summary>
        Waiting,
    }

    /// <summary>One Start of a peripheral worker thread: its stop flag. Each
    /// Start makes its own, the way each ChromaBackend.Start makes its own
    /// token, so a worker that outlived a timed-out Stop reads its own flag
    /// set and ends, whatever a later Start of the same object does.</summary>
    internal sealed class WorkerRun
    {
        public volatile bool Stop;
    }

    /// <summary>One output path: a family and the key inside it (a HID++
    /// collection and device index, a Chroma category, an LED SDK type).</summary>
    internal readonly record struct OutputPath(OutputFamily Family, string Key)
    {
        /// <summary>True when the path reaches more than one device, so the
        /// assignment with the smallest displayed player number rules it.</summary>
        public bool Shared => Family != OutputFamily.HidppUnit;
    }

    /// <summary>A row's output paths.</summary>
    internal sealed class DeviceLinks
    {
        public Guid Device { get; init; }
        public OutputPath[] Lighting { get; init; } = Array.Empty<OutputPath>();
        public OutputPath[] Haptics { get; init; } = Array.Empty<OutputPath>();

        /// <summary>The HID++ units the row matches, whatever path lights or
        /// rumbles it, so Battery mode reads a Logitech device's charge while
        /// G HUB runs too.</summary>
        public string[] HidppUnits { get; init; } = Array.Empty<string>();

        /// <summary>A vendor row (Razer Chroma, Logitech LIGHTSYNC,
        /// SteelSeries GG, Razer Sensa): it takes the paths no assigned
        /// device of its own claims, so a mouse set up on its own Lighting tab
        /// keeps that color.</summary>
        public bool CatchAll { get; init; }

        public PeripheralOutputKinds Kinds
            => (Lighting.Length > 0 ? PeripheralOutputKinds.Lighting : PeripheralOutputKinds.None)
             | (Haptics.Length > 0 ? PeripheralOutputKinds.Haptics : PeripheralOutputKinds.None);
    }

    /// <summary>An immutable snapshot of every row's paths, swapped whole by
    /// the linker so a reader never sees half a pass.</summary>
    internal sealed class LinkTable
    {
        public static readonly LinkTable Empty = new(Array.Empty<DeviceLinks>());

        public IReadOnlyDictionary<Guid, DeviceLinks> ByDevice { get; }
        public IReadOnlyDictionary<OutputPath, Guid[]> ByPath { get; }

        public LinkTable(IEnumerable<DeviceLinks> rows)
        {
            var byDevice = new Dictionary<Guid, DeviceLinks>();
            var byPath = new Dictionary<OutputPath, List<Guid>>();
            foreach (var row in rows)
            {
                if (row == null || row.Device == Guid.Empty) continue;
                byDevice[row.Device] = row;
                foreach (var path in row.Lighting) Add(byPath, path, row.Device);
                foreach (var path in row.Haptics) Add(byPath, path, row.Device);
            }
            ByDevice = byDevice;
            var frozen = new Dictionary<OutputPath, Guid[]>(byPath.Count);
            foreach (var pair in byPath) frozen[pair.Key] = pair.Value.ToArray();
            ByPath = frozen;
        }

        private static void Add(Dictionary<OutputPath, List<Guid>> map, OutputPath path, Guid device)
        {
            if (!map.TryGetValue(path, out var list)) map[path] = list = new List<Guid>();
            if (!list.Contains(device)) list.Add(device);
        }

        public DeviceLinks For(Guid device) => ByDevice.TryGetValue(device, out var links) ? links : null;

        /// <summary>True when both tables link the same rows to the same
        /// paths, so the linker publishes only a change.</summary>
        public bool SameAs(LinkTable other)
        {
            if (other == null || other.ByDevice.Count != ByDevice.Count) return false;
            foreach (var pair in ByDevice)
            {
                var theirs = other.For(pair.Key);
                if (theirs == null || theirs.CatchAll != pair.Value.CatchAll
                    || !SamePaths(theirs.Lighting, pair.Value.Lighting)
                    || !SamePaths(theirs.Haptics, pair.Value.Haptics)
                    || !theirs.HidppUnits.AsSpan().SequenceEqual(pair.Value.HidppUnits))
                    return false;
            }
            return true;
        }

        private static bool SamePaths(OutputPath[] a, OutputPath[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// The meeting point between the engine and the peripheral backends
    /// (#494). A mouse, keyboard or vendor row assigned to a virtual
    /// controller takes that controller's rumble and lighting through its own
    /// tabs, the way a DualSense does: Step 2 hands each haptic row its
    /// combined level (<see cref="SetMotors"/>), the slot's effects
    /// dispatcher hands each lit row its color (<see cref="SetLighting"/>),
    /// and the backends read them per path.
    ///
    /// <para>A path that reaches one device takes that device's output. A
    /// shared path (a Chroma category, an LED SDK type, GameSense) goes to
    /// the device on the controller with the smallest displayed player
    /// number, the precedence <c>InputService.ApplyGuideLeds</c> gives the
    /// Steam Controller's process-wide home LED. A vendor row yields a
    /// path to any device that claims it on its own. Identify takes a
    /// shared haptic path for the length of its train
    /// (<see cref="BeginIdentify(Guid, int)"/>).</para>
    ///
    /// <para>Every setter is lock-free or takes only this class's leaf
    /// state, so the poll thread and the dispatcher never wait on a
    /// backend's I/O.</para>
    /// </summary>
    internal static class PeripheralOutputs
    {
        private static LinkTable s_links = LinkTable.Empty;
        private static int s_linkVersion;

        public static LinkTable Links => Volatile.Read(ref s_links);
        public static int LinkVersion => Volatile.Read(ref s_linkVersion);

        /// <summary>Raised after a new link table is published. Any thread.</summary>
        public static event Action LinksChanged;

        public static void PublishLinks(LinkTable table)
        {
            Volatile.Write(ref s_links, table ?? LinkTable.Empty);
            Interlocked.Increment(ref s_linkVersion);
            try { LinksChanged?.Invoke(); } catch { }
        }

        public static bool HasHaptics(Guid device) => Links.For(device)?.Haptics.Length > 0;
        public static bool HasLighting(Guid device) => Links.For(device)?.Lighting.Length > 0;

        /// <summary>A row that takes rumble as a haptic peripheral: one with a
        /// haptic path now, or whose record says it has one while its device
        /// sleeps. Every writer of a level keys on this, so the level stays
        /// current while the path is down and a woken device plays what the
        /// game asks for now.</summary>
        public static bool TakesHaptics(PadForge.Engine.Data.UserDevice ud)
            => ud != null && (ud.HasPeripheralHaptics || HasHaptics(ud.InstanceGuid));

        /// <summary>A mouse, keyboard or vendor row that rumbles, for the
        /// Force Feedback tab and the Devices page's rumble chip: this PC's
        /// (<see cref="TakesHaptics"/>), or a mouse or keyboard a linked PC
        /// forwards with rumble, whose level ships back to that PC.</summary>
        public static bool IsHapticPeripheral(PadForge.Engine.Data.UserDevice ud)
            => TakesHaptics(ud)
               || (ud?.Device is PadForge.Engine.RemoteLink.RemotePeerDevice peer && peer.HasRumble
                   && (ud.CapType == PadForge.Engine.InputDeviceType.Mouse
                       || ud.CapType == PadForge.Engine.InputDeviceType.Keyboard));

        // ─────────────────────────────────────────────
        //  What the tabs show
        // ─────────────────────────────────────────────

        private static PeripheralPresence s_presence = PeripheralPresence.None;
        private static HidppSnapshot s_hidpp = HidppSnapshot.Empty;
        private static string[] s_ledSdkPaintable;
        private static readonly int[] s_states = new int[Enum.GetValues(typeof(OutputFamily)).Length];

        /// <summary>Raised when a backend's state, the presence of vendor
        /// software, the found HID++ units or a row's recorded outputs
        /// change. Any thread. The owner marshals.</summary>
        public static event Action StatusChanged;

        public static PeripheralPresence Presence
        {
            get => Volatile.Read(ref s_presence);
            set
            {
                var next = value ?? PeripheralPresence.None;
                var previous = Interlocked.Exchange(ref s_presence, next);
                if (previous != next) RaiseStatusChanged();
            }
        }

        /// <summary>The HID++ worker's latest units, for a tab naming the
        /// device its row reaches.</summary>
        public static HidppSnapshot Hidpp
        {
            get => Volatile.Read(ref s_hidpp);
            set
            {
                var next = value ?? HidppSnapshot.Empty;
                if (!ReferenceEquals(Interlocked.Exchange(ref s_hidpp, next), next)) RaiseStatusChanged();
            }
        }

        /// <summary>The LED SDK paths the engine the worker last loaded can
        /// paint, empty while the running software's engine cannot be loaded,
        /// and null while nothing is claimed or no Logitech software runs. The
        /// Lighting tab names a device type that engine cannot paint instead
        /// of a route.</summary>
        public static string[] LedSdkPaintable
        {
            get => Volatile.Read(ref s_ledSdkPaintable);
            set
            {
                var previous = Interlocked.Exchange(ref s_ledSdkPaintable, value);
                bool same = previous == null || value == null
                    ? previous == value
                    : previous.AsSpan().SequenceEqual(value);
                if (!same) RaiseStatusChanged();
            }
        }

        public static void SetBackendState(OutputFamily family, BackendState state)
        {
            if (Interlocked.Exchange(ref s_states[(int)family], (int)state) != (int)state)
                RaiseStatusChanged();
        }

        public static BackendState StateOf(OutputFamily family)
            => (BackendState)Volatile.Read(ref s_states[(int)family]);

        /// <summary>For a row whose recorded outputs changed while the link
        /// table did not.</summary>
        public static void NotifyStatusChanged() => RaiseStatusChanged();

        private static void RaiseStatusChanged()
        {
            try { StatusChanged?.Invoke(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  The engine and its slots
        // ─────────────────────────────────────────────

        private static int s_running;
        private static Func<int, bool> s_slotHasController;

        /// <summary>True while the peripheral host runs, which is while the
        /// engine runs. A stopped engine sends nothing to any device, and
        /// every device goes back to its own software, so the tabs' route
        /// lines say nothing then, the rule the other status lines follow on
        /// an engine stop (#469).</summary>
        public static bool Running => Volatile.Read(ref s_running) != 0;

        /// <summary>The host's start and stop. A change raises
        /// <see cref="StatusChanged"/>, so a tab drops or restores its line.</summary>
        internal static void SetRunning(bool running)
        {
            int next = running ? 1 : 0;
            if (Interlocked.Exchange(ref s_running, next) != next) RaiseStatusChanged();
        }

        /// <summary>Whether a slot still holds a virtual controller, which
        /// the host publishes from its owner while it runs. Null treats every
        /// slot as without one.</summary>
        internal static Func<int, bool> SlotHasController
        {
            get => Volatile.Read(ref s_slotHasController);
            set => Volatile.Write(ref s_slotHasController, value);
        }

        /// <summary>Whether a slot lights and rumbles anything: a live effects
        /// dispatcher lights it, or a virtual controller still sits there. A
        /// reorder moves the controllers first and rebuilds the slots'
        /// dispatchers after, one slot at a time, so a slot can be without a
        /// dispatcher for a second or two while its controller is in place,
        /// and this stays true through it. The link pass prunes the claims of
        /// a slot this answers false for, and the tabs read the same answer,
        /// so a tab never names a route the pass has let go.</summary>
        public static bool SlotLive(int slot)
            => UserEffectsDispatcher.HasLiveDispatcher(slot) || SlotHasController?.Invoke(slot) == true;

        // ─────────────────────────────────────────────
        //  Haptics
        // ─────────────────────────────────────────────

        /// <summary>A row's level. A write and the focus edge's keep-or-zero
        /// decision take its lock, so a relayed write never lands between the
        /// edge's look at the relay flag and its zero. Readers read the bits
        /// without it.</summary>
        private sealed class HapticLevel
        {
            public int AmplitudeBits;
            public int ResendOwed;
            /// <summary>1 when a Remote Link peer wrote the level.</summary>
            public int Relayed;
        }

        private static readonly ConcurrentDictionary<Guid, HapticLevel> s_levels = new();

        /// <summary>Wakes the haptic backends when a level starts, so the
        /// first pulse of new rumble plays at once.</summary>
        public static event Action HapticsChanged;

        /// <summary>A haptic row's combined motor levels from Step 2, already
        /// scaled by its Force Feedback settings and folded for a device
        /// without trigger motors. A device that plays one waveform or one
        /// intensity takes the stronger motor. The level is kept whether or
        /// not a path is linked now: a backend plays only a linked path, so a
        /// device that wakes plays the level the game holds, and a stop sent
        /// while it slept is never lost. <paramref name="relayed"/> marks a
        /// level a Remote Link peer sent, which the focus edge leaves playing
        /// (<see cref="SilenceHaptics"/>).</summary>
        public static void SetMotors(Guid device, ushort left, ushort right, bool relayed = false)
        {
            int bits = BitConverter.SingleToInt32Bits(Math.Max(left, right) / 65535f);
            if (bits == 0)
            {
                StopHaptics(device);
                return;
            }
            var level = s_levels.GetOrAdd(device, _ => new HapticLevel());
            int previous;
            lock (level)
            {
                Volatile.Write(ref level.Relayed, relayed ? 1 : 0);
                previous = Interlocked.Exchange(ref level.AmplitudeBits, bits);
            }
            if (previous == 0) RaiseHapticsChanged();
        }

        /// <summary>A row's level to zero, for a device going offline, its
        /// row retiring or being removed, or a new force-feedback cache, which
        /// starts from zero. Nothing to do for a row that never had one.</summary>
        public static void StopHaptics(Guid device)
        {
            if (!s_levels.TryGetValue(device, out var level)) return;
            lock (level)
            {
                Volatile.Write(ref level.Relayed, 0);
                Interlocked.Exchange(ref level.AmplitudeBits, 0);
            }
        }

        /// <summary>The row's level, 0..1.</summary>
        public static float AmplitudeOf(Guid device)
            => s_levels.TryGetValue(device, out var level)
                ? BitConverter.Int32BitsToSingle(Volatile.Read(ref level.AmplitudeBits))
                : 0f;

        /// <summary>The engine's silence edge: stop, focus suspend and the
        /// crash quiesce, where Step 2 does not run. Every level drops to zero
        /// and each row owes a resend, so the level a game still asks for
        /// comes back the moment Step 2 runs again instead of reading as
        /// unchanged. Idle is no such edge: Step 2 runs there and writes the
        /// level itself, as it does for SDL rumble.
        ///
        /// <para>Focus suspend passes <paramref name="keepRelayed"/>: a level
        /// a Remote Link peer drives keeps playing, as a peer-driven SDL
        /// rumble does, since the relay still writes while the engine is
        /// suspended and a peer sends a steady level only once. An engine
        /// stop ends the relay too, so it silences every level.</para></summary>
        public static void SilenceHaptics(bool keepRelayed = false)
        {
            foreach (var pair in s_levels)
            {
                var level = pair.Value;
                lock (level)
                {
                    if (keepRelayed && Volatile.Read(ref level.Relayed) != 0) continue;
                    if (Interlocked.Exchange(ref level.AmplitudeBits, 0) != 0)
                        Volatile.Write(ref level.ResendOwed, 1);
                }
            }
        }

        /// <summary>True once after a silence edge zeroed this row.</summary>
        public static bool ConsumeResend(Guid device)
            => s_levels.TryGetValue(device, out var level)
               && Interlocked.Exchange(ref level.ResendOwed, 0) != 0;

        private static void RaiseHapticsChanged()
        {
            try { HapticsChanged?.Invoke(); } catch { }
        }

        /// <summary>The rows Identify buzzes, each with the tick its claim
        /// lapses at. GameSense drives every tactile Rival at once and the
        /// Razer Sensa row runs its engine only while it is assigned, so a
        /// level Identify writes for an unassigned row, or for a Rival another
        /// controller's Rival rules, would play nowhere. A row Identify buzzes
        /// rules its shared haptic path for the train instead
        /// (<see cref="TryResolveHapticRuler"/>), and the host runs the Sensa
        /// worker for it. The train ends its claim, and the lapse bounds a
        /// claim that is never ended, since for that long it takes an assigned
        /// Rival's rumble from its controller.</summary>
        private static readonly ConcurrentDictionary<Guid, long> s_identify = new();

        /// <summary>Raised when Identify starts or ends on a row. Any thread.
        /// Subscribers must not block.</summary>
        public static event Action IdentifyChanged;

        /// <summary>Identify starts buzzing a row, for at most
        /// <paramref name="durationMs"/>.</summary>
        public static void BeginIdentify(Guid device, int durationMs)
            => BeginIdentify(device, durationMs, Environment.TickCount64);

        /// <summary><see cref="BeginIdentify(Guid, int)"/> at a given tick, so
        /// a test hands the claim and the read one clock.</summary>
        internal static void BeginIdentify(Guid device, int durationMs, long now)
        {
            if (device == Guid.Empty) return;
            s_identify[device] = now + Math.Max(0, durationMs);
            RaiseIdentifyChanged();
        }

        /// <summary>Identify's train on a row ended.</summary>
        public static void EndIdentify(Guid device)
        {
            if (s_identify.TryRemove(device, out _)) RaiseIdentifyChanged();
        }

        /// <summary>Whether Identify buzzes a row now.</summary>
        public static bool IsIdentifying(Guid device) => IsIdentifying(device, Environment.TickCount64);

        internal static bool IsIdentifying(Guid device, long now)
            => s_identify.TryGetValue(device, out long until) && now < until;

        private static void RaiseIdentifyChanged()
        {
            try { IdentifyChanged?.Invoke(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  Lighting
        // ─────────────────────────────────────────────

        private sealed class LightClaim
        {
            public int Player;
            public int Rgb;
        }

        /// <summary>One claim per (device, slot): a mouse on two virtual
        /// controllers holds one from each, and the ruling picks between
        /// them the way it picks between two devices on a shared path, so
        /// the two dispatchers never take turns repainting it.</summary>
        private static readonly ConcurrentDictionary<(Guid Device, int Slot), LightClaim> s_claims = new();
        private static int s_lightingVersion;

        /// <summary>Bumped on every claim, release and color change.</summary>
        public static int LightingVersion => Volatile.Read(ref s_lightingVersion);

        /// <summary>Wakes the lighting backends on a change.</summary>
        public static event Action LightingChanged;

        /// <summary>Raised when a claim starts, ends or changes rank, never
        /// for a color alone, so a tab naming the controller that rules a
        /// shared path can follow it without redrawing at animation speed.
        /// Any thread. The owner marshals.</summary>
        public static event Action ClaimsChanged;

        /// <summary>The color a lit row shows for one slot, from that slot's
        /// effects dispatcher. <paramref name="player"/> is the slot's
        /// displayed player number, which ranks this claim against the
        /// device's claims from other slots and against other devices on a
        /// shared path.</summary>
        public static void SetLighting(Guid device, int slot, int player, byte r, byte g, byte b)
        {
            int rgb = (r << 16) | (g << 8) | b;
            bool changed = false, ranked = false;
            s_claims.AddOrUpdate((device, slot),
                _ =>
                {
                    changed = true;
                    ranked = true;
                    return new LightClaim { Player = player, Rgb = rgb };
                },
                (_, claim) =>
                {
                    lock (claim)
                    {
                        if (claim.Player != player || claim.Rgb != rgb)
                        {
                            ranked = claim.Player != player;
                            claim.Player = player;
                            claim.Rgb = rgb;
                            changed = true;
                        }
                    }
                    return claim;
                });
            if (changed) BumpLighting();
            if (ranked) RaiseClaimsChanged();
        }

        /// <summary>This slot no longer lights the row: the row left the
        /// slot, or its Lighting tab gave the device back. Another slot's
        /// claim on the same row is left alone.</summary>
        public static void ReleaseLighting(Guid device, int slot)
        {
            if (!s_claims.TryRemove((device, slot), out _)) return;
            BumpLighting();
            RaiseClaimsChanged();
        }

        /// <summary>Every claim on a row, for a row that retired or was
        /// removed from the Devices page.</summary>
        public static void ReleaseDevice(Guid device)
        {
            bool any = false;
            foreach (var key in s_claims.Keys)
                if (key.Device == device && s_claims.TryRemove(key, out _)) any = true;
            if (!any) return;
            BumpLighting();
            RaiseClaimsChanged();
        }

        /// <summary>Drops every claim whose device is no longer assigned to
        /// its slot, or whose slot <paramref name="slotLive"/> answers false
        /// for. Unassigning a device runs no pass on its old slot's
        /// dispatcher, and a slot whose virtual controller went away has none,
        /// so without this a claim would hold its device on a color nobody
        /// sets. The link pass calls it with the current assignments and
        /// <see cref="SlotLive"/>.</summary>
        public static void PruneClaims(ISet<(Guid Device, int Slot)> assigned, Func<int, bool> slotLive = null)
        {
            if (assigned == null) return;
            bool any = false;
            foreach (var key in s_claims.Keys)
            {
                bool keep = assigned.Contains(key) && (slotLive == null || slotLive(key.Slot));
                if (!keep && s_claims.TryRemove(key, out _)) any = true;
            }
            if (!any) return;
            BumpLighting();
            RaiseClaimsChanged();
        }

        /// <summary>Every claim, for the host's stop: the backends hand their
        /// devices back on their own, and nothing claimed then carries into
        /// the next engine start.</summary>
        public static void ClearClaims()
        {
            if (s_claims.IsEmpty) return;
            s_claims.Clear();
            BumpLighting();
            RaiseClaimsChanged();
        }

        public static bool IsLit(Guid device)
        {
            foreach (var key in s_claims.Keys)
                if (key.Device == device) return true;
            return false;
        }

        private static void BumpLighting()
        {
            Interlocked.Increment(ref s_lightingVersion);
            try { LightingChanged?.Invoke(); } catch { }
        }

        private static void RaiseClaimsChanged()
        {
            try { ClaimsChanged?.Invoke(); } catch { }
        }

        /// <summary>One claimant of a path, as the ruling sees it.</summary>
        internal readonly record struct Claimant(Guid Device, bool CatchAll, int Player, int Slot, int Rgb);

        /// <summary>The ruling between the claimants of one path, pure so the
        /// rule is testable: a device that claims the path on its own beats a
        /// vendor row, then the smallest displayed player number, then the
        /// lowest slot, then the device id so the answer never flips between
        /// two equal claims.</summary>
        internal static bool Rules(in Claimant a, in Claimant b)
        {
            if (a.CatchAll != b.CatchAll) return !a.CatchAll;
            int pa = a.Player > 0 ? a.Player : int.MaxValue;
            int pb = b.Player > 0 ? b.Player : int.MaxValue;
            if (pa != pb) return pa < pb;
            if (a.Slot != b.Slot) return a.Slot < b.Slot;
            return a.Device.CompareTo(b.Device) < 0;
        }

        /// <summary>The color a lighting path shows, or false when nothing
        /// assigned claims it, which is the backend's cue to hand the device
        /// back to its own software. <paramref name="ruler"/> is the winning
        /// claim, for a tab naming the controller that rules a shared path.</summary>
        public static bool TryResolveColor(OutputPath path, out int rgb, out Claimant ruler)
        {
            rgb = 0;
            ruler = default;
            var links = Links;
            if (!links.ByPath.ContainsKey(path)) return false;
            bool found = false;
            foreach (var pair in s_claims)
            {
                var row = links.For(pair.Key.Device);
                if (row == null || Array.IndexOf(row.Lighting, path) < 0) continue;
                Claimant c;
                lock (pair.Value)
                    c = new Claimant(pair.Key.Device, row.CatchAll, pair.Value.Player, pair.Key.Slot, pair.Value.Rgb);
                if (!found || Rules(c, ruler))
                {
                    ruler = c;
                    found = true;
                }
            }
            if (!found) return false;
            rgb = ruler.Rgb;
            return true;
        }

        public static bool TryResolveColor(OutputPath path, out int rgb)
            => TryResolveColor(path, out rgb, out _);

        // ─────────────────────────────────────────────
        //  Set Chroma Color (#468), per slot
        // ─────────────────────────────────────────────

        /// <summary>How long a macro color outlives its latest assertion. The
        /// action asserts on every poll, a millisecond apart, so this only
        /// ever measures the gap after the action ends.</summary>
        internal const int MacroAssertWindowMs = 120;

        /// <summary>Per slot: the tick of the latest assertion shifted left
        /// 24 bits, then the color, so one atomic read gives a matching
        /// pair. Zero before any.</summary>
        private static readonly long[] s_chromaMacro = new long[InputManager.MaxPads];

        /// <summary>Raised when a slot's Set Chroma Color starts or changes
        /// color. Any thread. Subscribers must not block.</summary>
        internal static event Action ChromaMacroChanged;

        /// <summary>A Set Chroma Color action is current on this slot (#468).
        /// It paints the Razer devices assigned to the slot, whether or not
        /// their Lighting tabs take control, and lets go within
        /// <see cref="MacroAssertWindowMs"/> of the action ending. Called
        /// from the poll thread on every frame the action is current.</summary>
        public static void AssertChromaMacro(int slot, byte r, byte g, byte b)
            => AssertChromaMacro(slot, r, g, b, Environment.TickCount64);

        /// <summary><see cref="AssertChromaMacro(int, byte, byte, byte)"/> at
        /// a given tick, so a test hands the assertion and the read one
        /// clock.</summary>
        internal static void AssertChromaMacro(int slot, byte r, byte g, byte b, long now)
        {
            if ((uint)slot >= (uint)s_chromaMacro.Length) return;
            int rgb = (r << 16) | (g << 8) | b;
            long next = (Math.Max(now, 1) << 24) | (long)rgb;
            long previous = Interlocked.Exchange(ref s_chromaMacro[slot], next);
            bool fresh = previous == 0 || (int)(previous & 0xFFFFFF) != rgb
                || now - (previous >> 24) > MacroAssertWindowMs;
            // Only the Chroma worker paints a macro color, so only it wakes.
            if (fresh)
            {
                try { ChromaMacroChanged?.Invoke(); } catch { }
            }
        }

        /// <summary>The slot's live Set Chroma Color, if one was asserted
        /// within the window.</summary>
        public static bool TryGetChromaMacro(int slot, long now, out int rgb)
        {
            rgb = 0;
            if ((uint)slot >= (uint)s_chromaMacro.Length) return false;
            long packed = Volatile.Read(ref s_chromaMacro[slot]);
            if (packed == 0 || now - (packed >> 24) > MacroAssertWindowMs) return false;
            rgb = (int)(packed & 0xFFFFFF);
            return true;
        }

        /// <summary>The first tick after <paramref name="now"/> at which a
        /// live Set Chroma Color lapses, or long.MaxValue while none is live.
        /// The Chroma worker ends its wait there, so the pass that lets go of
        /// the macro runs at the lapse instead of at its next poll.</summary>
        internal static long NextChromaMacroLapse(long now)
        {
            long next = long.MaxValue;
            for (int slot = 0; slot < s_chromaMacro.Length; slot++)
            {
                long packed = Volatile.Read(ref s_chromaMacro[slot]);
                if (packed == 0) continue;
                // TryGetChromaMacro holds the color through the stamp plus
                // the window, so the first tick past that is the lapse.
                long lapse = (packed >> 24) + MacroAssertWindowMs + 1;
                if (lapse > now && lapse < next) next = lapse;
            }
            return next;
        }

        /// <summary>The devices assigned to a slot, for a Set Chroma Color,
        /// which paints the Razer devices among them.</summary>
        public static ISet<Guid> DevicesOnSlot(int slot)
        {
            var set = new HashSet<Guid>();
            var settings = SettingsManager.UserSettings;
            if (settings == null) return set;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.MapTo == slot && us.InstanceGuid != Guid.Empty) set.Add(us.InstanceGuid);
            }
            return set;
        }

        /// <summary>A slot's displayed player number, or its index plus one
        /// for a slot in no order list, the fallback every caller of
        /// GetGlobalSlotNumber takes.</summary>
        public static int DisplayedSlotNumber(int slot)
        {
            int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(slot);
            return number > 0 ? number : slot + 1;
        }

        /// <summary>True while any slot's Set Chroma Color is live.</summary>
        public static bool AnyChromaMacro(long now)
        {
            for (int slot = 0; slot < s_chromaMacro.Length; slot++)
                if (TryGetChromaMacro(slot, now, out _)) return true;
            return false;
        }

        // ─────────────────────────────────────────────
        //  Battery
        // ─────────────────────────────────────────────

        /// <summary>A row's charge for the Battery lighting mode: the first
        /// HID++ unit it links to that reports one, or -1. Razer and
        /// SteelSeries software report no charge here, so those rows read
        /// as unknown and the mode holds its full-charge color.</summary>
        public static int BatteryOf(Guid device)
        {
            var links = Links.For(device);
            if (links == null) return -1;
            var units = Hidpp.Units;
            foreach (var key in links.HidppUnits)
            {
                foreach (var unit in units)
                    if (unit.Key == key && unit.BatteryPercent >= 0) return unit.BatteryPercent;
            }
            return -1;
        }

        /// <summary>The number a device ranks by on a shared haptic path
        /// that only a linked PC drives: past every controller on this PC, so
        /// a local assignment still comes first.</summary>
        internal const int PeerDrivenPlayer = 1000;

        /// <summary>A row's rank on a shared haptic path: its smallest
        /// displayed player number on this PC, else <see cref="PeerDrivenPlayer"/>
        /// for a device a linked PC drives (#494), whose relayed level a
        /// forwarded Rival plays here, else 0, which never rules by rank.</summary>
        public static int HapticRulingPlayer(Guid device)
        {
            int player = SettingsManager.SlotOrders.GetIdentityPlayerNumber(device);
            if (player > 0) return player;
            var ud = SettingsManager.FindDeviceByInstanceGuid(device);
            return ud != null && RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath) ? PeerDrivenPlayer : 0;
        }

        /// <summary>The device that rules a shared path among the given
        /// assigned rows, for the haptic backends, which have no claim to read
        /// the player number from. A row Identify buzzes rules the path for
        /// its train, ahead of every rank, the one whose claim lapses last
        /// when two do (<see cref="BeginIdentify(Guid, int)"/>). Otherwise
        /// <paramref name="playerOf"/> answers a row's rank
        /// (<see cref="HapticRulingPlayer"/>), 0 for an unassigned row, which
        /// never rules by rank.</summary>
        public static bool TryResolveHapticRuler(OutputPath path, Func<Guid, int> playerOf, out Guid ruler)
        {
            ruler = Guid.Empty;
            var links = Links;
            if (!links.ByPath.TryGetValue(path, out var devices)) return false;
            long now = Environment.TickCount64;
            long lapse = long.MinValue;
            foreach (var device in devices)
            {
                var row = links.For(device);
                if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                if (!s_identify.TryGetValue(device, out long until) || now >= until || until <= lapse) continue;
                lapse = until;
                ruler = device;
            }
            if (ruler != Guid.Empty) return true;
            Claimant best = default;
            bool found = false;
            foreach (var device in devices)
            {
                var row = links.For(device);
                if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                int player = playerOf(device);
                if (player <= 0) continue;
                var c = new Claimant(device, row.CatchAll, player, 0, 0);
                if (!found || Rules(c, best))
                {
                    best = c;
                    found = true;
                }
            }
            if (!found) return false;
            ruler = best.Device;
            return true;
        }

        /// <summary>Test seam: forgets every link, level, claim and state.</summary>
        internal static void ResetForTest()
        {
            PublishLinks(LinkTable.Empty);
            s_levels.Clear();
            s_claims.Clear();
            s_identify.Clear();
            Volatile.Write(ref s_running, 0);
            Volatile.Write(ref s_slotHasController, null);
            Array.Clear(s_chromaMacro);
            Interlocked.Increment(ref s_lightingVersion);
            Volatile.Write(ref s_presence, PeripheralPresence.None);
            Volatile.Write(ref s_hidpp, HidppSnapshot.Empty);
            Volatile.Write(ref s_ledSdkPaintable, null);
            for (int i = 0; i < s_states.Length; i++) Volatile.Write(ref s_states[i], 0);
        }
    }
}
