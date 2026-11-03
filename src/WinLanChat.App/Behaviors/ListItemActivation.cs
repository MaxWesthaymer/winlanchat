using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinLanChat.App.Behaviors;

public static class ListItemActivation
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(ListItemActivation),
            new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject target) => (ICommand?)target.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject target, ICommand? value) => target.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ListBox listBox)
            return;

        listBox.MouseLeftButtonUp -= OnMouseLeftButtonUp;
        listBox.PreviewKeyDown -= OnPreviewKeyDown;
        if (args.NewValue is ICommand)
        {
            listBox.MouseLeftButtonUp += OnMouseLeftButtonUp;
            listBox.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        var listBox = (ListBox)sender;
        if (args.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem { IsSelected: true })
            Activate(listBox, args);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !args.IsRepeat)
            Activate((ListBox)sender, args);
    }

    private static void Activate(ListBox listBox, RoutedEventArgs args)
    {
        ICommand? command = GetCommand(listBox);
        if (listBox.IsEnabled && listBox.SelectedItem is not null && command?.CanExecute(null) == true)
        {
            args.Handled = true;
            command.Execute(null);
        }
    }
}
