using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PadForge.Common
{
    /// <summary>One language the interface is translated into.</summary>
    public sealed class LanguageInfo
    {
        internal LanguageInfo(string tag, string englishName, string nativeName, bool rightToLeft, bool complete)
        {
            Tag = tag;
            EnglishName = englishName;
            NativeName = nativeName;
            IsRightToLeft = rightToLeft;
            IsComplete = complete;
            Culture = CultureInfo.GetCultureInfo(tag);
        }

        /// <summary>The culture name, and the tag in the file name
        /// Strings.&lt;tag&gt;.resx. English is "en" and has no tag in its
        /// file name.</summary>
        public string Tag { get; }

        public string EnglishName { get; }

        /// <summary>The language's name in itself, as the picker lists it.</summary>
        public string NativeName { get; }

        public bool IsRightToLeft { get; }

        /// <summary>True when every string is translated. A language that
        /// is not complete shows English wherever its translation has no
        /// string: a gap is left where no correct wording was known, and
        /// nothing is made up to fill it.</summary>
        public bool IsComplete { get; }

        public CultureInfo Culture { get; }

        /// <summary>The English name as the picker shows it beside the
        /// language's own, and nothing where the two are the same.</summary>
        public string EnglishNameIfDifferent
            => string.Equals(EnglishName, NativeName, StringComparison.OrdinalIgnoreCase) ? "" : EnglishName;

        public override string ToString() => NativeName;
    }

    /// <summary>
    /// The languages the interface is translated into (#505). There is one
    /// row for each in Resources/Strings/Languages.tsv, beside the
    /// Strings.&lt;tag&gt;.resx it names, and that table is the only list:
    /// the language picker, the tests and the measuring tools all read it.
    ///
    /// <para>Windows and .NET know about half of these tags. The names come
    /// from the table so that every language is listed under its own name,
    /// whatever the PC knows about it.</para>
    /// </summary>
    internal static class LanguageCatalog
    {
        internal const string ResourceName = "PadForge.Languages.tsv";

        private static readonly Lazy<IReadOnlyList<LanguageInfo>> _all = new(Load);

        /// <summary>Every language, in the order the picker lists them: by
        /// own name, Latin first and then script by script.</summary>
        internal static IReadOnlyList<LanguageInfo> All => _all.Value;

        internal static LanguageInfo English => Find("en");

        internal static LanguageInfo Find(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (LanguageInfo language in All)
                if (string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase)) return language;
            return null;
        }

        /// <summary>The language a culture is shown in: its own row, else
        /// the nearest parent that has one (de-AT reads de, zh-TW reads
        /// zh-Hant). Null when no row answers, which is English.</summary>
        internal static LanguageInfo Resolve(CultureInfo culture)
        {
            // The walk ends at the invariant culture, whose name is empty.
            CultureInfo c = culture;
            while (c != null && c.Name.Length > 0)
            {
                LanguageInfo hit = Find(c.Name) ?? Find(Alias(c.Name));
                if (hit != null) return hit;
                c = c.Parent;
            }
            return null;
        }

        /// <summary>Where a culture's own parents never reach the row its
        /// speakers read. Portuguese outside Brazil reads Portugal's, bare
        /// Chinese reads Simplified, Windows calls Tagalog fil and Central
        /// Kurdish ku-Arab.</summary>
        private static string Alias(string name)
        {
            switch (name)
            {
                case "pt": return "pt-PT";
                case "zh": return "zh-Hans";
                case "nb":
                case "nn": return "no";
                case "tl": return "fil";
                case "ku-Arab": return "ckb";
                default: return null;
            }
        }

        /// <summary>True when the interface reads right to left under this
        /// culture. Only a language in the table can say so: Hebrew Windows
        /// with no Hebrew translation shows English, left to right.</summary>
        internal static bool IsRightToLeft(CultureInfo culture) => Resolve(culture)?.IsRightToLeft ?? false;

        private static IReadOnlyList<LanguageInfo> Load()
        {
            var rows = new List<LanguageInfo>();
            using (Stream stream = typeof(LanguageCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("The language table " + ResourceName + " is not in the assembly.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] cells = line.Split('\t');
                    rows.Add(new LanguageInfo(cells[0], cells[1], cells[2], cells[3] == "rtl", cells[5] == "full"));
                }
            }
            CompareInfo invariant = CultureInfo.InvariantCulture.CompareInfo;
            rows.Sort((a, b) => invariant.Compare(a.NativeName, b.NativeName,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));
            return rows;
        }
    }
}
