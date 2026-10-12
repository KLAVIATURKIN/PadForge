using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Runs the peripheral outputs while the engine runs (#494): the HID++,
    /// GameSense, Razer Chroma and LED SDK workers, the Razer Sensa worker
    /// while its row is assigned or Identify buzzes it, and the linker pass
    /// that ties device rows to paths.
    ///
    /// <para>Nothing here opens a vendor SDK for a device nobody assigned,
    /// except for the length of an Identify train on that device's row
    /// (<see cref="PeripheralOutputs.BeginIdentify(Guid, int)"/>): the linker
    /// reads presence from the registry, files and process names, and each
    /// backend reaches its vendor only while a path it serves is claimed.
    /// Assignment drives every output, the way it drives a gamepad's.</para>
    /// </summary>
    internal sealed class PeripheralOutputHost : IDisposable
    {
        internal const int LinkMs = 500;
        internal const int PresenceMs = 5000;

        /// <summary>How long a Sensa worker that ended on its own waits
        /// before the next one, the worker's own provider retry.</summary>
        internal const int SensaRestartMs = 30000;

        /// <summary>How long Stop waits for a link pass to end.</summary>
        internal const int StopJoinMs = 3000;

        private readonly HidppBackend _hidpp;
        private readonly GameSenseBackend _gameSense;
        private readonly ChromaBackend _chroma;
        private readonly LedSdkBackend _ledSdk;
        private readonly Func<SensaHapticsService> _sensaFactory;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<PeripheralPresence> _readPresence;
        private readonly int _stopJoinMs;
        private readonly int _linkMs;
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>Held by a pass while it publishes or starts the Sensa
        /// worker, and by Stop while it marks the pass's run stopped. A pass
        /// that Stop's join gave up on then either finished its publish
        /// before Stop's teardown, which overwrites it, or sees the stop and
        /// publishes nothing.</summary>
        private readonly object _gate = new();
        private SensaHapticsService _sensa;
        private Action<SensaServiceState> _sensaReport;
        private long _sensaRestartAt;
        private Thread _thread;
        private WorkerRun _run;
        private int _disposed;
        private int _passes;

        /// <summary>The slots <see cref="PeripheralOutputs.SlotLive"/>
        /// answered true for at the last pass, one bit each, or -1 before the
        /// first. Link thread only.</summary>
        private long _liveSlots = -1;

        /// <summary>Raised from the host thread when a row's recorded
        /// outputs changed, so the owner saves the settings and refreshes the
        /// device list. The tabs refresh on <see cref="PeripheralOutputs.LinksChanged"/>
        /// and <see cref="PeripheralOutputs.StatusChanged"/>, raised first.</summary>
        public event Action CapabilitiesChanged;

        public PeripheralOutputHost()
            : this(new HidppBackend(), new GameSenseBackend(), () => new SensaHapticsService(),
                new ChromaBackend(), new LedSdkBackend()) { }

        /// <summary>Whether a slot still holds a virtual controller, set by
        /// the owner and published while the host runs
        /// (<see cref="PeripheralOutputs.SlotHasController"/>), where
        /// <see cref="PeripheralOutputs.SlotLive"/> reads it. A reorder moves
        /// the controllers first and rebuilds the slots' effects dispatchers
        /// after, one slot at a time, so a slot can be without a dispatcher
        /// for a second or two while its controller is in place. Null treats
        /// every slot as without one.</summary>
        public Func<int, bool> SlotHasController { get; set; }

        /// <summary>Test seam: the backends, the Sensa worker's factory, the
        /// container lookup, the presence read, Stop's join and the wait
        /// between timed link passes, so a bench drives the host against
        /// scripted parts.</summary>
        internal PeripheralOutputHost(HidppBackend hidpp, GameSenseBackend gameSense, Func<SensaHapticsService> sensa,
            ChromaBackend chroma = null, LedSdkBackend ledSdk = null, Func<string, Guid> containerOf = null,
            Func<PeripheralPresence> readPresence = null, int stopJoinMs = StopJoinMs, int linkMs = LinkMs)
        {
            _hidpp = hidpp;
            _gameSense = gameSense;
            _sensaFactory = sensa;
            _chroma = chroma;
            _ledSdk = ledSdk;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _readPresence = readPresence ?? PeripheralPresence.Read;
            _stopJoinMs = stopJoinMs;
            _linkMs = linkMs;
        }

        /// <summary>Test seam: runs on the link thread right before a pass
        /// takes the gate, with "publish" before its publish and "sensa"
        /// before it starts the Sensa worker, so a bench can hold a pass there
        /// while Stop runs.</summary>
        internal static Action<string> BeforeGate;

        /// <summary>Link passes that have ended, so a bench can tell the loop
        /// is at rest between two.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        public void Start()
        {
            if (_thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _liveSlots = -1;
            PeripheralOutputs.SlotHasController = SlotHasController;
            PeripheralOutputs.SetRunning(true);
            // Identify on the Razer Sensa row starts its worker at once, not
            // at the next pass.
            PeripheralOutputs.IdentifyChanged += Nudge;
            _hidpp.SnapshotChanged += Nudge;
            _hidpp.Start();
            _gameSense.Start();
            _chroma?.Start();
            _ledSdk?.Start();
            PeripheralOutputs.Presence = _readPresence();
            _thread = new Thread(() => Loop(run)) { IsBackground = true, Name = "PeripheralLink" };
            _thread.Start();
        }

        /// <summary>Brings the next link pass forward: devices came or went.</summary>
        public void Nudge()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        public void Stop()
        {
            if (_thread != null)
            {
                // Under the gate: a pass already publishing finishes first,
                // and one that has not reached its publish sees the stop and
                // publishes nothing, so nothing a pass does lands after the
                // teardown below.
                lock (_gate) _run.Stop = true;
                Nudge();
                try { _thread.Join(_stopJoinMs); } catch { }
                _thread = null;
                _run = null;
            }
            PeripheralOutputs.IdentifyChanged -= Nudge;
            StopSensa();
            _ledSdk?.Stop();
            _chroma?.Stop();
            _gameSense.Stop();
            _hidpp.SnapshotChanged -= Nudge;
            _hidpp.Stop();
            PeripheralOutputs.SetRunning(false);
            PeripheralOutputs.SlotHasController = null;
            PeripheralOutputs.ClearClaims();
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            PeripheralOutputs.Hidpp = HidppSnapshot.Empty;
        }

        private void Loop(WorkerRun run)
        {
            long nextPresence = Environment.TickCount64 + PresenceMs;
            while (!run.Stop)
            {
                try
                {
                    long now = Environment.TickCount64;
                    if (now >= nextPresence)
                    {
                        PeripheralOutputs.Presence = _readPresence();
                        nextPresence = now + PresenceMs;
                    }
                    Link(run);
                    SensaLifecycle(run);
                }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL link pass fault: " + ex.GetType().Name);
                }
                Interlocked.Increment(ref _passes);
                // A pass held up past Stop's join, a Sensa worker slow to
                // stop, can come back after Dispose released the handle.
                try { _wake.WaitOne(_linkMs); }
                catch (ObjectDisposedException) { return; }
            }
        }

        /// <summary>The interface path whose container a row's vendor channel
        /// shares. An analog keyboard row's own path is synthetic, so it gives
        /// the HID collection it reads.</summary>
        internal static string ContainerPath(Engine.Data.UserDevice ud)
            => ud.Device is AnalogKeyboardDevice analog ? analog.HidPath : ud.DevicePath;

        /// <summary>A row path's container ID, from the cache or from
        /// <paramref name="lookup"/>, a configuration manager round trip that
        /// answers Guid.Empty when it fails. A failed lookup is not kept, so
        /// the next pass asks again: kept, it would leave the row unmatched to
        /// its vendor channel for the rest of the engine run, a device that
        /// comes back on the same path included.</summary>
        internal static Guid ResolveContainer(Dictionary<string, Guid> cache, string path, Func<string, Guid> lookup)
        {
            if (cache.TryGetValue(path, out var container)) return container;
            try { container = lookup(path); }
            catch { container = Guid.Empty; }
            if (container != Guid.Empty) cache[path] = container;
            return container;
        }

        /// <summary>Forgets the container of every path no row used this
        /// pass, so a device that leaves and comes back is looked up again,
        /// the rule HidppBackend.Scan keeps for its own paths.</summary>
        internal static void ForgetContainers(Dictionary<string, Guid> cache, ISet<string> used)
        {
            List<string> gone = null;
            foreach (var path in cache.Keys)
                if (!used.Contains(path)) (gone ??= new List<string>()).Add(path);
            if (gone == null) return;
            foreach (var path in gone) cache.Remove(path);
        }

        /// <summary>One pass: snapshot the rows, link them, publish a change,
        /// and keep each row's recorded outputs current.</summary>
        private void Link(WorkerRun run)
        {
            var presence = PeripheralOutputs.Presence;
            var hidpp = _hidpp.Snapshot;
            bool charge = ChargesChanged(PeripheralOutputs.Hidpp, hidpp);

            var rows = new List<LinkRow>();
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            var users = new List<Engine.Data.UserDevice>();
            var paths = new List<string>();
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null || !ud.IsOnline) continue;
                    if (ud.Device is PeripheralOutputRow vendorRow)
                    {
                        rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId, Guid.Empty, vendorRow.Kind));
                        users.Add(ud);
                        paths.Add(null);
                        continue;
                    }
                    if (!PeripheralLinker.IsLinkable(ud.CapType)) continue;
                    // A device forwarded from another PC plays its rumble
                    // there, so only that PC links it to its vendor channel.
                    if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath)) continue;
                    if (ud.VendorId != PeripheralLinker.LogitechVid && ud.VendorId != PeripheralLinker.RazerVid
                        && ud.VendorId != PeripheralLinker.SteelSeriesVid) continue;
                    rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId,
                        Guid.Empty, null));
                    users.Add(ud);
                    paths.Add(ContainerPath(ud));
                }
            }
            // Container IDs outside the device lock: a configuration manager
            // call is a kernel round trip, once per path that answers it.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].VendorRow != null) continue;
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                used.Add(path);
                rows[i] = rows[i] with { Container = ResolveContainer(_containers, path, _containerOf) };
            }
            ForgetContainers(_containers, used);

            var table = PeripheralLinker.Build(rows, hidpp, presence);

            // A device unassigned from a slot keeps no claim from it, and a
            // slot that lights nothing keeps none. Read after the device lock
            // is released, never nested inside it.
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                var assigned = new HashSet<(Guid Device, int Slot)>();
                lock (settings.SyncRoot)
                {
                    foreach (var us in settings.Items)
                        if (us != null && us.MapTo >= 0 && us.InstanceGuid != Guid.Empty)
                            assigned.Add((us.InstanceGuid, us.MapTo));
                    // Pruned under the lock the snapshot was read under, so an
                    // assignment lands either before the snapshot, which keeps
                    // its claim, or after the prune, before which no
                    // dispatcher can have claimed for it. A slot keeps its
                    // claims while a dispatcher lights it or a controller still
                    // sits there (PeripheralOutputs.SlotLive), so a reorder's
                    // rebuild never hands its devices back and takes them
                    // again. The prune takes no other lock, and its events only
                    // set a wake or queue a tab refresh.
                    PeripheralOutputs.PruneClaims(assigned, PeripheralOutputs.SlotLive);
                }
            }

            // A slot that starts or stops lighting anything changes what the
            // tabs say even where no claim was pruned: a rumble row, or a
            // device whose Lighting tab leaves it alone.
            long live = 0;
            for (int slot = 0; slot < InputManager.MaxPads && slot < 64; slot++)
                if (PeripheralOutputs.SlotLive(slot)) live |= 1L << slot;
            bool liveChanged = _liveSlots != -1 && live != _liveSlots;
            _liveSlots = live;

            BeforeGate?.Invoke("publish");
            lock (_gate)
            {
                // A pass Stop's join gave up on publishes nothing: the stop's
                // own teardown has the last word.
                if (run.Stop) return;
                PeripheralOutputs.Hidpp = hidpp;

                // The records first, so a tab that refreshes on the new table
                // reads the record that goes with it.
                bool changed = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    var ud = users[i];
                    var links = table.For(rows[i].Device);
                    // A Logitech row whose container is still being probed, or
                    // before the first scan, keeps what it had: a mouse asleep
                    // at launch keeps its tabs.
                    bool stillLooking = rows[i].VendorId == PeripheralLinker.LogitechVid && rows[i].VendorRow == null
                        && (!hidpp.Scanned || hidpp.PendingContainers.Contains(rows[i].Container));
                    int next = PeripheralLinker.Capabilities(ud.PeripheralOutputs, links, stillLooking);
                    if (next == ud.PeripheralOutputs) continue;
                    ud.PeripheralOutputs = next;
                    changed = true;
                }

                bool relinked = !table.SameAs(PeripheralOutputs.Links);
                if (relinked)
                {
                    PeripheralOutputs.PublishLinks(table);
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL links: {table.ByDevice.Count} row(s), {table.ByPath.Count} path(s)");
                }
                else if (changed || liveChanged)
                {
                    // A record that moved under an unchanged table, or a slot
                    // that came or went, still changes what the tabs show.
                    PeripheralOutputs.NotifyStatusChanged();
                }
                if (changed)
                {
                    try { CapabilitiesChanged?.Invoke(); } catch { }
                }
                // Battery mode is drawn on demand, and a device that gained or
                // lost a path is drawn on the next pass, so both ask for one
                // once the snapshot and the table that maps a unit to its row
                // are published. A new path reaches slots whose last pass lit
                // nothing too.
                if (relinked) UserEffectsDispatcher.RequestPeripheralRefreshAll(evenUnlit: true);
                else if (charge) UserEffectsDispatcher.RequestPeripheralRefreshAll();
            }
        }

        /// <summary>Whether any unit's charge differs between two snapshots, a
        /// unit found or gone included.</summary>
        internal static bool ChargesChanged(HidppSnapshot before, HidppSnapshot after)
        {
            if (ReferenceEquals(before, after)) return false;
            var old = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var unit in before?.Units ?? Array.Empty<HidppUnit>())
                old[unit.Key] = unit.BatteryPercent;
            int seen = 0;
            foreach (var unit in after?.Units ?? Array.Empty<HidppUnit>())
            {
                if (!old.TryGetValue(unit.Key, out int percent) || percent != unit.BatteryPercent) return true;
                seen++;
            }
            return seen != old.Count;
        }

        /// <summary>The Sensa worker runs while the Sensa row is assigned to
        /// a virtual controller or Identify buzzes it, and stops when it is
        /// neither, which stops the engine's events and releases the
        /// provider. Identify nudges the pass, so the worker starts inside
        /// the train. A worker that ends on its own while the row stays
        /// assigned, because the engine did not load or a straggling
        /// predecessor kept the slot (<see cref="SensaHapticsService"/>'s
        /// bounded join), is started again after <see cref="SensaRestartMs"/>,
        /// so a missing HAR.dll does not spin.</summary>
        private void SensaLifecycle(WorkerRun run)
        {
            var sensaGuid = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            bool linked = PeripheralOutputs.HasHaptics(sensaGuid);
            bool assigned = linked && SettingsManager.SlotOrders.GetIdentityPlayerNumber(sensaGuid) > 0;
            bool identifying = linked && PeripheralOutputs.IsIdentifying(sensaGuid);
            if (!assigned && !identifying)
            {
                _sensaRestartAt = 0;
                if (_sensa == null) return;
                StopSensa();
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker stopped (row unassigned)");
                return;
            }

            long now = Environment.TickCount64;
            if (_sensa != null && !_sensa.WorkerAlive)
            {
                StopSensa();
                _sensaRestartAt = now + SensaRestartMs;
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker ended on its own, starting again later");
            }
            if (_sensa != null || now < _sensaRestartAt) return;

            BeforeGate?.Invoke("sensa");
            lock (_gate)
            {
                // A pass Stop's join gave up on starts nothing: no Stop is
                // left to end the worker.
                if (run.Stop) return;
                var sensa = _sensaFactory();
                // Only the current worker reports: a stopped one's last word
                // would otherwise land after its successor's.
                Action<SensaServiceState> report = state =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _sensa), sensa)) return;
                    PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics,
                        state == SensaServiceState.Active ? BackendState.Connected : BackendState.Waiting);
                };
                sensa.StateChanged += report;
                _sensaReport = report;
                Volatile.Write(ref _sensa, sensa);
                sensa.Start();
            }
            PadForge.Engine.SdlDiagLog.WriteLine(assigned
                ? "PERIPHERAL Sensa worker started (row assigned)"
                : "PERIPHERAL Sensa worker started (Identify)");
        }

        /// <summary>For an abnormal exit, after the engine's quiesce zeroed
        /// every level: waits up to <paramref name="waitMs"/> in all for GG
        /// to be told (<see cref="GameSenseBackend.WaitForSilence"/>) and for
        /// the Sensa worker to render the zero
        /// (<see cref="SensaHapticsService.WaitForSilence"/>). A HID++ pulse
        /// ends on its own.</summary>
        public void WaitForSilence(int waitMs)
        {
            long until = Environment.TickCount64 + waitMs;
            _gameSense.WaitForSilence(waitMs);
            Volatile.Read(ref _sensa)?.WaitForSilence((int)Math.Max(0, until - Environment.TickCount64));
        }

        /// <summary>For an abnormal exit: hands the HID++ units lit directly
        /// back to their own effect (<see cref="HidppBackend.ReleaseClaimedNow"/>).
        /// Chroma and GameSense end their sessions on their own timeouts, and
        /// the LED SDK goes with the process.</summary>
        public void ReleaseLightingNow() => _hidpp.ReleaseClaimedNow();

        private void StopSensa()
        {
            var sensa = Interlocked.Exchange(ref _sensa, null);
            if (sensa == null) return;
            if (_sensaReport != null) sensa.StateChanged -= _sensaReport;
            _sensaReport = null;
            try { sensa.Dispose(); } catch { }
            PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Idle);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _ledSdk?.Dispose();
            _chroma?.Dispose();
            _gameSense.Dispose();
            _hidpp.Dispose();
            _wake.Dispose();
        }
    }
}
