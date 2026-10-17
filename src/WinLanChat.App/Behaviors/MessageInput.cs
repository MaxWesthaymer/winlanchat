using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinLanChat.App.Behaviors;

public static class MessageInput
{
    public static readonly DependencyProperty SendOnEnterCommandProperty =
        DependencyProperty.RegisterAttached("SendOnEnterCommand", typeof(ICommand), typeof(MessageInput),
            new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetSendOnEnterCommand(DependencyObject target) =>
        (ICommand?)target.GetValue(SendOnEnterCommandProperty);

    public static void SetSendOnEnterCommand(DependencyObject target, ICommand? value) =>
        target.SetValue(SendOnEnterCommandProperty, value);

    private static void OnCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBox textBox)
            return;

        textBox.PreviewKeyDown -= OnPreviewKeyDown;
        if (args.NewValue is ICommand)
            textBox.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;

        args.Handled = true;
        ICommand? command = GetSendOnEnterCommand((TextBox)sender);
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
