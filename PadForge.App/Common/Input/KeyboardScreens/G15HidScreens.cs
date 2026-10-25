using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The monochrome screens drawn without LGS (issue #519): the G15, G15
    /// v2, G510, G510s, G13, Z-10 and GamePanel, over HID.
    ///
    /// <para>A frame is one 992-byte output report, written with WriteFile
    /// on the collection whose OutputReportByteLength is 992, through
    /// <see cref="RawHidOutput"/>, the wheel writers' path. A collection of
    /// any other length is never written. The keys come from the same
    /// device's input reports, read on <see cref="VendorHidReader"/>'s
    /// threads and decoded by <see cref="KeyboardScreenFormats.DecodeMonoKeys"/>.
    /// While LGS draws, the writer stands down and the readers pass on the
    /// round applet key alone, which LGS never hands an applet. LGS hands
    /// the other four over itself while PadForge's applet is in front.</para>
    ///
    /// <para>Runs on the host's worker. The scan opens a collection for its
    /// caps with no access asked, VendorHidRuntime's probe, and keeps each
    /// path's verdict until the path goes.</para>
    /// </summary>
    internal sealed class G15HidScreens : IDisposable
    {
        private const int ScanMs = 3000;

        private sealed class Collection
        {
            public string Path;
            public ushort ProductId;
            public bool Screen;
            public VendorHidReader Reader;
            public long Written = -1;
        }

        private readonly Dictionary<string, Collection> _collections = new(StringComparer.OrdinalIgnoreCase);
        private long _nextScan;
        private volatile bool _appletKeyOnly;
        private byte[] _report;
        private long _reportVersion = -1;
        private bool _wroteFrame;

        /// <summary>The models whose screen collection is open, for the
        /// status line, or empty.</summary>
        public string ScreenNames { get; private set; } = string.Empty;

        /// <summary>LGS took the screens over: what PadForge wrote is gone
        /// under LGS's own drawing, so Off later clears nothing of
        /// LGS's.</summary>
        public void ForgetWrites()
        {
            _wroteFrame = false;
            _reportVersion = -1;
            foreach (var c in _collections.Values) c.Written = -1;
        }

        /// <summary>One worker pass. <paramref name="frame"/> is the
        /// monochrome picture as B, G, R, A, or null when PadForge shows
        /// nothing, and <paramref name="draw"/> false while LGS draws.</summary>
        public void Step(byte[] frame, long version, bool draw, bool appletKeyOnly, long now)
        {
            _appletKeyOnly = appletKeyOnly;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanMs;
                Scan();
            }
            if (!draw) return;

            if (frame == null)
            {
                // Off without LGS: nothing else draws the screen, so the
                // last picture PadForge wrote is cleared once.
                if (!_wroteFrame) return;
                _report = KeyboardScreenFormats.ToG15Report(new byte[KeyboardScreenFormats.MonoWidth * KeyboardScreenFormats.MonoHeight]);
                _reportVersion = -2;
                _wroteFrame = false;
            }
            else if (_reportVersion != version)
            {
                _report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(frame));
                _reportVersion = version;
                _wroteFrame = true;
            }
            if (_report == null) return;

            foreach (var c in _collections.Values)
            {
                if (!c.Screen || c.Written == _reportVersion) continue;
                if (RawHidOutput.Write(c.Path, _report)) c.Written = _reportVersion;
                else
                {
                    // Gone or refused: the next scan finds it again or drops
                    // it, and its cached handle and length go now.
                    RawHidOutput.ResetDevice(c.Path);
                    _nextScan = 0;
                }
            }
        }

        private void Scan()
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, pid) in MonochromePaths())
            {
                present.Add(path);
                if (_collections.TryGetValue(path, out var known))
                {
                    // A reader whose device went quiet for good is opened
                    // again on this scan.
                    if (known.Reader != null && !known.Reader.IsAttached)
                    {
                        known.Reader.Dispose();
                        known.Reader = null;
                        KeyboardScreenKeys.Clear(Source(path));
                        OpenReader(known);
                    }
                    continue;
                }
                var probed = Probe(path, pid, out bool unreadable);
                if (probed == null)
                {
                    // A collection that could not be read is asked again on
                    // the next scan. One that is no screen and carries no key
                    // is kept as a verdict.
                    if (!unreadable) _collections[path] = new Collection { Path = path, ProductId = pid };
                    continue;
                }
                _collections[path] = probed;
            }

            foreach (string gone in _collections.Keys.Where(p => !present.Contains(p)).ToList())
                Drop(gone);

            ScreenNames = string.Join(", ", _collections.Values.Where(c => c.Screen)
                .Select(c => KeyboardScreenDevices.NameOf(c.ProductId)).Distinct());
        }

        private void Drop(string path)
        {
            if (!_collections.Remove(path, out var c)) return;
            c.Reader?.Dispose();
            KeyboardScreenKeys.Clear(Source(path));
            RawHidOutput.ResetDevice(path);
        }

        private static string Source(string path) => "hid:" + path;

        /// <summary>The caps of one collection, read through an open that asks
        /// no access. Null for a collection that is neither the screen nor a
        /// key collection, and null with <paramref name="unreadable"/> set
        /// when it could not be opened or read.</summary>
        private Collection Probe(string path, ushort pid, out bool unreadable)
        {
            unreadable = true;
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS) return null;
                unreadable = false;
                bool screen = caps.OutputReportByteLength == KeyboardScreenFormats.G15ReportLength;
                // Windows keeps keyboards and mice to itself, so their
                // collections are not asked for reads.
                bool keys = caps.InputReportByteLength > 0
                            && !(caps.UsagePage == 0x01 && (caps.Usage == 0x06 || caps.Usage == 0x02));
                if (!screen && !keys) return null;
                var c = new Collection { Path = path, ProductId = pid, Screen = screen };
                if (keys) OpenReader(c, caps.InputReportByteLength);
                return c;
            }
            catch (Exception)
            {
                unreadable = true;
                return null;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                handle.Dispose();
            }
        }

        private readonly Dictionary<string, ushort> _inputLengths = new(StringComparer.OrdinalIgnoreCase);

        private void OpenReader(Collection c, ushort inputLength = 0)
        {
            if (inputLength > 0) _inputLengths[c.Path] = inputLength;
            else if (!_inputLengths.TryGetValue(c.Path, out inputLength)) return;
            var reader = new VendorHidReader(new VendorHidCollection
            {
                Path = c.Path,
                VendorId = KeyboardScreenDevices.LogitechVid,
                ProductId = c.ProductId,
                InputReportLength = inputLength,
            });
            ushort pid = c.ProductId;
            string source = Source(c.Path);
            reader.ReportReceived += (_, report, length) =>
            {
                int bits = KeyboardScreenFormats.DecodeMonoKeys(pid, report.AsSpan(0, length));
                if (bits < 0) return;
                if (_appletKeyOnly) bits &= 1 << KeyboardScreenFormats.MonoAppletKey;
                KeyboardScreenKeys.Report(source, bits);
            };
            if (reader.Open()) c.Reader = reader;
            else reader.Dispose();
        }

        /// <summary>Every present HID interface of a monochrome screen
        /// model, by the VID and PID its path carries.</summary>
        private static List<(string Path, ushort Pid)> MonochromePaths()
        {
            var list = new List<(string, ushort)>();
            SonyHeadsetHid.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SonyHeadsetHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                SonyHeadsetHid.DIGCF_PRESENT | SonyHeadsetHid.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                var iface = new SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA>()
                };
                for (uint index = 0; SonyHeadsetHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface); index++)
                {
                    string path = VendorHidRuntime.GetInterfacePath(set, ref iface);
                    if (TryReadIds(path, out ushort vid, out ushort pid) && KeyboardScreenDevices.IsMonochrome(vid, pid))
                        list.Add((path, pid));
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }

        /// <summary>The VID and PID an interface path carries as
        /// <c>vid_046d&amp;pid_c22d</c>.</summary>
        internal static bool TryReadIds(string path, out ushort vid, out ushort pid)
        {
            vid = pid = 0;
            if (string.IsNullOrEmpty(path)) return false;
            int v = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
            int p = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            return v >= 0 && p >= 0 && v + 8 <= path.Length && p + 8 <= path.Length
                   && ushort.TryParse(path.AsSpan(v + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                   && ushort.TryParse(path.AsSpan(p + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }

        public void Dispose()
        {
            foreach (string path in _collections.Keys.ToList()) Drop(path);
            ScreenNames = string.Empty;
        }
    }
}
