using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinLanChat.App;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

internal static class UiFileTransferChecks
{
    internal static async Task RunAsync(MainWindow window, Action<string> render)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WinLanChat-ui-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        object original = window.DataContext;
        try
        {
            string source = Path.Combine(directory, "Прикреплённый отчёт — октябрь 2026.txt");
            await File.WriteAllTextAsync(source, "Проверка прикрепления файла без текста");
            var dialogs = new TestDialogs { Attachment = source };
            using var available = new TcpListener(IPAddress.Loopback, 0);
            available.Start();
            int port = ((IPEndPoint)available.LocalEndpoint).Port;
            available.Stop();
            await using var viewModel = new MainViewModel(window.Dispatcher, fileDialogs: dialogs) { LocalPort = port.ToString() };
            window.DataContext = viewModel;
            await viewModel.InitializeAsync();
            UiFileDropChecks.CheckDisconnected(window, source);
            await using var peer = new ChatNode("Тест вложений");
            var offers = Channel.CreateUnbounded<FileOffer>();
            var messages = Channel.CreateUnbounded<string>();
            peer.FileOffered += offer => offers.Writer.TryWrite(offer);
            peer.MessageReceived += text => messages.Writer.TryWrite(text);
            peer.Start(0);
            await peer.ConnectAsync("127.0.0.1", port);
            await UntilAsync(() => viewModel.IsConnected);
            viewModel.Draft = "   ";
            if (viewModel.SendCommand.CanExecute(null)) throw new Exception("Empty message without a file can be sent.");
            await AttachAsync(viewModel);
            if (!viewModel.HasDraftAttachment || !viewModel.SendCommand.CanExecute(null))
                throw new Exception("Attaching a file did not enable sending an empty message.");
            viewModel.RemoveAttachmentCommand.Execute(viewModel.DraftAttachments.Single());
            if (viewModel.HasDraftAttachment || viewModel.SendCommand.CanExecute(null))
                throw new Exception("Removing the only attachment left empty send enabled.");
            viewModel.Draft = "";
            await UiFileDropChecks.RunAsync(window, viewModel, source, render);
            viewModel.SendCommand.Execute(null);
            FileOffer outgoing = await offers.Reader.ReadAsync();
            FileAttachmentViewModel sent = viewModel.Messages.Single().Attachment!;
            if (outgoing.Text != "" || viewModel.HasDraftAttachment || viewModel.Messages.Single().HasText)
                throw new Exception("Sending an attachment without a caption changed its text or retained the draft file.");
            render("window-file-without-caption.png");

            // A second message must be sendable while the first file is awaiting consent.
            viewModel.Draft = "Можно продолжать переписку";
            if (!viewModel.SendCommand.CanExecute(null)) throw new Exception("File transfer disabled chat composition.");
            viewModel.SendCommand.Execute(null);
            if (await messages.Reader.ReadAsync() != "Можно продолжать переписку") throw new Exception("Chat did not reach the peer.");

            Guid incomingId = Guid.NewGuid();
            Task incomingSend = peer.SendFileAsync(incomingId, source, "Подпись к входящему файлу");
            await UntilAsync(() => viewModel.Messages.Any(m => m.Attachment?.Id == incomingId));
            FileAttachmentViewModel incoming = viewModel.Messages.Single(m => m.Attachment?.Id == incomingId).Attachment!;
            // Closing the save dialog must leave the offer available and write no file.
            incoming.SaveCommand.Execute(null);
            if (!incoming.CanAccept || Directory.GetFiles(directory).Length != 1)
                throw new Exception("Dismissing the save dialog accepted or wrote a file.");
            await AttachAsync(viewModel);
            render("window-files-pending.png");
            viewModel.RemoveAttachmentCommand.Execute(viewModel.DraftAttachments.Single());
            await peer.AcceptFileAsync(outgoing.Id, Path.Combine(directory, "outgoing.txt"));
            await UntilAsync(() => sent.State == FileTransferState.Completed);
            dialogs.Destination = Path.Combine(directory, "incoming.txt");
            incoming.SaveCommand.Execute(null);
            await incomingSend;
            await UntilAsync(() => incoming.State == FileTransferState.Completed);
            if (incoming.SavedPath != dialogs.Destination || !File.Exists(dialogs.Destination)
                || await File.ReadAllTextAsync(dialogs.Destination) != await File.ReadAllTextAsync(source))
                throw new Exception("Save command did not save to the user-approved location.");
            render("window-files-completed.png");

            Guid declined = Guid.NewGuid();
            Task declinedSend = peer.SendFileAsync(declined, source);
            await UntilAsync(() => viewModel.Messages.Any(m => m.Attachment?.Id == declined));
            var declinedAttachment = viewModel.Messages.Single(m => m.Attachment?.Id == declined).Attachment!;
            declinedAttachment.RejectCommand.Execute(null);
            await declinedSend;
            await UntilAsync(() => declinedAttachment.State == FileTransferState.Rejected);

            await AttachAsync(viewModel);
            viewModel.SendCommand.Execute(null);
            await offers.Reader.ReadAsync();
            var cancelled = viewModel.Messages.Last().Attachment!;
            cancelled.CancelCommand.Execute(null);
            await UntilAsync(() => cancelled.State == FileTransferState.Cancelled);
            if (viewModel.HasError) throw new Exception($"Attachment UI error: {viewModel.ErrorMessage}");
            await CheckMultipleFilesAsync(window, viewModel, dialogs, peer, offers, source, directory, render);
        }
        finally
        {
            window.DataContext = original;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckMultipleFilesAsync(MainWindow window, MainViewModel viewModel, TestDialogs dialogs,
        ChatNode peer, Channel<FileOffer> offers, string source, string directory, Action<string> render)
    {
        string second = Path.Combine(directory, "Второй файл.txt");
        string empty = Path.Combine(directory, "Пустой файл.txt");
        await File.WriteAllTextAsync(second, "Данные второго файла");
        await File.WriteAllBytesAsync(empty, []);
        string[] paths = [source, second, empty];
        dialogs.Attachments = [source, second, empty, source];
        viewModel.Draft = "Несколько файлов с одной подписью";
        await AttachAsync(viewModel);
        if (!viewModel.DraftAttachments.Select(file => file.Path).SequenceEqual(paths))
            throw new Exception("Multiple selection did not retain each unique file in order.");
        dialogs.Attachments = null;
        dialogs.Attachment = null;
        viewModel.AttachFileCommand.Execute(null);
        if (viewModel.DraftAttachments.Count != 3)
            throw new Exception("Cancelling multiple selection removed existing draft attachments.");
        render("window-multiple-files-with-caption.png");

        int before = viewModel.Messages.Count;
        viewModel.SendCommand.Execute(null);
        var received = new List<FileOffer>();
        for (int i = 0; i < paths.Length; i++) received.Add(await offers.Reader.ReadAsync());
        var sent = viewModel.Messages.Skip(before).Select(message => message.Attachment!).ToArray();
        if (viewModel.HasDraftAttachment || viewModel.Draft != "" || sent.Length != paths.Length
            || sent.Any(attachment => received.All(offer => offer.Id != attachment.Id))
            || received.Count(offer => offer.Text == "Несколько файлов с одной подписью") != 1
            || received.Count(offer => offer.Text == "") != paths.Length - 1
            || sent[0].Offer.Text != "Несколько файлов с одной подписью")
            throw new Exception("Sending multiple attachments lost a file, repeated the caption or retained the draft.");

        await Task.WhenAll(sent.Select(async (attachment, index) =>
        {
            string destination = Path.Combine(directory, $"batch-{index}.received");
            await peer.AcceptFileAsync(attachment.Id, destination);
            byte[] actual = await File.ReadAllBytesAsync(destination);
            byte[] expected = await File.ReadAllBytesAsync(paths[index]);
            if (!actual.SequenceEqual(expected))
                throw new Exception("A file in a multiple-attachment send changed its contents.");
        }));
        await UntilAsync(() => sent.All(attachment => attachment.State == FileTransferState.Completed));
        render("window-multiple-files-completed.png");

        // Rejection of one file must not cancel its neighbour, including without a caption.
        dialogs.Attachments = [second, empty];
        await AttachAsync(viewModel);
        before = viewModel.Messages.Count;
        viewModel.SendCommand.Execute(null);
        for (int i = 0; i < 2; i++)
            if ((await offers.Reader.ReadAsync()).Text != "")
                throw new Exception("A file-only batch acquired an unexpected caption.");
        sent = viewModel.Messages.Skip(before).Select(message => message.Attachment!).ToArray();
        await peer.RejectFileAsync(sent[0].Id);
        await UntilAsync(() => sent[0].State == FileTransferState.Rejected);
        if (sent[1].State != FileTransferState.Waiting)
            throw new Exception("Rejecting a batch attachment changed another file's state.");
        await peer.AcceptFileAsync(sent[1].Id, Path.Combine(directory, "empty-batch.received"));
        await UntilAsync(() => sent[1].State == FileTransferState.Completed);
        if (viewModel.HasError) throw new Exception($"Multiple-attachment UI error: {viewModel.ErrorMessage}");

        // Capacity failures must preserve both the text and files so the user can retry.
        string[] fullBatch = Enumerable.Range(0, ChatNode.MaximumPendingTransfers)
            .Select(index => Path.Combine(directory, $"capacity-{index}.txt")).ToArray();
        foreach (string path in fullBatch) await File.WriteAllTextAsync(path, "Capacity check");
        dialogs.Attachments = [source];
        await AttachAsync(viewModel);
        dialogs.Attachments = fullBatch;
        viewModel.AttachFileCommand.Execute(null);
        await UntilAsync(() => viewModel.HasError && viewModel.AttachFileCommand.CanExecute(null));
        if (viewModel.DraftAttachments.Single().Path != source)
            throw new Exception("Adding past the draft capacity partially changed the draft.");
        viewModel.RemoveAttachmentCommand.Execute(viewModel.DraftAttachments.Single());
        await AttachAsync(viewModel);
        if (viewModel.DraftAttachments.Count != fullBatch.Length || viewModel.HasError)
            throw new Exception("A full batch could not be attached after freeing draft capacity.");
        render("window-full-attachment-list.png");
        before = viewModel.Messages.Count;
        viewModel.SendCommand.Execute(null);
        for (int i = 0; i < fullBatch.Length; i++) await offers.Reader.ReadAsync();
        sent = viewModel.Messages.Skip(before).Select(message => message.Attachment!).ToArray();
        dialogs.Attachments = [source];
        await AttachAsync(viewModel);
        viewModel.Draft = "Сохранить черновик при нехватке места";
        viewModel.SendCommand.Execute(null);
        await UntilAsync(() => viewModel.HasError && viewModel.SendCommand.CanExecute(null));
        if (viewModel.DraftAttachments.Single().Path != source || viewModel.Draft != "Сохранить черновик при нехватке места"
            || viewModel.Messages.Count != before + fullBatch.Length || !viewModel.ErrorMessage.Contains("16"))
            throw new Exception("Transfer capacity failure lost the draft or partially sent it.");
        foreach (var attachment in sent) attachment.CancelCommand.Execute(null);
        await UntilAsync(() => sent.All(attachment => attachment.State == FileTransferState.Cancelled));
        viewModel.RemoveAttachmentCommand.Execute(viewModel.DraftAttachments.Single());
        viewModel.Draft = "";
        viewModel.DismissErrorCommand.Execute(null);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task AttachAsync(MainViewModel viewModel)
    {
        viewModel.AttachFileCommand.Execute(null);
        await UntilAsync(() => viewModel.HasDraftAttachment && viewModel.AttachFileCommand.CanExecute(null));
    }

    private sealed class TestDialogs : IFileDialogs
    {
        public string? Attachment { get; set; }
        public string? Destination { get; set; }
        public string[]? Attachments { get; set; }
        public string[]? ChooseAttachments() => Attachments ?? (Attachment is { } path ? [path] : null);
        public string? ChooseDestination(string fileName) => Destination;
    }
}
