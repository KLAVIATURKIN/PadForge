using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    // ─────────────────────────────────────────────
    //  Test doubles the TouchSense tests share
    // ─────────────────────────────────────────────

    /// <summary>A scripted WinUSB interface for a TouchSense mouse (#494).
    /// It answers as the iFeel Mouse of the 2001 capture answers unless a
    /// test changes a field, and writes every call into a journal: the
    /// setup packet and the data of each transfer as hexadecimal text, so a
    /// test reads the wire as it would be.</summary>
    internal sealed class FakeTouchSenseUsb : ITouchSenseUsb
    {
        public byte InterfaceClass = 0x03, InPipe = 0x81, OutPipe = 0x01;
        public byte[] Configuration = TouchSenseProtocolTests.Configuration;
        public byte[] ReportDescriptor = TouchSenseProtocolTests.ReportDescriptor;
        public bool DescribeFails, DescribeThrows, ConfigurationFails, DescriptorFails;

        /// <summary>How many bytes short the report descriptor comes back.</summary>
        public int DescriptorShortBy;
        public volatile bool FailControlOut, FailWritePipe;

        /// <summary>The next read's answer: the bytes read, or the error
        /// with -1, as <see cref="ITouchSenseUsb.ReadPipe"/> gives them.</summary>
        public Func<(int Got, int Error, byte[] Report)> Read = () => (0, 0, null);

        public readonly ConcurrentQueue<string> Journal = new();
        private int _inFlight, _disposals;

        /// <summary>True when the interface was freed while a transfer was
        /// still inside it.</summary>
        public volatile bool FreedUnderATransfer;
        public bool Disposed => Volatile.Read(ref _disposals) > 0;
        public int Disposals => Volatile.Read(ref _disposals);

        private static string Text(byte[] bytes)
            => bytes == null ? "" : BitConverter.ToString(bytes).Replace('-', ' ');

        public string[] Calls(string kind) => Journal.Where(line => line.StartsWith(kind + " ", StringComparison.Ordinal) || line == kind).ToArray();

        public bool Describe(out byte interfaceClass, out byte interruptIn, out byte interruptOut)
        {
            Journal.Enqueue("describe");
            if (DescribeThrows) throw new InvalidOperationException("scripted");
            interfaceClass = InterfaceClass;
            interruptIn = InPipe;
            interruptOut = OutPipe;
            return !DescribeFails;
        }

        public int ReadConfiguration(byte[] buffer)
        {
            Journal.Enqueue("config");
            if (ConfigurationFails || Configuration == null) return 0;
            int n = Math.Min(buffer.Length, Configuration.Length);
            Array.Copy(Configuration, buffer, n);
            return n;
        }

        public bool ControlIn(byte[] setup, byte[] buffer, out int length)
        {
            Journal.Enqueue("in " + Text(setup));
            length = 0;
            if (DescriptorFails) return false;
            length = Math.Max(0, Math.Min(buffer.Length, ReportDescriptor.Length) - DescriptorShortBy);
            Array.Copy(ReportDescriptor, buffer, length);
            return true;
        }

        public bool ControlOut(byte[] setup, byte[] data)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                Journal.Enqueue("out " + Text(setup) + " | " + Text(data));
                return !FailControlOut;
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public bool WritePipe(byte pipe, byte[] data)
        {
            Journal.Enqueue($"pipe {pipe:X2} | " + Text(data));
            return !FailWritePipe;
        }

        public int ReadPipe(byte pipe, byte[] buffer, out int error)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                Journal.Enqueue($"read {pipe:X2}");
                var (got, code, report) = Read();
                error = code;
                if (got > 0 && report != null) Array.Copy(report, buffer, Math.Min(got, buffer.Length));
                Journal.Enqueue("read done");
                return got;
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public void SetTimeout(byte pipe, int milliseconds) => Journal.Enqueue($"timeout {pipe:X2} {milliseconds}");

        public Action Canceled;

        public void CancelIo()
        {
            Journal.Enqueue("cancel");
            Canceled?.Invoke();
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _inFlight) > 0) FreedUnderATransfer = true;
            Interlocked.Increment(ref _disposals);
            Journal.Enqueue("dispose");
        }
    }

    /// <summary>A scripted channel to one bound mouse, for the backend and
    /// the row (#494): it records every frame with the tick it came at,
    /// fails every send while <see cref="FailSends"/> is set, and hands the
    /// reader the reports a test delivers.</summary>
    internal sealed class FakeTouchSenseChannel : ITouchSenseChannel
    {
        private readonly BlockingCollection<byte[]> _reports = new();
        private int _closed, _disposals, _attempts, _queued, _handedOut, _fed;

        public FakeTouchSenseChannel(string path, Func<long> clock = null)
        {
            Path = path;
            Clock = clock ?? (() => 0);
        }

        public string Path { get; }
        public bool ReadsPointer { get; init; } = true;

        /// <summary>The clock a sent frame is stamped with.</summary>
        public Func<long> Clock;
        public volatile bool FailSends;
        public volatile bool FailReads;

        /// <summary>Runs inside every send before the frame is taken, and
        /// inside every read before it waits, so a test holds a worker or
        /// a reader in a transfer.</summary>
        public Action BeforeSend, BeforeRead;
        public readonly ConcurrentQueue<(byte[] Frame, long Tick)> Sent = new();

        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public int Disposals => Volatile.Read(ref _disposals);

        /// <summary>Every call of <see cref="Send"/>, the failed ones too.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>The frames the channel took, as text.</summary>
        public string[] Frames => Sent.Select(s => BitConverter.ToString(s.Frame).Replace('-', ' ')).ToArray();

        /// <summary>The tick each of them came at.</summary>
        public long[] SentAt => Sent.Select(s => s.Tick).ToArray();

        public bool Send(byte[] frame)
        {
            Interlocked.Increment(ref _attempts);
            BeforeSend?.Invoke();
            if (!IsOpen || FailSends || frame == null) return false;
            Sent.Enqueue(((byte[])frame.Clone(), Clock()));
            return true;
        }

        /// <summary>Hands the reader one report, and returns once the
        /// reader has come back for the next one, so what it did with this
        /// one is done.</summary>
        public void Deliver(params byte[] report)
        {
            int mine = Interlocked.Increment(ref _queued);
            _reports.Add(report);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _fed) >= mine, 30000), "the reader took the report");
        }

        public bool TryRead(Span<byte> report, out int length)
        {
            length = 0;
            // The one reader is back, so every report handed out so far
            // has been dealt with.
            Volatile.Write(ref _fed, Volatile.Read(ref _handedOut));
            BeforeRead?.Invoke();
            if (!IsOpen || FailReads) return false;
            // A short wait stands in for the pipe's timeout.
            if (!_reports.TryTake(out var next, 5)) return IsOpen && !FailReads;
            length = Math.Min(next.Length, report.Length);
            next.AsSpan(0, length).CopyTo(report);
            Interlocked.Increment(ref _handedOut);
            return true;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            Volatile.Write(ref _closed, 1);
        }
    }

    // ─────────────────────────────────────────────
    //  The channel over a scripted WinUSB interface
    // ─────────────────────────────────────────────

    /// <summary>The one WinUSB handle of a bound TouchSense mouse (#494):
    /// how it opens, which transfer carries a frame, what a read means and
    /// how it closes, against a scripted interface that answers as the
    /// iFeel Mouse of the 2001 capture does (tactile-cvs
    /// tactile/support/logs/ifeel.log,v). No device is opened here.</summary>
    public class TouchSenseChannelTests
    {
        private const string Path = @"\\?\usb#vid_046d&pid_c030#6&1c2d3e4f&0&2#{fd826c66-3556-4845-9436-0fe7580ded02}";

        private static string Text(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        private static (TouchSenseChannel Channel, FakeTouchSenseUsb Usb) Open(Action<FakeTouchSenseUsb> script = null,
            int maxReadFaults = TouchSenseChannel.MaxReadFaults)
        {
            var usb = new FakeTouchSenseUsb();
            script?.Invoke(usb);
            return (TouchSenseChannel.Open(Path, usb, maxReadFaults, readFaultPauseMs: 0), usb);
        }

        // ── Open ──

        /// <summary>The whole open, in order: the control pipe's timeout,
        /// the interface and its pipes, the configuration descriptor, the
        /// report descriptor at the length the HID descriptor gives
        /// (ifeel.log,v:85-89 and 226), SET_IDLE once (ifeel.log,v:195),
        /// and the two pipes' timeouts. Nothing else goes to the mouse,
        /// and no frame goes out before one is sent.</summary>
        [Fact]
        public void TheOpen_ReadsTheDescriptorChecksItsShapeAndSendsSetIdleOnce()
        {
            var (channel, usb) = Open();
            Assert.NotNull(channel);
            Assert.Equal(new[]
            {
                "timeout 00 1000",
                "describe",
                "config",
                "in 81 06 00 22 00 00 4A 00",
                "out 21 0A 00 00 00 00 00 00 | ",
                "timeout 81 100",
                "timeout 01 1000",
            }, usb.Journal);
            Assert.Equal(Path, channel.Path);
            Assert.True(channel.IsOpen);
            Assert.True(channel.ReadsPointer);
            Assert.False(channel.UsesInterruptOut);
            Assert.False(usb.Disposed);
            Assert.Equal((1000, 1000, 100), (TouchSenseChannel.ControlTimeoutMs, TouchSenseChannel.WriteTimeoutMs, TouchSenseChannel.ReadTimeoutMs));
            channel.Dispose();
        }

        /// <summary>A device that is not what the open expects is not
        /// opened: its interface is closed again, and nothing was written
        /// to it, SET_IDLE included.</summary>
        [Fact]
        public void ADeviceThatDoesNotDeclareTheBuzzReport_IsNotOpened_AndNothingIsWrittenToIt()
        {
            var refused = new (string Why, Action<FakeTouchSenseUsb> Script)[]
            {
                ("the interface cannot be read", u => u.DescribeFails = true),
                ("the interface throws", u => u.DescribeThrows = true),
                ("not a HID interface", u => u.InterfaceClass = 0xFF),
                ("no interrupt IN pipe", u => u.InPipe = 0),
                ("no configuration descriptor", u => u.ConfigurationFails = true),
                ("no HID descriptor in it", u => u.Configuration = TouchSenseProtocolTests.Hex("09 02 12 00 01 01 00 A0 FA 09 04 00 00 02 03 01 02 00")),
                ("the report descriptor is refused", u => u.DescriptorFails = true),
                ("the report descriptor comes back short", u => u.DescriptorShortBy = 1),
                // Eight bytes where the buzz report has seven.
                ("another output report", u => u.ReportDescriptor = Swap(TouchSenseProtocolTests.ReportDescriptor, "95 07 91 02", "95 08 91 02")),
                // The report as a feature report.
                ("no output report", u => u.ReportDescriptor = Swap(TouchSenseProtocolTests.ReportDescriptor, "95 07 91 02", "95 07 B1 02")),
            };
            foreach (var (why, script) in refused)
            {
                var (channel, usb) = Open(script);
                Assert.True(channel == null, why);
                Assert.Equal(1, usb.Disposals);
                Assert.Empty(usb.Calls("out"));
                Assert.Empty(usb.Calls("pipe"));
            }
            Assert.Null(TouchSenseChannel.Open(Path, (ITouchSenseUsb)null));
        }

        private static byte[] Swap(byte[] source, string find, string replace)
        {
            string text = Text(source);
            Assert.Contains(find, text);
            return TouchSenseProtocolTests.Hex(text.Replace(find, replace));
        }

        [Fact]
        public void AMouseWithNoInterruptOutPipe_OpensOnTheControlTransferAlone()
        {
            var (channel, usb) = Open(u => u.OutPipe = 0);
            Assert.NotNull(channel);
            Assert.Equal(new[] { "timeout 00 1000", "timeout 81 100" }, usb.Calls("timeout"));

            // With nothing to fall back to, a failed control transfer is a failed frame.
            usb.FailControlOut = true;
            Assert.False(channel.Send(TouchSenseProtocol.Stop()));
            Assert.Empty(usb.Calls("pipe"));
            Assert.False(channel.UsesInterruptOut);
            channel.Dispose();
        }

        /// <summary>A sibling whose input report is not the iFeel Mouse's
        /// still opens, since it declares the buzz report, and is read as
        /// no pointer.</summary>
        [Fact]
        public void AMouseWhoseInputReportDiffers_OpensWithNoPointer()
        {
            // A fourth button, as the MouseMan has, in the first vendor bit's place.
            byte[] descriptor = Swap(TouchSenseProtocolTests.ReportDescriptor,
                "19 01 29 03 15 00 25 01 75 01 95 03 81 02 95 05", "19 01 29 04 15 00 25 01 75 01 95 04 81 02 95 04");
            Assert.Equal(TouchSenseProtocolTests.ReportDescriptor.Length, descriptor.Length);
            var (channel, usb) = Open(u => u.ReportDescriptor = descriptor);
            Assert.NotNull(channel);
            Assert.False(channel.ReadsPointer);
            Assert.Single(usb.Calls("out"));
            channel.Dispose();
        }

        /// <summary>SET_IDLE's answer decides nothing: a mouse that
        /// refuses it opens all the same, as Bobruk's driver goes on
        /// whatever its own SET_IDLE returned (tactile-ifeel
        /// iFeel/ifeel.c:282).</summary>
        [Fact]
        public void AMouseThatRefusesSetIdle_StillOpens()
        {
            var (channel, usb) = Open(u => u.FailControlOut = true);
            Assert.NotNull(channel);
            Assert.Equal(new[] { "out 21 0A 00 00 00 00 00 00 | " }, usb.Calls("out"));
            Assert.Equal(new[] { "timeout 00 1000", "timeout 81 100", "timeout 01 1000" }, usb.Calls("timeout"));
            channel.Dispose();
        }

        // ── Send ──

        /// <summary>A frame goes out as the control transfer the vendor's
        /// own stack used, byte for byte what the capture shows
        /// (ifeel.log,v:257-280), and the interrupt pipe is left alone.</summary>
        [Fact]
        public void AFrame_GoesOutAsTheControlTransferTheVendorUsed()
        {
            var (channel, usb) = Open();
            Assert.True(channel.Send(TouchSenseProtocol.FrameFor(0.5)));
            Assert.True(channel.Send(TouchSenseProtocol.Stop()));
            Assert.Equal(new[]
            {
                "out 21 0A 00 00 00 00 00 00 | ",
                "out 21 09 00 02 00 00 07 00 | 11 0A A8 0A 00 1E 00",
                "out 21 09 00 02 00 00 07 00 | 13 00 00 00 00 00 00",
            }, usb.Calls("out"));
            Assert.Empty(usb.Calls("pipe"));
            Assert.False(channel.UsesInterruptOut);

            // Only a whole frame goes out.
            Assert.False(channel.Send(null));
            Assert.False(channel.Send(new byte[6]));
            Assert.False(channel.Send(new byte[8]));
            Assert.Equal(3, usb.Calls("out").Length);
            channel.Dispose();
        }

        /// <summary>When the control transfer fails and the interrupt OUT
        /// pipe takes the same seven bytes, as Bobruk's driver sends them
        /// (tactile-ifeel iFeel/ifeel.c:92-110), the channel stays on the
        /// pipe for the rest of its life: no later frame tries the control
        /// transfer again.</summary>
        [Fact]
        public void WhenTheControlTransferFails_AndThePipeTakesTheFrame_TheChannelStaysOnThePipe()
        {
            var (channel, usb) = Open();
            usb.FailControlOut = true;
            Assert.True(channel.Send(TouchSenseProtocol.FrameFor(1)));
            Assert.True(channel.UsesInterruptOut);
            // The control transfer works again, and is not asked.
            usb.FailControlOut = false;
            Assert.True(channel.Send(TouchSenseProtocol.Stop()));
            Assert.True(channel.Send(TouchSenseProtocol.StopAll()));

            Assert.Equal(new[]
            {
                "out 21 0A 00 00 00 00 00 00 | ",
                "out 21 09 00 02 00 00 07 00 | 11 0A FF 0A 00 1E 00",
            }, usb.Calls("out"));
            Assert.Equal(new[]
            {
                "pipe 01 | 11 0A FF 0A 00 1E 00",
                "pipe 01 | 13 00 00 00 00 00 00",
                "pipe 01 | F3 00 00 00 00 00 00",
            }, usb.Calls("pipe"));

            // On the pipe, a failed write is a failed frame.
            usb.FailWritePipe = true;
            Assert.False(channel.Send(TouchSenseProtocol.Stop()));
            channel.Dispose();
        }

        [Fact]
        public void WhenBothTransfersFail_TheFrameFails_AndTheControlTransferStaysFirst()
        {
            var (channel, usb) = Open();
            usb.FailControlOut = true;
            usb.FailWritePipe = true;
            Assert.False(channel.Send(TouchSenseProtocol.Stop()));
            Assert.False(channel.UsesInterruptOut);
            Assert.Single(usb.Calls("pipe"));

            usb.FailControlOut = false;
            Assert.True(channel.Send(TouchSenseProtocol.Stop()));
            Assert.Single(usb.Calls("pipe"));
            channel.Dispose();
        }

        // ── Read ──

        [Fact]
        public void ARead_GivesAReport_OrNothingInTime_OrTheEndOfTheChannel()
        {
            var answers = new ConcurrentQueue<(int, int, byte[])>();
            var (channel, usb) = Open(u => u.Read = () => answers.TryDequeue(out var next) ? next : (0, 0, null));
            var report = new byte[TouchSenseChannel.ReadBufferLength];

            answers.Enqueue((4, 0, new byte[] { 0x01, 0x05, 0xFB, 0x00 }));
            Assert.True(channel.TryRead(report, out int length));
            Assert.Equal("01 05 FB 00", Text(report.AsSpan(0, length).ToArray()));

            // The pipe's timeout: nothing came, which is no fault.
            Assert.True(channel.TryRead(report, out length));
            Assert.Equal(0, length);

            // A report longer than the caller's room is cut to it, never written past it.
            answers.Enqueue((8, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            var small = new byte[4];
            Assert.True(channel.TryRead(small, out length));
            Assert.Equal((4, "01 02 03 04"), (length, Text(small)));
            Assert.Equal("read 81", usb.Calls("read")[0]);

            // The device left.
            answers.Enqueue((-1, 1167, null));
            Assert.False(channel.TryRead(report, out length));
            Assert.Equal(0, length);
            channel.Dispose();
        }

        /// <summary>The errors that say the device left or the read was
        /// called off end the channel at once, the list the DualShock 3
        /// reader ends on (Ds3DirectService.cs:1009-1012). Any other error
        /// is tried again.</summary>
        [Theory]
        [InlineData(2, true)]      // ERROR_FILE_NOT_FOUND
        [InlineData(6, true)]      // ERROR_INVALID_HANDLE
        [InlineData(31, true)]     // ERROR_GEN_FAILURE
        [InlineData(433, true)]    // ERROR_NO_SUCH_DEVICE
        [InlineData(995, true)]    // ERROR_OPERATION_ABORTED
        [InlineData(1167, true)]   // ERROR_DEVICE_NOT_CONNECTED
        [InlineData(5, false)]
        [InlineData(87, false)]
        [InlineData(1, false)]
        public void AFailedRead_EndsTheChannel_OnlyForAnErrorThatSaysTheDeviceLeft(int error, bool gone)
        {
            Assert.Equal(gone, TouchSenseChannel.IsGone(error));
            var (channel, _) = Open(u => u.Read = () => (-1, error, null));
            Assert.Equal(!gone, channel.TryRead(new byte[8], out int length));
            Assert.Equal(0, length);
            channel.Dispose();
        }

        /// <summary>A read that keeps failing for another reason gives up
        /// after its count of faults in a row, and one good read in
        /// between starts the count again.</summary>
        [Fact]
        public void AReadThatKeepsFailing_GivesUpAfterItsCount_AndAGoodReadStartsTheCountAgain()
        {
            int fail = 1;
            var (channel, _) = Open(u => u.Read = () => Volatile.Read(ref fail) != 0 ? (-1, 5, null) : (0, 0, null), maxReadFaults: 3);
            var report = new byte[8];
            Assert.True(channel.TryRead(report, out _));
            Assert.True(channel.TryRead(report, out _));
            // A read that works, here one that times out.
            Volatile.Write(ref fail, 0);
            Assert.True(channel.TryRead(report, out _));
            Volatile.Write(ref fail, 1);
            Assert.True(channel.TryRead(report, out _));
            Assert.True(channel.TryRead(report, out _));
            Assert.False(channel.TryRead(report, out _));
            // About a second of faults in a build's own numbers.
            Assert.Equal(1000, TouchSenseChannel.MaxReadFaults * TouchSenseChannel.ReadFaultPauseMs);
            channel.Dispose();
        }

        // ── Close ──

        [Fact]
        public void ClosingTheChannel_CallsOffTheReadThenFreesTheInterface_Once()
        {
            var (channel, usb) = Open();
            int before = usb.Journal.Count;
            channel.Dispose();
            Assert.Equal(new[] { "cancel", "dispose" }, usb.Journal.Skip(before));
            Assert.False(channel.IsOpen);
            // Nothing goes out on a closed channel, and no frame was sent by the close itself.
            Assert.False(channel.Send(TouchSenseProtocol.Stop()));
            Assert.False(channel.TryRead(new byte[8], out _));
            Assert.Single(usb.Calls("out"));

            channel.Dispose();
            channel.Dispose();
            Assert.Equal(1, usb.Disposals);
            Assert.Single(usb.Calls("cancel"));
        }

        /// <summary>A close waits for the read another thread is inside:
        /// the interface is never freed under a transfer. A second closer
        /// returns only once the first has freed the handle, so whoever
        /// opens the mouse next never meets a handle that is still held.
        /// The read here ignores the cancel and ends when the test says.</summary>
        [Fact]
        public void AClose_WaitsForTheReadInFlight_AndASecondCloserWaitsForTheFirst()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var canceled = new ManualResetEventSlim();
            var (channel, usb) = Open(u =>
            {
                u.Read = () =>
                {
                    entered.Set();
                    release.Wait();
                    return (0, 0, null);
                };
                u.Canceled = canceled.Set;
            });

            var reader = new Thread(() => channel.TryRead(new byte[8], out _)) { IsBackground = true };
            reader.Start();
            Assert.True(entered.Wait(30000), "the read began");

            var first = new Thread(channel.Dispose) { IsBackground = true };
            first.Start();
            Assert.True(canceled.Wait(30000), "the close called the read off");
            // Closed to new transfers at once, with the handle still held.
            Assert.False(channel.IsOpen);
            var second = new Thread(channel.Dispose) { IsBackground = true };
            second.Start();
            // Neither can finish while the read is inside the interface.
            Assert.False(first.Join(50));
            Assert.False(second.Join(50));
            Assert.False(usb.Disposed);

            release.Set();
            Assert.True(reader.Join(30000));
            Assert.True(first.Join(30000));
            Assert.True(second.Join(30000));
            Assert.Equal(1, usb.Disposals);
            Assert.False(usb.FreedUnderATransfer);
            var journal = usb.Journal.ToArray();
            Assert.True(Array.IndexOf(journal, "read done") < Array.IndexOf(journal, "dispose"), string.Join(", ", journal));
        }

        // ── The real interface ──

        /// <summary>Every winusb.dll and kernel32.dll entry point the real
        /// interface declares resolves, so a misspelled one fails here and
        /// not on the first bound mouse. No device is touched.</summary>
        [Fact]
        public void TheRealInterfacesEntryPoints_Resolve()
        {
            Assert.Null(Record.Exception(() => Marshal.PrelinkAll(typeof(WinUsbTouchSenseUsb))));
            Assert.Equal(8, Marshal.SizeOf<WinUsbTouchSenseUsb.WINUSB_SETUP_PACKET>());
        }

        /// <summary>The eight setup bytes become WinUSB's packet with the
        /// three 16-bit fields read low byte first, so what the protocol
        /// class builds is what goes on the wire.</summary>
        [Fact]
        public void ASetupPacket_KeepsItsEightBytes()
        {
            Assert.True(WinUsbTouchSenseUsb.TryPacket(TouchSenseProtocol.SetReportSetup(), out var report));
            Assert.Equal((0x21, 0x09, 0x0200, 0x0000, 7), (report.RequestType, report.Request, report.Value, report.Index, report.Length));
            Assert.True(WinUsbTouchSenseUsb.TryPacket(TouchSenseProtocol.SetIdleSetup(), out var idle));
            Assert.Equal((0x21, 0x0A, 0x0000, 0x0000, 0), (idle.RequestType, idle.Request, idle.Value, idle.Index, idle.Length));
            Assert.True(WinUsbTouchSenseUsb.TryPacket(TouchSenseProtocol.ReportDescriptorSetup(0x4A), out var descriptor));
            Assert.Equal((0x81, 0x06, 0x2200, 0x0000, 0x4A), (descriptor.RequestType, descriptor.Request, descriptor.Value, descriptor.Index, descriptor.Length));
            Assert.True(WinUsbTouchSenseUsb.TryPacket(new byte[] { 1, 2, 0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A }, out var any));
            Assert.Equal((0x1234, 0x5678, 0x9ABC), (any.Value, any.Index, any.Length));
            Assert.False(WinUsbTouchSenseUsb.TryPacket(null, out _));
            Assert.False(WinUsbTouchSenseUsb.TryPacket(new byte[7], out _));
        }

        /// <summary>A path no device is at opens nothing and throws
        /// nothing.</summary>
        [Fact]
        public void APathNoDeviceIsAt_OpensNothing()
        {
            const string nowhere = @"\\?\usb#vid_046d&pid_c030#padforge-test-no-such-device#{fd826c66-3556-4845-9436-0fe7580ded02}";
            // The open itself is under test here, so the test host's
            // refusal is lifted for it. The path names no device.
            using var real = RealTouchSenseAccess.AllowForTest();
            Assert.Null(WinUsbTouchSenseUsb.Open(nowhere));
            Assert.Null(WinUsbTouchSenseUsb.Open(null));
            Assert.Null(WinUsbTouchSenseUsb.Open(""));
            Assert.Null(TouchSenseChannel.Open(nowhere));
        }

        // ── The table of open channels ──

        [Fact]
        public void TheTable_ListsAnOpenChannelByItsPath_AndDropsOneThatClosed()
        {
            var table = new TouchSenseChannels();
            int opened = 0;
            table.Opened += () => opened++;
            Assert.Null(table.Find(Path));

            var channel = new FakeTouchSenseChannel(Path);
            table.Publish(channel);
            Assert.Equal(1, opened);
            Assert.Same(channel, table.Find(Path));
            // Windows compares interface paths without regard to case.
            Assert.Same(channel, table.Find(Path.ToUpperInvariant()));
            Assert.Null(table.Find(Path.Replace("&2#", "&3#")));
            Assert.Null(table.Find(null));
            Assert.Null(table.Find(""));

            // Closed by whoever saw a transfer fail: it is found no more, and it leaves the table.
            channel.Dispose();
            Assert.Null(table.Find(Path));
            Assert.Equal(0, table.Count);

            table.Publish(null);
            Assert.Equal(1, opened);
        }

        [Fact]
        public void ForgettingAChannel_NeverTakesANewerOneOfTheSameMouseOffTheTable()
        {
            var table = new TouchSenseChannels();
            var old = new FakeTouchSenseChannel(Path);
            var fresh = new FakeTouchSenseChannel(Path);
            table.Publish(old);
            old.Dispose();
            table.Publish(fresh);
            // The old row is retired after the mouse was opened again.
            table.Forget(old);
            Assert.Same(fresh, table.Find(Path));
            table.Forget(fresh);
            Assert.Null(table.Find(Path));
            table.Forget(null);
            Assert.Equal(0, table.Count);
        }

        /// <summary>The driver switch lets go of a node before it asks
        /// Windows to change that node's driver: the channel of the mouse
        /// at the node is closed when the call returns, and no other
        /// mouse's, a port whose ID begins the same among them.</summary>
        [Fact]
        public void ClosingTheChannelAtANode_ClosesThatMouseAlone_BeforeItReturns()
        {
            var table = new TouchSenseChannels();
            var here = new FakeTouchSenseChannel(Path);
            var neighbor = new FakeTouchSenseChannel(Path.Replace("&0&2#", "&0&21#"));
            var sibling = new FakeTouchSenseChannel(Path.Replace("pid_c030", "pid_c032"));
            table.Publish(here);
            table.Publish(neighbor);
            table.Publish(sibling);

            table.CloseAt(@"USB\VID_046D&PID_C030\6&1C2D3E4F&0&2");
            Assert.False(here.IsOpen);
            Assert.Equal(1, here.Disposals);
            Assert.True(neighbor.IsOpen);
            Assert.True(sibling.IsOpen);
            Assert.Null(table.Find(Path));
            Assert.Equal(2, table.Count);

            // A node with no mouse open, and no node at all.
            table.CloseAt(@"USB\VID_046D&PID_C030\6&1C2D3E4F&0&2");
            table.CloseAt(@"USB\VID_046D&PID_C030\6&1C2D3E4F&0&9");
            table.CloseAt(null);
            table.CloseAt("");
            Assert.Equal(2, table.Count);
            Assert.True(neighbor.IsOpen);
            Assert.True(sibling.IsOpen);
            Assert.Equal(1, here.Disposals);
        }

        /// <summary>A subscriber that throws costs nobody the listing.</summary>
        [Fact]
        public void AListenerThatThrows_DoesNotStopAListing()
        {
            var table = new TouchSenseChannels();
            table.Opened += () => throw new InvalidOperationException("scripted");
            var channel = new FakeTouchSenseChannel(Path);
            table.Publish(channel);
            Assert.Same(channel, table.Find(Path));
        }
    }
}
