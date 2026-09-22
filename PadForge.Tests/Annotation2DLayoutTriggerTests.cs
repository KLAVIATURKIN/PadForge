using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 2D preview's annotation overlay drew nothing when it was turned
    /// on. The chips were built, but the pass that places them read the
    /// overlay canvas at 0 wide and returned, and the only thing that re-ran
    /// it was the VIEW's SizeChanged. That event never fires when a child
    /// goes from collapsed to shown, because the view's own size does not
    /// change and SizeChanged does not bubble. Resizing the window made the
    /// chips appear. The 3D twin re-runs its layout on a timer and never
    /// hit this.
    ///
    /// The view itself cannot be built here: its markup needs an app-level
    /// style, and the suite runs with no Application object (see
    /// DsuAutomaticMotionTests). So two tests pin the WPF behavior the fix
    /// relies on, on a grid and canvas shaped like the view's, and a
    /// source-shape test pins that the view listens to the canvas.
    /// </summary>
    public class Annotation2DLayoutTriggerTests
    {
        /// <summary>The first time the toggle shows the canvas it reads 0
        /// wide, and only the canvas reports the size it gets next.</summary>
        [Fact]
        public void FirstShow_ReadsZeroWide_AndOnlyTheCanvasReportsItsSize()
        {
            RunSta(() =>
            {
                var (grid, canvas) = Layout(800, 500);
                int gridEvents = 0, canvasEvents = 0;
                grid.SizeChanged += (s, e) => gridEvents++;
                canvas.SizeChanged += (s, e) => canvasEvents++;

                canvas.Visibility = Visibility.Visible;
                // What LayoutAnnotations saw when the toggle turned it on.
                Assert.Equal(0, canvas.ActualWidth);

                grid.UpdateLayout();
                Assert.Equal(800, canvas.ActualWidth);
                Assert.Equal(1, canvasEvents);
                Assert.Equal(0, gridEvents);
            });
        }

        /// <summary>A window resized while the overlay was off: the canvas
        /// comes back at its old size, and only its own event carries the
        /// new one. The view's event fired during the resize, while the
        /// overlay was off and the layout pass returned early.</summary>
        [Fact]
        public void ShowAfterAResizeWhileHidden_ReadsTheOldSize_AndOnlyTheCanvasReportsTheNewOne()
        {
            RunSta(() =>
            {
                var (grid, canvas) = Layout(800, 500);
                canvas.Visibility = Visibility.Visible;
                grid.UpdateLayout();
                canvas.Visibility = Visibility.Collapsed;
                grid.UpdateLayout();
                grid.Width = 1000;
                grid.Height = 600;
                var size = new Size(1000, 600);
                grid.Measure(size);
                grid.Arrange(new Rect(size));
                grid.UpdateLayout();

                int gridEvents = 0, canvasEvents = 0;
                grid.SizeChanged += (s, e) => gridEvents++;
                canvas.SizeChanged += (s, e) => canvasEvents++;

                canvas.Visibility = Visibility.Visible;
                Assert.Equal(800, canvas.ActualWidth);

                grid.UpdateLayout();
                Assert.Equal(1000, canvas.ActualWidth);
                Assert.Equal(1, canvasEvents);
                Assert.Equal(0, gridEvents);
            });
        }

        [Fact]
        public void The2DViewLaysOutTheAnnotationsWhenTheCanvasSizeChanges()
        {
            string src = Live(Read("PadForge.App/Views/ControllerModel2DView.xaml.cs"));
            Assert.Contains("AnnotationCanvas.SizeChanged += (s, e) => LayoutAnnotations();",
                src, StringComparison.Ordinal);
        }

        /// <summary>A grid holding a collapsed, clipped canvas and nothing
        /// else, the shape of ControllerModel2DView's overlay, laid out once
        /// at the given size.</summary>
        private static (Grid Grid, Canvas Canvas) Layout(double width, double height)
        {
            var canvas = new Canvas { Visibility = Visibility.Collapsed, ClipToBounds = true };
            var grid = new Grid { Width = width, Height = height };
            grid.Children.Add(canvas);
            var size = new Size(width, height);
            grid.Measure(size);
            grid.Arrange(new Rect(size));
            grid.UpdateLayout();
            return (grid, canvas);
        }

        private static string Read(string rel, [CallerFilePath] string me = null)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path);
            return File.ReadAllText(path);
        }

        /// <summary>Drops whole-line comments, so a commented-out line
        /// cannot satisfy the assertion.</summary>
        private static string Live(string body)
            => string.Join("\n", body.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "layout run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
