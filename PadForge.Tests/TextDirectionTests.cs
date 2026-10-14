using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every TextBlock in a right-to-left window takes the direction its own
    /// text needs (#505). WPF lays each TextBlock out in its window's direction
    /// and has no bracket-pair rule: a device named "Xbox One Controller
    /// (2015)" read "(2015) Xbox One Controller", and "Igor Budzhak (MIT)"
    /// followed by Arabic put the closing bracket on the far side of the name.
    /// Each test checks where WPF draws the characters, in window
    /// coordinates, and the device name test draws the same name unwatched
    /// beside it as the positive control.
    /// </summary>
    public class TextDirectionTests
    {
        // Letters, not words: right-to-left text on either side of a credit.
        private const string Before = "ابت ";
        private const string After = " ثجح";
        private const string Device = "Xbox One Controller (2015)";

        // Three later values: the first update and the ones after it must all arrive.
        private static readonly string[] Credits = { "chmfp (GPL-3.0-or-later)", "antifier (MIT)", "VRPN (BSL-1.0)" };

        private sealed class Source : INotifyPropertyChanged
        {
            private string _value;
            public string Value
            {
                get => _value;
                set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        /// <summary>A right-to-left window, watched or not, that stays as
        /// built once it has loaded.
        ///
        /// <para>UiLanguageTests calls UiLanguage.Start, which registers a
        /// class handler on Window.Loaded for the rest of the test process
        /// (EventManager class handlers cannot be removed). Once it has run,
        /// every window that loads takes the direction and the Watch value of
        /// the loading thread's UI culture, and on this English thread that
        /// turned these windows left to right with Watch off. Three tests of
        /// this class failed whenever the start happened to run first. So the
        /// window's direction, language and Watch are set again after it
        /// loads.</para></summary>
        private static Window Show(UIElement content, bool watch)
        {
            TextDirection.Start();
            var language = XmlLanguage.GetLanguage("ar");
            var window = new Window
            {
                Content = content, Width = 700, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, FlowDirection = FlowDirection.RightToLeft,
                Language = language,
            };
            if (watch) TextDirection.SetWatch(window, true);
            window.Show();
            window.Language = language;
            window.FlowDirection = FlowDirection.RightToLeft;
            TextDirection.SetWatch(window, watch);
            Drain(window);
            return window;
        }

        private static void Drain(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        /// <summary>Where the character at <paramref name="index"/> starts, in
        /// window coordinates, whichever Run holds it.</summary>
        private static double X(TextBlock block, Visual root, int index)
        {
            TextPointer p = block.ContentStart;
            int seen = 0;
            while (p != null)
            {
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    int length = p.GetTextRunLength(LogicalDirection.Forward);
                    if (index < seen + length)
                    {
                        Rect r = p.GetPositionAtOffset(index - seen).GetCharacterRect(LogicalDirection.Forward);
                        return block.TransformToAncestor(root).Transform(r.TopLeft).X;
                    }
                    seen += length;
                    p = p.GetPositionAtOffset(length);
                }
                else
                    p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
            throw new InvalidOperationException("index " + index + " is past the text");
        }

        /// <summary>The text a block shows, Runs joined.</summary>
        private static string Shown(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

        private static TextBlock Find(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock block) return block;
                var found = Find(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>"Name (note)" reads left to right on screen.</summary>
        private static void AssertReadsLeftToRight(TextBlock block, Visual root, string text, string credit)
        {
            int start = text.IndexOf(credit, StringComparison.Ordinal);
            Assert.True(start >= 0, credit + " is not in " + text);
            int open = text.IndexOf('(', start), close = text.IndexOf(')', start);
            double name = X(block, root, start), o = X(block, root, open), note = X(block, root, open + 1), c = X(block, root, close);
            Assert.True(name < o && o < note && note < c,
                $"{credit} is not drawn left to right: name {name}, ( {o}, note {note}, ) {c}");
        }

        // ─────────────────────────────────────────────
        //  Latin text takes the direction of its first letter
        // ─────────────────────────────────────────────

        [Theory]
        [InlineData("Xbox 360", false)]
        [InlineData("PadForge 5.0", false)]
        [InlineData("v5.0", false)]
        [InlineData(Device, true)]
        [InlineData("Controller (XBOX 360 For Windows)", true)]
        [InlineData("2 Players", true)]
        [InlineData("Loading…", true)]
        [InlineData("{Delete}", true)]
        [InlineData("  Xbox  ", false)]
        [InlineData("", false)]
        public void LatinTextThatARightToLeftLineDrawsDifferentlyIsFound(string text, bool needs)
            => Assert.Equal(needs, TextDirection.NeedsLeftToRight(text));

        [Fact]
        public void ADeviceNameReadsLeftToRight()
        {
            RunSta(() =>
            {
                var watched = new TextBlock { Text = Device };
                var plain = new TextBlock { Text = Device };
                Window window = Show(watched, watch: true), control = Show(plain, watch: false);
                try
                {
                    Assert.Equal(FlowDirection.LeftToRight, watched.FlowDirection);
                    AssertReadsLeftToRight(watched, window, Device, Device);
                    // The same name unwatched: WPF draws "(2015)" left of "Xbox".
                    Assert.True(X(plain, control, Device.IndexOf('(')) < X(plain, control, 0),
                        "unwatched, the name no longer draws its brackets first: WPF may lay out Latin text by its first letter now");
                }
                finally { window.Close(); control.Close(); }
            });
        }

        /// <summary>The block keeps its place: its alignment and padding are
        /// mirrored with its direction, and both come back when the window
        /// turns left to right.</summary>
        [Fact]
        public void AFlippedBlockKeepsItsPlaceAndComesBack()
        {
            RunSta(() =>
            {
                var block = new TextBlock { Text = Device, Padding = new Thickness(8, 0, 2, 0) };
                Window window = Show(block, watch: true);
                try
                {
                    Assert.Equal(TextAlignment.Right, block.TextAlignment);
                    Assert.Equal(new Thickness(2, 0, 8, 0), block.Padding);

                    TextDirection.SetWatch(window, false);
                    Drain(window);
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(TextBlock.TextAlignmentProperty));
                    Assert.Equal(new Thickness(8, 0, 2, 0), block.Padding);
                    Assert.Equal(FlowDirection.RightToLeft, block.FlowDirection);
                }
                finally { window.Close(); }
            });
        }

        [Fact]
        public void TextInALeftToRightPinIsLeftAlone()
        {
            RunSta(() =>
            {
                var block = new TextBlock { Text = Device };
                var pin = new Border { FlowDirection = FlowDirection.LeftToRight, Child = block };
                Window window = Show(pin, watch: true);
                try
                {
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    Assert.Equal(TextAlignment.Left, block.TextAlignment);
                }
                finally { window.Close(); }
            });
        }

        // ─────────────────────────────────────────────
        //  Mixed text gets its Latin credits as Runs
        // ─────────────────────────────────────────────

        /// <summary>New Inlines cost a laid-out TextBlock the binding on its
        /// Text, so the text is taken over from that binding's source: every
        /// later value arrives, with its credits as Runs.</summary>
        [Fact]
        public void BoundMixedTextKeepsItsCreditsTogether()
        {
            RunSta(() =>
            {
                var source = new Source { Value = Before + "Igor Budzhak (MIT)" + After };
                var block = new TextBlock();
                block.SetBinding(TextBlock.TextProperty, new Binding(nameof(Source.Value)) { Source = source });
                Window window = Show(block, watch: true);
                try
                {
                    AssertReadsLeftToRight(block, window, source.Value, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        source.Value = Before + credit + After;
                        Drain(window);
                        Assert.Equal(source.Value, Shown(block));
                        AssertReadsLeftToRight(block, window, source.Value, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>Text a data template binds is taken over: the Runs follow
        /// the item from then on.</summary>
        [Fact]
        public void TemplateTextFollowsItsItem()
        {
            RunSta(() =>
            {
                var item = new Source { Value = Before + "Igor Budzhak (MIT)" + After };
                var factory = new FrameworkElementFactory(typeof(TextBlock));
                factory.SetBinding(TextBlock.TextProperty, new Binding(nameof(Source.Value)));
                var list = new ItemsControl
                {
                    ItemsSource = new ObservableCollection<Source> { item },
                    ItemTemplate = new DataTemplate { VisualTree = factory },
                };
                Window window = Show(list, watch: true);
                try
                {
                    TextBlock block = Find(list);
                    Assert.NotNull(block);
                    AssertReadsLeftToRight(block, window, item.Value, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        item.Value = Before + credit + After;
                        Drain(window);
                        Assert.Equal(item.Value, Shown(block));
                        AssertReadsLeftToRight(block, window, item.Value, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>A string a ContentPresenter shows is taken over through
        /// its Content.</summary>
        [Fact]
        public void ContentPresenterTextFollowsItsContent()
        {
            RunSta(() =>
            {
                var content = new ContentControl { Content = Before + "Igor Budzhak (MIT)" + After };
                Window window = Show(content, watch: true);
                try
                {
                    TextBlock block = Find(content);
                    Assert.NotNull(block);
                    AssertReadsLeftToRight(block, window, (string)content.Content, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        content.Content = Before + credit + After;
                        Drain(window);
                        block = Find(content);
                        Assert.Equal((string)content.Content, Shown(block));
                        AssertReadsLeftToRight(block, window, (string)content.Content, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>A tooltip opens outside its window's tree and is watched
        /// as it opens.</summary>
        [Fact]
        public void ATooltipIsWatchedAsItOpens()
        {
            RunSta(() =>
            {
                var button = new Button { Content = "x", Width = 40, Height = 30 };
                var tip = new ToolTip { Content = Device, PlacementTarget = button };
                button.ToolTip = tip;
                Window window = Show(button, watch: true);
                try
                {
                    tip.IsOpen = true;
                    Drain(window);
                    TextBlock block = Find(tip);
                    Assert.NotNull(block);
                    Assert.Equal(FlowDirection.RightToLeft, tip.FlowDirection);
                    Assert.True(TextDirection.GetWatch(tip));
                    Assert.Equal(FlowDirection.LeftToRight, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    tip.IsOpen = false;
                }
                finally { window.Close(); }
            });
        }

        [Fact]
        public void TheLanguageWatchesOnlyARightToLeftWindow()
        {
            RunSta(() =>
            {
                var window = new Window();
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.RightToLeft);
                Assert.True(TextDirection.GetWatch(window));
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.LeftToRight);
                Assert.False(TextDirection.GetWatch(window));
                UiLanguage.KeepLeftToRight(window);
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.RightToLeft);
                Assert.False(TextDirection.GetWatch(window));
            });
        }
    }
}
