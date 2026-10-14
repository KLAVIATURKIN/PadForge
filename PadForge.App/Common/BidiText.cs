using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PadForge.Common
{
    /// <summary>
    /// Text and tooltips that keep a Latin name and its bracketed note
    /// together in a right-to-left paragraph (#505).
    ///
    /// <para>WPF's bidi pass has no bracket-pair rule (Unicode's N0): its
    /// ResolveNeutrals gives a bracket the direction of the strong text on
    /// both sides, or else the paragraph's. In "Igor Budzhak (MIT)" followed
    /// by Arabic, the opening bracket sits between two Latin letters and stays
    /// left to right, while the closing one sits between Latin and Arabic,
    /// takes the paragraph's direction, and is drawn mirrored on the far side
    /// of the name: "(Igor Budzhak (MIT". Drawn unwrapped, the translations
    /// carried 96 such pairs in Arabic, 41 in Persian, 111 in Urdu and 133 in
    /// Central Kurdish, most of them in the About page's credits.</para>
    ///
    /// <para>A Unicode direction mark after each bracket is the textbook fix,
    /// and the translation rules keep invisible characters out of every
    /// string. Each Latin stretch that holds a bracket pair goes into a Run of
    /// its own with FlowDirection LeftToRight instead, an embedding that keeps
    /// both brackets inside it. In a left-to-right paragraph the embedding
    /// changes nothing, so the Inlines need no rebuilding when the window's
    /// direction flips. Text with nothing to wrap stays plain Text.</para>
    ///
    /// <para>Bind these in place of Text and ToolTip. New Inlines cost a
    /// laid-out TextBlock the binding on its Text, and the next value, such
    /// as the one a language switch sends, would never arrive. A binding on
    /// this property is untouched by the Inlines. <see cref="TextDirection"/>
    /// does the same for text bound the ordinary way.</para>
    /// </summary>
    public static class BidiText
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(BidiText),
                new PropertyMetadata(null, OnTextChanged));

        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

        /// <summary>A tooltip drawn as a TextBlock that carries <see cref="TextProperty"/>.
        /// The tooltip template's own TextBlock style still wraps it.</summary>
        public static readonly DependencyProperty ToolTipProperty =
            DependencyProperty.RegisterAttached("ToolTip", typeof(string), typeof(BidiText),
                new PropertyMetadata(null, OnToolTipChanged));

        public static void SetToolTip(DependencyObject obj, string value) => obj.SetValue(ToolTipProperty, value);
        public static string GetToolTip(DependencyObject obj) => (string)obj.GetValue(ToolTipProperty);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block)
                return;
            string text = (string)e.NewValue ?? string.Empty;
            var pieces = Split(text);
            if (pieces.TrueForAll(p => !p.LeftToRight))
            {
                block.Text = text;
                return;
            }
            block.Inlines.Clear();
            foreach (var (piece, leftToRight) in pieces)
            {
                var run = new Run(piece);
                if (leftToRight)
                    run.FlowDirection = FlowDirection.LeftToRight;
                block.Inlines.Add(run);
            }
        }

        private static void OnToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
                return;
            string text = (string)e.NewValue;
            if (string.IsNullOrEmpty(text))
            {
                element.ToolTip = null;
                return;
            }
            if (element.ToolTip is not TextBlock block)
            {
                block = new TextBlock();
                element.ToolTip = block;
            }
            SetText(block, text);
        }

        /// <summary>The text cut into pieces, each flagged when it goes into a
        /// left-to-right Run. A stretch with no right-to-left letter in it is
        /// cut at each bracket whose partner lies outside it: that bracket
        /// belongs to a parenthetical holding right-to-left text, and the
        /// paragraph draws it correctly. A piece that then holds a bracket goes
        /// into the Run, from its first left-to-right letter, digit or opening
        /// bracket to its last letter, digit or closing bracket.</summary>
        internal static List<(string Text, bool LeftToRight)> Split(string text)
        {
            var pieces = new List<(string, bool)>();
            if (string.IsNullOrEmpty(text))
                return pieces;

            int done = 0;
            int i = 0;
            while (i < text.Length)
            {
                if (IsRightToLeftLetter(text[i]))
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < text.Length && !IsRightToLeftLetter(text[i]))
                    i++;

                var cuts = new SortedSet<int>();
                var open = new Stack<int>();
                for (int k = start; k < i; k++)
                {
                    if (IsOpening(text[k]))
                        open.Push(k);
                    else if (IsClosing(text[k]))
                    {
                        if (open.Count > 0)
                            open.Pop();
                        else
                            cuts.Add(k);
                    }
                }
                foreach (int k in open)
                    cuts.Add(k);
                cuts.Add(i);

                int from = start;
                foreach (int cut in cuts)
                {
                    Wrap(text, from, cut - 1, pieces, ref done);
                    from = cut + 1;
                }
            }
            if (done < text.Length)
                pieces.Add((text.Substring(done), false));
            return pieces;
        }

        /// <summary>Adds text[first..last] as a left-to-right piece when it
        /// holds a bracket, narrowed to its first left-to-right letter, digit
        /// or opening bracket and its last letter, digit or closing bracket,
        /// with the text before it as a plain piece.</summary>
        private static void Wrap(string text, int first, int last, List<(string, bool)> pieces, ref int done)
        {
            while (first <= last && !IsLeftToRight(text[first]) && !IsOpening(text[first]))
                first++;
            while (last >= first && !IsLeftToRight(text[last]) && !IsClosing(text[last]))
                last--;
            bool bracket = false;
            for (int k = first; k <= last && !bracket; k++)
                bracket = IsOpening(text[k]);
            if (!bracket)
                return;
            if (first > done)
                pieces.Add((text.Substring(done, first - done), false));
            pieces.Add((text.Substring(first, last - first + 1), true));
            done = last + 1;
        }

        private static bool IsOpening(char c) => c == '(' || c == '[' || c == '{';

        private static bool IsClosing(char c) => c == ')' || c == ']' || c == '}';

        /// <summary>The Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan and
        /// Mandaic blocks and their presentation forms.</summary>
        private static bool InRightToLeftBlock(char c) =>
            (c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF');

        private static bool IsRightToLeftLetter(char c) => InRightToLeftBlock(c) && char.IsLetter(c);

        private static bool IsLeftToRight(char c) => char.IsLetterOrDigit(c) && !InRightToLeftBlock(c);
    }
}
