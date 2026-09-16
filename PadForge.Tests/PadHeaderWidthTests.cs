using System;
using System.IO;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pad page's first tier could not fit at the supported minimum window
    /// width, and not only there. Measured with tools/rowmeasure against the
    /// real controls, the real styles and the app font, worst locale:
    ///
    /// <para>scope label 74.0 + identity chip 190.0 + preset chip 516.7 + six
    /// tabs 528.0 = 1316.7. At the 900 minimum with the navigation pane open
    /// (244 wide) the tier has 648. The left cluster alone takes 788.7, so the
    /// right-docked tab strip was handed a negative width and clipped away
    /// entirely. At the shipped default window of 1100 the tier has 848
    /// against the same 1316.7, so the tabs were already losing width at the
    /// size the app opens at.</para>
    /// </summary>
    public class PadHeaderWidthTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string PadPage() => File.ReadAllText(Path.Combine(
            RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));

        /// <summary>The tier wraps. A panel that cannot wrap gives the last
        /// child whatever is left, which was nothing.</summary>
        [Fact]
        public void TheFirstTierWraps()
        {
            string xaml = PadPage();

            Assert.Contains("<WrapPanel Orientation=\"Horizontal\" VerticalAlignment=\"Center\">", xaml);
            Assert.DoesNotContain("<DockPanel LastChildFill=\"False\" VerticalAlignment=\"Center\">", xaml);
        }

        /// <summary>The tab strip is an ordinary wrapped child now. Left
        /// docked to the right edge it kept its position and lost its
        /// width.</summary>
        [Fact]
        public void TheTabStripNoLongerDocksRight()
        {
            Assert.DoesNotContain("<StackPanel DockPanel.Dock=\"Right\" Orientation=\"Horizontal\">", PadPage());
        }

        /// <summary>The device selector had a floor and no ceiling in an Auto
        /// column, so one long device name grew the column without bound and
        /// took the width from the device tabs wrapping beside it.</summary>
        [Fact]
        public void TheDeviceSelectorHasACeilingAndAFloor()
        {
            Assert.Contains("MinWidth=\"200\" MaxWidth=\"320\" Margin=\"4,2\" VerticalAlignment=\"Center\">",
                PadPage());
        }

        /// <summary>The measurement harness carries the tier, so the next
        /// change to it can be measured rather than estimated.</summary>
        [Fact]
        public void TheHarnessMeasuresTheTier()
        {
            string prog = File.ReadAllText(Path.Combine(
                RepoRoot(), "tools", "rowmeasure", "Program.cs"));

            Assert.Contains("BuildPadHeader();", prog);
            Assert.Contains("hdr-scope", prog);
            Assert.Contains("hdr-identity", prog);
            Assert.Contains("hdr-tab-part", prog);
            Assert.Contains("Pad page first tier", prog);
        }
    }
}
