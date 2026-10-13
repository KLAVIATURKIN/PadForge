using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using PadForge.Common;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #505: the languages the interface is translated into are one table,
    /// Resources/Strings/Languages.tsv, read by <see cref="LanguageCatalog"/>.
    /// Before it, the list was spelled out in the settings view model, in
    /// seven test files and in two measuring tools, and a language added in
    /// one of them was missing from the rest.
    ///
    /// <para>These tests hold the table to the files beside it and to what
    /// the build made of them, so a row with no translation, a translation
    /// with no row, and a translation the build left out all fail here.</para>
    /// </summary>
    public class LanguageCatalogTests
    {
        /// <summary>How many languages the tree holds, English included.
        /// Hard-coded on purpose: a language dropped from the table and from
        /// the tree together would shrink every other test's coverage in
        /// silence, and this number would not move with it.</summary>
        internal const int LanguageCount = 10;

        private static string StringsDirectory()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(d.FullName, "PadForge.App", "Resources", "Strings");
        }

        private static SortedSet<string> KeysOf(string fileName)
        {
            string xml = File.ReadAllText(Path.Combine(StringsDirectory(), fileName));
            return new SortedSet<string>(
                Regex.Matches(xml, @"<data name=""([^""]+)""").Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        }

        /// <summary>Every language but English, for the tests that read a translation.</summary>
        public static IEnumerable<object[]> Translations()
            => LanguageCatalog.All.Where(l => l.Tag != "en").Select(l => new object[] { l.Tag });

        [Fact]
        public void TheTableHasARowForEnglishAndOneForEachTranslationInTheTree()
        {
            var files = Directory.EnumerateFiles(StringsDirectory(), "Strings.*.resx")
                .Select(p => Path.GetFileNameWithoutExtension(p).Substring("Strings.".Length))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = LanguageCatalog.All.Select(l => l.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains("en", rows);
            Assert.True(File.Exists(Path.Combine(StringsDirectory(), "Strings.resx")));
            rows.Remove("en");
            Assert.Empty(rows.Except(files).OrderBy(t => t));   // a row with no translation
            Assert.Empty(files.Except(rows).OrderBy(t => t));   // a translation with no row
            Assert.Equal(LanguageCount, LanguageCatalog.All.Count);
        }

        [Fact]
        public void EveryRowIsWhole_AndNoTwoShareATagOrAName()
        {
            foreach (LanguageInfo language in LanguageCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(language.Tag));
                Assert.False(string.IsNullOrWhiteSpace(language.EnglishName), language.Tag);
                Assert.False(string.IsNullOrWhiteSpace(language.NativeName), language.Tag);
                Assert.Equal(language.NativeName, language.NativeName.Trim());
                Assert.Equal(language.Tag, language.Culture.Name, ignoreCase: true);
                Assert.Same(language, LanguageCatalog.Find(language.Tag));
                Assert.Same(language, LanguageCatalog.Find(language.Tag.ToUpperInvariant()));
            }
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.Tag.ToLowerInvariant()).Distinct().Count());
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.NativeName).Distinct().Count());
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.EnglishName).Distinct().Count());
            Assert.Null(LanguageCatalog.Find(null));
            Assert.Null(LanguageCatalog.Find(""));
            Assert.Null(LanguageCatalog.Find("zz"));
            Assert.Equal("English", LanguageCatalog.English.NativeName);
            Assert.Equal("", LanguageCatalog.English.EnglishNameIfDifferent);
        }

        [Fact]
        public void ThePickerListsThemByTheirOwnName()
        {
            CompareInfo invariant = CultureInfo.InvariantCulture.CompareInfo;
            var names = LanguageCatalog.All.Select(l => l.NativeName).ToList();
            foreach (var (before, after) in names.Zip(names.Skip(1)))
            {
                Assert.True(invariant.Compare(before, after, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) <= 0,
                    before + " is listed ahead of " + after);
            }
        }

        /// <summary>The table says which translations have every string.
        /// Counted here from the files, so the column cannot claim a
        /// translation is whole when it is not, or the other way round.</summary>
        [Theory]
        [MemberData(nameof(Translations))]
        public void TheCoverageColumnIsWhatTheFileHolds(string tag)
        {
            SortedSet<string> baseKeys = KeysOf("Strings.resx"), keys = KeysOf("Strings." + tag + ".resx");
            Assert.True(baseKeys.Count > 1000);
            Assert.NotEmpty(keys);
            Assert.Empty(keys.Except(baseKeys));
            Assert.Equal(baseKeys.IsSubsetOf(keys), LanguageCatalog.Find(tag).IsComplete);
        }

        /// <summary>Every translation reaches the build as a satellite
        /// resource. MSBuild leaves a tag it does not know in the main
        /// assembly, where no culture finds it, unless the project names
        /// the culture itself (PadForge.App.csproj).</summary>
        [Theory]
        [MemberData(nameof(Translations))]
        public void TheBuildCarriesTheTranslation(string tag)
        {
            var set = Strings.ResourceManager.GetResourceSet(
                CultureInfo.GetCultureInfo(tag), createIfNotExists: true, tryParents: false);
            Assert.True(set != null, tag + " did not build into a satellite resource");
            string saved = Strings.ResourceManager.GetString("Common_Save", CultureInfo.GetCultureInfo(tag));
            Assert.False(string.IsNullOrEmpty(saved));
        }

        [Fact]
        public void NoTranslationWasLeftInTheMainAssembly()
        {
            string[] stray = typeof(Strings).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("PadForge.Resources.Strings.Strings.", StringComparison.Ordinal)
                            && n != "PadForge.Resources.Strings.Strings.resources")
                .ToArray();
            Assert.Empty(stray);
            Assert.Contains(LanguageCatalog.ResourceName, typeof(Strings).Assembly.GetManifestResourceNames());
        }

        [Theory]
        [InlineData("en", "en")]
        [InlineData("en-GB", "en")]
        [InlineData("de", "de")]
        [InlineData("de-AT", "de")]
        [InlineData("fr-CA", "fr")]
        [InlineData("es-MX", "es")]
        [InlineData("it-CH", "it")]
        [InlineData("nl-BE", "nl")]
        [InlineData("ja-JP", "ja")]
        [InlineData("ko-KR", "ko")]
        [InlineData("pt-BR", "pt-BR")]
        [InlineData("zh-Hans", "zh-Hans")]
        [InlineData("zh-CN", "zh-Hans")]
        [InlineData("zh-SG", "zh-Hans")]
        public void ACultureReadsItsOwnRowOrItsNearestParents(string culture, string row)
            => Assert.Same(LanguageCatalog.Find(row), LanguageCatalog.Resolve(CultureInfo.GetCultureInfo(culture)));

        [Fact]
        public void ACultureWithNoRowReadsNothing_WhichIsEnglish()
        {
            Assert.Null(LanguageCatalog.Resolve(null));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.InvariantCulture));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.GetCultureInfo("he-IL")));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.GetCultureInfo("eo")));
        }

        /// <summary>Direction comes from the table, so a right-to-left
        /// Windows with no translation of its own shows English left to
        /// right. Where .NET knows a language's direction, the table agrees.</summary>
        [Fact]
        public void DirectionComesFromTheTable()
        {
            foreach (LanguageInfo language in LanguageCatalog.All)
            {
                Assert.Equal(language.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    UiLanguage.Direction(language.Culture));
                Assert.Equal(language.IsRightToLeft, LanguageCatalog.IsRightToLeft(language.Culture));
                if (language.Culture.TextInfo.IsRightToLeft) Assert.True(language.IsRightToLeft, language.Tag);
            }
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(CultureInfo.GetCultureInfo("he-IL")));
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(null));
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(CultureInfo.InvariantCulture));
        }
    }
}
