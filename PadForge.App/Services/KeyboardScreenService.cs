using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>What a kind of keyboard screen shows (issue #519). The VMU's
    /// list in the VMU's order (<see cref="DreamcastScreenMode"/>), with a
    /// stopwatch and the light bar's color appended. The settings file stores
    /// the names, so none is renamed, and the values stay as numbered.</summary>
    public enum KeyboardScreenMode
    {
        /// <summary>Nothing from PadForge. Under LGS the screen stays with
        /// LGS's applets.</summary>
        Off = 0,
        ProfileName = 1,
        ProfileNumber = 2,
        /// <summary>The active profile's screen picture, else its name.</summary>
        ProfilePicture = 3,
        /// <summary>The time of day, HH:MM.</summary>
        Clock = 4,
        /// <summary>Hours and minutes since the active profile switched in.</summary>
        PlayTime = 5,
        /// <summary>A picture chosen for the screen, panned when it is larger.</summary>
        Picture = 6,
        /// <summary>The timer the Stopwatch macro runs.</summary>
        Stopwatch = 7,
        /// <summary>The color of the lowest slot's DualShock 4 or DualSense
        /// light bar, @eVenent's light bar sync mode (discussion #507).</summary>
        Lightbar = 8,
    }

    /// <summary>What the Stopwatch macro does.</summary>
    public enum KeyboardScreenStopwatchAction
    {
        StartPause = 0,
        Reset = 1,
    }

    /// <summary>Which way the Pan Keyboard Screen Picture macro moves the
    /// screen's window over its picture.</summary>
    public enum KeyboardScreenPanDirection
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3,
    }

    /// <summary>One kind of keyboard screen's choices (issue #519), kept in
    /// the settings file by the kind.</summary>
    public class KeyboardScreenData
    {
        /// <summary>The mode a screen shows until the user picks another: the
        /// light bar's color, the mode @eVenent asked to have when nothing
        /// else is set.</summary>
        public const KeyboardScreenMode DefaultMode = KeyboardScreenMode.Lightbar;

        [XmlAttribute] public KeyboardScreenKind Kind { get; set; }

        [XmlElement] public KeyboardScreenMode Mode { get; set; } = DefaultMode;

        /// <summary>The chosen picture's file: relative under the app folder,
        /// absolute elsewhere (IconPackageManager.MakeStoredPath).</summary>
        [XmlElement] public string Picture { get; set; }

        /// <summary>The window's top-left corner on a picture larger than the
        /// screen, in the picture's pixels.</summary>
        [XmlElement] public int PanX { get; set; }
        [XmlElement] public int PanY { get; set; }

        [XmlIgnore]
        internal bool IsDefault => Mode == DefaultMode && string.IsNullOrEmpty(Picture) && PanX == 0 && PanY == 0;

        /// <summary>The loaded list with undefined kinds and entries left at
        /// every default dropped, a mode the file names that no build knows
        /// put back to the default, a negative pan put to zero, and of a
        /// kind's other entries the first one kept, BlissBoxPortData's
        /// cleanup.</summary>
        internal static List<KeyboardScreenData> Normalize(KeyboardScreenData[] screens)
        {
            var list = new List<KeyboardScreenData>();
            if (screens == null) return list;
            var seen = new HashSet<KeyboardScreenKind>();
            foreach (var screen in screens)
            {
                if (screen == null || !Enum.IsDefined(typeof(KeyboardScreenKind), screen.Kind)) continue;
                if (!Enum.IsDefined(typeof(KeyboardScreenMode), screen.Mode)) screen.Mode = DefaultMode;
                if (screen.PanX < 0) screen.PanX = 0;
                if (screen.PanY < 0) screen.PanY = 0;
                if (string.IsNullOrWhiteSpace(screen.Picture)) screen.Picture = null;
                if (screen.IsDefault || !seen.Add(screen.Kind)) continue;
                list.Add(screen);
            }
            return list;
        }
    }

    /// <summary>
    /// The keyboard screens (issue #519): a mode per kind of screen, the
    /// macros' shows, stopwatch and pan, and the engine host that writes the
    /// pictures. <see cref="DreamcastScreenService"/>'s shape: ticked on the
    /// UI thread, where WPF draws, from the UI timer while the engine runs,
    /// whether or not PadForge has focus, with macro requests queued from the
    /// poll thread. The engine's stop resets it (<see cref="Reset"/>), which
    /// closes the host and the screens' sessions.
    ///
    /// <para>A Show Keyboard Screen macro plays over whatever the mode shows
    /// and hands the screen back when it ends.</para>
    /// </summary>
    public sealed class KeyboardScreenService
    {
        public const int MaxFrames = 8;
        public const int MinFrameMs = 100;
        private const int TickMs = 250;
        private const int MaxQueuedRequests = 64;

        /// <summary>The S.T.R.I.K.E. 7 library PadForge downloads: Mad Catz's
        /// SDK, kept under the temp folder beside the voice models.</summary>
        internal static string DownloadedStrike7Library
            => Path.Combine(Path.GetTempPath(), "PadForge", "strike7", PadForge.Engine.Common.MadCatz.Strike7Library.LibraryName);

        /// <summary>The SHA-256 of API/x64/Strike7API.dll in strike7_api 1.1.</summary>
        internal const string Strike7LibrarySha256 = "86808D4FCE73E05D06CE01A1269D8FD711F45B8A31A6BE0AF648DCF8F1E9194C";

        private abstract record Request;
        private sealed record ShowRequest(KeyboardScreenKind Kind, string Frames, int FrameMs, int Repeat) : Request;
        private sealed record ModeRequest(KeyboardScreenKind Kind, KeyboardScreenMode Mode) : Request;
        private sealed record StopwatchRequest(KeyboardScreenStopwatchAction Action) : Request;
        private sealed record PanRequest(KeyboardScreenPanDirection Direction, int Step) : Request;

        private static readonly ConcurrentQueue<Request> _requests = new();

        private readonly ViewModels.SettingsViewModel _settings;
        private readonly Action _markDirty;
        private readonly Action<string> _log;
        private KeyboardScreenHost _host;
        private readonly Dictionary<KeyboardScreenKind, KeyboardScreenShow> _shows = new();
        /// <summary>Each kind's last composed picture and what it shows. A
        /// frame of the larger screens is over half a megabyte and the tick
        /// runs four times a second, so a picture is drawn again only when
        /// what it shows changes.</summary>
        private readonly Dictionary<KeyboardScreenKind, (string What, byte[] Frame)> _composed = new();
        private readonly Dictionary<string, BitmapSource> _pictures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<KeyboardScreenKind, Shown> _shown = new();
        private long _nextTick;
        private bool _playStarted;
        private string _playProfile;
        private long _playStart;

        // One stopwatch for every screen.
        private static readonly object _stopwatchLock = new();
        private static bool _stopwatchRunning;
        private static long _stopwatchElapsed, _stopwatchSince;

        /// <summary>Why a kind shows nothing in a mode that shows something,
        /// for its status line.</summary>
        internal enum Blank { None, Off, NoLightbar, NoPicture }

        private readonly record struct Shown(Blank Blank);

        public KeyboardScreenService(ViewModels.SettingsViewModel settings, Action markDirty, Action<string> log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _markDirty = markDirty ?? (() => { });
            _log = log ?? (_ => { });
        }

        // ─────────────────────────────────────────────
        //  Macro requests, from the poll thread
        // ─────────────────────────────────────────────

        private static void Enqueue(Request request)
        {
            if (!KeyboardScreensRuntime.Enabled) return;
            if (_requests.Count >= MaxQueuedRequests) _requests.TryDequeue(out _);
            _requests.Enqueue(request);
        }

        /// <summary>Show Keyboard Screen: frames over the kind's mode.</summary>
        public static void RequestShow(KeyboardScreenKind kind, string frames, int frameMs, int repeat)
            => Enqueue(new ShowRequest(kind, frames, frameMs, repeat));

        /// <summary>Keyboard Screen Mode: the kind's mode, kept as its
        /// setting.</summary>
        public static void RequestMode(KeyboardScreenKind kind, KeyboardScreenMode mode)
            => Enqueue(new ModeRequest(kind, mode));

        public static void RequestStopwatch(KeyboardScreenStopwatchAction action)
            => Enqueue(new StopwatchRequest(action));

        /// <summary>Pan Keyboard Screen Picture: moves every kind's window over
        /// its picture, stopping at the picture's edges.</summary>
        public static void RequestPan(KeyboardScreenPanDirection direction, int step)
            => Enqueue(new PanRequest(direction, step));

        internal static int PendingRequests => _requests.Count;

        internal static void DropRequests()
        {
            while (_requests.TryDequeue(out _)) { }
        }

        // ─────────────────────────────────────────────
        //  Settings
        // ─────────────────────────────────────────────

        public KeyboardScreenData Get(KeyboardScreenKind kind)
        {
            foreach (var screen in _settings.KeyboardScreens)
                if (screen.Kind == kind) return screen;
            return null;
        }

        /// <summary>Changes a kind's choices, drops an entry left at every
        /// default, and marks the settings to save.</summary>
        public void Update(KeyboardScreenKind kind, Action<KeyboardScreenData> change)
        {
            var screen = Get(kind);
            if (screen == null)
            {
                screen = new KeyboardScreenData { Kind = kind };
                _settings.KeyboardScreens.Add(screen);
            }
            change(screen);
            if (screen.IsDefault) _settings.KeyboardScreens.Remove(screen);
            _pictures.Clear();
            _composed.Clear();
            _nextTick = 0;
            _markDirty();
        }

        // ─────────────────────────────────────────────
        //  The tick
        // ─────────────────────────────────────────────

        /// <summary>One pass while the engine runs and the feature is on.
        /// Four a second, every tick while a show plays, and at once after a
        /// change.</summary>
        public void Tick()
        {
            long now = Environment.TickCount64;
            if (now < _nextTick) return;

            EnsureHost();
            while (_requests.TryDequeue(out var request)) Apply(request, now);
            TrackPlayTime(now);

            while (_host.TryTakeDrag(out int dx, out int dy))
                PanKind(KeyboardScreenKind.Strike7, dx, dy);

            _host.SetStrike7Library(Strike7LibraryPath(out string sha256), sha256);
            foreach (var kind in KeyboardScreenFormats.Kinds)
            {
                byte[] frame = ShowFrame(kind, now);
                var blank = Blank.None;
                frame ??= Compose(kind, Get(kind), now, out blank);
                _shown[kind] = new Shown(blank);
                _host.Publish(kind, frame);
            }
            _nextTick = now + (_shows.Count > 0 ? 0 : TickMs);
        }

        /// <summary>The library the S.T.R.I.K.E. 7 engine loads: the user's
        /// pick, else PadForge's download. Only the download is held to the
        /// SDK's hash.</summary>
        internal string Strike7LibraryPath(out string sha256)
        {
            string picked = _settings.Strike7LibraryPath;
            if (!string.IsNullOrWhiteSpace(picked))
            {
                sha256 = string.Empty;
                return IconPackageManager.ResolvePath(picked);
            }
            sha256 = Strike7LibrarySha256;
            return DownloadedStrike7Library;
        }

        private void EnsureHost()
        {
            if (_host != null) return;
            var (icon, width, height) = Strike7Icon();
            _host = new KeyboardScreenHost(icon, width, height, _log);
            _host.Start();
        }

        /// <summary>The engine stopped, or the feature was switched off: the
        /// host closes every screen's session, and the shows and requests go.
        /// Play time starts over with the next start. The stopwatch keeps its
        /// time.</summary>
        public void Reset()
        {
            var host = _host;
            _host = null;
            host?.Dispose();
            _shows.Clear();
            _shown.Clear();
            _composed.Clear();
            _playStarted = false;
            DropRequests();
            KeyboardScreenKeys.ClearAll();
            LightbarColors.Clear();
        }

        internal bool Running => _host != null;

        private void Apply(Request request, long now)
        {
            switch (request)
            {
                case ShowRequest show:
                {
                    var frames = DecodeFrames(show.Kind, show.Frames);
                    if (frames.Count > 0) _shows[show.Kind] = new KeyboardScreenShow(frames, show.FrameMs, show.Repeat, now);
                    break;
                }
                case ModeRequest mode when Enum.IsDefined(typeof(KeyboardScreenMode), mode.Mode):
                    Update(mode.Kind, d => d.Mode = mode.Mode);
                    break;
                case StopwatchRequest stopwatch:
                    Stopwatch(stopwatch.Action, now);
                    break;
                case PanRequest pan:
                {
                    int step = Math.Max(1, pan.Step);
                    var (dx, dy) = pan.Direction switch
                    {
                        KeyboardScreenPanDirection.Left => (-step, 0),
                        KeyboardScreenPanDirection.Right => (step, 0),
                        KeyboardScreenPanDirection.Up => (0, -step),
                        _ => (0, step),
                    };
                    foreach (var kind in KeyboardScreenFormats.Kinds) PanKind(kind, dx, dy);
                    break;
                }
            }
        }

        /// <summary>Moves a kind's window over its picture, held to the
        /// picture's edges. A kind with no picture is left as it is.</summary>
        private void PanKind(KeyboardScreenKind kind, int dx, int dy)
        {
            var data = Get(kind);
            var picture = Picture(data?.Picture);
            if (picture == null) return;
            var (maxX, maxY) = KeyboardScreenRenderer.PanLimits(picture, kind);
            int x = Math.Clamp(data.PanX + dx, 0, maxX), y = Math.Clamp(data.PanY + dy, 0, maxY);
            if (x == data.PanX && y == data.PanY) return;
            var pictures = new Dictionary<string, BitmapSource>(_pictures, StringComparer.OrdinalIgnoreCase);
            Update(kind, d =>
            {
                d.PanX = x;
                d.PanY = y;
            });
            // A pan is no change to the picture, so the decoded one stays.
            foreach (var (path, bitmap) in pictures) _pictures[path] = bitmap;
        }

        private void TrackPlayTime(long now)
        {
            string profile = SettingsManager.ActiveProfileId ?? string.Empty;
            if (_playStarted && string.Equals(profile, _playProfile, StringComparison.Ordinal)) return;
            _playStarted = true;
            _playProfile = profile;
            _playStart = now;
        }

        internal static void Stopwatch(KeyboardScreenStopwatchAction action, long now)
        {
            lock (_stopwatchLock)
            {
                if (action == KeyboardScreenStopwatchAction.Reset)
                {
                    _stopwatchRunning = false;
                    _stopwatchElapsed = 0;
                    return;
                }
                if (_stopwatchRunning) _stopwatchElapsed += now - _stopwatchSince;
                else _stopwatchSince = now;
                _stopwatchRunning = !_stopwatchRunning;
            }
        }

        internal static TimeSpan StopwatchTime(long now)
        {
            lock (_stopwatchLock)
                return TimeSpan.FromMilliseconds(Math.Max(0, _stopwatchElapsed + (_stopwatchRunning ? now - _stopwatchSince : 0)));
        }

        private byte[] ShowFrame(KeyboardScreenKind kind, long now)
        {
            if (!_shows.TryGetValue(kind, out var show)) return null;
            var frame = show.Frame(now);
            if (frame == null) _shows.Remove(kind);
            return frame;
        }

        // ─────────────────────────────────────────────
        //  What a mode shows
        // ─────────────────────────────────────────────

        /// <summary>The picture a kind's mode shows now, or null for nothing
        /// with <paramref name="blank"/> saying why.</summary>
        internal byte[] Compose(KeyboardScreenKind kind, KeyboardScreenData data, long now, out Blank blank)
        {
            blank = Blank.None;
            var mode = data?.Mode ?? KeyboardScreenData.DefaultMode;
            switch (mode)
            {
                case KeyboardScreenMode.ProfileName:
                    return Text(kind, DreamcastScreenService.ActiveProfileName());
                case KeyboardScreenMode.ProfileNumber:
                    return Text(kind, DreamcastScreenService.ActiveProfileNumber().ToString(CultureInfo.InvariantCulture));
                case KeyboardScreenMode.ProfilePicture:
                {
                    string path = ActiveProfilePicture();
                    var picture = Picture(path);
                    return picture != null
                        ? Memo(kind, "fitted|" + path, () => KeyboardScreenRenderer.RenderFitted(picture, kind))
                        : Text(kind, DreamcastScreenService.ActiveProfileName());
                }
                case KeyboardScreenMode.Clock:
                    return Text(kind, DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                case KeyboardScreenMode.PlayTime:
                    return Text(kind, DreamcastScreenService.FormatPlayTime(
                        TimeSpan.FromMilliseconds(Math.Max(0, now - (_playStarted ? _playStart : now)))));
                case KeyboardScreenMode.Picture:
                {
                    var picture = Picture(data?.Picture);
                    if (picture != null)
                        return Memo(kind, string.Format(CultureInfo.InvariantCulture, "window|{0}|{1}|{2}", data.Picture, data.PanX, data.PanY),
                            () => KeyboardScreenRenderer.RenderWindow(picture, kind, data.PanX, data.PanY));
                    blank = Blank.NoPicture;
                    return null;
                }
                case KeyboardScreenMode.Stopwatch:
                    return Text(kind, FormatStopwatch(StopwatchTime(now)));
                case KeyboardScreenMode.Lightbar:
                    if (LightbarColors.TryGetLowest(IsOnItsSlot, out var color))
                        return Memo(kind, string.Format(CultureInfo.InvariantCulture, "color|{0}|{1}|{2}", color.R, color.G, color.B),
                            () => KeyboardScreenRenderer.RenderColor(color.R, color.G, color.B, kind));
                    blank = Blank.NoLightbar;
                    return null;
                default:
                    blank = Blank.Off;
                    return null;
            }
        }

        /// <summary>Whether a pad whose light bar color is on record is still
        /// online and mapped to the slot the color came from.</summary>
        private static bool IsOnItsSlot(Guid device, int slot)
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return false;
            bool online = false;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.InstanceGuid == device) { online = ud.IsOnline; break; }
            }
            if (!online) return false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == device && us.MapTo == slot) return true;
            }
            return false;
        }

        internal static string FormatStopwatch(TimeSpan time)
            => time.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", (int)time.TotalHours, time.Minutes, time.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", time.Minutes, time.Seconds);

        private byte[] Text(KeyboardScreenKind kind, string text)
            => Memo(kind, "text|" + text, () => KeyboardScreenRenderer.RenderText(text, kind));

        /// <summary>A kind's picture of <paramref name="what"/>, drawn only
        /// when the kind last showed something else.</summary>
        private byte[] Memo(KeyboardScreenKind kind, string what, Func<byte[]> draw)
        {
            if (_composed.TryGetValue(kind, out var last) && string.Equals(last.What, what, StringComparison.Ordinal))
                return last.Frame;
            byte[] frame = draw();
            _composed[kind] = (what, frame);
            return frame;
        }

        private BitmapSource Picture(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            if (_pictures.TryGetValue(storedPath, out var cached)) return cached;
            var picture = KeyboardScreenRenderer.LoadPicture(storedPath);
            // A picture that will not load is asked for again only after the
            // next change, not four times a second.
            _pictures[storedPath] = picture;
            return picture;
        }

        private string ActiveProfilePicture()
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) return _settings.DefaultProfileKeyboardScreenPicture;
            return SettingsManager.Profiles.Find(p => p.Id == id)?.KeyboardScreenPicture;
        }

        /// <summary>What a kind's dialog previews for a mode and a picture,
        /// drawn the way the screen draws it.</summary>
        internal byte[] Preview(KeyboardScreenKind kind, KeyboardScreenData data, string profilePicture, string profileName)
        {
            long now = Environment.TickCount64;
            if (data?.Mode == KeyboardScreenMode.ProfilePicture)
            {
                var picture = KeyboardScreenRenderer.LoadPicture(profilePicture);
                return picture != null
                    ? KeyboardScreenRenderer.RenderFitted(picture, kind)
                    : KeyboardScreenRenderer.RenderText(profileName, kind);
            }
            if (data?.Mode == KeyboardScreenMode.Picture)
            {
                var picture = KeyboardScreenRenderer.LoadPicture(data.Picture);
                return picture == null ? null : KeyboardScreenRenderer.RenderWindow(picture, kind, data.PanX, data.PanY);
            }
            return Compose(kind, data, now, out _);
        }

        /// <summary>A kind's status line: what draws it, or why nothing
        /// does. Empty while the service is not running.</summary>
        internal string StatusLine(KeyboardScreenKind kind)
        {
            if (_host == null) return string.Empty;
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string what;
            var shown = _shown.TryGetValue(kind, out var sh) ? sh.Blank : Blank.None;
            if (shown == Blank.Off) what = s.KeyboardScreens_Status_Off;
            else
            {
                var status = _host.StatusOf(kind);
                what = status.Route switch
                {
                    KeyboardScreenRoute.Lgs => s.KeyboardScreens_Status_Lgs,
                    KeyboardScreenRoute.Direct => string.Format(culture, s.KeyboardScreens_Status_Direct_Format, status.Detail),
                    KeyboardScreenRoute.G19Preparing => s.KeyboardScreens_Status_G19Preparing,
                    KeyboardScreenRoute.G19OtherDriver => string.Format(culture, s.KeyboardScreens_Status_G19OtherDriver_Format, status.Detail),
                    KeyboardScreenRoute.ArxNoLibrary => s.KeyboardScreens_Status_ArxNoLibrary,
                    KeyboardScreenRoute.ArxNotRunning => s.KeyboardScreens_Status_ArxNotRunning,
                    KeyboardScreenRoute.ArxWaiting => s.KeyboardScreens_Status_ArxWaiting,
                    KeyboardScreenRoute.ArxOnPhone => s.KeyboardScreens_Status_ArxOnPhone,
                    KeyboardScreenRoute.Strike7NoLibrary => s.KeyboardScreens_Status_Strike7NoLibrary,
                    KeyboardScreenRoute.Strike7Changed => s.KeyboardScreens_Status_Strike7Changed,
                    KeyboardScreenRoute.Strike7NeedsRuntime => s.KeyboardScreens_Status_Strike7NeedsRuntime,
                    KeyboardScreenRoute.Strike7LoadFailed => s.KeyboardScreens_Status_Strike7LoadFailed,
                    KeyboardScreenRoute.Strike7NotRunning => s.KeyboardScreens_Status_Strike7NotRunning,
                    KeyboardScreenRoute.Strike7Waiting => s.KeyboardScreens_Status_Strike7Waiting,
                    KeyboardScreenRoute.Strike7Showing => s.KeyboardScreens_Status_Strike7Showing,
                    KeyboardScreenRoute.NotOnArm64 => s.Common_NotAvailableOnArm64,
                    _ => s.KeyboardScreens_Status_NotFound,
                };
                if (shown == Blank.NoLightbar) what = string.Format(culture, s.KeyboardScreens_Status_NoLightbar_Format, what);
                else if (shown == Blank.NoPicture) what = string.Format(culture, s.KeyboardScreens_Status_NoPicture_Format, what);
            }
            return string.Format(culture, s.KeyboardScreens_Status_Line_Format, KindName(kind), what);
        }

        /// <summary>The route a kind's screen takes now, for the Settings
        /// card's buttons.</summary>
        internal KeyboardScreenRoute RouteOf(KeyboardScreenKind kind)
            => _host?.StatusOf(kind).Route ?? KeyboardScreenRoute.NotFound;

        internal static string KindName(KeyboardScreenKind kind)
        {
            var s = Strings.Instance;
            return kind switch
            {
                KeyboardScreenKind.Monochrome => s.KeyboardScreens_Kind_Monochrome,
                KeyboardScreenKind.Color => s.KeyboardScreens_Kind_Color,
                KeyboardScreenKind.Arx => s.KeyboardScreens_Kind_Arx,
                _ => s.KeyboardScreens_Kind_Strike7,
            };
        }

        /// <summary>A mode's name. The VMU's modes keep the VMU's words.</summary>
        internal static string ModeName(KeyboardScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                KeyboardScreenMode.ProfileName => s.DreamcastScreen_Mode_ProfileName,
                KeyboardScreenMode.ProfileNumber => s.DreamcastScreen_Mode_ProfileNumber,
                KeyboardScreenMode.ProfilePicture => s.DreamcastScreen_Mode_ProfilePicture,
                KeyboardScreenMode.Clock => s.DreamcastScreen_Mode_Clock,
                KeyboardScreenMode.PlayTime => s.DreamcastScreen_Mode_PlayTime,
                KeyboardScreenMode.Picture => s.DreamcastScreen_Mode_Picture,
                KeyboardScreenMode.Stopwatch => s.KeyboardScreens_Mode_Stopwatch,
                KeyboardScreenMode.Lightbar => s.KeyboardScreens_Mode_Lightbar,
                _ => s.KeyboardScreens_Mode_Off,
            };
        }

        internal static string DirectionName(KeyboardScreenPanDirection direction)
        {
            var s = Strings.Instance;
            return direction switch
            {
                KeyboardScreenPanDirection.Left => s.Pad_ScrollLeft,
                KeyboardScreenPanDirection.Right => s.Pad_ScrollRight,
                KeyboardScreenPanDirection.Up => s.Pad_ScrollUp,
                _ => s.Pad_ScrollDown,
            };
        }

        // ─────────────────────────────────────────────
        //  Show frames
        // ─────────────────────────────────────────────

        /// <summary>Up to eight pictures from a Show Keyboard Screen action,
        /// each a base64 PNG, drawn at the kind's size.</summary>
        internal static List<byte[]> DecodeFrames(KeyboardScreenKind kind, string frames)
        {
            var list = new List<byte[]>();
            if (string.IsNullOrEmpty(frames)) return list;
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            foreach (var part in frames.Split(','))
            {
                if (list.Count == MaxFrames) break;
                byte[] png;
                try { png = Convert.FromBase64String(part.Trim()); }
                catch (FormatException) { continue; }
                var picture = BoundedBitmap.FromBytes(png, width, height);
                if (picture != null) list.Add(KeyboardScreenRenderer.RenderFitted(picture, kind));
            }
            return list;
        }

        /// <summary>A picture file as one Show Keyboard Screen frame: fitted to
        /// the kind's size, cut to one bit on the monochrome screen, and
        /// encoded as PNG in base64. Null when the file holds no picture WPF
        /// decodes.</summary>
        internal static string ImportFrame(string path, KeyboardScreenKind kind)
        {
            var picture = BoundedBitmap.FromBytes(MenuIconResolver.ReadCapped(path),
                KeyboardScreenRenderer.MaxPictureSide, KeyboardScreenRenderer.MaxPictureSide);
            if (picture == null) return null;
            return EncodeFrame(KeyboardScreenRenderer.RenderFitted(picture, kind), kind);
        }

        internal static string EncodeFrame(byte[] bgra, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }

        public static string JoinFrames(IEnumerable<string> frames)
        {
            var parts = new List<string>();
            foreach (var frame in frames)
            {
                if (parts.Count == MaxFrames) break;
                if (!string.IsNullOrEmpty(frame)) parts.Add(frame);
            }
            return string.Join(",", parts);
        }

        /// <summary>The applet's icon on the V.E.N.O.M. screen: PadForge's
        /// name on its ember, at the 100 by 70 of Mad Catz's example
        /// icon.</summary>
        private static (byte[] Bgra, int Width, int Height) Strike7Icon()
        {
            const int width = 100, height = 70;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // App.xaml's EmberBrush.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C)), null, new System.Windows.Rect(0, 0, width, height));
                var text = new FormattedText("PadForge", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold,
                        System.Windows.FontStretches.Normal), 18, Brushes.White, 1.0);
                dc.DrawText(text, new System.Windows.Point((width - text.Width) / 2, (height - text.Height) / 2));
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            var bgra = new byte[width * height * 4];
            converted.CopyPixels(bgra, width * 4, 0);
            return (bgra, width, height);
        }
    }

    /// <summary>A Show Keyboard Screen action playing on one kind of screen
    /// (#519): each frame for its frame time, the frames repeated the given
    /// number of times.</summary>
    internal sealed class KeyboardScreenShow
    {
        private readonly List<byte[]> _frames;
        private readonly int _frameMs;
        private readonly int _total;
        private readonly long _start;

        public KeyboardScreenShow(List<byte[]> frames, int frameMs, int repeat, long now)
        {
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
            if (frames.Count == 0) throw new ArgumentException("A show needs a picture.", nameof(frames));
            _frameMs = Math.Max(KeyboardScreenService.MinFrameMs, frameMs);
            _total = frames.Count * Math.Max(1, repeat);
            _start = now;
        }

        /// <summary>The frame to show now, or null once every frame has had
        /// its time.</summary>
        public byte[] Frame(long now)
        {
            long index = Math.Max(0, now - _start) / _frameMs;
            return index >= _total ? null : _frames[(int)(index % _frames.Count)];
        }
    }
}
