using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// American and British English each follow their own conventions (#505).
    /// Strings.resx is American English, as the Microsoft Writing Style Guide
    /// writes it. Strings.en-GB.resx holds only the strings British English
    /// writes differently, as Microsoft's English (UK) localization style
    /// guide sets them out: -ise and -our spellings, no comma before the last
    /// item of a list, full stops outside quotation marks. Every other string
    /// reads the American text, which is the same in both.
    ///
    /// <para>tools/l10n/en-GB-spelling.tsv pairs American spellings with
    /// British ones. The British interface carries no word of the first
    /// column and the American one no word of the second, outside the names
    /// that keep their owner's spelling.</para>
    /// </summary>
    public class BritishEnglishTests
    {
        /// <summary>Names that keep their American spelling in British
        /// English: an algorithm, a product and a project.</summary>
        private static readonly string[] KeptNames = { "$Q Recognizer", "Wooting Analog SDK", "razer-analog-keyboard" };

        /// <summary>British forms that are American words too: the plural
        /// nouns of analysis and paralysis.</summary>
        private static readonly HashSet<string> SharedForms = new(StringComparer.Ordinal) { "analyses", "paralyses" };

        private static readonly Regex Word = new(@"(?<![A-Za-z0-9_$])[A-Za-z]+(?![A-Za-z0-9_])", RegexOptions.Compiled);
        private static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);

        [Fact]
        public void BritishEnglishHasARowAndAFileOfItsOwn()
        {
            LanguageInfo british = LanguageCatalog.Find("en-GB");
            Assert.NotNull(british);
            Assert.Equal("English (United Kingdom)", british.NativeName);
            Assert.True(british.IsComplete);
            Assert.True(LocaleFiles.IsOtherEnglish(british));
            Assert.False(LocaleFiles.IsOtherEnglish(LanguageCatalog.English));
            var own = Values("Strings.en-GB.resx");
            Assert.True(own.Count > 200, "only " + own.Count + " British strings");
            Assert.True(own.Count < Values("Strings.resx").Count / 4, "British English holds only what it writes differently");
        }

        [Fact]
        public void TheBritishInterfaceCarriesNoAmericanSpelling()
        {
            var pairs = Pairs();
            var british = Values("Strings.en-GB.resx");
            var problems = new List<string>();
            foreach (var (key, american) in Values("Strings.resx"))
            {
                string shown = british.TryGetValue(key, out string own) ? own : american;
                foreach (string word in Words(shown))
                    if (pairs.TryGetValue(word.ToLowerInvariant(), out string form))
                        problems.Add(key + ": " + word + ", British " + form);
            }
            Assert.True(problems.Count == 0, problems.Count + " American spelling(s) in British English:\n" + string.Join("\n", problems.Take(40)));
        }

        [Fact]
        public void TheAmericanInterfaceCarriesNoBritishSpelling()
        {
            var britishForms = Pairs().Values.Where(f => !SharedForms.Contains(f)).ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var (key, american) in Values("Strings.resx"))
                foreach (string word in Words(american))
                    if (britishForms.Contains(word.ToLowerInvariant()))
                        problems.Add(key + ": " + word);
            Assert.True(problems.Count == 0, problems.Count + " British spelling(s) in American English:\n" + string.Join("\n", problems.Take(40)));
        }

        /// <summary>Both checks find the spelling they look for, and pass
        /// the names that keep theirs.</summary>
        [Fact]
        public void TheSpellingChecksSeparateTheTwoEnglishes()
        {
            var pairs = Pairs();
            Assert.Equal("colour", pairs["color"]);
            Assert.Equal("centre", pairs["center"]);
            Assert.Equal("customise", pairs["customize"]);
            Assert.Equal("analogue", pairs["analog"]);
            var words = Words("Pick a Color, then {0:F2} centered.");
            Assert.Contains("Color", words);
            Assert.Contains("centered", words);
            Assert.DoesNotContain("F", words);
            // Names and identifiers keep their spelling.
            words = Words("The Wooting Analog SDK, $Q Recognizer and ShapeRecognizer read the AnalogKeys project.");
            Assert.DoesNotContain("Analog", words);
            Assert.DoesNotContain("Recognizer", words);
            Assert.DoesNotContain("ShapeRecognizer", words);
            Assert.DoesNotContain("AnalogKeys", words);
            Assert.Contains("project", words);
        }

        /// <summary>The words of a string as its reader sees them, without
        /// placeholders, kept names or camel-case identifiers.</summary>
        private static List<string> Words(string text)
        {
            string bare = Placeholder.Replace(text, " ");
            foreach (string name in KeptNames.Concat(Names()))
                if (name.Length > 2 && bare.Contains(name, StringComparison.Ordinal))
                    bare = bare.Replace(name, " ");
            return Word.Matches(bare).Select(m => m.Value)
                .Where(w => !w.Skip(1).Any(char.IsUpper))
                .ToList();
        }

        private static Dictionary<string, string> Pairs()
            => Lines(Path.Combine(RepoRoot(), "tools", "l10n", "en-GB-spelling.tsv"))
                .Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        private static readonly Lazy<string[]> NameList = new(() => Lines(Path.Combine(RepoRoot(), "tools", "l10n", "names.txt")).ToArray());

        private static string[] Names() => NameList.Value;

        private static IEnumerable<string> Lines(string path)
            => File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#');

        private static Dictionary<string, string> Values(string file)
            => XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings", file)).Root.Elements("data")
                .Where(d => d.Attribute("type") == null)
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");

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
