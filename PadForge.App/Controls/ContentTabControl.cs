using System;
using System.Windows;
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
        static ContentTabControl()
        {
            // The tab strip and the content take focus, and the control has
            // no keyboard role of its own: Ctrl+Tab reaches TabControl's
            // OnKeyDown from the focused content. TabControl leaves itself
            // focusable while out of the Tab order (IsTabStop false), so UI
            // Automation listed a focusable pane with no name.
            FocusableProperty.OverrideMetadata(typeof(ContentTabControl), new FrameworkPropertyMetadata(false));
        }

        protected override void OnInitialized(EventArgs e)
        {
            // The TabControl style when nothing else styles this one.
            BaseStyle.Take(this, typeof(TabControl));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ContentPeer(this);

        private sealed class ContentPeer : FrameworkElementAutomationPeer
        {
            public ContentPeer(ContentTabControl owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        }
    }
}
