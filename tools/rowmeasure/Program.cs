using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using IOPath = System.IO.Path;

// Width measurement for the fixed-size rows the 2026-09-15 audit flagged as
// possibly overflowing their containers. Renders the REAL controls with the
// app's implicit styles and the app font inside an off-screen window, then
// reads back ActualWidth after the render. The sibling harness under
// tools/combomeasure does the same for the Indicator LEDs combos; this one
// covers the Profiles shortcut row, the raw hat strip, the equalizer row and
// the gesture recorder's hint, across every locale.
//
// No estimates. Every number printed is what the layout engine produced.

internal static class Program
{
    static readonly string ResxDir = FindResxDir();

    static string FindResxDir()
    {
        const string rel = @"PadForge.App\Resources\Strings";
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = IOPath.Combine(dir.FullName, rel);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Run from inside the repo tree.");
    }

    static readonly (string tag, string file)[] Locales =
    {
        ("en", "Strings.resx"), ("de", "Strings.de.resx"), ("es", "Strings.es.resx"),
        ("fr", "Strings.fr.resx"), ("it", "Strings.it.resx"), ("ja", "Strings.ja.resx"),
        ("ko", "Strings.ko.resx"), ("nl", "Strings.nl.resx"),
        ("pt-BR", "Strings.pt-BR.resx"), ("zh-Hans", "Strings.zh-Hans.resx"),
    };

    static Dictionary<string, Dictionary<string, string>> _strings;
    static FontFamily _bodyFont;

    // The reset control is a fixed 28 by 28 with a 4 left margin (the shared
    // ember icon button style), so it costs 32 wherever it appears.
    const double ResetCost = 32;

    sealed class Probe
    {
        public string Group, Locale, Note;
        public FrameworkElement El;
        public double Declared;
    }

    static readonly List<Probe> _probes = new();

    static void LoadStrings()
    {
        _strings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (tag, file) in Locales)
        {
            string path = IOPath.Combine(ResxDir, file);
            if (!File.Exists(path)) continue;
            foreach (var d in XDocument.Load(path).Root.Elements("data"))
            {
                string key = (string)d.Attribute("name");
                string val = (string)d.Element("value");
                if (key == null || val == null) continue;
                if (!_strings.TryGetValue(key, out var byLocale))
                    _strings[key] = byLocale = new Dictionary<string, string>(StringComparer.Ordinal);
                byLocale[tag] = val;
            }
        }
    }

    static string S(string key, string tag)
        => _strings.TryGetValue(key, out var m) && m.TryGetValue(tag, out var v) ? v : null;

    static ComboBox Combo(IEnumerable<string> items)
    {
        var cb = new ComboBox();
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedIndex = 0;
        return cb;
    }

    static TextBlock Text(string s, double size = 13)
        => new TextBlock { Text = s, FontSize = size };

    static void Add(string group, string locale, string note, FrameworkElement el, double declared)
    {
        // Left, not the vertical panel's default stretch: a stretched child
        // reports the PANEL's width back, which is not what any of these rows
        // would ask for.
        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
        _probes.Add(new Probe { Group = group, Locale = locale, Note = note, El = el, Declared = declared });
    }

    [STAThread]
    static void Main()
    {
        LoadStrings();

        var app = new Application();
        app.Resources.MergedDictionaries.Add(
            new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        var baseStyle = (Style)app.Resources["DefaultComboBoxStyle"];
        var comboStyle = new Style(typeof(ComboBox), baseStyle);
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        comboStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        app.Resources[typeof(ComboBox)] = comboStyle;

        _bodyFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Orientation = Orientation.Vertical };

        BuildProfilesRow();
        BuildHatStrip();
        BuildEqRow();
        BuildRecorderHint();
        BuildPadHeader();

        foreach (var p in _probes) panel.Children.Add(p.El);

        var win = new Window
        {
            Content = panel,
            FontFamily = _bodyFont,
            FontSize = 13.0,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000, Top = -32000,
            Width = 400, Height = 400,
        };
        win.ContentRendered += (_, __) =>
        {
            panel.UpdateLayout();
            Report();
            app.Shutdown();
        };
        win.Show();
        app.Run();
    }

    // ── C226: the Profiles page shortcut row ────────────────────────────
    static void BuildProfilesRow()
    {
        var modeKeys = new[]
        {
            "Profiles_ShortcutMode_Next", "Profiles_ShortcutMode_Previous",
            "Profiles_ShortcutMode_Specific", "Profiles_ShortcutMode_ToggleWindow",
            "Profiles_ShortcutMode_ToggleVCsDisabled",
        };
        foreach (var (tag, _) in Locales)
        {
            var items = modeKeys.Select(k => S(k, tag)).Where(v => v != null).ToList();
            if (items.Count == 0) continue;
            Add("profiles-mode", tag, "mode picker natural", Combo(items), 290);
        }
    }

    // ── C228: the raw hat strip in the 340-wide device detail pane ───────
    static void BuildHatStrip()
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        cell.Children.Add(new Canvas { Width = 36, Height = 36 });
        cell.Children.Add(Text("POV 0", 10));
        Add("hat-cell", "en", "one hat cell plus its right margin", cell, 0);
    }

    // ── C285: the equalizer row's fixed columns ──────────────────────────
    static void BuildEqRow()
    {
        var checkAndReset = new StackPanel { Orientation = Orientation.Horizontal };
        checkAndReset.Children.Add(new CheckBox());
        Add("eq-col0", "en", "checkbox (a 32 reset sits beside it)", checkAndReset, 42);

        var typeKeys = new[]
        {
            "Pad_Audio_EqType_Peaking", "Pad_Audio_EqType_LowShelf",
            "Pad_Audio_EqType_HighShelf", "Pad_Audio_EqType_LowPass",
            "Pad_Audio_EqType_HighPass", "Pad_Audio_EqType_Notch",
        };
        foreach (var (tag, _) in Locales)
        {
            var items = typeKeys.Select(k => S(k, tag)).Where(v => v != null).ToList();
            if (items.Count == 0) continue;
            Add("eq-col1", tag, "type picker natural", Combo(items), 150);
        }

        foreach (var (name, declared, sample) in new[]
                 { ("eq-col2 frequency", 96.0, "20000"), ("eq-col3 gain", 88.0, "-24.0"), ("eq-col4 Q", 78.0, "0.707") })
            Add(name, "en", "editor (a 32 reset docks right, 6 panel margin)",
                new TextBox { Text = sample }, declared);
    }

    // ── C286: the recorder dialog's sample-count hint ────────────────────
    static void BuildRecorderHint()
    {
        foreach (var (tag, _) in Locales)
        {
            string label = S("Recorder_SampleCount_Label", tag);
            if (label != null) Add("recorder-label", tag, "label", Text(label), 0);
            string hint = S("Recorder_SampleCount_Hint", tag);
            if (hint != null) Add("recorder-hint", tag, "hint on one line", Text(hint, 12), 0);
        }
    }

    // ── C275: the pad page's first-tier header ──────────────────────────
    //
    // A DockPanel with LastChildFill false. The scope label, the identity chip
    // and the preset chip are declared first, so they reserve their width
    // first; the six tabs are docked right and take what is left. Each piece
    // is measured with the real font and the real chrome.
    static void BuildPadHeader()
    {
        // Scope label: "SLOT" uppercased plus the number, telemetry font at
        // 10, inside an 8,0,4,0 margin.
        foreach (var (tag, _) in Locales)
        {
            string slot = S("Dashboard_Slot", tag);
            if (slot == null) continue;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 4, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = slot.ToUpperInvariant(),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
            });
            sp.Children.Add(new TextBlock
            {
                Text = "16",
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(3, 0, 0, 0),
            });
            Add("hdr-scope", tag, "SLOT N label with its margin", sp, 0);
        }

        // Identity chip: a bordered pill, 14 icon, then the output type name
        // and its instance number at 14. The longest type name is the one that
        // sizes it.
        var typeKeys = new[]
        {
            "ControllerType_Xbox", "ControllerType_PlayStation",
            "ControllerType_Nintendo", "ControllerType_Extended",
            "ControllerType_KeyboardMouse", "ControllerType_MIDI",
            "ControllerType_VR",
        };
        foreach (var (tag, _) in Locales)
        {
            foreach (string key in typeKeys)
            {
                string name = S(key, tag);
                if (name == null) continue;
                var inner = new StackPanel { Orientation = Orientation.Horizontal };
                inner.Children.Add(new Canvas { Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0) });
                inner.Children.Add(new TextBlock
                {
                    Text = name + " #16",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                });
                var chip = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(4, 4, 2, 4),
                    Child = inner,
                };
                Add("hdr-identity", tag, key, chip, 0);
            }
        }

        // Preset chip: the word Preset, then the profile combo, then a reset.
        // The combo is capped at 380 and its content is the profile name.
        foreach (var (tag, _) in Locales)
        {
            string label = S("Pad_Preset", tag);
            if (label != null) Add("hdr-preset-label", tag, "the word Preset", Text(label, 12.5), 0);
        }

        // Six tabs. Each is a RadioButton with a 12,5,12,7 content margin and
        // a 2,4 outer margin, so a tab costs its text plus 28.
        var tabKeys = new[]
        {
            "Pad_Tab_Preview", "Pad_Mappings", "Pad_Macros",
            "Pad_Menus", "Pad_Tab_BassShakers", "Pad_Tab_Output",
        };
        foreach (var (tag, _) in Locales)
        {
            double sum = 0;
            bool complete = true;
            foreach (string key in tabKeys)
            {
                string t = S(key, tag);
                if (t == null) { complete = false; break; }
                var tb = Text(t, 15);
                var host = new Border { Padding = new Thickness(12, 5, 12, 7), Margin = new Thickness(2, 4, 2, 4), Child = tb };
                Add("hdr-tab-part", tag, key, host, 0);
                sum += 1; // placeholder, the real sum is taken in Report
            }
            if (!complete || sum == 0) continue;
        }
    }

    static void Report()
    {
        double Max(string group) => _probes.Where(p => p.Group == group)
                                           .Select(p => p.El.ActualWidth).DefaultIfEmpty(0).Max();
        string Worst(string group)
        {
            double m = Max(group);
            return _probes.Where(p => p.Group == group && Math.Abs(p.El.ActualWidth - m) < 0.01)
                          .Select(p => p.Locale).FirstOrDefault() ?? "";
        }

        Console.WriteLine("== Profiles shortcut row (declared window minimum 900) ==");
        Console.WriteLine($"  mode picker natural            : {Max("profiles-mode"):F1}  declared 290 (worst {Worst("profiles-mode")})");
        double actions = 3 * ResetCost;
        double total = (290 + ResetCost + 6) + (140 + ResetCost + 6) + (290 + ResetCost + 6) + 6 + actions;
        Console.WriteLine($"  action cluster (three buttons) : {actions:F1}");
        Console.WriteLine($"  row total at declared widths   : {total:F1}");
        Console.WriteLine($"  VERDICT                        : {(total > 900 ? "OVERFLOWS" : "fits")}");
        Console.WriteLine();

        Console.WriteLine("== Raw hat strip (detail pane 340) ==");
        double one = Max("hat-cell");
        const double usablePane = 340 - 14 - 14 - 14;
        Console.WriteLine($"  one hat cell                   : {one:F1}");
        Console.WriteLine($"  usable content width           : {usablePane:F1}");
        for (int hats = 2; hats <= 4; hats++)
            Console.WriteLine($"  {hats} hats in one row            : {one * hats:F1}"
                              + (one * hats > usablePane ? "  OVERFLOWS" : "  fits"));
        Console.WriteLine();

        Console.WriteLine("== Equalizer row columns ==");
        double col0 = Max("eq-col0") + ResetCost;
        Console.WriteLine($"  col 0 checkbox plus reset      : {col0:F1}  declared 42"
                          + (col0 > 42 ? "  OVERFLOWS" : "  fits"));
        double col1 = Max("eq-col1");
        Console.WriteLine($"  col 1 type picker natural      : {col1:F1}  declared 150 (worst {Worst("eq-col1")})"
                          + (col1 > 150 ? "  OVERFLOWS" : "  fits"));
        foreach (var (group, declared) in new[]
                 { ("eq-col2 frequency", 96.0), ("eq-col3 gain", 88.0), ("eq-col4 Q", 78.0) })
        {
            double need = Max(group) + ResetCost + 6;
            Console.WriteLine($"  {group,-22}         : {need:F1}  declared {declared}"
                              + (need > declared ? "  OVERFLOWS" : "  fits"));
        }
        Console.WriteLine();

        Console.WriteLine("== Gesture recorder sample-count row (dialog 640, not resizable) ==");
        const double usableRow = 640 - 32;
        double fixedPart = Max("recorder-label") + 8 + 60 + ResetCost + 8 + 12;
        double hint = Max("recorder-hint");
        Console.WriteLine($"  label plus picker plus reset   : {fixedPart:F1}");
        Console.WriteLine($"  hint on ONE line               : {hint:F1} (worst {Worst("recorder-hint")})");
        Console.WriteLine($"  usable row width               : {usableRow:F1}");
        Console.WriteLine($"  VERDICT                        : {(fixedPart + hint > usableRow ? "the hint CANNOT fit on one line" : "fits")}");
        Console.WriteLine($"  width a wrapping hint would get: {usableRow - fixedPart:F1}");
        Console.WriteLine();

        // ── C275 ──
        // At the 900 window minimum the navigation pane is open at 244, so the
        // page gets 656 before its own padding. The compact pane (48) leaves
        // 852. Both are reported; the open pane is the case that has to work.
        Console.WriteLine("== Pad page first tier (window minimum 900) ==");
        double scope = Max("hdr-scope");
        double identity = Max("hdr-identity");
        double presetLabel = Max("hdr-preset-label");
        // The profile combo is capped at 380 and the reset costs 32; the chip
        // border adds a 10,4,4,4 margin.
        double preset = presetLabel + 4 + 380 + ResetCost + 8 + 14;

        // Tabs: per locale, the six parts summed. The widest locale wins.
        double tabs = 0; string tabWorst = "";
        foreach (var (tag, _) in Locales)
        {
            double sum = _probes.Where(p => p.Group == "hdr-tab-part" && p.Locale == tag)
                                .Sum(p => p.El.ActualWidth);
            if (sum > tabs) { tabs = sum; tabWorst = tag; }
        }

        Console.WriteLine($"  scope label                    : {scope:F1} (worst {Worst("hdr-scope")})");
        Console.WriteLine($"  identity chip                  : {identity:F1} (worst {Worst("hdr-identity")})");
        Console.WriteLine($"  preset chip at its 380 cap     : {preset:F1}");
        Console.WriteLine($"  six tabs                       : {tabs:F1} (worst {tabWorst})");
        double tier = scope + identity + preset + tabs + 8;
        Console.WriteLine($"  tier total                     : {tier:F1}");
        foreach (var (paneName, pane) in new[] { ("open 244", 244.0), ("compact 48", 48.0) })
        {
            double avail = 900 - pane - 8;
            Console.WriteLine($"  available, pane {paneName,-10}     : {avail:F1}"
                              + (tier > avail ? "   OVERFLOWS" : "   fits"));
        }
        double leftOfTabs = scope + identity + preset + 8;
        Console.WriteLine($"  left cluster alone             : {leftOfTabs:F1}");
        Console.WriteLine($"  width left for the tabs (open) : {900 - 244 - 8 - leftOfTabs:F1}"
                          + (900 - 244 - 8 - leftOfTabs < tabs ? "   TABS CLIPPED" : "   tabs fit"));
    }
}
