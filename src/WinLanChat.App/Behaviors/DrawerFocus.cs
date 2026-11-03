using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinLanChat.App.Behaviors;

/// <summary>Moves keyboard focus into a drawer and returns it to the menu when closed.</summary>
public static class DrawerFocus
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(DrawerFocus), new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty ReturnTargetProperty = DependencyProperty.RegisterAttached(
        "ReturnTarget", typeof(UIElement), typeof(DrawerFocus));

    public static bool GetIsOpen(DependencyObject target) => (bool)target.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject target, bool value) => target.SetValue(IsOpenProperty, value);
    public static UIElement? GetReturnTarget(DependencyObject target) => (UIElement?)target.GetValue(ReturnTargetProperty);
    public static void SetReturnTarget(DependencyObject target, UIElement? value) => target.SetValue(ReturnTargetProperty, value);

    private static void OnIsOpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement drawer)
            return;

        drawer.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (GetIsOpen(drawer))
                drawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else
                GetReturnTarget(drawer)?.Focus();
        });
    }
}
