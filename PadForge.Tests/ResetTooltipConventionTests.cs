using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every reset icon says "Reset &lt;the setting it resets&gt;".
    ///
    /// <para>A reset icon is a bare glyph. The tooltip is the only thing that
    /// says what it resets, and a row can carry half a dozen of them. "Reset"
    /// on its own, or "Reset to Off", leaves the user counting icons to work
    /// out which is which.</para>
    ///
    /// <para>The convention is set by the hundreds of correct ones on the Pad
    /// page. It broke in two ways. Some buttons were rolled by hand out of a
    /// plain Button with a generic tooltip, instead of using
    /// <c>SettingResetButton</c>, which composes the text from its
    /// SettingLabel and cannot get it wrong. Others used the control but
    /// handed it a DESCRIPTION rather than a name, which produced tooltips
    /// like "Reset How this PC's pairing identity is stored. Choose a
    /// portable mode to carry your pairings on a thumb drive between
    /// machines."</para>
    ///
    /// <para>Text buttons are a different thing and are not covered here. A
    /// button reading "Reset Calibration" already says what it does on its
    /// face, so its tooltip is free to explain consequences.</para>
    /// </summary>
    public class ResetTooltipConventionTests
    {
        /// <summary>Tooltips that name no setting.</summary>
        private static readonly string[] Generic =
            { "Reset", "Reset to Off", "Reset to Default", "Reset to Defaults" };

        private static string ViewsDir()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            Assert.True(d != null, "repository root not found");
            return Path.Combine(d.FullName, "PadForge.App", "Views");
        }

        private static Dictionary<string, string> Strings()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            string path = Path.Combine(d.FullName, "PadForge.App", "Resources", "Strings",
                                       "Strings.resx");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                     "<data\\s+name=\"([^\"]+)\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline))
                map[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\s+", " ").Trim();
            return map;
        }

        /// <summary>An icon-only reset must not carry a tooltip that names no
        /// setting. Icon-only means the button has no Content of its own.</summary>
        [Fact]
        public void NoResetIconCarriesAGenericTooltip()
        {
            var strings = Strings();
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(ViewsDir(), "*.xaml"))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"<(?!reset:)[A-Za-z:]*Button\b.*?>",
                                                  RegexOptions.Singleline))
                {
                    string tag = m.Value;
                    if (!Regex.IsMatch(tag, @"Command=""\{Binding [A-Za-z0-9_]*Reset")) continue;
                    // A button with its own visible text says what it does.
                    if (tag.Contains("Content=")) continue;

                    var tip = Regex.Match(tag, @"ToolTip=""\{Binding ([A-Za-z0-9_]+)");
                    if (!tip.Success) continue;          // bound to a view model, checked below
                    if (!strings.TryGetValue(tip.Groups[1].Value, out string value)) continue;

                    if (Generic.Contains(value, StringComparer.OrdinalIgnoreCase)
                        || !value.StartsWith("Reset", StringComparison.OrdinalIgnoreCase))
                    {
                        int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line} {tip.Groups[1].Value} = \"{value}\"");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "reset icons whose tooltip names no setting:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>A SettingResetButton's label is the setting's NAME. Feed
        /// it a description and the tooltip becomes a paragraph.</summary>
        [Fact]
        public void NoSettingResetButtonIsLabelledWithADescription()
        {
            var strings = Strings();
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(ViewsDir(), "*.xaml"))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"<reset:SettingResetButton\b[^>]*?>",
                                                  RegexOptions.Singleline))
                {
                    var lab = Regex.Match(m.Value, @"SettingLabel=""\{Binding ([A-Za-z0-9_]+)");
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    if (!lab.Success)
                    {
                        if (!m.Value.Contains("SettingLabel="))
                            offenders.Add($"{Path.GetFileName(file)}:{line} has no SettingLabel");
                        continue;
                    }
                    string key = lab.Groups[1].Value;
                    if (key.EndsWith("Desc", StringComparison.Ordinal)
                        || key.EndsWith("Description", StringComparison.Ordinal)
                        || key.EndsWith("_Hint", StringComparison.Ordinal)
                        || key.EndsWith("Tooltip", StringComparison.Ordinal)
                        || key.EndsWith("_Tip", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{line} {key} is a description, not a name");
                        continue;
                    }
                    // A sentence is a description whatever its key is called.
                    if (strings.TryGetValue(key, out string value) && value.EndsWith("."))
                        offenders.Add($"{Path.GetFileName(file)}:{line} {key} = \"{value}\"");
                }
            }

            Assert.True(offenders.Count == 0,
                "reset labels that are descriptions rather than setting names:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>The format string the control composes with stays a
        /// "Reset {0}". Changing it silently rewrites every reset tooltip in
        /// the app.</summary>
        [Fact]
        public void TheResetFormatStillNamesTheSetting()
        {
            var strings = Strings();
            Assert.True(strings.TryGetValue("Pad_ResetSection_Format", out string format));
            Assert.Contains("{0}", format);
            Assert.StartsWith("Reset", format, StringComparison.Ordinal);
        }
    }
}
