using System;
using System.Windows;

namespace PadForge.Controls
{
    /// <summary>
    /// Gives a control subclass the style its base class would have. WPF finds
    /// an implicit style by the element's own type
    /// (FrameworkElement.UpdateStyleProperty), so a subclass such as
    /// SegmentButton would miss the Button style and change the look.
    ///
    /// <para>The reference to the base style is set only when nothing else
    /// styles the element, once it is initialized. A style set in XAML, in
    /// code, or by the DataTemplate the element comes from stays. Set earlier,
    /// in the constructor, the reference is a local value, which outranks a
    /// template's values, so a type tile on the Dashboard slot card lost its
    /// own style and its tooltip.</para>
    /// </summary>
    internal static class BaseStyle
    {
        internal static void Take(FrameworkElement element, Type baseType)
        {
            BaseValueSource source = DependencyPropertyHelper.GetValueSource(element, FrameworkElement.StyleProperty).BaseValueSource;
            if (source is BaseValueSource.Default or BaseValueSource.Unknown)
                element.SetResourceReference(FrameworkElement.StyleProperty, baseType);
        }
    }
}
