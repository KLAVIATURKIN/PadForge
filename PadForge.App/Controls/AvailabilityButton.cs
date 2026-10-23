using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>A button that stays clickable and unchanged on screen while
    /// the choice it offers is unavailable, its handler then doing nothing,
    /// such as an Add Controller entry for a type at capacity. Its peer
    /// reports it unavailable while <see cref="IsAvailable"/> is false, so a
    /// screen reader says so in place of offering an action that does
    /// nothing. The tooltip that gives the reason stays its help text.</summary>
    public sealed class AvailabilityButton : Button
    {
        public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
            nameof(IsAvailable), typeof(bool), typeof(AvailabilityButton),
            new PropertyMetadata(true, OnIsAvailableChanged));

        /// <summary>Whether the choice can be made now. It changes nothing on
        /// screen.</summary>
        public bool IsAvailable
        {
            get => (bool)GetValue(IsAvailableProperty);
            set => SetValue(IsAvailableProperty, value);
        }

        protected override void OnInitialized(EventArgs e)
        {
            // The Button style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Button));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new AvailabilityButtonAutomationPeer(this);

        private static void OnIsAvailableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var button = (AvailabilityButton)d;
            if (UIElementAutomationPeer.FromElement(button) is AvailabilityButtonAutomationPeer peer && button.IsEnabled)
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.IsEnabledProperty, (bool)e.OldValue, (bool)e.NewValue);
        }

        /// <summary>ButtonAutomationPeer, its Invoke pattern included, whose
        /// enabled state also needs the choice to be available. Invoke then
        /// refuses an unavailable choice the way it refuses a disabled button
        /// (ElementNotEnabledException).</summary>
        private sealed class AvailabilityButtonAutomationPeer : ButtonAutomationPeer
        {
            public AvailabilityButtonAutomationPeer(AvailabilityButton owner) : base(owner) { }

            protected override bool IsEnabledCore() => base.IsEnabledCore() && ((AvailabilityButton)Owner).IsAvailable;
        }
    }
}
