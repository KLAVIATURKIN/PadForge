using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>A TabControl whose template shows only the selected content,
    /// for a page that draws its own tab strip (the controller page's
    /// RadioButtons). WPF's TabControl peer lists its children through the
    /// items host, and with no TabPanel in the template it lists none
    /// (ItemsControlAutomationPeer.GetChildrenCore returns null when
    /// IsVirtualized and ItemsHost is null), so every control on every tab
    /// was missing from UI Automation and from screen readers. This peer is a
    /// pane whose children are the visual children, which hold only the
    /// selected content, as TabItemAutomationPeer.GetChildrenCore exposes a
    /// selected tab's content.</summary>
    public sealed class ContentTabControl : TabControl
    {
        public ContentTabControl()
        {
            // An implicit style is found by the element's own type
            // (FrameworkElement.UpdateStyleProperty), so the TabControl style
            // is referenced by its key, as SettingResetButton references its
            // own style.
            SetResourceReference(StyleProperty, typeof(TabControl));
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ContentPeer(this);

        private sealed class ContentPeer : FrameworkElementAutomationPeer
        {
            public ContentPeer(ContentTabControl owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        }
    }
}
