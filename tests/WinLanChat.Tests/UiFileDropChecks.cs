using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinLanChat.App;
using WinLanChat.App.Behaviors;
using WinLanChat.App.ViewModels;

internal static class UiFileDropChecks
{
    internal static void CheckDisconnected(MainWindow window, string source)
    {
        var target = (UIElement)window.FindName("ChatDropTarget");
        DragEventArgs over = Raise(target, DragDrop.PreviewDragOverEvent, Files(source));
        DragEventArgs drop = Raise(target, DragDrop.PreviewDropEvent, Files(source));
        if (!over.Handled || over.Effects != DragDropEffects.None || drop.Effects != DragDropEffects.None
            || FileDrop.GetIsDragOver(target) || ((MainViewModel)window.DataContext).HasDraftAttachment)
            throw new Exception("A disconnected conversation accepted a file drop.");
    }

    internal static async Task RunAsync(MainWindow window, MainViewModel viewModel, string source, Action<string> render)
    {
        var target = (UIElement)window.FindName("ChatDropTarget");
        var input = (UIElement)window.FindName("MessageDraftInput");
        if (!target.AllowDrop || !ReferenceEquals(FileDrop.GetCommand(target), viewModel.DropFileCommand))
            throw new Exception("The chat file drop command is not bound.");

        DragEventArgs text = Raise(input, DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.UnicodeText, "text"));
        if (text.Handled || FileDrop.GetIsDragOver(target))
            throw new Exception("File drops intercepted normal text dragging.");
        var empty = Files();
        if (Raise(target, DragDrop.PreviewDragOverEvent, empty).Effects != DragDropEffects.None
            || Raise(target, DragDrop.PreviewDropEvent, empty).Effects != DragDropEffects.None
            || viewModel.HasDraftAttachment)
            throw new Exception("An empty file drop was accepted.");
        if (Raise(input, DragDrop.PreviewDropEvent, Files(source), DragDropEffects.Move).Effects != DragDropEffects.None)
            throw new Exception("A move-only file drop was accepted.");

        viewModel.Draft = "Подпись сохранена";
        DragEventArgs over = Raise(input, DragDrop.PreviewDragEnterEvent, Files(source));
        if (!over.Handled || over.Effects != DragDropEffects.Copy || !FileDrop.GetIsDragOver(target))
            throw new Exception("Dragging a file over the message input did not activate copy feedback.");
        render("window-file-drop.png");
        Raise(target, DragDrop.PreviewDragLeaveEvent, Files(source));
        if (FileDrop.GetIsDragOver(target)) throw new Exception("Drag leave retained the drop overlay.");
        Raise(target, DragDrop.PreviewDragOverEvent, Files(source));
        DragEventArgs drop = Raise(input, DragDrop.PreviewDropEvent, Files(source));
        await UntilAsync(() => viewModel.HasDraftAttachment && viewModel.DropFileCommand.CanExecute(new[] { source }));
        if (!drop.Handled || drop.Effects != DragDropEffects.Copy || FileDrop.GetIsDragOver(target)
            || viewModel.DraftAttachments.Single().FileName != Path.GetFileName(source) || viewModel.Draft != "Подпись сохранена"
            || viewModel.Messages.Count != 0 || !viewModel.SendCommand.CanExecute(null))
            throw new Exception("Dropping a file changed the draft, sent it immediately or failed to attach it.");

        string second = source + ".second";
        await File.WriteAllTextAsync(second, "Второй файл");
        try
        {
            var multiple = Files(source, second, second);
            if (Raise(input, DragDrop.PreviewDragOverEvent, multiple).Effects != DragDropEffects.Copy)
                throw new Exception("Dragging multiple files did not show copy feedback.");
            Raise(input, DragDrop.PreviewDropEvent, multiple);
            await UntilAsync(() => viewModel.DraftAttachments.Count == 2 && viewModel.DropFileCommand.CanExecute(new[] { source }));
            if (!viewModel.DraftAttachments.Select(file => file.Path).SequenceEqual(new[] { source, second })
                || viewModel.Draft != "Подпись сохранена" || viewModel.Messages.Count != 0)
                throw new Exception("A batch drop replaced the draft, duplicated a path or sent files immediately.");
            render("window-multiple-attachments.png");
            Button remove = Descendants(window).OfType<Button>().Single(button =>
                button.CommandParameter is DraftAttachmentViewModel file && file.Path == second);
            if (remove.Command is null || !remove.Command.CanExecute(remove.CommandParameter))
                throw new Exception("A draft attachment's remove button is not bound.");
            remove.Command.Execute(remove.CommandParameter);
            if (viewModel.DraftAttachments.Single().Path != source || !viewModel.SendCommand.CanExecute(null))
                throw new Exception("Removing one attachment removed the other or disabled sending.");
        }
        finally { File.Delete(second); }

        foreach (string invalid in new[] { Path.GetDirectoryName(source)!, source + ".missing" })
        {
            Raise(input, DragDrop.PreviewDropEvent, Files(source, invalid));
            await UntilAsync(() => viewModel.HasError && viewModel.DropFileCommand.CanExecute(new[] { source }));
            if (viewModel.DraftAttachments.Single().FileName != Path.GetFileName(source) || viewModel.Draft != "Подпись сохранена")
                throw new Exception("An invalid drop replaced the existing attachment or caption.");
        }
        Raise(target, DragDrop.PreviewDropEvent,
            Files(Enumerable.Range(0, 17).Select(index => source + index).ToArray()));
        await UntilAsync(() => viewModel.HasError && viewModel.DropFileCommand.CanExecute(new[] { source }));
        if (viewModel.DraftAttachments.Count != 1 || !viewModel.ErrorMessage.Contains("16"))
            throw new Exception("An oversized batch changed the draft or did not explain the limit.");
        viewModel.RemoveAttachmentCommand.Execute(viewModel.DraftAttachments.Single());
        Raise(target, DragDrop.PreviewDropEvent, Files(source));
        await UntilAsync(() => viewModel.HasDraftAttachment && viewModel.DropFileCommand.CanExecute(new[] { source }));
        if (viewModel.HasError) throw new Exception("A valid drop did not clear the previous attachment error.");
        viewModel.Draft = "";
    }

    private static DataObject Files(params string[] paths) => new(DataFormats.FileDrop, paths);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static DragEventArgs Raise(UIElement source, RoutedEvent routedEvent, IDataObject data,
        DragDropEffects allowedEffects = DragDropEffects.Copy)
    {
        // WPF creates these arguments internally for OLE. Raise the same routed preview events
        // to cover bindings and parent interception without automating Explorer or the mouse.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.None, allowedEffects, source, new Point(1, 1)], null)!;
        args.RoutedEvent = routedEvent;
        source.RaiseEvent(args);
        return args;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
