using System.Windows.Automation.Peers;
using Wpf.Ui.Controls;

namespace PadForge.Controls
{
    /// <summary>The sidebar footer's separator, kept out of UI Automation's
    /// control view. As a footer item it was a list item of its own, read as
    /// its type name (Wpf.Ui.Controls.NavigationViewItemSeparator), since the
    /// item wrapper takes its control-element state from the item's own peer
    /// (ItemAutomationPeer.IsControlElementCore) and Separator's peer counts
    /// as a control element.</summary>
    public sealed class QuietNavigationSeparator : NavigationViewItemSeparator
    {
        public QuietNavigationSeparator()
        {
            // An implicit style is found by the element's own type
            // (FrameworkElement.UpdateStyleProperty), so wpf-ui's separator
            // style is referenced by its key, as ContentTabControl references
            // the TabControl style.
            SetResourceReference(StyleProperty, typeof(NavigationViewItemSeparator));
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

        private sealed class QuietPeer : SeparatorAutomationPeer
        {
            public QuietPeer(QuietNavigationSeparator owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
