using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;

namespace PadForge.Controls
{
    /// <summary>A wpf-ui FontIcon that only decorates what sits beside it:
    /// the icon of a navigation entry, a symbol inside a named button. It
    /// draws exactly as a FontIcon does. FontIcon draws its glyph with a
    /// TextBlock it builds in code (FontIcon.InitializeChildren), outside any
    /// template, so TextBlockAutomationPeer exposed the glyph's private-use
    /// character to screen readers. This icon's peer is neither a control nor
    /// a content element and lists no children, so the glyph leaves the
    /// control view with it.</summary>
    public sealed class DecorativeFontIcon : Wpf.Ui.Controls.FontIcon
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The FontIcon style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Wpf.Ui.Controls.FontIcon));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);
    }

    /// <summary>A wpf-ui SymbolIcon that only decorates, hidden from screen
    /// readers as DecorativeFontIcon is.</summary>
    public sealed class DecorativeSymbolIcon : Wpf.Ui.Controls.SymbolIcon
    {
        protected override void OnInitialized(EventArgs e)
        {
            // The SymbolIcon style when nothing else styles this one.
            BaseStyle.Take(this, typeof(Wpf.Ui.Controls.SymbolIcon));
            base.OnInitialized(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);
    }

    /// <summary>A peer outside the control and content views, with no
    /// children, so nothing the icon draws reaches a screen reader.</summary>
    internal sealed class DecorativeIconPeer : FrameworkElementAutomationPeer
    {
        public DecorativeIconPeer(FrameworkElement owner) : base(owner) { }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override List<AutomationPeer> GetChildrenCore() => null;
    }
}
