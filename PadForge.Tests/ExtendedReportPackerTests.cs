using System;
using System.IO;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// An Extended slot advertises up to 8 axes, 4 hats and 128 buttons, and
    /// the wire carried far less: the descriptor emitted one hat whatever the
    /// POV count, the fixed gamepad state holds 32 buttons in a uint and one
    /// hat, and the profile names only a left and a right trigger so a third
    /// and fourth were built, shown in the grid, and never written. A
    /// trigger-heavy layout exhausted the SDK's four-usage trigger pool and
    /// threw, and the catch ran the catalog profile instead.
    ///
    /// <para>The layout the packer writes against is not a guess. PadForge
    /// emits this descriptor itself, in the order these tests assert.</para>
    /// </summary>
    public class ExtendedReportPackerTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static CustomControllerLayout Layout(int sticks, int triggers, int povs, int buttons)
            => new CustomControllerLayout
            {
                Sticks = sticks,
                Triggers = triggers,
                Povs = povs,
                Buttons = buttons,
                Axes = sticks * 2 + triggers,
            };

        private static RawHidState State(int axes, int buttons, int povs)
            => RawHidState.Create(axes, buttons, povs);

        /// <summary>Eight 16-bit axes are 16 bytes, one hat byte per POV, one
        /// bit per button rounded up to the byte the report ends on.</summary>
        [Fact]
        public void TheReportSizeIsTheDeclaredFieldsRoundedToABoundary()
        {
            // 8 axes = 16, 4 hats = 4, 64 buttons = 8.
            Assert.Equal(28, ExtendedReportPacker.ReportSize(Layout(2, 4, 4, 64)));
            // The same layout with one hat, which is all the descriptor used
            // to emit whatever the POV count.
            Assert.Equal(25, ExtendedReportPacker.ReportSize(Layout(2, 4, 1, 64)));
            // Four sticks spend the same eight axes with no triggers.
            Assert.Equal(28, ExtendedReportPacker.ReportSize(Layout(4, 0, 4, 64)));
            // A button count that does not fill its last byte still ends on one.
            Assert.Equal(5, ExtendedReportPacker.ReportSize(Layout(1, 0, 0, 1)));
            Assert.Equal(0, ExtendedReportPacker.ReportSize(Layout(0, 0, 0, 0)));
        }

        /// <summary>Every layout the slot can express fits the submit buffer,
        /// so the packer can never be the thing that truncates.</summary>
        [Fact]
        public void EveryLegalLayoutFitsTheSubmitBuffer()
        {
            for (int sticks = 0; sticks <= 4; sticks++)
            {
                int maxTriggers = 8 - sticks * 2;
                for (int triggers = 0; triggers <= maxTriggers; triggers++)
                {
                    int size = ExtendedReportPacker.ReportSize(Layout(sticks, triggers, 4, 128));
                    Assert.True(size <= 64, $"{sticks}/{triggers} packs to {size}");
                    Assert.True(size <= ExtendedReportPacker.MaxReportSize);
                }
            }
        }

        /// <summary>Axes are 16-bit unsigned, little-endian, in declaration
        /// order. The raw surface rests a trigger at the low end and a stick at
        /// center, and one shift carries both.</summary>
        [Fact]
        public void AxesLandInDeclarationOrderAsUnsignedLittleEndian()
        {
            var layout = Layout(2, 2, 0, 8);
            var raw = State(8, 8, 0);
            // Interleaved raw order: stick 0 at axes 0 and 1, trigger 0 at 2,
            // stick 1 at 3 and 4, trigger 1 at 5.
            raw.Axes[0] = short.MaxValue;   // stick 0 X hard right
            raw.Axes[1] = 0;                // stick 0 Y centered
            raw.Axes[2] = short.MinValue;   // trigger 0 released
            raw.Axes[3] = short.MinValue;   // stick 1 X hard left
            raw.Axes[5] = short.MaxValue;   // trigger 1 fully pulled

            var dest = new byte[ExtendedReportPacker.MaxReportSize];
            int len = ExtendedReportPacker.Pack(raw, layout, dest);
            Assert.Equal(13, len);          // 6 axes = 12 bytes, 8 buttons = 1

            Assert.Equal(65535, dest[0] | (dest[1] << 8));   // stick 0 X
            Assert.Equal(32768, dest[2] | (dest[3] << 8));   // stick 0 Y
            Assert.Equal(0, dest[4] | (dest[5] << 8));       // stick 1 X
            Assert.Equal(32768, dest[6] | (dest[7] << 8));   // stick 1 Y
            Assert.Equal(0, dest[8] | (dest[9] << 8));       // trigger 0
            Assert.Equal(65535, dest[10] | (dest[11] << 8)); // trigger 1
        }

        /// <summary>Every declared hat gets its own byte. Three of a four-hat
        /// layout had no field at all.</summary>
        [Fact]
        public void EveryDeclaredHatGetsItsOwnByte()
        {
            var layout = Layout(0, 0, 4, 0);
            var raw = State(0, 0, 4);
            raw.Povs[0] = 0;        // north
            raw.Povs[1] = 9000;     // east
            raw.Povs[2] = 27000;    // west
            raw.Povs[3] = -1;       // centered

            var dest = new byte[ExtendedReportPacker.MaxReportSize];
            int len = ExtendedReportPacker.Pack(raw, layout, dest);

            Assert.Equal(4, len);
            Assert.Equal(0, dest[0]);
            Assert.Equal(2, dest[1]);
            Assert.Equal(6, dest[2]);
            Assert.Equal(8, dest[3]);   // the null the logical range leaves free
        }

        /// <summary>Buttons past the 32nd reach the wire. The fixed state holds
        /// them in a uint, which is where they used to stop.</summary>
        [Fact]
        public void ButtonsPastTheThirtySecondReachTheWire()
        {
            var layout = Layout(0, 0, 0, 128);
            var raw = State(0, 128, 0);
            raw.SetButton(0, true);
            raw.SetButton(31, true);
            raw.SetButton(32, true);
            raw.SetButton(64, true);
            raw.SetButton(127, true);

            var dest = new byte[ExtendedReportPacker.MaxReportSize];
            int len = ExtendedReportPacker.Pack(raw, layout, dest);

            Assert.Equal(16, len);
            Assert.Equal(0x01, dest[0]);        // button 0
            Assert.Equal(0x80, dest[3]);        // button 31
            Assert.Equal(0x01, dest[4]);        // button 32
            Assert.Equal(0x01, dest[8]);        // button 64
            Assert.Equal(0x80, dest[15]);       // button 127
        }

        /// <summary>The buttons start after the axes and the hats, not at byte
        /// zero. Off by one here is silent on the wire.</summary>
        [Fact]
        public void ButtonsStartAfterTheAxesAndHats()
        {
            var layout = Layout(1, 1, 2, 8);
            var raw = State(3, 8, 2);
            raw.SetButton(0, true);

            var dest = new byte[ExtendedReportPacker.MaxReportSize];
            int len = ExtendedReportPacker.Pack(raw, layout, dest);

            // 1 stick = 4 bytes, 1 trigger = 2, 2 hats = 2, 8 buttons = 1.
            Assert.Equal(9, len);
            Assert.Equal(0x01, dest[8]);
            Assert.Equal(0x00, dest[7]);
        }

        /// <summary>The raw path is taken only when the fixed state cannot
        /// carry the layout, so the proven path keeps every ordinary
        /// slot.</summary>
        [Fact]
        public void TheRawPathIsTakenOnlyWhenTheFixedStateCannotCarryIt()
        {
            Assert.False(ExtendedReportPacker.NeedsRawReport(Layout(2, 2, 1, 32)));
            Assert.False(ExtendedReportPacker.NeedsRawReport(Layout(2, 2, 0, 11)));
            Assert.True(ExtendedReportPacker.NeedsRawReport(Layout(2, 2, 1, 33)));
            Assert.True(ExtendedReportPacker.NeedsRawReport(Layout(2, 2, 2, 8)));
        }

        /// <summary>The descriptor emits one hat per declared POV. A single
        /// call was the whole POV defect.</summary>
        [Fact]
        public void TheDescriptorEmitsOneHatPerDeclaredPov()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));

            Assert.Contains("for (int h = 0; h < povs; h++)\r\n                                descBuilder.AddHat();", src);
            Assert.DoesNotContain("if (povs > 0)\r\n                                descBuilder.AddHat();", src);
        }

        /// <summary>Triggers past the SDK's four-usage pool take their own
        /// axes instead of throwing the whole build into the catalog-profile
        /// fallback.</summary>
        [Fact]
        public void TriggersPastThePoolTakeTheirOwnAxes()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));

            Assert.Contains("int pooledTriggers = System.Math.Min(triggers,", src);
            Assert.Contains("descBuilder.AddAxis(ExtraTriggerAxis(t - pooledTriggers), 16, 0, 65535);", src);
            // The extras sit outside every usage the SDK's classifier names, so
            // one cannot take a trigger role from a real trigger.
            Assert.Contains("HIDMaestro.HMAxis.Vbrx", src);
            Assert.Contains("HIDMaestro.HMAxis.Vno", src);
        }

        /// <summary>On the fixed path a trigger past the profile's named pair
        /// is written through the usage the descriptor gave it.</summary>
        [Fact]
        public void TheFixedPathWritesEveryTriggerItDeclared()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "HMaestroVirtualController.cs"));

            Assert.Contains("DeclaredAxisAt(sticks, triggers, t)", src);
            Assert.DoesNotContain("int triggersToWrite = System.Math.Min(triggers, profileTriggers.Count);", src);
        }
    }
}
