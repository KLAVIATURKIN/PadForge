using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard and screen-reader work changes nothing a mouse user sees.
    /// Each element it put in place of another draws the same pixels as the
    /// element it replaced, rendered side by side here: a ClickBorder in
    /// place of a Border, a GlyphText in place of a TextBlock, the motor test
    /// area's ClickBorder around the Grid that held its attributes, and the
    /// Paired PCs list inside a Border that holds its margin, empty or not.
    /// </summary>
    public class AccessibilityLooksTheSameTests
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
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static byte[] Pixels(FrameworkElement element, int width, int height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        private static void SameDrawing(FrameworkElement before, FrameworkElement after, int width = 160, int height = 60)
        {
            byte[] a = Pixels(before, width, height);
            byte[] b = Pixels(after, width, height);
            Assert.True(a.Any(p => p != 0), "the reference drew nothing, so the comparison proves nothing");
            int differing = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                    differing++;
            Assert.True(differing == 0, differing + " pixel(s) differ");
        }

        private static Style ChipStyle()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x33))));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x58, 0xB6, 0xE4))));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(6)));
            style.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(8, 3, 8, 3)));
            return style;
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        [Fact]
        public void AClickBorder_DrawsAsTheBorderItReplaced() => RunSta(() =>
        {
            var border = new Border { Style = ChipStyle(), Child = Label("GAMEPAD 9"), Cursor = System.Windows.Input.Cursors.Hand };
            var chip = new ClickBorder { Style = ChipStyle(), Child = Label("GAMEPAD 9"), Cursor = System.Windows.Input.Cursors.Hand, IsSelected = false };
            SameDrawing(border, chip);

            var selected = new ClickBorder { Style = ChipStyle(), Child = Label("ALL 36"), IsSelected = true };
            SameDrawing(new Border { Style = ChipStyle(), Child = Label("ALL 36") }, selected);

            var preset = new ClickBorder { Style = ChipStyle(), Child = Label("Preset"), IsChecked = true, ClickMode = ClickMode.Press };
            SameDrawing(new Border { Style = ChipStyle(), Child = Label("Preset") }, preset);
        });

        [Fact]
        public void AGlyphText_DrawsAsTheTextBlockItReplaced() => RunSta(() =>
        {
            TextBlock Arrow(TextBlock tb)
            {
                tb.Text = "▾";
                tb.FontSize = 9;
                tb.Margin = new Thickness(5, 0, 0, 0);
                tb.Opacity = 0.7;
                tb.Foreground = Brushes.White;
                tb.VerticalAlignment = VerticalAlignment.Center;
                return tb;
            }
            SameDrawing(Arrow(new TextBlock()), Arrow(new GlyphText()), 40, 20);
        });

        [Fact]
        public void TheMotorTestArea_DrawsAsTheGridItWrapped() => RunSta(() =>
        {
            Grid Content(Grid grid)
            {
                grid.Children.Add(new Border { Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(0x25, 0x30, 0x49)) });
                grid.Children.Add(new TextBlock { Text = "Left Motor", FontSize = 13, Foreground = Brushes.Gainsboro, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                return grid;
            }
            var before = Content(new Grid { Margin = new Thickness(0, 0, 12, 0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand });
            var after = new ClickBorder
            {
                Margin = new Thickness(0, 0, 12, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ClickMode = ClickMode.Press,
                Child = Content(new Grid()),
            };
            SameDrawing(before, after, 200, 40);
        });

        [Fact]
        public void ThePairedPcsList_KeepsItsPlace_EmptyOrNot() => RunSta(() =>
        {
            Style Collapsing()
            {
                var style = new Style(typeof(ItemsControl));
                style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
                var empty = new Trigger { Property = ItemsControl.HasItemsProperty, Value = false };
                empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
                style.Triggers.Add(empty);
                return style;
            }
            StackPanel Card(bool wrapped, string[] peers)
            {
                var list = new ItemsControl { ItemsSource = peers };
                list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
                list.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
                list.ItemTemplate.VisualTree.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                var panel = new StackPanel { Background = Brushes.Black };
                panel.Children.Add(Label("Paired PCs"));
                if (wrapped)
                {
                    list.Style = Collapsing();
                    panel.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = list });
                }
                else
                {
                    list.Margin = new Thickness(0, 12, 0, 0);
                    panel.Children.Add(list);
                }
                panel.Children.Add(Label("Revoke All"));
                return panel;
            }
            SameDrawing(Card(false, Array.Empty<string>()), Card(true, Array.Empty<string>()), 200, 80);
            SameDrawing(Card(false, new[] { "OFFICE-PC", "DEN-LAPTOP" }), Card(true, new[] { "OFFICE-PC", "DEN-LAPTOP" }), 200, 100);
        });

        [Fact]
        public void ACurveEditorWithoutKeyboardFocus_MarksNoPoint() => RunSta(() =>
        {
            var editor = new CurveEditor { CurveString = "0,0;0.5,0.25;1,1", IsSigned = false, IsFullWidth = true, ChartHeight = 150, Width = 400 };
            var window = new Window
            {
                Width = 500, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = editor,
            };
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.False(editor.IsKeyboardFocused);
                var canvas = (Canvas)editor.FindName("ChartCanvas");
                // The handles are the 10-pixel ellipses. The 7-pixel live dot
                // is ember-hot by design.
                var handles = canvas.Children.OfType<Ellipse>().Where(e => e.Width == 10).ToList();
                Assert.Equal(3, handles.Count);
                var emberHot = Color.FromRgb(0xFF, 0xA2, 0x4D);
                Assert.DoesNotContain(handles, h => h.Fill is SolidColorBrush b && b.Color == emberHot);
                Assert.All(handles, h => Assert.Null(h.Effect));
                // The coordinate readout shows only during a drag, as before.
                Assert.DoesNotContain(canvas.Children.OfType<TextBlock>(), t => t.Visibility == Visibility.Visible);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
