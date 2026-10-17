using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WinLanChat.App.Behaviors;

public static class ChatScroll
{
    public static readonly DependencyProperty FollowMessagesProperty =
        DependencyProperty.RegisterAttached("FollowMessages", typeof(bool), typeof(ChatScroll),
            new PropertyMetadata(false, OnChanged));
    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached("Handler", typeof(NotifyCollectionChangedEventHandler), typeof(ChatScroll));

    public static bool GetFollowMessages(DependencyObject target) => (bool)target.GetValue(FollowMessagesProperty);
    public static void SetFollowMessages(DependencyObject target, bool value) => target.SetValue(FollowMessagesProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ListBox list)
            return;
        if ((bool)args.NewValue)
        {
            list.Loaded += OnLoaded;
            list.Unloaded += OnUnloaded;
            if (list.IsLoaded)
                Subscribe(list);
        }
        else
        {
            list.Loaded -= OnLoaded;
            list.Unloaded -= OnUnloaded;
            Unsubscribe(list);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Subscribe((ListBox)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe((ListBox)sender);
    private static void Subscribe(ListBox list)
    {
        Unsubscribe(list);
        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add)
                return;
            list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (list.IsLoaded && list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            }));
        };
        list.SetValue(HandlerProperty, handler);
        ((INotifyCollectionChanged)list.Items).CollectionChanged += handler;
    }

    private static void Unsubscribe(ListBox list)
    {
        if (list.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler handler)
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged -= handler;
            list.ClearValue(HandlerProperty);
        }
    }
}
