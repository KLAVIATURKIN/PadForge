using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// One G19's color screen drawn without LGS (issue #519), over WinUSB on
    /// interface 0 of 046D:C229: frames to bulk OUT endpoint 0x02, the
    /// display keys from interrupt IN endpoint 0x81 (gnome15 pylibg19
    /// g19.py, G19USB G19Constants.cs). <see cref="G19UsbScreens"/> keeps
    /// one for each G19 plugged in, by the instance ID of its interface 0.
    ///
    /// <para>Interface 0 is bound to WinUSB through
    /// <see cref="VendorUsbDriverInstaller"/> and its rules: only from no
    /// driver or HidUsb, never from another vendor's driver, one INF naming
    /// the VID, PID and MI the way libwdi writes them. Interface 1, the
    /// G-keys, stays with Windows. The bind runs only while LGS does not draw
    /// the screens, and <see cref="Restore"/> hands the node back when the
    /// feature is switched off.</para>
    ///
    /// <para>The open and the pipe reads follow Ds3DirectService's USB path:
    /// an overlapped handle, WinUsb_Initialize, the pipes found by their
    /// type and direction, a transfer timeout on each, and synchronous calls
    /// on that handle. Frames are written on the host's worker and the keys
    /// read on a thread of their own.</para>
    /// </summary>
    internal sealed class G19UsbScreen : IDisposable
    {
        internal const ushort Vid = KeyboardScreenDevices.LogitechVid;
        internal const ushort Pid = KeyboardScreenDevices.G19;
        internal const int LookMs = 5000;
        private const int BindRetryMs = 60000;
        private const string KeysSource = "g19";

        /// <summary>Where interface 0 stands, for the status line.</summary>
        public enum State { None, Binding, OtherDriver, Open }

        private readonly Action<string> _log;
        private readonly string _instanceId;
        private IntPtr _dev = InvalidHandle;
        private IntPtr _ifh;
        private byte _outPipe, _inPipe;
        private ushort _inPacket;
        private Thread _reader;
        private volatile bool _open;
        private volatile bool _binding;
        private long _nextLook, _nextBind;
        private long _written = -1;

        public State Current { get; private set; } = State.None;

        /// <summary>The driver interface 0 sits on when it is another
        /// vendor's, for the status line.</summary>
        public string OtherDriver { get; private set; } = string.Empty;

        public G19UsbScreen(string instanceId, Action<string> log)
        {
            _instanceId = instanceId;
            _log = log ?? (_ => { });
            Info = KeyboardScreenInfo.G19(instanceId);
        }

        /// <summary>The screen this G19 is.</summary>
        public KeyboardScreenInfo Info { get; }

        /// <summary>One worker pass. <paramref name="mayBind"/> is false while
        /// LGS draws the screens. A screen already on PadForge's binding is
        /// drawn either way: LGS cannot reach it then.</summary>
        public void Step(byte[] frame, long version, bool mayBind, long now, List<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            if (!_open)
            {
                if (_binding || now < _nextLook) return;
                _nextLook = now + LookMs;
                Look(mayBind, now, nodes);
                if (!_open) return;
            }

            byte[] bgra = frame;
            long wanted = version;
            if (bgra == null)
            {
                // Off: nothing else draws a screen PadForge holds, so the
                // last picture is cleared to black once.
                if (_written < 0) return;
                bgra = new byte[KeyboardScreenFormats.ColorWidth * KeyboardScreenFormats.ColorHeight * 4];
                wanted = -2;
            }
            if (_written == wanted) return;
            var data = KeyboardScreenFormats.ToG19Frame(bgra);
            if (WinUsb_WritePipe(_ifh, _outPipe, data, (uint)data.Length, out uint moved, IntPtr.Zero) && moved == data.Length)
                _written = wanted;
            else
            {
                _log("G19 screen: a frame write failed (" + Marshal.GetLastWin32Error() + "), closing the screen");
                Close();
            }
        }

        private void Look(bool mayBind, long now, List<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            var node = nodes?.FirstOrDefault(n => string.Equals(n.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase)) ?? default;
            if (string.IsNullOrEmpty(node.InstanceId))
            {
                Current = State.None;
                return;
            }
            string service = node.Service ?? string.Empty;
            if (service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
            {
                var paths = VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid);
                string path = paths?.FirstOrDefault(p => VendorUsbDriverInstaller.NamesNode(new[] { p }, node.InstanceId));
                if (path != null && Open(path))
                {
                    Current = State.Open;
                    return;
                }
                // WinUSB with no interface of PadForge's: another program's
                // binding, which is not PadForge's to take.
                Current = path == null ? State.OtherDriver : State.Binding;
                OtherDriver = service;
                return;
            }
            if (service.Length != 0 && !service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase))
            {
                Current = State.OtherDriver;
                OtherDriver = service;
                return;
            }
            Current = State.Binding;
            if (!mayBind || now < _nextBind) return;
            _nextBind = now + BindRetryMs;
            _binding = true;
            var plan = new VendorUsbDriverInstaller.BindPlan(VendorUsbDriverInstaller.BindId(node), "Logitech G19 screen");
            // The bind signs, stocks and forces a package, which takes
            // seconds, so it runs off the worker and the other screens keep
            // drawing.
            Task.Run(() =>
            {
                try { VendorUsbDriverInstaller.Bind(plan, node.InstanceId, nodes, _log, CancellationToken.None); }
                catch (Exception ex) { _log("G19 screen: bind failed: " + ex.Message); }
                finally
                {
                    _nextLook = 0;
                    _binding = false;
                }
            });
        }

        private bool Open(string path)
        {
            // The test host opens no device (DirectDeviceAccess).
            if (DirectDeviceAccess.Refuses(path)) return false;
            IntPtr dev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (dev == InvalidHandle) return false;
            if (!WinUsb_Initialize(dev, out IntPtr ifh))
            {
                CloseHandle(dev);
                return false;
            }
            byte outPipe = 0, inPipe = 0;
            ushort inPacket = 0;
            if (WinUsb_QueryInterfaceSettings(ifh, 0, out var idesc))
            {
                for (byte i = 0; i < idesc.bNumEndpoints; i++)
                {
                    if (!WinUsb_QueryPipe(ifh, 0, i, out var pipe)) continue;
                    if (pipe.PipeId == 0x02 && pipe.PipeType == 2 /* bulk */) outPipe = pipe.PipeId;
                    else if (pipe.PipeId == 0x81 && pipe.PipeType == 3 /* interrupt */)
                    {
                        inPipe = pipe.PipeId;
                        inPacket = pipe.MaximumPacketSize;
                    }
                }
            }
            if (outPipe == 0)
            {
                WinUsb_Free(ifh);
                CloseHandle(dev);
                return false;
            }
            // A frame is one bulk write, gnome15's and G19USB's 1000 ms each,
            // and a short read timeout wakes the key reader to check that the
            // screen is still open.
            uint writeTimeout = 1000, readTimeout = 100;
            WinUsb_SetPipePolicy(ifh, outPipe, PIPE_TRANSFER_TIMEOUT, 4, ref writeTimeout);
            _dev = dev;
            _ifh = ifh;
            _outPipe = outPipe;
            _inPipe = inPipe;
            _inPacket = Math.Max(inPacket, (ushort)2);
            _written = -1;
            _open = true;
            if (inPipe != 0)
            {
                WinUsb_SetPipePolicy(ifh, inPipe, PIPE_TRANSFER_TIMEOUT, 4, ref readTimeout);
                _reader = new Thread(ReadKeys) { IsBackground = true, Name = "PadForge.G19Keys" };
                _reader.Start();
            }
            _log("G19 screen: open on " + path);
            return true;
        }

        private void ReadKeys()
        {
            var buffer = new byte[_inPacket];
            while (_open)
            {
                if (WinUsb_ReadPipe(_ifh, _inPipe, buffer, (uint)buffer.Length, out uint got, IntPtr.Zero))
                {
                    int bits = KeyboardScreenFormats.DecodeG19Keys(buffer.AsSpan(0, (int)Math.Min(got, (uint)buffer.Length)));
                    if (bits >= 0) KeyboardScreenKeys.Report(Info.Id, KeysSource, bits);
                    continue;
                }
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_SEM_TIMEOUT) continue;
                // Unplugged or stopped: the worker's next write closes the
                // handle, and the next look opens it again.
                break;
            }
            KeyboardScreenKeys.Clear(Info.Id, KeysSource);
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            var reader = _reader;
            _reader = null;
            // The reader wakes within its 100 ms timeout. One still inside a
            // native call keeps the handles, which are left rather than freed
            // under it (the headset reader's rule).
            if (reader != null && !reader.Join(1000)) return;
            WinUsb_Free(_ifh);
            CloseHandle(_dev);
            _ifh = IntPtr.Zero;
            _dev = InvalidHandle;
            _nextLook = 0;
            Current = State.None;
            KeyboardScreenKeys.Clear(Info.Id, KeysSource);
        }

        public void Dispose() => Close();

        /// <summary>Gives every G19's interface 0 that is on PadForge's
        /// WinUSB package back to Windows, for the feature switched off.
        /// Blocks for seconds, so the caller runs it off the UI thread.</summary>
        public static void Restore(Action<string> log)
        {
            var nodes = VendorUsbDriverInstaller.ListPresentUsbNodes();
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node.Vid != Vid || node.Pid != Pid || node.Interface != 0 || string.IsNullOrEmpty(node.InstanceId)) continue;
                if (!(node.Service ?? string.Empty).Equals("WINUSB", StringComparison.OrdinalIgnoreCase)) continue;
                if (!VendorUsbDriverInstaller.HasActiveInterface(node.InstanceId)) continue;
                VendorUsbDriverInstaller.Restore(node.InstanceId, log ?? (_ => { }), CancellationToken.None);
            }
        }

        // ── WinUSB interop, Ds3DirectService's declarations ─────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct USB_INTERFACE_DESCRIPTOR { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_SEM_TIMEOUT = 121;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(IntPtr dev, out IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr ifh, byte pipeId, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr ifh, byte pipeId, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr ifh, byte alt, out USB_INTERFACE_DESCRIPTOR desc);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr ifh, byte alt, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipe);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr ifh, byte pipeId, uint policyType, uint valueLen, ref uint value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    }

    /// <summary>
    /// Every G19 plugged in (issue #519), a <see cref="G19UsbScreen"/> each,
    /// found by the instance ID of its interface 0 on a look every
    /// <see cref="G19UsbScreen.LookMs"/>. A G19 that leaves takes its screen
    /// and its keys with it. Runs on the host's worker.
    /// </summary>
    internal sealed class G19UsbScreens : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Dictionary<string, G19UsbScreen> _screens = new(StringComparer.OrdinalIgnoreCase);
        private List<VendorUsbDriverInstaller.UsbNode> _nodes = new();
        private long _nextLook;

        public G19UsbScreens(Action<string> log) => _log = log ?? (_ => { });

        /// <summary>The G19s plugged in, in id order.</summary>
        public IReadOnlyList<KeyboardScreenInfo> Screens { get; private set; } = Array.Empty<KeyboardScreenInfo>();

        /// <summary>The G19 a screen id names, or null.</summary>
        public G19UsbScreen Find(string id)
        {
            foreach (var screen in _screens.Values)
                if (string.Equals(screen.Info.Id, id, StringComparison.Ordinal)) return screen;
            return null;
        }

        /// <summary>One worker pass. <paramref name="mayBind"/> is false while
        /// LGS draws the screens.</summary>
        public void Step(Func<string, (byte[] Frame, long Version)> frameOf, bool mayBind, long now)
        {
            if (now >= _nextLook)
            {
                _nextLook = now + G19UsbScreen.LookMs;
                _nodes = VendorUsbDriverInstaller.ListPresentUsbNodes() ?? new List<VendorUsbDriverInstaller.UsbNode>();
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in _nodes)
                {
                    if (node.Vid != G19UsbScreen.Vid || node.Pid != G19UsbScreen.Pid || node.Interface != 0
                        || string.IsNullOrEmpty(node.InstanceId)) continue;
                    present.Add(node.InstanceId);
                    if (!_screens.ContainsKey(node.InstanceId))
                        _screens[node.InstanceId] = new G19UsbScreen(node.InstanceId, _log);
                }
                foreach (string gone in _screens.Keys.Where(id => !present.Contains(id)).ToList())
                {
                    var screen = _screens[gone];
                    _screens.Remove(gone);
                    screen.Dispose();
                    KeyboardScreenKeys.ClearScreen(screen.Info.Id);
                }
                Screens = _screens.Values.Select(s => s.Info).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            }
            foreach (var screen in _screens.Values)
            {
                var (frame, version) = frameOf(screen.Info.Id);
                try { screen.Step(frame, version, mayBind, now, _nodes); }
                catch (Exception ex) { _log("G19 screen: pass failed: " + ex.Message); }
            }
        }

        public void Dispose()
        {
            foreach (var screen in _screens.Values)
            {
                screen.Dispose();
                KeyboardScreenKeys.ClearScreen(screen.Info.Id);
            }
            _screens.Clear();
            Screens = Array.Empty<KeyboardScreenInfo>();
        }
    }
}
