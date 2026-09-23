using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard-and-mouse preview's wheel is both a live surface (the middle
    /// button) and a recording flash surface. The per-frame repaint must spare
    /// every flash target that paints the wheel, or it erases the flash the
    /// frame after it is drawn. Horizontal scroll was added to the flash and
    /// never to the repaint guard, so its flash lasted one frame.
    /// </summary>
    public class KbmPreviewFlashGuardTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void TheRepaintSparesEveryTargetWhoseFlashPaintsTheWheel()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "KBMPreviewView.xaml.cs"));
            string[] lines = src.Split('\n');

            // Every flash branch that paints the wheel, by the target it tests.
            var flashTargets = new HashSet<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("_scrollWheelPill.Fill = highlight")) continue;
                for (int j = i; j >= 0; j--)
                {
                    if (!lines[j].Contains("if (_flashTarget ==")) continue;
                    foreach (Match m in Regex.Matches(lines[j], "_flashTarget == \"(\\w+)\""))
                        flashTargets.Add(m.Groups[1].Value);
                    break;
                }
            }
            // The collector itself works: it finds the button and both scroll pairs.
            Assert.Contains("KbmMBtn2", flashTargets);
            Assert.Contains("KbmScroll", flashTargets);
            Assert.Contains("KbmScrollH", flashTargets);

            // The guard in front of the live repaint of the wheel.
            int repaint = src.IndexOf("_scrollWheelPill.Fill = p ?", StringComparison.Ordinal);
            Assert.True(repaint > 0);
            int guardStart = src.LastIndexOf("if ((_flashTarget !=", repaint, StringComparison.Ordinal);
            Assert.True(guardStart > 0);
            string guard = src.Substring(guardStart, repaint - guardStart);
            var spared = Regex.Matches(guard, "_flashTarget != \"(\\w+)\"")
                .Select(m => m.Groups[1].Value).ToHashSet();

            Assert.Empty(flashTargets.Except(spared));
        }
    }
}
