using PadForge.Engine.Common.Logitech;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The Logitech screens drawn through LGS's LCD library (issue #519):
    /// the monochrome GamePanel screens, the G19's color screen and LGS's
    /// LCD Emulator beside them. LGS takes one picture for every screen of
    /// a type, so the host hands it each kind's leading screen's picture.
    ///
    /// <para>The applet is registered only for the kinds that have something
    /// to show, so a kind set to Off stays with LGS's own applets, and a
    /// change in that set registers it again. <c>LogiLcdInit</c> failing
    /// means LGS is not running, and it is tried again every
    /// <see cref="InitRetryMs"/>. <c>LogiLcdUpdate</c> runs on every pass,
    /// as logitech-lcd's examples call it every frame, and the keys are read
    /// after it: LGS reports them to the applet in front alone. LGS names no
    /// keyboard a key came from, so the host gives each one to a screen
    /// (<see cref="ButtonFlags"/>).</para>
    ///
    /// <para>The library is loaded once for the process and never freed
    /// (<see cref="LogitechLcdLibrary"/>). Every call is made on the host's
    /// worker, so the library sees one thread.</para>
    /// </summary>
    internal sealed class LgsLcdScreens : System.IDisposable
    {
        public const int InitRetryMs = 10000;
        private const int LoadRetryMs = 30000;
        private const int ConnectedPollMs = 1000;
        /// <summary>How long the screens may have nothing to show before the
        /// applet leaves LGS, so a mode switched in passing does not take it
        /// down and back up.</summary>
        private const int IdleShutdownMs = 2000;

        private static readonly object s_loadLock = new();
        private static LogitechLcdLibrary s_library;
        private static long s_nextLoad;
        private static string s_reason = "missing";

        private int _types;
        private long _nextInit, _nextConnected, _idleSince = -1;
        private long _monoWritten = -1, _colorWritten = -1;

        /// <summary>LGS took the applet: it draws every Logitech screen it
        /// sees, and the direct writers stand down for those.</summary>
        public bool Active { get; private set; }
        public bool MonoConnected { get; private set; }
        public bool ColorConnected { get; private set; }

        /// <summary>LGS's button flags held now
        /// (<see cref="KeyboardScreenFormats.LgsButtons"/>), zero while the
        /// applet is down.</summary>
        public int ButtonFlags { get; private set; }

        /// <summary>Why the library is not loaded: "gate", "missing", "load"
        /// or "exports", or empty once it is.</summary>
        public static string LoadReason
        {
            get { lock (s_loadLock) return s_library != null ? string.Empty : s_reason; }
        }

        private static LogitechLcdLibrary Library(long now)
        {
            lock (s_loadLock)
            {
                if (s_library != null || now < s_nextLoad) return s_library;
                s_nextLoad = now + LoadRetryMs;
                s_library = LogitechLcdLibrary.TryLoad(out s_reason);
                return s_library;
            }
        }

        /// <summary>One worker pass with the monochrome and color pictures,
        /// each null when PadForge shows nothing there.</summary>
        public void Step(byte[] mono, long monoVersion, byte[] color, long colorVersion, long now)
        {
            int wanted = (mono != null ? LogitechLcdLibrary.TypeMono : 0) | (color != null ? LogitechLcdLibrary.TypeColor : 0);
            var lib = Library(now);
            if (lib == null)
            {
                Active = MonoConnected = ColorConnected = false;
                ButtonFlags = 0;
                return;
            }

            if (wanted == 0)
            {
                if (!Active) return;
                if (_idleSince < 0) _idleSince = now;
                if (now - _idleSince < IdleShutdownMs)
                {
                    lib.Update();
                    return;
                }
                Shutdown(lib);
                return;
            }
            _idleSince = -1;

            if (!Active || _types != wanted)
            {
                if (now < _nextInit) return;
                if (Active) Shutdown(lib);
                if (!lib.Init("PadForge", wanted))
                {
                    _nextInit = now + InitRetryMs;
                    return;
                }
                Active = true;
                _types = wanted;
                _monoWritten = _colorWritten = -1;
                _nextConnected = 0;
            }

            if (mono != null && _monoWritten != monoVersion
                && lib.MonoSetBackground(KeyboardScreenFormats.ToLgsMono(mono)))
                _monoWritten = monoVersion;
            if (color != null && _colorWritten != colorVersion && lib.ColorSetBackground(color))
                _colorWritten = colorVersion;
            lib.Update();

            int flags = 0;
            foreach (var (flag, _, _) in KeyboardScreenFormats.LgsButtons)
                if (lib.IsButtonPressed(flag)) flags |= flag;
            ButtonFlags = flags;

            if (now >= _nextConnected)
            {
                _nextConnected = now + ConnectedPollMs;
                MonoConnected = lib.IsConnected(LogitechLcdLibrary.TypeMono);
                ColorConnected = lib.IsConnected(LogitechLcdLibrary.TypeColor);
            }
        }

        private void Shutdown(LogitechLcdLibrary lib)
        {
            lib.Shutdown();
            Active = MonoConnected = ColorConnected = false;
            ButtonFlags = 0;
            _types = 0;
            _idleSince = -1;
        }

        public void Dispose()
        {
            LogitechLcdLibrary lib;
            lock (s_loadLock) lib = s_library;
            if (Active && lib != null) Shutdown(lib);
        }
    }
}
