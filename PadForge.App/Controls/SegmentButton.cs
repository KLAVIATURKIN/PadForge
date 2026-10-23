using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>A button that is one choice of a set drawn as buttons, such
    /// as a slot's controller type tiles. It looks and clicks like any
    /// Button. Its peer reports a radio button whose selected state is
    /// <see cref="IsSelected"/>, so a screen reader hears which choice is
    /// current, where a plain Button reads every tile alike.</summary>
    public sealed class SegmentButton : Button
    {
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(SegmentButton),
            new PropertyMetadata(false, OnIsSelectedChanged));

        /// <summary>Whether this choice is the current one. It changes
        /// nothing on screen. The style shows the current choice through its
        /// own triggers.</summary>
        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        protected override void OnInitialized(EventArgs e)
        {
            // The Button style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Button));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new SegmentButtonAutomationPeer(this);

        // ToggleButton.OnIsCheckedChanged raises the peer's state change the
        // same way.
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (UIElementAutomationPeer.FromElement((SegmentButton)d) is SegmentButtonAutomationPeer peer)
                peer.RaiseIsSelectedChanged((bool)e.OldValue, (bool)e.NewValue);
        }

        /// <summary>RadioButtonAutomationPeer's control type and selection
        /// item pattern, over a Button. Select clicks the button through
        /// ButtonAutomationPeer, as Invoke would, and no Invoke pattern is
        /// offered beside it.</summary>
        private sealed class SegmentButtonAutomationPeer : ButtonBaseAutomationPeer, ISelectionItemProvider
        {
            public SegmentButtonAutomationPeer(SegmentButton owner) : base(owner) { }

            private SegmentButton Segment => (SegmentButton)Owner;

            protected override string GetClassNameCore() => "RadioButton";

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;

            public override object GetPattern(PatternInterface patternInterface)
                => patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

            void ISelectionItemProvider.Select()
            {
                if (!IsEnabled())
                    throw new ElementNotEnabledException();
                if (!Segment.IsSelected)
                    ((IInvokeProvider)new ButtonAutomationPeer(Segment)).Invoke();
            }

            void ISelectionItemProvider.AddToSelection()
            {
                if (!Segment.IsSelected)
                    throw new InvalidOperationException();
            }

            void ISelectionItemProvider.RemoveFromSelection()
            {
                if (Segment.IsSelected)
                    throw new InvalidOperationException();
            }

            bool ISelectionItemProvider.IsSelected => Segment.IsSelected;

            IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => null;

            internal void RaiseIsSelectedChanged(bool oldValue, bool newValue)
            {
                RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, oldValue, newValue);
                if (newValue)
                    RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
        }
    }
}
