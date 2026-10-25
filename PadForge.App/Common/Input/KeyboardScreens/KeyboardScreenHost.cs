using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>What draws a kind of screen, or why nothing does, for the
    /// status line (issue #519).</summary>
    internal enum KeyboardScreenRoute
    {
        NotFound = 0,
        /// <summary>Through LGS's LCD library.</summary>
        Lgs,
        /// <summary>Directly, on the models the detail names.</summary>
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
    /// screen service composes each kind's picture on the UI thread and
    /// hands it over with <see cref="Publish"/>, and the worker keeps the
    /// latest one per kind and writes it where it changed.
    ///
    /// <para>Which engine draws follows one rule. When LGS's library loads
    /// and <c>LogiLcdInit</c> succeeds, LGS draws every Logitech screen it
    /// sees and the direct writers stand down: two programs writing one
    /// screen would fight, and LGS owns its screens while it runs. Otherwise
    /// each present Logitech screen gets its direct writer, and the init is
    /// tried again on the slow lane. A G19 already on PadForge's WinUSB
    /// binding is drawn directly either way, since LGS cannot reach it. Arx
    /// and the S.T.R.I.K.E. 7 run beside whichever of these is active.</para>
    /// </summary>
    internal sealed class KeyboardScreenHost : IDisposable
    {
        public const int LoopMs = 20;

        private readonly object _lock = new();
        private readonly byte[][] _frames = new byte[KeyboardScreenFormats.Kinds.Length][];
        private readonly long[] _versions = new long[KeyboardScreenFormats.Kinds.Length];
        private readonly KeyboardScreenStatus[] _status = new KeyboardScreenStatus[KeyboardScreenFormats.Kinds.Length];
        private readonly AutoResetEvent _wake = new(false);
        private readonly Action<string> _log;
        private readonly LgsLcdScreens _lgs = new();
        private readonly G15HidScreens _hid = new();
        private readonly G19UsbScreen _g19;
        private readonly ArxScreen _arx = new();
        private readonly Strike7Screen _strike7;
        private Thread _thread;
        private volatile bool _stop;
        private string _strike7Path, _strike7Sha256;
        private bool _lgsWasActive;

        public KeyboardScreenHost(byte[] strike7Icon, int iconWidth, int iconHeight, Action<string> log)
        {
            _log = log ?? (_ => { });
            _g19 = new G19UsbScreen(_log);
            _strike7 = new Strike7Screen(strike7Icon, iconWidth, iconHeight);
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge.KeyboardScreens" };
            _thread.Start();
        }

        /// <summary>A kind's picture as top-down B, G, R, A at its size, or
        /// null when PadForge shows nothing there. A picture equal to the
        /// last is no change.</summary>
        public void Publish(KeyboardScreenKind kind, byte[] bgra)
        {
            if (_stop) return;
            int k = (int)kind;
            lock (_lock)
            {
                var last = _frames[k];
                if (ReferenceEquals(last, bgra)
                    || (last != null && bgra != null && last.AsSpan().SequenceEqual(bgra))) return;
                _frames[k] = bgra;
                _versions[k]++;
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

        public KeyboardScreenStatus StatusOf(KeyboardScreenKind kind)
        {
            lock (_lock) return _status[(int)kind];
        }

        /// <summary>A finger's drag on the V.E.N.O.M. screen, a step at a
        /// time.</summary>
        public bool TryTakeDrag(out int dx, out int dy) => _strike7.TryTakeDrag(out dx, out dy);

        private void Run()
        {
            while (!_stop)
            {
                byte[] mono, color, arx, strike7;
                long monoV, colorV, arxV, strike7V;
                string strike7Path, strike7Sha256;
                lock (_lock)
                {
                    mono = _frames[(int)KeyboardScreenKind.Monochrome];
                    monoV = _versions[(int)KeyboardScreenKind.Monochrome];
                    color = _frames[(int)KeyboardScreenKind.Color];
                    colorV = _versions[(int)KeyboardScreenKind.Color];
                    arx = _frames[(int)KeyboardScreenKind.Arx];
                    arxV = _versions[(int)KeyboardScreenKind.Arx];
                    strike7 = _frames[(int)KeyboardScreenKind.Strike7];
                    strike7V = _versions[(int)KeyboardScreenKind.Strike7];
                    strike7Path = _strike7Path;
                    strike7Sha256 = _strike7Sha256;
                }
                long now = Environment.TickCount64;

                // Each engine on its own, so one that throws leaves the
                // others drawing.
                try { _lgs.Step(mono, monoV, color, colorV, now); }
                catch (Exception ex) { _log("Keyboard screens: LGS pass failed: " + ex.Message); }
                bool lgs = _lgs.Active;
                if (lgs && !_lgsWasActive) _hid.ForgetWrites();
                _lgsWasActive = lgs;

                try { _hid.Step(mono, monoV, MonoDrawsDirectly(lgs, mono != null, _lgs.MonoConnected), appletKeyOnly: lgs, now); }
                catch (Exception ex) { _log("Keyboard screens: monochrome pass failed: " + ex.Message); }
                try { _g19.Step(color, colorV, G19MayBind(lgs), now); }
                catch (Exception ex) { _log("Keyboard screens: G19 pass failed: " + ex.Message); }
                try { _arx.Step(arx, arxV, now); }
                catch (Exception ex) { _log("Keyboard screens: Arx pass failed: " + ex.Message); }
                try { _strike7.Step(strike7, strike7V, strike7Path, strike7Sha256, now); }
                catch (Exception ex) { _log("Keyboard screens: S.T.R.I.K.E. 7 pass failed: " + ex.Message); }

                var monoStatus = MonoStatus(lgs);
                var colorStatus = ColorStatus(lgs);
                var arxStatus = ArxStatus();
                var strike7Status = Strike7Status(strike7Path);
                lock (_lock)
                {
                    _status[(int)KeyboardScreenKind.Monochrome] = monoStatus;
                    _status[(int)KeyboardScreenKind.Color] = colorStatus;
                    _status[(int)KeyboardScreenKind.Arx] = arxStatus;
                    _status[(int)KeyboardScreenKind.Strike7] = strike7Status;
                }

                _wake.WaitOne(LoopMs);
            }
        }

        /// <summary>Whether the monochrome screens' direct writer draws. It
        /// stands down while LGS took the applet, unless PadForge has a
        /// picture and LGS sees no monochrome screen to show it on. A screen
        /// LGS does not see is one no other program draws.</summary>
        internal static bool MonoDrawsDirectly(bool lgsActive, bool hasFrame, bool lgsSeesMono)
            => !lgsActive || (hasFrame && !lgsSeesMono);

        /// <summary>Whether the G19's screen interface may be bound to
        /// WinUSB: never while LGS took the applet, since LGS's own driver
        /// serves that interface then.</summary>
        internal static bool G19MayBind(bool lgsActive) => !lgsActive;

        private KeyboardScreenStatus MonoStatus(bool lgs)
        {
            if (lgs && _lgs.MonoConnected) return new(KeyboardScreenRoute.Lgs);
            string names = _hid.ScreenNames;
            return names.Length > 0 ? new(KeyboardScreenRoute.Direct, names) : new(KeyboardScreenRoute.NotFound);
        }

        private KeyboardScreenStatus ColorStatus(bool lgs)
        {
            switch (_g19.Current)
            {
                case G19UsbScreen.State.Open:
                    return new(KeyboardScreenRoute.Direct, "G19");
                case G19UsbScreen.State.OtherDriver when !(lgs && _lgs.ColorConnected):
                    return new(KeyboardScreenRoute.G19OtherDriver, _g19.OtherDriver);
                case G19UsbScreen.State.Binding when !lgs:
                    return new(KeyboardScreenRoute.G19Preparing);
            }
            return lgs && _lgs.ColorConnected ? new(KeyboardScreenRoute.Lgs) : new(KeyboardScreenRoute.NotFound);
        }

        private KeyboardScreenStatus ArxStatus()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return new(KeyboardScreenRoute.NotOnArm64);
            if (ArxScreen.LoadReason.Length > 0) return new(KeyboardScreenRoute.ArxNoLibrary);
            if (!_arx.Active) return new(KeyboardScreenRoute.ArxNotRunning);
            return _arx.OnPhone ? new(KeyboardScreenRoute.ArxOnPhone) : new(KeyboardScreenRoute.ArxWaiting);
        }

        private KeyboardScreenStatus Strike7Status(string path)
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
            KeyboardScreenKeys.ClearAll();
            _wake.Dispose();
        }
    }
}
