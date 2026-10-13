using System.Collections.Generic;
using System.Linq;
using PadForge.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// The translations' file names, for the tests that read the .resx
    /// sources. They come from the language table (#505). Seven test files
    /// each spelled out the ten names before, and a language added to the
    /// tree was read by none of them.
    ///
    /// <para>A test that requires a key, or a fact about its text, in every
    /// file reads the whole files: Strings.resx and every complete
    /// translation into another language. A partial translation shows English
    /// for what it leaves out, and another variety of English (en-GB) holds
    /// only the strings it writes differently, so for both Strings.resx
    /// answers for what the file does not hold.</para>
    /// </summary>
    internal static class LocaleFiles
    {
        internal static string FileOf(LanguageInfo language)
            => language == LanguageCatalog.English ? "Strings.resx" : "Strings." + language.Tag + ".resx";

        /// <summary>The file a tag reads, with "en" and "en-US" both naming
        /// Strings.resx.</summary>
        internal static string FileOfTag(string tag)
            => string.Equals(tag, "en", System.StringComparison.OrdinalIgnoreCase) ? "Strings.resx" : FileOf(LanguageCatalog.Find(tag));

        /// <summary>True for a variety of English other than the American
        /// English of Strings.resx.</summary>
        internal static bool IsOtherEnglish(LanguageInfo language)
            => language != LanguageCatalog.English && language.Culture.TwoLetterISOLanguageName == "en";

        /// <summary>American English and every complete translation into
        /// another language: the files that hold every string.</summary>
        internal static IEnumerable<LanguageInfo> Whole
            => LanguageCatalog.All.Where(l => l == LanguageCatalog.English || (l.IsComplete && !IsOtherEnglish(l)));

        /// <summary>Strings.resx and the file of every complete translation
        /// into another language.</summary>
        internal static string[] Complete => Whole.Select(FileOf).ToArray();

        /// <summary>The same as file-name endings: "" for American English,
        /// ".de" for German.</summary>
        internal static string[] CompleteSuffixes
            => Whole.Select(l => l == LanguageCatalog.English ? "" : "." + l.Tag).ToArray();

        /// <summary>The same as tags, with "en-US" for American English.</summary>
        internal static string[] CompleteTags => Whole.Select(l => l.Tag).ToArray();

        /// <summary>Every complete translation into another language, without
        /// American English.</summary>
        internal static string[] CompleteTranslations
            => Whole.Where(l => l != LanguageCatalog.English).Select(l => l.Tag).ToArray();
    }
}
