using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// Puts the keyboard focus on the first control of a dialog's content
    /// when a key press opened the dialog, so the next key press acts there
    /// in place of on the window itself. Set on the content area, below the
    /// dialog's header, so the focus lands on the dialog's first field or
    /// list and never on the header's Close button. A dialog opened by the
    /// mouse opens as it always did, with nothing focused: a text box given
    /// the focus would show its caret and its focused border to a mouse
    /// user. Nothing moves when something in the window already has the
    /// focus.
    /// </summary>
    public static class InitialFocus
    {
        public static readonly DependencyProperty FirstProperty = DependencyProperty.RegisterAttached(
            "First", typeof(bool), typeof(InitialFocus), new PropertyMetadata(false, OnFirstChanged));

        public static bool GetFirst(DependencyObject d) => (bool)d.GetValue(FirstProperty);

        public static void SetFirst(DependencyObject d, bool value) => d.SetValue(FirstProperty, value);

        private static void OnFirstChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement content && (bool)e.NewValue)
                content.Loaded += OnLoaded;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var content = (FrameworkElement)sender;
            content.Loaded -= OnLoaded;
            if (!KeyboardPopup.FromKeyboard)
                return;
            content.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (Window.GetWindow(content) is not Window window)
                    return;
                if (Keyboard.FocusedElement is DependencyObject focused && focused != window && window.IsAncestorOf(focused))
                    return;
                content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }));
        }
    }
}
