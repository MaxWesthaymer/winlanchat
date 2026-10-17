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
            viewModel.RemoveAttachmentCommand.Execute(null);
            if (viewModel.HasDraftAttachment || viewModel.SendCommand.CanExecute(null))
                throw new Exception("Removing the only attachment left empty send enabled.");
            viewModel.Draft = "";
            await AttachAsync(viewModel);
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
            viewModel.RemoveAttachmentCommand.Execute(null);
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
        }
        finally
        {
            window.DataContext = original;
            Directory.Delete(directory, recursive: true);
        }
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
        public string? ChooseAttachment() => Attachment;
        public string? ChooseDestination(string fileName) => Destination;
    }
}
