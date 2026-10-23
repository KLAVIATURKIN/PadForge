using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The automation peers that give PadForge's look-alike buttons a role
    /// and a state: SegmentButton reads as a radio button, StateToggleButton
    /// as a toggle, AvailabilityButton as unavailable when its choice is.
    /// Each keeps the Button style, and each acts through the button's own
    /// click, as ButtonAutomationPeer.Invoke does.
    /// </summary>
    public class AccessibilityPeerTests
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

        /// <summary>Runs the dispatcher until the input-priority work queued
        /// so far is done: ButtonAutomationPeer.Invoke clicks through
        /// Dispatcher.BeginInvoke at Input priority.</summary>
        private static void Pump()
            => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        private static AutomationPeer PeerOf(UIElement element)
            => UIElementAutomationPeer.CreatePeerForElement(element);

        [Fact]
        public void ASegmentButton_ReadsAsARadioButton_SelectedWhenItsChoiceIsCurrent() => RunSta(() =>
        {
            int clicks = 0;
            var tile = new SegmentButton();
            tile.Click += (s, e) => clicks++;
            var peer = PeerOf(tile);

            Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));
            var item = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.False(item.IsSelected);

            // Select clicks a choice that is not current, as a mouse click
            // does, and leaves the current one alone.
            item.Select();
            Pump();
            Assert.Equal(1, clicks);
            tile.IsSelected = true;
            Assert.True(item.IsSelected);
            item.Select();
            Pump();
            Assert.Equal(1, clicks);
            Assert.Throws<InvalidOperationException>(() => item.RemoveFromSelection());
        });

        [Fact]
        public void AStateToggleButton_ReadsAsAToggle_OnWhileItsThingIsOn() => RunSta(() =>
        {
            int clicks = 0;
            var power = new StateToggleButton();
            power.Click += (s, e) => clicks++;
            var peer = PeerOf(power);

            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            // Axe's ButtonInvokeAndTogglePatterns rejects a button with both.
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
            power.IsOn = true;
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            toggle.Toggle();
            Pump();
            Assert.Equal(1, clicks);
        });

        [Fact]
        public void AnAvailabilityButton_ReadsUnavailable_WhileItsChoiceIs_AndStillLooksAndClicksTheSame() => RunSta(() =>
        {
            int clicks = 0;
            var entry = new AvailabilityButton();
            entry.Click += (s, e) => clicks++;
            var peer = PeerOf(entry);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));

            Assert.True(peer.IsEnabled());
            invoke.Invoke();
            Pump();
            Assert.Equal(1, clicks);

            entry.IsAvailable = false;
            Assert.True(entry.IsEnabled);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
        });

        private static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit)
                    return hit;
                if (Find<T>(child) is T deeper)
                    return deeper;
            }
            return null;
        }

        [Fact]
        public void AButtonMadeByADataTemplate_KeepsTheTemplatesStyle_OrTakesTheButtonStyle() => RunSta(() =>
        {
            // A template's values rank below a local value, so a style reference
            // set in the constructor once replaced the slot card's own tile style.
            var marker = new SolidColorBrush(Colors.Tomato);
            var implicitStyle = new Style(typeof(System.Windows.Controls.Button));
            implicitStyle.Setters.Add(new Setter(Control.BackgroundProperty, marker));
            var list = new ItemsControl
            {
                ItemsSource = new[] { "slot" },
                ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                    " xmlns:c=\"clr-namespace:PadForge.Controls;assembly=PadForge\"><StackPanel>" +
                    "<c:SegmentButton><Button.Style><Style TargetType=\"Button\">" +
                    "<Setter Property=\"ToolTip\" Value=\"Tip\"/></Style></Button.Style></c:SegmentButton>" +
                    "<c:StateToggleButton/></StackPanel></DataTemplate>"),
            };
            list.Resources.Add(typeof(System.Windows.Controls.Button), implicitStyle);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = list,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var tile = Find<SegmentButton>(list);
                var power = Find<StateToggleButton>(list);
                Assert.NotNull(tile);
                Assert.NotNull(power);
                Assert.Equal("Tip", tile.ToolTip);
                Assert.NotSame(implicitStyle, tile.Style);
                Assert.Same(implicitStyle, power.Style);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void EachButton_TakesTheButtonStyle_WhenItHasNoneOfItsOwn() => RunSta(() =>
        {
            var marker = new SolidColorBrush(Colors.Tomato);
            var style = new Style(typeof(System.Windows.Controls.Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, marker));
            var panel = new StackPanel();
            panel.Resources.Add(typeof(System.Windows.Controls.Button), style);
            var buttons = new System.Windows.Controls.Button[] { new SegmentButton(), new StateToggleButton(), new AvailabilityButton() };
            foreach (var b in buttons)
                panel.Children.Add(b);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = panel,
            };
            try
            {
                window.Show();
                foreach (var b in buttons)
                {
                    Assert.Same(style, b.Style);
                    Assert.Same(marker, b.Background);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
