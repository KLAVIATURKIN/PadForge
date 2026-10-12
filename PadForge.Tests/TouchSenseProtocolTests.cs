using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The buzz motor of a TouchSense mouse, byte for byte (#494). No iFeel
    /// is on the bench, so every frame here is checked against the bytes on
    /// record: the 2001 capture of the vendor's own driver stack
    /// (tactile-cvs tactile/support/logs/ifeel.log,v and ifeel.data,v), Dan
    /// Moore's write-up (iFeel index.html), Joshua Bobruk's Linux driver
    /// (tactile-ifeel iFeel/ifeel.c), and the vendor's 2003 driver,
    /// LMouFrc.dll of MouseWare 9.80, by its virtual addresses.
    /// </summary>
    public class TouchSenseProtocolTests
    {
        /// <summary>The iFeel Mouse's report descriptor, 74 bytes
        /// (ifeel.log,v:216-224).</summary>
        internal static readonly byte[] ReportDescriptor = Hex(
            "05 01 09 02 A1 01 A1 02 05 09 19 01 29 03 15 00",
            "25 01 75 01 95 03 81 02 95 05 06 00 FF 09 01 81",
            "02 05 01 09 01 A1 00 15 81 25 7F 75 08 95 02 09",
            "30 09 31 81 06 C0 09 38 95 01 81 06 C0 A1 02 06",
            "00 FF 09 02 95 07 91 02 C0 C0");

        /// <summary>The iFeel Mouse's configuration descriptor, 41 bytes
        /// (ifeel.log,v:85-89).</summary>
        internal static readonly byte[] Configuration = Hex(
            "09 02 29 00 01 01 00 A0 FA 09 04 00 00 02 03 01",
            "02 00 09 21 00 01 21 01 22 4A 00 07 05 81 03 08",
            "00 0A 07 05 01 03 08 00 0A");

        /// <summary>A plain three-button mouse with no wheel and no output
        /// report: the boot mouse of the HID specification's appendix.</summary>
        private static readonly byte[] BootMouse = Hex(
            "05 01 09 02 A1 01 09 01 A1 00 05 09 19 01 29 03",
            "15 00 25 01 95 03 75 01 81 02 95 01 75 05 81 01",
            "05 01 09 30 09 31 15 81 25 7F 75 08 95 02 81 06",
            "C0 C0");

        internal static byte[] Hex(params string[] lines)
            => string.Join(" ", lines).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => Convert.ToByte(b, 16)).ToArray();

        private static string Text(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>The descriptor with the bytes at one place replaced.</summary>
        private static byte[] With(byte[] source, string find, string replace)
        {
            string text = Text(source);
            Assert.Contains(find, text);
            int at = text.IndexOf(find, StringComparison.Ordinal);
            Assert.Equal(at, text.LastIndexOf(find, StringComparison.Ordinal));
            return Hex(text.Remove(at, find.Length).Insert(at, replace));
        }

        // ── Frames ──

        [Theory]
        // The first buzz frame of the capture (ifeel.data,v:54).
        [InlineData(0xC2, 0x000A, 0xC8, "11 0A C2 0A 00 C8 00")]
        // Its strongest (ifeel.data,v:79).
        [InlineData(0xD4, 0x0007, 0xFF, "11 0A D4 07 00 FF 00")]
        // Moore's example: intensity 200, delay 3, count 69 (index.html:95-103).
        [InlineData(0xC8, 0x0003, 0x45, "11 0A C8 03 00 45 00")]
        // Bobruk's template (ifeel.c:73).
        [InlineData(0xFF, 0x00FF, 0x01, "11 0A FF FF 00 01 00")]
        // The vendor's 16-bit delay, low byte first (LMouFrc.dll 0x10008502 to 0x1000850F).
        [InlineData(0x80, 0x0123, 0x02, "11 0A 80 23 01 02 00")]
        public void ABuzzFrame_IsTheCommandTheStrengthTheDelayAndTheCount(int strength, int delay, int count, string bytes)
            => Assert.Equal(bytes, Text(TouchSenseProtocol.Buzz((byte)strength, (ushort)delay, (byte)count)));

        [Fact]
        public void TheOtherThreeFrames_AreOneCommandByteAndSixZeros()
        {
            // The capture (ifeel.data,v:31), Moore (ifeel-beos.cpp:26), the
            // LibUsbDotNet program (Program.cs:118) and the vendor's driver
            // (0x100085BB to 0x100085E5).
            Assert.Equal("13 00 00 00 00 00 00", Text(TouchSenseProtocol.Stop()));
            // The capture (ifeel.data,v:33) and the vendor's driver (0x10007F0A to 0x10007F31).
            Assert.Equal("F3 00 00 00 00 00 00", Text(TouchSenseProtocol.StopAll()));
            // The capture (ifeel.data,v:30) and the vendor's driver (0x10008045 to 0x100080B7).
            Assert.Equal("14 00 00 00 00 00 00", Text(TouchSenseProtocol.KeepAlive()));
            Assert.Equal(7, TouchSenseProtocol.FrameLength);
            // Every call hands out its own array, so no caller can alter another's frame.
            Assert.NotSame(TouchSenseProtocol.Stop(), TouchSenseProtocol.Stop());
        }

        [Fact]
        public void TheControlRequests_AreTheOnesTheCaptureShows()
        {
            // SET_REPORT, report type Output, report ID 0, interface 0 (ifeel.log,v:280).
            Assert.Equal("21 09 00 02 00 00 07 00", Text(TouchSenseProtocol.SetReportSetup()));
            // SET_IDLE (ifeel.log,v:195).
            Assert.Equal("21 0A 00 00 00 00 00 00", Text(TouchSenseProtocol.SetIdleSetup()));
            // The report descriptor, asked for at the length the HID
            // descriptor gives. The capture's host asked for 0x8A and got 0x4A (ifeel.log,v:226).
            Assert.Equal("81 06 00 22 00 00 4A 00", Text(TouchSenseProtocol.ReportDescriptorSetup(0x4A)));
            Assert.Equal("81 06 00 22 00 00 23 01", Text(TouchSenseProtocol.ReportDescriptorSetup(0x0123)));
        }

        // ── From a level to a frame ──

        [Theory]
        [InlineData(1.00, "11 0A FF 0A 00 1E 00")]
        [InlineData(0.50, "11 0A A8 0A 00 1E 00")]
        [InlineData(0.25, "11 0A 7C 0A 00 1E 00")]
        [InlineData(0.05, "11 0A 59 0A 00 1E 00")]
        [InlineData(0.02, "11 0A 54 0A 00 1E 00")]
        [InlineData(0.00, "13 00 00 00 00 00 00")]
        // Under the vendor's cut, which lifts no magnitude of 5 in 255 or less.
        [InlineData(0.019, "13 00 00 00 00 00 00")]
        // A level above 1 counts as 1.
        [InlineData(1.50, "11 0A FF 0A 00 1E 00")]
        public void ALevel_BecomesItsFrame(double level, string bytes)
            => Assert.Equal(bytes, Text(TouchSenseProtocol.FrameFor(level)));

        [Fact]
        public void TheShapersNumbers_AreTheOnesTheDesignGives()
        {
            Assert.Equal(0x50, TouchSenseProtocol.MinStrength);
            Assert.Equal(0xAF, TouchSenseProtocol.StrengthSpan);
            Assert.Equal(0.02, TouchSenseProtocol.OffBelow);
            Assert.Equal(10, TouchSenseProtocol.DelayMs);
            Assert.Equal(300, TouchSenseProtocol.HoldMs);
            // A frame ends on its own after 300 ms: 30 pulses 10 ms apart.
            Assert.Equal(30, TouchSenseProtocol.Count);
            Assert.Equal(TouchSenseProtocol.HoldMs, TouchSenseProtocol.Count * TouchSenseProtocol.DelayMs);
            Assert.Equal(100, TouchSenseProtocol.ResendMs);
            Assert.Equal(33, TouchSenseProtocol.MinGapMs);
            Assert.Equal(1000, TouchSenseProtocol.KeepAliveMs);
            // A held level is sent again long before its pulses run out.
            Assert.True(TouchSenseProtocol.ResendMs * 2 < TouchSenseProtocol.HoldMs);
        }

        /// <summary>The vendor's driver sends a magnitude above 5 in 255 as
        /// 0x50 + magnitude x 0xAF / 0xFF, rounded as MulDiv rounds, so no
        /// strength from 0x06 to 0x53 ever reaches the motor (LMouFrc.dll
        /// 0x1000829A to 0x100082B5). Every level the engine can hand over,
        /// one for each 16-bit motor value, gives the same answer.</summary>
        [Fact]
        public void TheStrength_FollowsTheVendorsLift_AndNeverFallsInTheGapBelowIt()
        {
            var seen = new HashSet<byte>();
            for (int motor = 0; motor <= 65535; motor++)
            {
                float level = motor / 65535f;
                byte strength = TouchSenseProtocol.StrengthFor(level);
                seen.Add(strength);
                if (level < TouchSenseProtocol.OffBelow) Assert.Equal(0, strength);
                else Assert.InRange(strength, 0x54, 0xFF);
            }
            Assert.Equal(0, seen.Min());
            Assert.Equal(0xFF, seen.Max());
            Assert.DoesNotContain(seen, s => s >= 0x01 && s <= 0x53);

            // The vendor's own rounding for each magnitude it lifts.
            for (int magnitude = 6; magnitude <= 255; magnitude++)
            {
                int vendor = Math.Min(0xFF, 0x50 + (magnitude * 0xAF + 0xFF / 2) / 0xFF);
                Assert.Equal(vendor, TouchSenseProtocol.StrengthFor(magnitude / 255.0));
            }
            Assert.Equal(0, TouchSenseProtocol.StrengthFor(5 / 255.0));

            // A level that is no number, or below zero, plays nothing.
            Assert.Equal(0, TouchSenseProtocol.StrengthFor(double.NaN));
            Assert.Equal(0, TouchSenseProtocol.StrengthFor(-1));
            Assert.Equal(0xFF, TouchSenseProtocol.StrengthFor(double.PositiveInfinity));
        }

        // ── The input report ──

        [Fact]
        public void AnInputReport_IsButtonsThenXYAndTheWheel()
        {
            Assert.True(TouchSenseProtocol.TryDecodeInput(Hex("01 05 FB 00"), out byte buttons, out sbyte dx, out sbyte dy, out sbyte wheel));
            Assert.Equal((TouchSenseProtocol.ButtonLeft, 5, -5, 0), (buttons, dx, dy, wheel));

            Assert.True(TouchSenseProtocol.TryDecodeInput(Hex("04 00 00 FF"), out buttons, out dx, out dy, out wheel));
            Assert.Equal((TouchSenseProtocol.ButtonMiddle, 0, 0, -1), (buttons, dx, dy, wheel));

            // The two vendor bits Bobruk's driver reads as side and extra (ifeel.c:143-144).
            Assert.True(TouchSenseProtocol.TryDecodeInput(Hex("18 00 00 00"), out buttons, out _, out _, out _));
            Assert.Equal(TouchSenseProtocol.ButtonSide | TouchSenseProtocol.ButtonExtra, buttons);
            Assert.True(TouchSenseProtocol.TryDecodeInput(Hex("02 80 7F 81"), out buttons, out dx, out dy, out wheel));
            Assert.Equal((TouchSenseProtocol.ButtonRight, -128, 127, -127), (buttons, dx, dy, wheel));

            // The bit of each button (ifeel.c:140-144).
            Assert.Equal((0x01, 0x02, 0x04, 0x08, 0x10), (TouchSenseProtocol.ButtonLeft, TouchSenseProtocol.ButtonRight,
                TouchSenseProtocol.ButtonMiddle, TouchSenseProtocol.ButtonSide, TouchSenseProtocol.ButtonExtra));

            // Anything that is not four bytes is no report of this mouse.
            Assert.False(TouchSenseProtocol.TryDecodeInput(Hex("01 05 FB"), out buttons, out dx, out dy, out wheel));
            Assert.Equal((0, 0, 0, 0), (buttons, dx, dy, wheel));
            Assert.False(TouchSenseProtocol.TryDecodeInput(Hex("01 05 FB 00 00"), out _, out _, out _, out _));
            Assert.False(TouchSenseProtocol.TryDecodeInput(ReadOnlySpan<byte>.Empty, out _, out _, out _, out _));
        }

        // ── The descriptor on the wire ──

        [Fact]
        public void TheBuzzReport_IsOneOutputItemOfSevenBytes_WithNoReportId()
        {
            Assert.Equal(74, ReportDescriptor.Length);
            Assert.True(TouchSenseProtocol.DeclaresBuzzReport(ReportDescriptor));

            // A Report ID item anywhere.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "A1 01 A1 02 05 09", "A1 01 85 01 A1 02 05 09")));
            // Eight bytes where the mouse has seven.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "95 07 91 02", "95 08 91 02")));
            // Seven fields of another size.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "09 02 95 07 91 02", "09 02 75 10 95 07 91 02")));
            // No Output item.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "09 02 95 07 91 02", "09 02 95 07")));
            // A second Output item.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "95 07 91 02", "95 07 91 02 95 01 91 01")));
            // The same report as a feature report.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "95 07 91 02", "95 07 B1 02")));
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(BootMouse));
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(ReadOnlySpan<byte>.Empty));
        }

        /// <summary>A descriptor that is cut short, or that no parser
        /// accepts, declares nothing: every truncation of the mouse's own
        /// descriptor short of the Output item's last byte, a pop with no
        /// push, a report size past 256 and an unknown global tag (the
        /// refusals of linux-input drivers/hid/hid-core.c:425-430, 478-485
        /// and 506-508).</summary>
        [Fact]
        public void ADescriptorThatIsCutShortOrMalformed_DeclaresNothing()
        {
            int output = Text(ReportDescriptor).IndexOf("91 02", StringComparison.Ordinal) / 3;
            for (int length = 0; length <= output + 1; length++)
                Assert.False(TouchSenseProtocol.DeclaresBuzzReport(ReportDescriptor.AsSpan(0, length)), "length " + length);
            // From the Output item's last byte on, the two End Collection items add nothing.
            for (int length = output + 2; length <= ReportDescriptor.Length; length++)
                Assert.True(TouchSenseProtocol.DeclaresBuzzReport(ReportDescriptor.AsSpan(0, length)), "length " + length);

            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "A1 01 A1 02 05 09", "A1 01 B4 A1 02 05 09")));
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "09 02 95 07 91 02", "09 02 76 01 01 95 07 91 02")));
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "A1 01 A1 02 05 09", "A1 01 C5 00 A1 02 05 09")));
            // A push and its pop, and a long item, change nothing.
            Assert.True(TouchSenseProtocol.DeclaresBuzzReport(With(ReportDescriptor, "A1 01 A1 02 05 09", "A1 01 A4 B4 FE 02 00 AA BB A1 02 05 09")));
            // A long item that runs past the end.
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(Hex("FE 05 00 01")));
            Assert.False(TouchSenseProtocol.DeclaresBuzzReport(Hex("FE")));
        }

        [Fact]
        public void ThePointerReport_IsTheIFeelMouses_OrIsNotRead()
        {
            Assert.True(TouchSenseProtocol.DeclaresPointerReport(ReportDescriptor));
            // The same report with no buzz report beside it.
            Assert.True(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "A1 02 06 00 FF 09 02 95 07 91 02 C0", "")));

            // A report ID, which would put a byte in front of the buttons.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "A1 01 A1 02 05 09", "A1 01 85 01 A1 02 05 09")));
            // A fourth button, as the MouseMan has: the bits move.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "19 01 29 03 15 00 25 01 75 01 95 03 81 02 95 05",
                "19 01 29 04 15 00 25 01 75 01 95 04 81 02 95 05")));
            // A fourth button in the first vendor bit's place: the rest
            // stays where it is, and the report is still not this mouse's.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "19 01 29 03 15 00 25 01 75 01 95 03 81 02 95 05",
                "19 01 29 04 15 00 25 01 75 01 95 04 81 02 95 04")));
            // The five bits above the buttons under another usage, and as padding.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "95 05 06 00 FF 09 01 81 02", "95 05 06 00 FF 09 03 81 02")));
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "95 05 06 00 FF 09 01 81 02", "95 05 06 00 FF 09 01 81 01")));
            // Y before X.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "09 30 09 31 81 06", "09 31 09 30 81 06")));
            // Absolute axes.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "09 30 09 31 81 06", "09 30 09 31 81 02")));
            // Axes of 16 bits.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "75 08 95 02 09 30", "75 10 95 02 09 30")));
            // No wheel.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "09 38 95 01 81 06", "")));
            // Another usage where the wheel is.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "09 38 95 01 81 06", "09 32 95 01 81 06")));
            // Buttons that are padding.
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "75 01 95 03 81 02 95 05", "75 01 95 03 81 01 95 05")));
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(BootMouse));
            Assert.False(TouchSenseProtocol.DeclaresPointerReport(ReadOnlySpan<byte>.Empty));

            // The buttons named one by one, and the wheel with its page
            // written into a four-byte usage, read the same.
            Assert.True(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "19 01 29 03 15 00", "09 01 09 02 09 03 15 00")));
            Assert.True(TouchSenseProtocol.DeclaresPointerReport(With(ReportDescriptor, "09 38 95 01 81 06", "0B 38 00 01 00 95 01 81 06")));
        }

        [Fact]
        public void TheReportDescriptorsLength_ComesFromTheHidDescriptorOfInterface0()
        {
            Assert.Equal(41, Configuration.Length);
            Assert.Equal(0x4A, TouchSenseProtocol.ReportDescriptorLength(Configuration));

            // No HID descriptor, a HID descriptor that lists no report
            // descriptor, and one that belongs to another interface.
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "09 21 00 01 21 01 22 4A 00 ", "")));
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "09 21 00 01 21 01 22 4A 00", "09 21 00 01 21 01 23 4A 00")));
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "09 04 00 00 02 03 01 02 00", "09 04 01 00 02 03 01 02 00")));
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "09 04 00 00 02 03 01 02 00", "09 04 00 01 02 03 01 02 00")));
            // The report descriptor listed second.
            Assert.Equal(0x4A, TouchSenseProtocol.ReportDescriptorLength(
                With(Configuration, "09 21 00 01 21 01 22 4A 00", "0C 21 00 01 21 02 23 10 00 22 4A 00")));
            // A length of zero, and one past the longest read.
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "22 4A 00", "22 00 00")));
            Assert.Equal(TouchSenseProtocol.MaxReportDescriptor,
                TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "22 4A 00", "22 00 04")));
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(With(Configuration, "22 4A 00", "22 01 04")));

            // Every truncation: the length is there once the HID descriptor is whole.
            int hidEnd = Text(Configuration).IndexOf("22 4A 00", StringComparison.Ordinal) / 3 + 3;
            for (int length = 0; length < hidEnd; length++)
                Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(Configuration.AsSpan(0, length)));
            Assert.Equal(0x4A, TouchSenseProtocol.ReportDescriptorLength(Configuration.AsSpan(0, hidEnd)));
            // A descriptor whose length byte is too small never loops.
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(Hex("00 02 29 00")));
            Assert.Equal(0, TouchSenseProtocol.ReportDescriptorLength(Hex("01 02 29 00")));
        }

        // ── The descriptor as Windows parsed it ──

        private static readonly TouchSenseValueCap BuzzCap = new(0, 0xFF00, 0x0002, 8, 7);

        [Fact]
        public void AMouseCollection_DeclaresTheBuzz_ByOneSevenByteVendorValue()
        {
            static bool Declares(TouchSenseValueCap[] values, ushort page = 1, ushort usage = 2, int output = 8,
                int feature = 0, int buttons = 0)
                => TouchSenseProtocol.MouseCollectionDeclaresBuzz(page, usage, output, feature, buttons, values);
            var buzz = new[] { BuzzCap };

            Assert.True(Declares(buzz));

            // Not a mouse collection.
            Assert.False(Declares(buzz, page: 0xFF00));
            Assert.False(Declares(buzz, usage: 6));
            // Another output report length, with the ID byte Windows adds.
            Assert.False(Declares(buzz, output: 7));
            Assert.False(Declares(buzz, output: 9));
            Assert.False(Declares(buzz, output: 0));
            // A feature report, which the mouse does not have.
            Assert.False(Declares(buzz, feature: 8));
            // An output button beside the value.
            Assert.False(Declares(buzz, buttons: 1));
            // No value, two values, and a value that differs in one field.
            Assert.False(Declares(Array.Empty<TouchSenseValueCap>()));
            Assert.False(Declares(new[] { BuzzCap, BuzzCap }));
            Assert.False(Declares(new[] { BuzzCap with { ReportId = 1 } }));
            Assert.False(Declares(new[] { BuzzCap with { UsagePage = 0xFF01 } }));
            Assert.False(Declares(new[] { BuzzCap with { Usage = 1 } }));
            Assert.False(Declares(new[] { BuzzCap with { BitSize = 16 } }));
            Assert.False(Declares(new[] { BuzzCap with { ReportCount = 8 } }));
            Assert.False(Declares(null));
        }

        // ── The models ──

        [Fact]
        public void TheMice_AreTheThreeTheVendorsDriverServes()
        {
            Assert.Equal(new (ushort, ushort)[] { (0x046D, 0xC030), (0x046D, 0xC032), (0x046D, 0xC031) },
                TouchSenseProtocol.Mice.Select(m => (m.VendorId, m.ProductId)));
            Assert.Equal("Logitech iFeel Mouse", TouchSenseProtocol.NameOf(0x046D, 0xC030));
            Assert.Equal("Logitech iFeel MouseMan", TouchSenseProtocol.NameOf(0x046D, 0xC032));
            Assert.Equal("Logitech iFeel Mouse", TouchSenseProtocol.NameOf(0x046D, 0xC031));
            Assert.True(TouchSenseProtocol.IsMouse(0x046D, 0xC030));
            // The second MouseMan ID has no entry in the vendor's driver,
            // and 046D:C034 is a MouseMan Optical with no motor.
            Assert.False(TouchSenseProtocol.IsMouse(0x046D, 0xC033));
            Assert.False(TouchSenseProtocol.IsMouse(0x046D, 0xC034));
            // The same product ID under another vendor.
            Assert.False(TouchSenseProtocol.IsMouse(0x045E, 0xC030));
            Assert.Null(TouchSenseProtocol.NameOf(0x046D, 0xC54D));
        }
    }
}
