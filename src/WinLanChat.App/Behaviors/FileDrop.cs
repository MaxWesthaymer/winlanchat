using System.Windows;
using System.Windows.Input;

namespace WinLanChat.App.Behaviors;

public static class FileDrop
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(FileDrop),
            new PropertyMetadata(null, OnCommandChanged));

    private static readonly DependencyPropertyKey IsDragOverPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly("IsDragOver", typeof(bool), typeof(FileDrop),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsDragOverProperty = IsDragOverPropertyKey.DependencyProperty;

    public static ICommand? GetCommand(DependencyObject target) => (ICommand?)target.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject target, ICommand? value) => target.SetValue(CommandProperty, value);
    public static bool GetIsDragOver(DependencyObject target) => (bool)target.GetValue(IsDragOverProperty);

    private static void OnCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        element.PreviewDragEnter -= OnDragOver;
        element.PreviewDragOver -= OnDragOver;
        element.PreviewDragLeave -= OnDragLeave;
        element.PreviewDrop -= OnDrop;
        element.SetValue(IsDragOverPropertyKey, false);
        if (args.NewValue is not ICommand) return;
        element.PreviewDragEnter += OnDragOver;
        element.PreviewDragOver += OnDragOver;
        element.PreviewDragLeave += OnDragLeave;
        element.PreviewDrop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        var element = (UIElement)sender;
        if (!args.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
        {
            element.SetValue(IsDragOverPropertyKey, false);
            return;
        }
        var paths = args.Data.GetData(DataFormats.FileDrop, autoConvert: false) as string[];
        bool accepted = (args.AllowedEffects & DragDropEffects.Copy) != 0
            && GetCommand(element)?.CanExecute(paths) == true;
        args.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        element.SetValue(IsDragOverPropertyKey, accepted);
        // Handle the preview event so TextBox cannot insert the filename into the draft.
        args.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs args) =>
        ((UIElement)sender).SetValue(IsDragOverPropertyKey, false);

    private static void OnDrop(object sender, DragEventArgs args)
    {
        var element = (UIElement)sender;
        element.SetValue(IsDragOverPropertyKey, false);
        if (!args.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        var paths = args.Data.GetData(DataFormats.FileDrop, autoConvert: false) as string[];
        ICommand? command = GetCommand(element);
        args.Effects = DragDropEffects.None;
        args.Handled = true;
        if ((args.AllowedEffects & DragDropEffects.Copy) == 0 || command?.CanExecute(paths) != true) return;
        args.Effects = DragDropEffects.Copy;
        command.Execute(paths);
    }
}
