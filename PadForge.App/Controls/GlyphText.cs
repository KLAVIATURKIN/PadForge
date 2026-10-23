using System;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>A TextBlock that draws an icon glyph from Segoe MDL2 Assets
    /// or Segoe Fluent Icons: the record dot inside a button, the symbol
    /// beside a section title. It draws exactly as a TextBlock does. Its peer
    /// is neither a control nor a content element, so a screen reader passes
    /// over it in place of reading a private-use character. A plain TextBlock
    /// leaves the control view only inside a ControlTemplate
    /// (TextBlockAutomationPeer.IsControlElementCore), and a button holding a
    /// glyph keeps the name AccessibleNames gives it.</summary>
    public sealed class GlyphText : TextBlock
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The TextBlock style when nothing else styles this one. The only
            // implicit TextBlock style in the app is wpf-ui's empty one, so
            // the glyph draws as a TextBlock would inside a template too.
            BaseStyle.Take(this, typeof(TextBlock));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

        private sealed class QuietPeer : TextBlockAutomationPeer
        {
            public QuietPeer(GlyphText owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
