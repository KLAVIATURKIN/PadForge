using System;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>An image that only decorates what sits beside it: the app
    /// icon beside "PadForge", a profile's program icon beside its name, a
    /// picture inside a button that is named already. It draws exactly as an
    /// Image does. Its peer is neither a control nor a content element, so a
    /// screen reader passes over it in place of announcing a nameless
    /// graphic. WPF has no AccessibilityView, so a peer is how an element
    /// leaves the control view.</summary>
    public sealed class DecorativeImage : Image
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The Image style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Image));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

        private sealed class QuietPeer : ImageAutomationPeer
        {
            public QuietPeer(DecorativeImage owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
