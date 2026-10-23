using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PadForge.Controls;
using PadForge.Resources.Strings;

namespace PadForge.Common
{
    /// <summary>
    /// Names controls for screen readers from text the UI already shows, as
    /// each control first shows. Nothing on screen changes and no string is
    /// added.
    /// The order is the HTML accessible-name order: visible text first, then
    /// the tooltip (HTML's title), then placeholder text.
    ///
    /// <para>A button that shows text keeps it as its name. One whose content
    /// is a panel holding text is labeled by the first text it shows, since
    /// FrameworkElement.GetPlainText gives a panel no text and the button
    /// would read nothing. A button that shows no text (a glyph, an icon, a
    /// shape) is named by its tooltip, when the tooltip is a string, or the
    /// string BidiText.ToolTip draws.</para>
    ///
    /// <para>An input (TextBox, NumberBox, PasswordBox, ComboBox, Slider,
    /// RichTextBox, ListBox) without a name of its own is labeled by the label
    /// beside it or above its row (FindLabel), and AccessibilityXamlTests
    /// counts the inputs that rule reaches as named. An input without such a
    /// label is named by its tooltip, or else by its placeholder text.</para>
    ///
    /// <para>A list item whose data has no ToString of its own and whose
    /// content is not text would read a type name, since
    /// ItemAutomationPeer.GetNameCore falls back to the item's ToString(). An
    /// enum value would read its code name in place of the text its template
    /// shows. Such an item is labeled by the first text it shows. Items that
    /// already read well are left alone.</para>
    ///
    /// <para>A name set in XAML or code is never replaced. A name set here is
    /// marked, so a control that shows again after being collapsed is named
    /// again.</para>
    /// </summary>
    internal static class AccessibleNames
    {
        private static bool _started;

        private static readonly DependencyProperty AutoProperty = DependencyProperty.RegisterAttached(
            "Auto", typeof(bool), typeof(AccessibleNames), new PropertyMetadata(false));

        /// <summary>Watches buttons, inputs and list items as they first
        /// show. Called once, at startup.</summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;
            // Loaded would miss most controls: WPF sends it only to an element
            // with a Loaded handler of its own, a class handler not counting
            // (FrameworkElement.ThisHasLoadedChangeEventHandler,
            // BroadcastEventHelper.OnBroadcastCallback). SizeChanged reaches
            // every element whose size changes (UIElement.
            // markForSizeChangedIfNeeded, FrameworkElement.OnRenderSizeChanged),
            // so a control is named when it first takes a size, which is when
            // it first shows, and again each time it shows after being
            // collapsed.
            var onInput = Sized(fe => NameInput(fe));
            foreach (Type type in new[] { typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(Slider), typeof(RichTextBox), typeof(ListBox) })
                EventManager.RegisterClassHandler(type, FrameworkElement.SizeChangedEvent, onInput);
            // Button, RepeatButton and ToggleButton, so CheckBox and
            // RadioButton too.
            EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.SizeChangedEvent,
                Sized(fe => NameButton((ButtonBase)fe)));
            // A ListBox, ListView or ComboBox wraps each item in a
            // ListBoxItem, a plain ItemsControl in a ContentPresenter.
            var onItem = Sized(fe => NameItem(fe));
            EventManager.RegisterClassHandler(typeof(ListBoxItem), FrameworkElement.SizeChangedEvent, onItem);
            EventManager.RegisterClassHandler(typeof(ContentPresenter), FrameworkElement.SizeChangedEvent, onItem);
        }

        /// <summary>A SizeChanged handler that names an element when it grows
        /// from no size, and leaves every later resize alone.</summary>
        private static SizeChangedEventHandler Sized(Action<FrameworkElement> name)
            => (sender, e) =>
            {
                if (e.PreviousSize.Width == 0 && e.PreviousSize.Height == 0 && sender is FrameworkElement fe)
                    name(fe);
            };

        /// <summary>"Slot 3": the label and number the Dashboard card shows
        /// for a slot, which name the slot wherever it appears.</summary>
        internal static string SlotTitle(int slotNumber)
            => Strings.Instance.Dashboard_Slot + " " + slotNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>Names a shown button that has no name of its own.</summary>
        internal static void NameButton(ButtonBase button)
        {
            if (IsTemplatePart(button) || HasOwnName(button))
                return;
            ClearAuto(button);
            if (button.Content is FrameworkElement content && content is not TextBlock
                && FirstShownText(content) is TextBlock text)
            {
                NameAfter(button, text);
            }
            else
            {
                var name = new MultiBinding { Converter = ButtonNameConverter.Instance };
                name.Bindings.Add(new Binding(nameof(FrameworkElement.ToolTip)) { Source = button });
                name.Bindings.Add(new Binding(nameof(ContentControl.Content)) { Source = button });
                name.Bindings.Add(new Binding { Path = new PropertyPath(BidiText.ToolTipProperty), Source = button });
                BindingOperations.SetBinding(button, AutomationProperties.NameProperty, name);
            }
            button.SetValue(AutoProperty, true);
        }

        /// <summary>Names a shown input that has no name of its own.</summary>
        internal static void NameInput(FrameworkElement input)
        {
            if (IsTemplatePart(input) || HasOwnName(input))
                return;
            ClearAuto(input);
            if (FindLabel(input) is object label)
            {
                SetLabel(input, label);
            }
            else
            {
                var name = new MultiBinding { Converter = FirstTextConverter.Instance };
                name.Bindings.Add(new Binding(nameof(FrameworkElement.ToolTip)) { Source = input });
                name.Bindings.Add(new Binding { Path = new PropertyPath(BidiText.ToolTipProperty), Source = input });
                if (input is Wpf.Ui.Controls.TextBox)
                    name.Bindings.Add(new Binding(nameof(Wpf.Ui.Controls.TextBox.PlaceholderText)) { Source = input });
                BindingOperations.SetBinding(input, AutomationProperties.NameProperty, name);
            }
            input.SetValue(AutoProperty, true);
        }

        /// <summary>Labels a shown list item container (a ListBoxItem, or
        /// the ContentPresenter a plain ItemsControl wraps an item in) that
        /// would read a type name or an enum's code name.</summary>
        internal static void NameItem(FrameworkElement container)
        {
            if (HasOwnName(container))
                return;
            // Cleared first: a recycled container may now hold an item that
            // reads well on its own.
            ClearAuto(container);
            // A ContentPresenter inside a control's template is no item:
            // only an items panel's children have an owner.
            ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(container);
            if (owner == null)
                return;
            object data = owner.ItemContainerGenerator.ItemFromContainer(container);
            if (data == null || data == DependencyProperty.UnsetValue)
                data = container;
            if (data is string || container is ContentControl { Content: string } || container is ContentPresenter { Content: string })
                return;
            // An enum value reads its code name, not the text its template
            // shows.
            string fallback = data.ToString() ?? string.Empty;
            if (data is not Enum && !fallback.StartsWith(data.GetType().ToString(), StringComparison.Ordinal))
                return;
            if (FirstShownText(container) is TextBlock text)
            {
                NameAfter(container, text);
                container.SetValue(AutoProperty, true);
            }
        }

        /// <summary>Names a control by a label FindLabel returned.</summary>
        private static void SetLabel(FrameworkElement fe, object label)
        {
            if (label is SettingResetButton reset)
                BindingOperations.SetBinding(fe, AutomationProperties.NameProperty,
                    new Binding(nameof(SettingResetButton.SettingLabel)) { Source = reset });
            else if (label is FrameworkElement element)
                NameAfter(fe, element);
        }

        /// <summary>Names a control after a label element. The name is bound
        /// to the label's text, so it follows the label through a language
        /// change, and LabeledBy records the relation. LabeledBy alone does
        /// not name a control: UIElementAutomationPeer.GetLabeledByCore reads
        /// only a label peer that already exists (UIElement.GetAutomationPeer),
        /// and a screen reader asks a focused control's name before anything
        /// has made its label's peer.</summary>
        private static void NameAfter(FrameworkElement fe, FrameworkElement label)
        {
            string path = label is TextBlock ? nameof(TextBlock.Text) : nameof(ContentControl.Content);
            BindingOperations.SetBinding(fe, AutomationProperties.NameProperty,
                new Binding(path) { Source = label, Mode = BindingMode.OneWay });
            AutomationProperties.SetLabeledBy(fe, label);
        }

        /// <summary>A part of another control's template, which that control
        /// names. An element made from a DataTemplate has a ContentPresenter
        /// as its templated parent and is named like any other, the line
        /// TextBlockAutomationPeer.IsControlElementCore draws too.</summary>
        private static bool IsTemplatePart(FrameworkElement fe)
            => fe.TemplatedParent != null && fe.TemplatedParent is not ContentPresenter;

        private static bool HasOwnName(FrameworkElement fe)
            => !(bool)fe.GetValue(AutoProperty)
               && (!string.IsNullOrEmpty(AutomationProperties.GetName(fe)) || AutomationProperties.GetLabeledBy(fe) != null);

        /// <summary>Drops a name set here before, so a control that shows
        /// again, in a new place or with a new item, is named afresh.</summary>
        private static void ClearAuto(FrameworkElement fe)
        {
            if (!(bool)fe.GetValue(AutoProperty))
                return;
            fe.ClearValue(AutomationProperties.NameProperty);
            fe.ClearValue(AutomationProperties.LabeledByProperty);
            fe.ClearValue(AutoProperty);
        }

        /// <summary>An input, for the label scan: a range slider sits
        /// between a label and the boxes that set its two ends.</summary>
        private static bool IsInput(DependencyObject d)
            => d is TextBox or PasswordBox or ComboBox or Slider or RichTextBox or Wpf.Ui.Controls.AutoSuggestBox or RangeSlider;

        /// <summary>The label an input takes: a TextBlock or Label, or a reset
        /// button whose SettingLabel names the setting. Null when none is
        /// found. The input's own row is searched first. When the input sits
        /// in a panel or border and nothing before it there stops the search,
        /// the search moves to the row that panel sits in, up to three levels:
        /// the label above a slider and its value box, or above the panel a
        /// dialog's picker sits in.</summary>
        internal static object FindLabel(FrameworkElement input)
        {
            FrameworkElement element = input;
            for (int level = 0; level <= 3; level++)
            {
                if (FindLabelAt(element, out bool open) is object label)
                    return label;
                if (!open || LogicalTreeHelper.GetParent(element) is not FrameworkElement row || row is not (Panel or Decorator))
                    return null;
                element = row;
            }
            return null;
        }

        /// <summary>The label beside an element in its parent: the nearest
        /// earlier sibling in its panel and Grid row that holds a label, with
        /// inputs, reset buttons and literal text without a letter (a dash
        /// between two range boxes) passed over, then the SettingLabel of a
        /// reset button later in the row. A reset button docked ahead of the
        /// element (a DockPanel lists the right-docked button first) counts
        /// when no text label precedes the element and no reset button
        /// follows it. Literal text right after an input is that input's unit
        /// (the Hz after a frequency box) and labels nothing. In a Grid, a row
        /// above the element's row that holds a label and no input labels it
        /// next. Open is whether nothing before the element stopped the
        /// search.</summary>
        private static object FindLabelAt(FrameworkElement input, out bool open)
        {
            open = false;
            if (LogicalTreeHelper.GetParent(input) is not DependencyObject parent)
                return null;
            var siblings = new List<FrameworkElement>();
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
                if (child is FrameworkElement fe)
                    siblings.Add(fe);
            int index = siblings.IndexOf(input);
            if (index < 0)
                return null;
            bool grid = parent is Grid;
            int row = Grid.GetRow(input);

            SettingResetButton before = null;
            bool passedInput = false;
            open = true;
            for (int i = index - 1; i >= 0; i--)
            {
                FrameworkElement s = siblings[i];
                if (grid && Grid.GetRow(s) != row)
                    continue;
                if (IsInput(s))
                {
                    passedInput = true;
                    continue;
                }
                if (s is SettingResetButton r)
                {
                    if (!passedInput && before == null && !string.IsNullOrEmpty(r.SettingLabel))
                        before = r;
                    continue;
                }
                if (IsSeparator(s))
                    continue;
                open = false;
                if (IsLiteral(s) && EarlierInRow(siblings, i, grid, row) is { } prior && IsInput(prior))
                    break;
                // A panel holding an input of its own is another control's
                // row, not this input's label.
                if (!HoldsInput(s) && FirstStringsLabel(s) is FrameworkElement label)
                    return label;
                break;
            }
            for (int i = index + 1; i < siblings.Count; i++)
            {
                FrameworkElement s = siblings[i];
                if (grid && Grid.GetRow(s) != row)
                    continue;
                if (s is SettingResetButton reset && !string.IsNullOrEmpty(reset.SettingLabel))
                    return reset;
                if (IsInput(s))
                    break;
            }
            if (before != null)
                return before;
            if (open && grid && row > 0)
                return RowAbove(siblings, row, ref open);
            return null;
        }

        /// <summary>The label in the Grid row above an element's row: a Grid
        /// that stacks a label row over a control row. An empty row above
        /// leaves the search open, and a row holding an input closes it with
        /// no label.</summary>
        private static FrameworkElement RowAbove(List<FrameworkElement> siblings, int row, ref bool open)
        {
            var above = siblings.FindAll(s => Grid.GetRow(s) == row - 1);
            if (above.Count == 0)
                return null;
            open = false;
            if (above.Exists(s => IsInput(s) || HoldsInput(s)))
                return null;
            foreach (FrameworkElement s in above)
                if (FirstStringsLabel(s) is FrameworkElement label)
                    return label;
            return null;
        }

        /// <summary>The first TextBlock or Label under an element, in document
        /// order, whose text is bound to a Strings key or written in the XAML
        /// with a letter in it (the R, G and B beside a color's sliders). A
        /// value bound to data is no label, nor is a dash or a unit
        /// sign.</summary>
        private static FrameworkElement FirstStringsLabel(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (d is TextBlock tb && IsLabelText(tb.Text) && IsLabelSource(tb, TextBlock.TextProperty))
                    return tb;
                if (d is Label lb && lb.Content is string s && IsLabelText(s) && IsLabelSource(lb, ContentControl.ContentProperty))
                    return lb;
                var children = new List<DependencyObject>();
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                    if (child is DependencyObject c)
                        children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
            return null;
        }

        /// <summary>Literal text without a letter, such as the dash between
        /// two range boxes.</summary>
        private static bool IsSeparator(FrameworkElement fe)
            => LiteralText(fe) is string text && !HasLetter(text);

        private static bool IsLiteral(FrameworkElement fe) => LiteralText(fe) != null;

        /// <summary>Text a TextBlock or Label is given in the XAML itself, not
        /// through a binding. Null otherwise.</summary>
        private static string LiteralText(FrameworkElement fe)
            => fe switch
            {
                TextBlock tb => tb.ReadLocalValue(TextBlock.TextProperty) as string,
                Label lb => lb.ReadLocalValue(ContentControl.ContentProperty) as string,
                _ => null,
            };

        /// <summary>The sibling before index in the same Grid row, or
        /// null.</summary>
        private static FrameworkElement EarlierInRow(List<FrameworkElement> siblings, int index, bool grid, int row)
        {
            for (int i = index - 1; i >= 0; i--)
                if (!grid || Grid.GetRow(siblings[i]) == row)
                    return siblings[i];
            return null;
        }

        /// <summary>Whether an element holds an input among its logical
        /// descendants.</summary>
        private static bool HoldsInput(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject c)
                    stack.Push(c);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (IsInput(d))
                    return true;
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                    if (child is DependencyObject c)
                        stack.Push(c);
            }
            return false;
        }

        /// <summary>Whether a label's text is bound to a Strings key, or is a
        /// literal holding a letter.</summary>
        private static bool IsLabelSource(DependencyObject d, DependencyProperty property)
        {
            if (BindingOperations.GetBinding(d, property) is Binding b)
                return ReferenceEquals(b.Source, Strings.Instance);
            return d.ReadLocalValue(property) is string literal && HasLetter(literal);
        }

        private static bool HasLetter(string text)
        {
            foreach (char c in text)
                if (char.IsLetter(c))
                    return true;
            return false;
        }

        private static bool IsLabelText(string text)
            => !string.IsNullOrWhiteSpace(text) && text != "%" && !HasGlyph(text);

        /// <summary>The first visible TextBlock in an element's visual tree
        /// that holds readable text.</summary>
        private static TextBlock FirstShownText(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                if (d is TextBlock tb && tb.IsVisible && IsLabelText(tb.Text))
                    return tb;
                int count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
                for (int i = count - 1; i >= 0; i--)
                    stack.Push(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        /// <summary>Whether text holds a Segoe icon glyph, the private-use
        /// range U+E000 to U+F8FF.</summary>
        private static bool HasGlyph(string text)
        {
            foreach (char c in text)
                if (c >= 0xE000 && c <= 0xF8FF)
                    return true;
            return false;
        }

        /// <summary>A button's name from [0] its ToolTip, [1] its Content and
        /// [2] its BidiText.ToolTip: the tooltip when it is a string and the
        /// content shows no text, otherwise empty. UI Automation treats an
        /// empty name as unset and falls back to the button's own text
        /// (FrameworkElementAutomationPeer.GetNameCore), so text a button
        /// shows stays its name. BidiText.ToolTip draws its string as a
        /// TextBlock, so the string is read from the attached property. Any
        /// other tooltip element gives empty: bound as is, it would read
        /// "System.Windows.Controls.ToolTip".</summary>
        internal sealed class ButtonNameConverter : IMultiValueConverter
        {
            internal static readonly ButtonNameConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Length < 2 || ShowsText(values[1]))
                    return string.Empty;
                if (values[0] is string tip && IsLabelText(tip))
                    return tip;
                return values.Length > 2 && values[2] is string bidi && IsLabelText(bidi) ? bidi : string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;

            /// <summary>Whether content is text a screen reader would speak: a
            /// string or a TextBlock holding no glyph.</summary>
            internal static bool ShowsText(object content)
            {
                string text = content switch
                {
                    string s => s,
                    TextBlock tb => tb.Text,
                    _ => null,
                };
                return text != null && IsLabelText(text);
            }
        }

        /// <summary>An unlabeled input's name: the first of its values (the
        /// ToolTip, the BidiText.ToolTip string, then the placeholder text)
        /// that is readable text, or empty.</summary>
        internal sealed class FirstTextConverter : IMultiValueConverter
        {
            internal static readonly FirstTextConverter Instance = new();

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                foreach (object value in values)
                    if (value is string text && IsLabelText(text))
                        return text;
                return string.Empty;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
        }
    }
}
