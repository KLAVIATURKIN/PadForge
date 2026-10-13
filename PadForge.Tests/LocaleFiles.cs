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
    /// file reads the complete translations. A partial one shows English for
    /// what it leaves out, so the English file answers for it.</para>
    /// </summary>
    internal static class LocaleFiles
    {
        internal static string FileOf(LanguageInfo language)
            => language.Tag == "en" ? "Strings.resx" : "Strings." + language.Tag + ".resx";

        /// <summary>Strings.resx and the file of every translation that has
        /// every string.</summary>
        internal static string[] Complete
            => LanguageCatalog.All.Where(l => l.IsComplete).Select(FileOf).ToArray();

        /// <summary>The same as file-name endings: "" for English, ".de" for
        /// German.</summary>
        internal static string[] CompleteSuffixes
            => LanguageCatalog.All.Where(l => l.IsComplete).Select(l => l.Tag == "en" ? "" : "." + l.Tag).ToArray();

        /// <summary>The same as tags, with "en" for English.</summary>
        internal static string[] CompleteTags
            => LanguageCatalog.All.Where(l => l.IsComplete).Select(l => l.Tag).ToArray();
    }
}
