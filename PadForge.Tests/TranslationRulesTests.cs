using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The rules every translation follows, checked on each file in the tree
    /// (#505). They hold the parts of a string that the program and the user
    /// depend on in any language: the placeholders the code fills, the line
    /// breaks, the names that stay in Latin letters, the digits, and the
    /// decimal numbers, which the number boxes show and read with a period in
    /// every language. A box reads a typed 1,0 as 10, so a tooltip that writes
    /// a value as 1,0 sends the user to the wrong one.
    ///
    /// <para>Six of the shipped translations wrote some of those values with a
    /// comma, four dropped "HID descriptor" from one tooltip, and German and
    /// Dutch put a possessive s on product names. These tests hold the
    /// corrections.</para>
    /// </summary>
    public class TranslationRulesTests
    {
        public static IEnumerable<object[]> Translations() =>
            LanguageCatalog.All.Where(l => l != LanguageCatalog.English).Select(l => new object[] { l.Tag });

        private static readonly Regex Numbered = new(@"\{\d+(?::[^{}]*)?\}", RegexOptions.Compiled);
        private static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);
        // A decimal number in running text, not part of a version or an address.
        private static readonly Regex DecimalNumber = new(@"(?<![\w.])\d+\.\d+(?![\d.])", RegexOptions.Compiled);

        // Characters a string never carries: format controls, direction marks,
        // variation selectors, fillers and every space but the plain one.
        private static readonly HashSet<int> Invisible = new()
        {
            0x00AD, 0x034F, 0x061C, 0x115F, 0x1160, 0x180E, 0x200B, 0x200E, 0x200F, 0x2060, 0x2061, 0x2062,
            0x2063, 0x2064, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069, 0x3164,
            0xFEFF, 0xFFA0, 0xFFF9, 0xFFFA, 0xFFFB, 0xFE00, 0xFE01, 0xFE02, 0xFE03, 0xFE04, 0xFE05, 0xFE06,
            0xFE07, 0xFE08, 0xFE09, 0xFE0A, 0xFE0B, 0xFE0C, 0xFE0D, 0xFE0E, 0xFE0F,
            0x00A0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009,
            0x200A, 0x202F, 0x205F, 0x3000,
        };

        private const char ZeroWidthNonJoiner = (char)0x200C;
        private const char ZeroWidthJoiner = (char)0x200D;
        private const char NoBreakSpace = (char)0x00A0;
        private const char ArabicIndicTwo = (char)0x0662;

        private static readonly Lazy<string[]> Names = new(() => File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "l10n", "names.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToArray());

        private static readonly Lazy<Dictionary<string, string>> English = new(() => Values("Strings.resx"));

        // The names each English string carries, worked out once for all the languages.
        private static readonly Dictionary<string, string[]> NamesBySource = new(StringComparer.Ordinal);

        [Theory]
        [MemberData(nameof(Translations))]
        public void EveryStringKeepsWhatTheProgramAndTheUserDependOn(string tag)
        {
            var problems = new List<string>();
            foreach (var (key, text) in Values("Strings." + tag + ".resx"))
            {
                // A key the English does not have is LocaleParityTests' to report.
                if (!English.Value.TryGetValue(key, out string source)) continue;
                problems.AddRange(Check(key, source, text));
            }
            Assert.True(problems.Count == 0,
                tag + " breaks the translation rules " + problems.Count + " time(s):\n" + string.Join("\n", problems.Take(40)));
        }

        /// <summary>Each way a translated string can break the rules, as a
        /// line naming the key.</summary>
        internal static IEnumerable<string> Check(string key, string source, string text)
        {
            var want = new HashSet<string>(Numbered.Matches(source).Select(m => m.Value));
            var got = new HashSet<string>(Numbered.Matches(text).Select(m => m.Value));
            if (!want.SetEquals(got))
                yield return key + ": placeholders " + string.Join(" ", want.OrderBy(p => p)) + " became " + string.Join(" ", got.OrderBy(p => p));
            if (Stray(source) != Stray(text))
                yield return key + ": a brace outside a placeholder";
            if (source.Count(c => c == '\n') != text.Count(c => c == '\n'))
                yield return key + ": the English has " + source.Count(c => c == '\n') + " line breaks, the translation " + text.Count(c => c == '\n');

            string bareSource = Placeholder.Replace(source, " ");
            string bareText = Placeholder.Replace(text, " ");
            foreach (Match m in DecimalNumber.Matches(bareSource))
                if (!HasNumber(bareText, m.Value))
                    yield return key + ": " + m.Value + " is not written as the English writes it";

            // Outside the placeholders: the F2 of {0:F2} is a format, not the key.
            foreach (string name in NamesIn(bareSource))
                if (!NameIn(bareText, name))
                    yield return key + ": the name " + name + " is missing or changed";

            int at = 0;
            while (at < text.Length)
            {
                char c = text[at];
                if (c == ZeroWidthNonJoiner || c == ZeroWidthJoiner)
                {
                    // A joiner spells a word in Persian, Kurdish, Urdu and the
                    // Indic scripts, between two of the word's own letters.
                    bool letters = at > 0 && at + 1 < text.Length && Joinable(text[at - 1]) && Joinable(text[at + 1]);
                    if (!letters) yield return key + ": U+" + ((int)c).ToString("X4") + " outside a word";
                }
                else if (Invisible.Contains(c))
                    yield return key + ": U+" + ((int)c).ToString("X4") + " in the text";
                else if (char.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber && (c < '0' || c > '9'))
                    yield return key + ": the digit U+" + ((int)c).ToString("X4");
                else if (char.IsControl(c) && c != '\n')
                    yield return key + ": the control character U+" + ((int)c).ToString("X4");
                at++;
            }
        }

        /// <summary>Every rule above reports a string that breaks it, and none
        /// reports the string that keeps them all. A check that only ever
        /// says zero has not been shown to work.</summary>
        [Fact]
        public void TheRulesCatchEachKindOfDefect()
        {
            const string source = "Sensitivity {0:F2} for {1}. 1.0 is the default, in PadForge.\nSee POV.";
            Assert.Empty(Check("K", source, "Sensibilité {0:F2} pour {1}. 1.0 par défaut, dans PadForge.\nVoir les POVs."));
            Assert.Empty(Check("K", source, "PadForge-ის {1} {0:F2}. 1.0 ნაგულისხმევია.\nPOV."));

            Assert.Single(Check("K", source, "Sensibilité {0} pour {1}. 1.0 par défaut, dans PadForge.\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1}. 1,0 par défaut, dans PadForge.\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1}. 1.0 par défaut, dans PadForge. Voir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1}. 1.0 par défaut, dans PadForges.\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1}. 1.0 par" + NoBreakSpace + "défaut, dans PadForge.\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1}. 1.0 par défaut, dans PadForge " + ArabicIndicTwo + ".\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité {0:F2} pour {1} }. 1.0 par défaut, dans PadForge.\nVoir POV."));
            Assert.Single(Check("K", source, "Sensibilité" + ZeroWidthNonJoiner + " {0:F2} pour {1}. 1.0 par défaut, dans PadForge.\nVoir POV."));
            // Between two letters of a word a joiner is spelling, and allowed.
            Assert.Empty(Check("K", "Do it", "می" + ZeroWidthNonJoiner + "شود"));
        }

        private static int Stray(string text)
        {
            string rest = Placeholder.Replace(text, "");
            return rest.Count(c => c == '{' || c == '}');
        }

        private static bool Joinable(char c)
        {
            UnicodeCategory category = char.GetUnicodeCategory(c);
            return char.IsLetter(c) || category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark;
        }

        /// <summary>True when the number stands in the text as a number of its
        /// own, whatever letters follow it.</summary>
        private static bool HasNumber(string text, string number)
            => Regex.IsMatch(text, @"(?<![\d.,])" + Regex.Escape(number) + @"(?![\d]|[.,]\d)");

        private static string[] NamesIn(string source)
        {
            lock (NamesBySource)
            {
                if (!NamesBySource.TryGetValue(source, out string[] found))
                {
                    found = Names.Value.Where(n => source.Contains(n, StringComparison.Ordinal) && NameIn(source, n)).ToArray();
                    NamesBySource[source] = found;
                }
                return found;
            }
        }

        /// <summary>The name stands on its own: no Latin letter or digit runs
        /// into it. A suffix after a hyphen or an apostrophe is the language's
        /// own, and an acronym may take a plural s.</summary>
        private static bool NameIn(string text, string name)
        {
            char last = name[name.Length - 1];
            string plural = char.IsUpper(last) || char.IsDigit(last) ? "s?" : "";
            return Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(name) + plural + @"(?![A-Za-z0-9])");
        }

        private static Dictionary<string, string> Values(string file)
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings", file));
            return doc.Root.Elements("data")
                .Where(d => d.Attribute("type") == null)
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
