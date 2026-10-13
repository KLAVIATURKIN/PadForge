using System;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace PadForge.Common
{
    /// <summary>
    /// Declares the interface language on every window, as xml:lang does in
    /// markup, so Chinese, Japanese and Korean text is drawn in the font of
    /// its own language (#504).
    ///
    /// <para>The three languages share their characters, and WPF picks the
    /// font for one from the language its text is declared in: Microsoft
    /// YaHei UI under zh-Hans, Microsoft JhengHei UI under zh-Hant, Yu
    /// Gothic UI under ja, Malgun Gothic under ko. Text with no declared
    /// language is en-US, and for en-US the Japanese font comes first. A
    /// Simplified Chinese interface was drawn in Yu Gothic UI.</para>
    ///
    /// <para>No other language is declared. A window's language is also the
    /// culture its bindings format and parse numbers in, and the pad page
    /// has some 250 number boxes that read and write the en-US notation.
    /// Declared German would turn a typed "0.5" into 5. The Chinese,
    /// Japanese and Korean cultures write numbers exactly as en-US does,
    /// which UiLanguageTests holds against every number format in the
    /// markup.</para>
    /// </summary>
    internal static class UiLanguage
    {
        /// <summary>The language WPF gives text that declares none.</summary>
        internal static readonly XmlLanguage Undeclared = XmlLanguage.GetLanguage("en-US");

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
            try { return XmlLanguage.GetLanguage(dash < 0 ? bare : name); }
            catch (ArgumentException) { return XmlLanguage.GetLanguage(bare); }
        }

        /// <summary>Declares the language on every window that opens from
        /// here on, and again on all of them whenever the language changes.
        /// Called once, at the top of startup. A window reads the language
        /// as it loads, so the saved one is in place by then.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            PadForge.Resources.Strings.Strings.CultureChanged += Apply;
            Apply();
        }

        /// <summary>A window declares its language as it loads, before its
        /// first frame. Menus, tooltips and drop-downs take the language of
        /// the window they open from.</summary>
        internal static void OnWindowLoaded(object sender, RoutedEventArgs e)
            => Declare(sender as Window, Declared(CultureInfo.CurrentUICulture));

        /// <summary>Declares the current language on every open window.</summary>
        internal static void Apply()
        {
            // Read on the calling thread, which is the one whose culture just changed.
            XmlLanguage language = Declared(CultureInfo.CurrentUICulture);
            Application app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) DeclareOnAll(app, language);
            else app.Dispatcher.BeginInvoke(new Action(() => DeclareOnAll(app, language)));
        }

        private static void DeclareOnAll(Application app, XmlLanguage language)
        {
            foreach (Window window in app.Windows) Declare(window, language);
        }

        internal static void Declare(Window window, XmlLanguage language)
        {
            if (window != null && !Equals(window.Language, language)) window.Language = language;
        }
    }
}
