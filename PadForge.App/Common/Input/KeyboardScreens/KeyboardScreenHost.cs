using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>What draws a screen, or why nothing does, for its line
    /// (issue #519).</summary>
    internal enum KeyboardScreenRoute
    {
        NotFound = 0,
        /// <summary>Through LGS's LCD library.</summary>
        Lgs,
        /// <summary>Directly, on the model the detail names.</summary>
        Direct,
        /// <summary>A G19 is plugged in and its screen interface is being
        /// bound to WinUSB.</summary>
        G19Preparing,
        /// <summary>The G19's screen interface is on another driver, named
        /// in the detail, which LGS installs.</summary>
        G19OtherDriver,
        /// <summary>LGS's Arx Control library is not installed.</summary>
        ArxNoLibrary,
        /// <summary>The library is there and LGS did not take the applet,
        /// which means LGS is not running.</summary>
        ArxNotRunning,
        ArxWaiting,
        ArxOnPhone,
        Strike7NoLibrary,
        /// <summary>The downloaded library no longer matches the SDK's.</summary>
        Strike7Changed,
        Strike7NeedsRuntime,
        Strike7LoadFailed,
        /// <summary>The library refused the applet: the keyboard's own
        /// software is not running.</summary>
        Strike7NotRunning,
        Strike7Waiting,
        Strike7Showing,
        /// <summary>The vendor library is x64 and this build is not.</summary>
        NotOnArm64,
    }

    internal readonly record struct KeyboardScreenStatus(KeyboardScreenRoute Route, string Detail = "");

    /// <summary>
    /// The keyboard screen engines on one worker thread (issue #519). The
    /// screen service composes each present screen's picture on the UI
    /// thread and hands it over with <see cref="Publish"/>, and the worker
    /// keeps the latest one per screen and writes it where it changed. The
    /// worker finds the screens, publishes them for their rows
    /// (<see cref="KeyboardScreensRuntime.Screens"/>) and keeps each one's
    /// status.
    ///
    /// <para>Which engine draws follows one rule. When LGS's library loads
    /// and <c>LogiLcdInit</c> succeeds, LGS draws every Logitech screen it
    /// sees and the direct writers stand down: two programs writing one
    /// screen would fight, and LGS owns its screens while it runs. LGS takes
    /// one picture for all of its monochrome screens and one for all of its
    /// color screens (LogitechLCDLib.h names no device), so it gets the
    /// picture of each kind's leading screen, the first in id order
    /// (<see cref="Leader"/>), and the other screens' dialogs say so.
    /// Otherwise each present Logitech screen gets its own picture through
    /// its direct writer, and the init is tried again on the slow lane. A
    /// G19 already on PadForge's WinUSB binding is drawn directly either
    /// way, since LGS cannot reach it. Arx and the S.T.R.I.K.E. 7 run beside
    /// whichever of these is active, each one screen, since each vendor
    /// library draws one applet wherever it reaches.</para>
    ///
    /// <para>LGS names no keyboard for a key either. A monochrome key it
    /// hands over goes to each keyboard whose own HID reader saw that key go
    /// down within <see cref="LgsKeyWindowMs"/>, and to the leading screen
    /// when no reader did. A color key goes to the leading color
    /// screen.</para>
    /// </summary>
    internal sealed class KeyboardScreenHost : IDisposable
    {
        public const int LoopMs = 20;
        internal const long LgsKeyWindowMs = 2 * KeyboardScreenKeys.PulseMs;
        private const string LgsKeysSource = "lgs";

        private readonly object _lock = new();
        private readonly Dictionary<string, (byte[] Frame, long Version)> _frames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyboardScreenStatus> _status = new(StringComparer.Ordinal);
        private readonly AutoResetEvent _wake = new(false);
        private readonly Action<string> _log;
        private readonly LgsLcdScreens _lgs = new();
        private readonly G15HidScreens _hid = new();
        private readonly G19UsbScreens _g19;
        private readonly ArxScreen _arx = new();
        private readonly Strike7Screen _strike7;
        private Thread _thread;
        private volatile bool _stop;
        private string _strike7Path, _strike7Sha256;
        private bool _lgsWasActive;
        private KeyboardScreenInfo[] _screens = Array.Empty<KeyboardScreenInfo>();
        private volatile bool _lgsActive;
        private KeyboardScreenStatus _arxStatus, _strike7Status;
        private readonly HashSet<string> _lgsKeyed = new(StringComparer.Ordinal);

        public KeyboardScreenHost(byte[] strike7Icon, int iconWidth, int iconHeight, Action<string> log)
        {
            _log = log ?? (_ => { });
            _g19 = new G19UsbScreens(_log);
            _strike7 = new Strike7Screen(strike7Icon, iconWidth, iconHeight);
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge.KeyboardScreens" };
            _thread.Start();
        }

        /// <summary>The screens present now, in id order.</summary>
        public KeyboardScreenInfo[] Screens
        {
            get { lock (_lock) return _screens; }
        }

        /// <summary>A screen's picture as top-down B, G, R, A at its kind's
        /// size, or null when PadForge shows nothing there. A picture equal
        /// to the last is no change.</summary>
        public void Publish(string screen, byte[] bgra)
        {
            if (_stop || string.IsNullOrEmpty(screen)) return;
            lock (_lock)
            {
                _frames.TryGetValue(screen, out var last);
                if (ReferenceEquals(last.Frame, bgra)
                    || (last.Frame != null && bgra != null && last.Frame.AsSpan().SequenceEqual(bgra))) return;
                _frames[screen] = (bgra, last.Version + 1);
            }
            _wake.Set();
        }

        /// <summary>The S.T.R.I.K.E. 7 library to load, and the SHA-256 it
        /// must have when it is PadForge's own download (empty for a copy
        /// the user picked).</summary>
        public void SetStrike7Library(string path, string sha256)
        {
            lock (_lock)
            {
                _strike7Path = path;
                _strike7Sha256 = sha256;
            }
        }

        /// <summary>A present screen's status, or NotFound.</summary>
        public KeyboardScreenStatus StatusOf(string screen)
        {
            lock (_lock) return _status.TryGetValue(screen ?? string.Empty, out var s) ? s : new(KeyboardScreenRoute.NotFound);
        }

        /// <summary>Arx Control's and the S.T.R.I.K.E. 7's status while
        /// neither has a screen yet: the library missing, LGS or the
        /// keyboard's software not running.</summary>
        public KeyboardScreenStatus ArxStatus
        {
            get { lock (_lock) return _arxStatus; }
        }

        public KeyboardScreenStatus Strike7Status
        {
            get { lock (_lock) return _strike7Status; }
        }

        /// <summary>Whether LGS draws the Logitech screens now.</summary>
        public bool LgsActive => _lgsActive;

        /// <summary>The screen whose picture LGS shows on every screen of the
        /// kind, when LGS draws and the kind has more than one screen, else
        /// null.</summary>
        public KeyboardScreenInfo SharedLeader(KeyboardScreenKind kind)
        {
            if (!_lgsActive || (kind != KeyboardScreenKind.Monochrome && kind != KeyboardScreenKind.Color)) return null;
            var screens = Screens;
            return screens.Count(s => s.Kind == kind) > 1 ? Leader(screens, kind) : null;
        }

        /// <summary>The first screen of a kind in id order, which LGS
        /// shows, or null.</summary>
        internal static KeyboardScreenInfo Leader(IReadOnlyList<KeyboardScreenInfo> screens, KeyboardScreenKind kind)
        {
            KeyboardScreenInfo leader = null;
            foreach (var s in screens)
                if (s.Kind == kind && (leader == null || string.CompareOrdinal(s.Id, leader.Id) < 0)) leader = s;
            return leader;
        }

        /// <summary>A finger's drag on the V.E.N.O.M. screen, a step at a
        /// time.</summary>
        public bool TryTakeDrag(out int dx, out int dy) => _strike7.TryTakeDrag(out dx, out dy);

        private (byte[] Frame, long Version) FrameOf(string screen)
        {
            lock (_lock) return _frames.TryGetValue(screen, out var f) ? f : (null, 0);
        }

        private void Run()
        {
            while (!_stop)
            {
                KeyboardScreenInfo[] screens;
                string strike7Path, strike7Sha256;
                lock (_lock)
                {
                    screens = _screens;
                    strike7Path = _strike7Path;
                    strike7Sha256 = _strike7Sha256;
                }
                long now = Environment.TickCount64;
                var monoLeader = Leader(screens, KeyboardScreenKind.Monochrome);
                var colorLeader = Leader(screens, KeyboardScreenKind.Color);
                var mono = monoLeader != null ? FrameOf(monoLeader.Id) : (null, 0);
                var color = colorLeader != null ? FrameOf(colorLeader.Id) : (null, 0);

                // Each engine on its own, so one that throws leaves the
                // others drawing.
                try { _lgs.Step(mono.Frame, mono.Version, color.Frame, color.Version, now); }
                catch (Exception ex) { _log("Keyboard screens: LGS pass failed: " + ex.Message); }
                bool lgs = _lgs.Active;
                if (lgs && !_lgsWasActive) _hid.ForgetWrites();
                _lgsWasActive = lgs;
                _lgsActive = lgs;

                try { _hid.Step(FrameOf, MonoDrawsDirectly(lgs, _lgs.MonoConnected), appletKeyOnly: lgs, now); }
                catch (Exception ex) { _log("Keyboard screens: monochrome pass failed: " + ex.Message); }
                try { _g19.Step(FrameOf, G19MayBind(lgs), now); }
                catch (Exception ex) { _log("Keyboard screens: G19 pass failed: " + ex.Message); }
                var arx = FrameOf(KeyboardScreenInfo.ArxId);
                try { _arx.Step(arx.Frame, arx.Version, now); }
                catch (Exception ex) { _log("Keyboard screens: Arx pass failed: " + ex.Message); }
                var strike7 = FrameOf(KeyboardScreenInfo.Strike7Id);
                try { _strike7.Step(strike7.Frame, strike7.Version, strike7Path, strike7Sha256, now); }
                catch (Exception ex) { _log("Keyboard screens: S.T.R.I.K.E. 7 pass failed: " + ex.Message); }

                DealLgsKeys(lgs ? _lgs.ButtonFlags : 0, monoLeader, colorLeader, now);

                var present = PresentScreens(strike7Path);
                var status = new Dictionary<string, KeyboardScreenStatus>(StringComparer.Ordinal);
                foreach (var s in present) status[s.Id] = StatusFor(s, lgs, strike7Path);
                var arxStatus = ArxStatusNow();
                var strike7Status = Strike7StatusNow(strike7Path);
                bool listChanged;
                lock (_lock)
                {
                    listChanged = !present.SequenceEqual(_screens);
                    if (listChanged) _screens = present;
                    _status.Clear();
                    foreach (var (id, s) in status) _status[id] = s;
                    _arxStatus = arxStatus;
                    _strike7Status = strike7Status;
                    // A picture of a screen that left is dropped with it.
                    foreach (string gone in _frames.Keys.Where(id => !status.ContainsKey(id)).ToList()) _frames.Remove(gone);
                }
                // A new array only when the list changed, which is what the
                // device sweep looks for.
                if (listChanged) KeyboardScreensRuntime.Screens = present;

                _wake.WaitOne(LoopMs);
            }
        }

        /// <summary>The screens the engines see now, in id order: each
        /// monochrome keyboard and G19, Arx Control while LGS's Arx library
        /// is installed, and the S.T.R.I.K.E. 7 while its library loads. The
        /// two vendor screens need no picture to be listed, so a row set to
        /// Off stays to be set again.</summary>
        private KeyboardScreenInfo[] PresentScreens(string strike7Path)
        {
            var list = new List<KeyboardScreenInfo>(_hid.Screens);
            list.AddRange(_g19.Screens);
            bool x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            if (x64 && ArxScreen.LoadReason.Length == 0) list.Add(KeyboardScreenInfo.Arx());
            if (x64 && !string.IsNullOrWhiteSpace(strike7Path) && _strike7.LoadReason.Length == 0) list.Add(KeyboardScreenInfo.Strike7());
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list.ToArray();
        }

        /// <summary>LGS's keys to the screens they came from (see the class
        /// notes), each screen's set reported under its own name.</summary>
        private void DealLgsKeys(int flags, KeyboardScreenInfo monoLeader, KeyboardScreenInfo colorLeader, long now)
        {
            var bits = new Dictionary<string, int>(StringComparer.Ordinal);
            if (flags != 0)
            {
                var screens = Screens;
                foreach (var (flag, kind, button) in KeyboardScreenFormats.LgsButtons)
                {
                    if ((flags & flag) == 0) continue;
                    if (kind == KeyboardScreenKind.Color)
                    {
                        if (colorLeader != null) bits[colorLeader.Id] = bits.GetValueOrDefault(colorLeader.Id) | 1 << button;
                        continue;
                    }
                    bool any = false;
                    foreach (var s in screens)
                    {
                        if (s.Kind != KeyboardScreenKind.Monochrome || !_hid.KeyRecentlyDown(s.Id, button, now, LgsKeyWindowMs)) continue;
                        bits[s.Id] = bits.GetValueOrDefault(s.Id) | 1 << button;
                        any = true;
                    }
                    if (!any && monoLeader != null) bits[monoLeader.Id] = bits.GetValueOrDefault(monoLeader.Id) | 1 << button;
                }
            }
            foreach (string id in _lgsKeyed.Where(id => !bits.ContainsKey(id)).ToList())
            {
                KeyboardScreenKeys.Clear(id, LgsKeysSource);
                _lgsKeyed.Remove(id);
            }
            foreach (var (id, b) in bits)
            {
                KeyboardScreenKeys.Report(id, LgsKeysSource, b);
                _lgsKeyed.Add(id);
            }
        }

        /// <summary>Whether the monochrome screens' direct writer draws. It
        /// stands down while LGS took the applet and sees a monochrome
        /// screen. A screen LGS does not see is one no other program
        /// draws.</summary>
        internal static bool MonoDrawsDirectly(bool lgsActive, bool lgsSeesMono)
            => !lgsActive || !lgsSeesMono;

        /// <summary>Whether a G19's screen interface may be bound to WinUSB:
        /// never while LGS took the applet, since LGS's own driver serves
        /// that interface then.</summary>
        internal static bool G19MayBind(bool lgsActive) => !lgsActive;

        private KeyboardScreenStatus StatusFor(KeyboardScreenInfo screen, bool lgs, string strike7Path)
        {
            switch (screen.Kind)
            {
                case KeyboardScreenKind.Monochrome:
                    return lgs && _lgs.MonoConnected
                        ? new(KeyboardScreenRoute.Lgs)
                        : new(KeyboardScreenRoute.Direct, screen.Model);
                case KeyboardScreenKind.Color:
                {
                    var g19 = _g19.Find(screen.Id);
                    switch (g19?.Current)
                    {
                        case G19UsbScreen.State.Open:
                            return new(KeyboardScreenRoute.Direct, "G19");
                        case G19UsbScreen.State.OtherDriver when !(lgs && _lgs.ColorConnected):
                            return new(KeyboardScreenRoute.G19OtherDriver, g19.OtherDriver);
                        case G19UsbScreen.State.Binding when !lgs:
                            return new(KeyboardScreenRoute.G19Preparing);
                    }
                    return lgs && _lgs.ColorConnected ? new(KeyboardScreenRoute.Lgs) : new(KeyboardScreenRoute.NotFound);
                }
                case KeyboardScreenKind.Arx:
                    return ArxStatusNow();
                default:
                    return Strike7StatusNow(strike7Path);
            }
        }

        private KeyboardScreenStatus ArxStatusNow()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return new(KeyboardScreenRoute.NotOnArm64);
            if (ArxScreen.LoadReason.Length > 0) return new(KeyboardScreenRoute.ArxNoLibrary);
            if (!_arx.Active) return new(KeyboardScreenRoute.ArxNotRunning);
            return _arx.OnPhone ? new(KeyboardScreenRoute.ArxOnPhone) : new(KeyboardScreenRoute.ArxWaiting);
        }

        private KeyboardScreenStatus Strike7StatusNow(string path)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return new(KeyboardScreenRoute.NotOnArm64);
            if (string.IsNullOrWhiteSpace(path)) return new(KeyboardScreenRoute.Strike7NoLibrary);
            switch (_strike7.LoadReason)
            {
                case "": break;
                case "hash": return new(KeyboardScreenRoute.Strike7Changed);
                case "runtime": return new(KeyboardScreenRoute.Strike7NeedsRuntime);
                case "load":
                case "exports": return new(KeyboardScreenRoute.Strike7LoadFailed);
                case "platform": return new(KeyboardScreenRoute.NotOnArm64);
                default: return new(KeyboardScreenRoute.Strike7NoLibrary);
            }
            if (!_strike7.Open) return new(KeyboardScreenRoute.Strike7NotRunning);
            return _strike7.Shown ? new(KeyboardScreenRoute.Strike7Showing) : new(KeyboardScreenRoute.Strike7Waiting);
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            var thread = _thread;
            _thread = null;
            // The worker finishes its pass first: a bind in flight runs on
            // its own task and ends on its own.
            if (thread != null && !thread.Join(5000)) return;
            try { _lgs.Dispose(); } catch (Exception) { }
            try { _hid.Dispose(); } catch (Exception) { }
            try { _g19.Dispose(); } catch (Exception) { }
            try { _arx.Dispose(); } catch (Exception) { }
            try { _strike7.Dispose(); } catch (Exception) { }
            KeyboardScreensRuntime.Screens = Array.Empty<KeyboardScreenInfo>();
            KeyboardScreenKeys.ClearAll();
            _wake.Dispose();
        }
    }
}
