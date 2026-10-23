using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #333: clicking the slot type badge crashed the window.
    /// The badge's label is a TextBlock built from inline Runs, so WPF
    /// reported the Run itself as the mouse event's OriginalSource, and the
    /// window-level focus-clearing walk called VisualTreeHelper.GetParent
    /// on it, which THROWS InvalidOperationException for anything that is
    /// not a Visual/Visual3D. The walk's own logical-tree fallback could
    /// never save it: the fallback ran after the throwing call and was
    /// gated on FrameworkElement, which a Run (FrameworkContentElement)
    /// is not. TreeWalk.Parent is now the single upward step for every
    /// walk seeded from OriginalSource: Visuals climb the visual tree,
    /// ContentElements climb the logical tree back to their hosting
    /// Visual.
    ///
    /// <para>WPF elements demand STA, so each test body runs on a
    /// dedicated STA thread and rethrows anything it caught.</para>
    /// </summary>
    public class TreeWalkTests
    {
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
            Assert.True(t.Join(15000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        /// <summary>The disease itself, pinned so nobody "simplifies" the
        /// guard away: the raw call the old code made really does throw on
        /// a Run. If a future WPF version stops throwing, this goes red
        /// and the guard can be reconsidered.</summary>
        [Fact]
        public void RawVisualTreeHelper_StillThrowsOnARun()
        {
            RunSta(() =>
            {
                var run = new Run("XBOX 1");
                new TextBlock().Inlines.Add(run);
                Assert.Throws<InvalidOperationException>(
                    () => VisualTreeHelper.GetParent(run));
            });
        }

        /// <summary>The cure: TreeWalk steps a Run to its hosting TextBlock
        /// through the logical tree instead of throwing.</summary>
        [Fact]
        public void TreeWalkParent_StepsARunToItsTextBlock()
        {
            RunSta(() =>
            {
                var run = new Run("XBOX 1");
                var tb = new TextBlock();
                tb.Inlines.Add(run);
                Assert.Same(tb, TreeWalk.Parent(run));
            });
        }

        /// <summary>The reported crash, end to end: the focus-clearing walk
        /// seeded with a Run must complete (and report "does not preserve
        /// focus", since nothing in a plain badge is an input control)
        /// rather than throw.</summary>
        [Fact]
        public void ClearFocusWalk_SurvivesARunAsTheClickTarget()
        {
            RunSta(() =>
            {
                var run = new Run("XBOX 1");
                var tb = new TextBlock();
                tb.Inlines.Add(run);
                var host = new Border { Child = tb };

                Assert.False(MainWindow.ClickTargetPreservesFocus(run));
                GC.KeepAlive(host);
            });
        }

        /// <summary>The preserve list still works through a ContentElement
        /// seed: a Run inside a TextBox's context (Hyperlink inside a
        /// ListBox item is the everyday case) must find the ancestor input
        /// control across the logical/visual seam.</summary>
        [Fact]
        public void ClearFocusWalk_FindsAPreservedAncestor_AcrossTheSeam()
        {
            RunSta(() =>
            {
                var run = new Run("device name");
                var tb = new TextBlock();
                tb.Inlines.Add(run);
                var lb = new ListBox();
                lb.Items.Add(tb);
                // Without template realization the visual chain is not
                // built, so the walk crosses Run -> TextBlock logically,
                // then TextBlock's logical parent chain reaches the ListBox.
                Assert.True(MainWindow.ClickTargetPreservesFocus(run));
            });
        }

        /// <summary>Null seed is a no-op, matching every caller's
        /// "as DependencyObject" seed shape.</summary>
        [Fact]
        public void TreeWalkParent_NullSeed_ReturnsNull()
        {
            Assert.Null(TreeWalk.Parent(null));
        }

        /// <summary>The same disease in Visual.IsAncestorOf: it throws on a
        /// ContentElement (VisualTreeUtils.AsVisual), and the dialog focus
        /// check reads Keyboard.FocusedElement, which a Hyperlink can
        /// be.</summary>
        [Fact]
        public void RawIsAncestorOf_StillThrowsOnAHyperlink()
        {
            RunSta(() =>
            {
                var link = new Hyperlink(new Run("Docs"));
                var host = new Border { Child = new TextBlock(link) };
                Assert.Throws<InvalidOperationException>(() => host.IsAncestorOf(link));
            });
        }

        /// <summary>The dialog focus check (InitialFocus) reads a focused
        /// Hyperlink in the window as focus inside it, and the window itself,
        /// a Hyperlink elsewhere and no focus as focus elsewhere, without
        /// throwing.</summary>
        [Fact]
        public void TheDialogFocusCheck_ReadsAFocusedHyperlinkWithoutThrowing()
        {
            RunSta(() =>
            {
                var link = new Hyperlink(new Run("Docs"));
                var button = new Button();
                var window = new Window
                {
                    Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000,
                    Content = new StackPanel { Children = { new TextBlock(link), button } },
                };
                var outside = new Hyperlink(new Run("Elsewhere"));
                var elsewhere = new TextBlock(outside);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Assert.True(InitialFocus.FocusIsInside(window, link));
                    Assert.True(InitialFocus.FocusIsInside(window, button));
                    Assert.False(InitialFocus.FocusIsInside(window, window));
                    Assert.False(InitialFocus.FocusIsInside(window, outside));
                    Assert.False(InitialFocus.FocusIsInside(window, null));
                }
                finally
                {
                    window.Close();
                }
                GC.KeepAlive(elsewhere);
            });
        }
    }
}
