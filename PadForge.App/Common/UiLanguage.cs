using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace PadForge.Common
{
    /// <summary>
    /// Gives every window the two things it takes from the interface
    /// language: the language its text is declared in, as xml:lang does in
    /// markup, and the direction it is laid out in.
    ///
    /// <para>The declared language is for Chinese, Japanese and Korean
    /// (#504). The three share their characters, and WPF picks the font for
    /// one from the language its text is declared in: Microsoft YaHei UI
    /// under zh-Hans, Microsoft JhengHei UI under zh-Hant, Yu Gothic UI
    /// under ja, Malgun Gothic under ko. Text with no declared language is
    /// en-US, and for en-US the Japanese font comes first. A Simplified
    /// Chinese interface was drawn in Yu Gothic UI.</para>
    ///
    /// <para>No other language is declared. A window's language is also the
    /// culture its bindings format and parse numbers in, and the pad page
    /// has some 250 number boxes that read and write the en-US notation.
    /// Declared German would turn a typed "0.5" into 5. So a language is
    /// declared only when its culture writes numbers as en-US does, which
    /// <see cref="WritesNumbersAsEnUsDoes"/> checks and UiLanguageTests
    /// holds against every number format in the markup.</para>
    ///
    /// <para>The direction is for Arabic, Persian, Urdu and Central Kurdish
    /// (#505). A window laid out right to left mirrors its panels. Drawings
    /// pin themselves left to right, so a controller's left stick stays on
    /// the left.</para>
    /// </summary>
    internal static class UiLanguage
    {
        /// <summary>The language WPF gives text that declares none.</summary>
        internal static readonly XmlLanguage Undeclared = XmlLanguage.GetLanguage("en-US");

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly ConcurrentDictionary<XmlLanguage, bool> _numbers = new();
        private static bool _started;

        /// <summary>The language a window declares while the interface is in
        /// <paramref name="ui"/>. A specific culture is passed through as it
        /// is (zh-TW, ja-JP): WPF walks its parents to the font table.</summary>
        internal static XmlLanguage Declared(CultureInfo ui)
        {
            string name = ui?.IetfLanguageTag ?? "";
            int dash = name.IndexOf('-');
            string language = (dash < 0 ? name : name.Substring(0, dash)).ToLowerInvariant();
            if (language != "zh" && language != "ja" && language != "ko") return Undeclared;
            // WPF has a table for zh-Hans and one for zh-Hant, none for bare zh.
            string bare = language == "zh" ? "zh-Hans" : language;
            XmlLanguage declared;
            try { declared = XmlLanguage.GetLanguage(dash < 0 ? bare : name); }
            catch (ArgumentException) { declared = XmlLanguage.GetLanguage(bare); }
            return WritesNumbersAsEnUsDoes(declared) ? declared : Undeclared;
        }

        /// <summary>True when bindings under <paramref name="language"/>
        /// format and parse a number exactly as they do under en-US. A tag
        /// with no culture of its own (zh-Latn) falls to the invariant
        /// culture, which writes a percentage another way, so it is false.</summary>
        internal static bool WritesNumbersAsEnUsDoes(XmlLanguage language)
            => _numbers.GetOrAdd(language, static l =>
            {
                CultureInfo culture;
                try { culture = l.GetSpecificCulture(); }
                catch (InvalidOperationException) { return false; }
                if (culture.Name.Length == 0) return false;
                NumberFormatInfo a = culture.NumberFormat, b = EnUs.NumberFormat;
                return a.NumberDecimalSeparator == b.NumberDecimalSeparator
                    && a.NumberGroupSeparator == b.NumberGroupSeparator
                    && a.NumberGroupSizes.AsSpan().SequenceEqual(b.NumberGroupSizes)
                    && a.NumberNegativePattern == b.NumberNegativePattern
                    && a.NegativeSign == b.NegativeSign
                    && a.PositiveSign == b.PositiveSign
                    && a.PercentSymbol == b.PercentSymbol
                    && a.PercentDecimalSeparator == b.PercentDecimalSeparator
                    && a.PercentGroupSeparator == b.PercentGroupSeparator
                    && a.PercentPositivePattern == b.PercentPositivePattern
                    && a.PercentNegativePattern == b.PercentNegativePattern
                    && a.DigitSubstitution == DigitShapes.None;
            });

        /// <summary>The direction a window is laid out in while the
        /// interface is in <paramref name="ui"/>. Only a language in the
        /// catalog reads right to left: Hebrew Windows with no Hebrew
        /// translation shows English, left to right.</summary>
        internal static FlowDirection Direction(CultureInfo ui)
            => LanguageCatalog.IsRightToLeft(ui) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        /// <summary>Declares the language and direction on every window
        /// that opens from here on, and again on all of them whenever the
        /// language changes. Called once, at the top of startup. A window
        /// reads the language as it loads, so the saved one is in place by
        /// then.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            PadForge.Resources.Strings.Strings.CultureChanged += Apply;
            Apply();
        }

        /// <summary>A window takes its language and direction as it loads,
        /// before its first frame. Menus, tooltips and drop-downs take both
        /// from the window they open from.</summary>
        internal static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            CultureInfo ui = CultureInfo.CurrentUICulture;
            Declare(sender as Window, Declared(ui), Direction(ui));
        }

        /// <summary>Declares the current language and direction on every open window.</summary>
        internal static void Apply()
        {
            // Read on the calling thread, which is the one whose culture just changed.
            CultureInfo ui = CultureInfo.CurrentUICulture;
            XmlLanguage language = Declared(ui);
            FlowDirection direction = Direction(ui);
            Application app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) DeclareOnAll(app, language, direction);
            else app.Dispatcher.BeginInvoke(new Action(() => DeclareOnAll(app, language, direction)));
        }

        private static void DeclareOnAll(Application app, XmlLanguage language, FlowDirection direction)
        {
            foreach (Window window in app.Windows) Declare(window, language, direction);
        }

        internal static void Declare(Window window, XmlLanguage language, FlowDirection direction)
        {
            if (window == null) return;
            if (!Equals(window.Language, language)) window.Language = language;
            if (window.FlowDirection != direction) window.FlowDirection = direction;
        }
    }
}
