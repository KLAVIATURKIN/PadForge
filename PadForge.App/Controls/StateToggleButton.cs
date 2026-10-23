using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>A button that turns something on and off, such as a slot's
    /// power or the input engine. It looks and clicks like any Button. Its
    /// peer reports a toggle button whose state is <see cref="IsOn"/>, so a
    /// screen reader hears "on" or "off" where a plain Button reads only its
    /// name.</summary>
    public sealed class StateToggleButton : Button
    {
        public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
            nameof(IsOn), typeof(bool), typeof(StateToggleButton),
            new PropertyMetadata(false, OnIsOnChanged));

        /// <summary>Whether the thing the button turns on and off is on. It
        /// changes nothing on screen. The style shows the state through its
        /// own triggers.</summary>
        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        protected override void OnInitialized(EventArgs e)
        {
            // The Button style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Button));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new StateToggleButtonAutomationPeer(this);

        // ToggleButton.OnIsCheckedChanged raises the peer's state change the
        // same way.
        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (UIElementAutomationPeer.FromElement((StateToggleButton)d) is StateToggleButtonAutomationPeer peer)
                peer.RaiseToggleStateChanged((bool)e.OldValue, (bool)e.NewValue);
        }

        /// <summary>ToggleButtonAutomationPeer's control type and toggle
        /// pattern, over a Button. Toggle clicks the button through
        /// ButtonAutomationPeer, as Invoke would. No Invoke pattern is offered
        /// beside it, since Axe's ButtonInvokeAndTogglePatterns rejects a
        /// button with both.</summary>
        private sealed class StateToggleButtonAutomationPeer : ButtonBaseAutomationPeer, IToggleProvider
        {
            public StateToggleButtonAutomationPeer(StateToggleButton owner) : base(owner) { }

            private StateToggleButton Toggler => (StateToggleButton)Owner;

            protected override string GetClassNameCore() => "Button";

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            public override object GetPattern(PatternInterface patternInterface)
                => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

            void IToggleProvider.Toggle()
            {
                if (!IsEnabled())
                    throw new ElementNotEnabledException();
                ((IInvokeProvider)new ButtonAutomationPeer(Toggler)).Invoke();
            }

            ToggleState IToggleProvider.ToggleState => Toggler.IsOn ? ToggleState.On : ToggleState.Off;

            internal void RaiseToggleStateChanged(bool oldValue, bool newValue)
            {
                if (oldValue != newValue)
                    RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                        oldValue ? ToggleState.On : ToggleState.Off,
                        newValue ? ToggleState.On : ToggleState.Off);
            }
        }
    }
}
