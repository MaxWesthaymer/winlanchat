using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

internal static class FileTransferChecks
{
    internal static async Task ExchangeAsync()
    {
        await using var pair = await Pair.CreateAsync();
        foreach (var (size, text) in new[] { (0, ""), (98_321, "   "), (2_097_163, "Отчёт за октябрь\nПодпись к файлу") })
        {
            string source = Path.Combine(pair.Directory, "Отчёт 👋.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(size));
            Guid id = Guid.NewGuid();
            Task<FileTransferUpdate> finished = TerminalAsync(pair.A, id);
            Task send = pair.A.SendFileAsync(id, source, text);
            FileOffer offer = await pair.OffersB.Reader.ReadAsync();
            if (offer.Id != id || offer.Text != text || offer.FileName != Path.GetFileName(source) || offer.Length != size)
                throw new Exception("File metadata was not preserved.");
            if (System.IO.Directory.GetFiles(pair.Downloads).Length != 0)
                throw new Exception("A file was written without consent.");
            // Waiting for consent must leave both directions of the conversation usable.
            await pair.A.SendAsync("Сообщение во время ожидания");
            if (await pair.TextB.Reader.ReadAsync() != "Сообщение во время ожидания") throw new Exception("Chat stalled.");
            string destination = Path.Combine(pair.Downloads, "выбранное имя.bin");
            await Task.WhenAll(send, pair.B.AcceptFileAsync(id, destination));
            if ((await finished).State != FileTransferState.Completed) throw new Exception("Sender did not receive save confirmation.");
            if (!await SameFileAsync(source, destination)) throw new Exception("File bytes were corrupted.");
            File.Delete(destination);
            if (System.IO.Directory.GetFiles(pair.Downloads).Length != 0) throw new Exception("Temporary file was leaked.");
        }

        string first = Path.Combine(pair.Directory, "first.bin");
        string second = Path.Combine(pair.Directory, "second.bin");
        await File.WriteAllBytesAsync(first, RandomNumberGenerator.GetBytes(1_000_001));
        await File.WriteAllBytesAsync(second, RandomNumberGenerator.GetBytes(1_234_567));
        Guid one = Guid.NewGuid(), two = Guid.NewGuid();
        Task sendOne = pair.A.SendFileAsync(one, first);
        Task sendTwo = pair.B.SendFileAsync(two, second, "Встречная передача");
        await pair.OffersB.Reader.ReadAsync();
        await pair.OffersA.Reader.ReadAsync();
        string targetOne = Path.Combine(pair.Downloads, "one.bin");
        string targetTwo = Path.Combine(pair.Downloads, "two.bin");
        await Task.WhenAll(sendOne, sendTwo, pair.A.AcceptFileAsync(two, targetTwo), pair.B.AcceptFileAsync(one, targetOne),
            Task.WhenAll(Enumerable.Range(0, 20).Select(i => pair.A.SendAsync($"chat {i}"))));
        for (int i = 0; i < 20; i++)
            if (await pair.TextB.Reader.ReadAsync() != $"chat {i}") throw new Exception("Chat frames interleaved with file data.");
        if (!await SameFileAsync(first, targetOne) || !await SameFileAsync(second, targetTwo))
            throw new Exception("Bidirectional transfers were corrupted.");
    }

    internal static async Task RejectCancelAndDisconnectAsync()
    {
        await using var pair = await Pair.CreateAsync();
        string source = Path.Combine(pair.Directory, "large.bin");
        // A sparse source is much larger than a chunk, without allocating a whole file in memory.
        using (var file = File.Create(source)) file.SetLength(128L * 1024 * 1024);
        Guid rejected = Guid.NewGuid();
        Task<FileTransferUpdate> rejectedState = TerminalAsync(pair.A, rejected);
        Task send = pair.A.SendFileAsync(rejected, source);
        await pair.OffersB.Reader.ReadAsync();
        await pair.B.RejectFileAsync(rejected);
        await send;
        if ((await rejectedState).State != FileTransferState.Rejected) throw new Exception("Rejection was not acknowledged.");

        Guid pending = Guid.NewGuid();
        Task<FileTransferUpdate> pendingState = TerminalAsync(pair.B, pending);
        using (var cancel = new CancellationTokenSource())
        {
            send = pair.A.SendFileAsync(pending, source, cancellationToken: cancel.Token);
            await pair.OffersB.Reader.ReadAsync();
            cancel.Cancel();
            await ExpectCancellationAsync(send);
            if ((await pendingState).State != FileTransferState.Cancelled) throw new Exception("Pending offer did not cancel.");
        }

        // Cancel from either side after actual bytes reach disk. Existing destination must survive.
        foreach (bool receiverCancels in new[] { true, false })
        {
            Guid active = Guid.NewGuid();
            string destination = Path.Combine(pair.Downloads, "existing.bin");
            await File.WriteAllTextAsync(destination, "preserve existing file");
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnProgress(FileTransferUpdate update)
            {
                if (update.Id == active && update.State == FileTransferState.Transferring && update.BytesTransferred > 0)
                {
                    pair.B.FileTransferChanged -= OnProgress;
                    Task task = (receiverCancels ? pair.B : pair.A).CancelFileAsync(active);
                    _ = task.ContinueWith(_ => cancellation.TrySetResult(), TaskScheduler.Default);
                }
            }
            pair.B.FileTransferChanged += OnProgress;
            Task<FileTransferUpdate> receiverState = TerminalAsync(pair.B, active);
            send = pair.A.SendFileAsync(active, source);
            await pair.OffersB.Reader.ReadAsync();
            Task receive = pair.B.AcceptFileAsync(active, destination, overwrite: true);
            await cancellation.Task;
            await ExpectCancellationAsync(send);
            await receive;
            if ((await receiverState).State != FileTransferState.Cancelled
                || await File.ReadAllTextAsync(destination) != "preserve existing file"
                || System.IO.Directory.GetFiles(pair.Downloads, "*.part").Length != 0)
                throw new Exception("Cancellation damaged the destination or leaked a partial file.");
            File.Delete(destination);
            await pair.A.SendAsync("after cancellation");
            if (await pair.TextB.Reader.ReadAsync() != "after cancellation") throw new Exception("Cancellation broke chat.");
        }

        Guid disconnected = Guid.NewGuid();
        string interrupted = Path.Combine(pair.Downloads, "interrupted.bin");
        Task<FileTransferUpdate> disconnectedState = TerminalAsync(pair.B, disconnected);
        void DisconnectOnProgress(FileTransferUpdate update)
        {
            if (update.Id == disconnected && update.BytesTransferred > 0) pair.A.Disconnect();
        }
        pair.B.FileTransferChanged += DisconnectOnProgress;
        send = pair.A.SendFileAsync(disconnected, source);
        await pair.OffersB.Reader.ReadAsync();
        Task receiving = pair.B.AcceptFileAsync(disconnected, interrupted);
        try { await send; throw new Exception("Disconnected transfer succeeded."); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        await receiving;
        if ((await disconnectedState).State != FileTransferState.Failed || File.Exists(interrupted)
            || System.IO.Directory.GetFiles(pair.Downloads).Length != 0)
            throw new Exception("Disconnect did not clean up the partial file.");
    }

    internal static async Task ValidateAsync()
    {
        foreach (string name in new[] { "../secret.txt", "..\\secret.txt", "C:\\temp.txt", "a/b", "a:b", "CON", "nul.txt",
            "LPT1.log", "com¹.txt", "x\n.bin", "..", "name.", "name ", new string('a', 256) })
        {
            if (ChatProtocol.IsValidFileName(name)) throw new Exception($"Unsafe filename accepted: {name}");
            await using var node = new ChatNode("Receiver");
            node.Start(0);
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node.Error += _ => rejected.TrySetResult();
            using var raw = new TcpClient();
            await raw.ConnectAsync("127.0.0.1", node.ListeningPort);
            await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("hello", Name: "Raw", SupportsFiles: true), default);
            await ChatProtocol.ReadAsync(raw.GetStream(), default);
            await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("file-offer", Text: "", TransferId: Guid.NewGuid(), FileName: name, FileSize: 1), default);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        foreach (var message in new[]
        {
            new WireMessage("file-offer", Text: "", TransferId: Guid.NewGuid(), FileName: "safe.bin", FileSize: -1),
            new WireMessage("file-offer", Text: "", TransferId: Guid.NewGuid(), FileName: "safe.bin", FileSize: long.MaxValue),
            new WireMessage("file-offer", Text: "", TransferId: Guid.Empty, FileName: "safe.bin", FileSize: 1),
            new WireMessage("file-offer", Text: new string('x', 8001), TransferId: Guid.NewGuid(), FileName: "safe.bin", FileSize: 1),
            new WireMessage("file-chunk", TransferId: Guid.NewGuid(), Data: new byte[ChatProtocol.FileChunkBytes + 1]),
            new WireMessage(null!)
        })
        {
            await using var node = new ChatNode("Receiver");
            node.Start(0);
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node.Error += _ => rejected.TrySetResult();
            using var raw = new TcpClient();
            await raw.ConnectAsync("127.0.0.1", node.ListeningPort);
            await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("hello", Name: "Raw", SupportsFiles: true), default);
            await ChatProtocol.ReadAsync(raw.GetStream(), default);
            await ChatProtocol.WriteAsync(raw.GetStream(), message, default);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await using var pair = await Pair.CreateAsync();
        string source = Path.Combine(pair.Directory, "source.txt");
        await File.WriteAllTextAsync(source, "new content");
        string destination = Path.Combine(pair.Downloads, "existing.txt");
        await File.WriteAllTextAsync(destination, "old content");
        Guid id = Guid.NewGuid();
        Task send = pair.A.SendFileAsync(id, source);
        await pair.OffersB.Reader.ReadAsync();
        try { await pair.B.AcceptFileAsync(id, destination); throw new Exception("Destination overwritten without permission."); }
        catch (IOException) { }
        await ExpectCancellationAsync(send);
        if (await File.ReadAllTextAsync(destination) != "old content") throw new Exception("Destination was damaged.");
        await pair.A.SendAsync("after invalid save");
        if (await pair.TextB.Reader.ReadAsync() != "after invalid save") throw new Exception("A save failure broke chat.");
    }

    internal static async Task RejectInvalidPayloadsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WinLanChat-invalid-files-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            foreach (string scenario in new[] { "no-consent", "short", "oversized", "duplicate", "shutdown" })
            {
                await using var node = new ChatNode("Receiver");
                node.Start(0);
                var offered = new TaskCompletionSource<FileOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
                var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                node.FileOffered += offer => offered.TrySetResult(offer);
                node.Error += _ => error.TrySetResult();
                Guid id = Guid.NewGuid();
                Task<FileTransferUpdate> state = TerminalAsync(node, id);
                using var raw = new TcpClient();
                await raw.ConnectAsync("127.0.0.1", node.ListeningPort);
                await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("hello", Name: "Raw", SupportsFiles: true), default);
                await ChatProtocol.ReadAsync(raw.GetStream(), default);
                var offerFrame = new WireMessage("file-offer", Text: "", TransferId: id, FileName: "file.bin", FileSize: 2);
                await ChatProtocol.WriteAsync(raw.GetStream(), offerFrame, default);
                await offered.Task;
                if (scenario is "no-consent" or "duplicate")
                {
                    await ChatProtocol.WriteAsync(raw.GetStream(), scenario == "duplicate" ? offerFrame
                        : new WireMessage("file-chunk", TransferId: id, Data: [1]), default);
                    await error.Task;
                    await state;
                }
                else
                {
                    string destination = Path.Combine(directory, "existing.bin");
                    await File.WriteAllTextAsync(destination, "preserve me");
                    Task receive = node.AcceptFileAsync(id, destination, overwrite: true);
                    if ((await ChatProtocol.ReadAsync(raw.GetStream(), default)).Kind != "file-accept")
                        throw new Exception("Expected file acceptance.");
                    await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("file-chunk", TransferId: id,
                        Data: scenario == "oversized" ? [1, 2, 3] : [1]), default);
                    if (scenario == "shutdown")
                        await node.DisposeAsync();
                    else
                        await ChatProtocol.WriteAsync(raw.GetStream(), new WireMessage("file-end", TransferId: id), default);
                    try { await receive; }
                    catch (OperationCanceledException) when (scenario == "shutdown") { }
                    if ((await state).State is not (FileTransferState.Failed or FileTransferState.Cancelled)
                        || await File.ReadAllTextAsync(destination) != "preserve me")
                        throw new Exception("An incomplete or oversized file replaced the destination.");
                    File.Delete(destination);
                }
                if (System.IO.Directory.GetFiles(directory).Length != 0)
                    throw new Exception($"Partial file leaked after {scenario}.");
            }
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task; throw new Exception("Cancelled transfer unexpectedly succeeded."); }
        catch (OperationCanceledException) { }
    }

    private static Task<FileTransferUpdate> TerminalAsync(ChatNode node, Guid id)
    {
        var completion = new TaskCompletionSource<FileTransferUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.FileTransferChanged += update =>
        {
            if (update.Id == id && update.State is not (FileTransferState.Waiting or FileTransferState.Transferring))
                completion.TrySetResult(update);
        };
        return completion.Task;
    }

    private static async Task<bool> SameFileAsync(string a, string b)
    {
        await using var first = File.OpenRead(a);
        await using var second = File.OpenRead(b);
        byte[] firstHash = await SHA256.HashDataAsync(first);
        byte[] secondHash = await SHA256.HashDataAsync(second);
        return firstHash.SequenceEqual(secondHash);
    }

    private sealed class Pair : IAsyncDisposable
    {
        public ChatNode A { get; } = new("A");
        public ChatNode B { get; } = new("B");
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"WinLanChat-files-{Guid.NewGuid():N}");
        public string Downloads => Path.Combine(Directory, "downloads");
        public Channel<FileOffer> OffersA { get; } = Channel.CreateUnbounded<FileOffer>();
        public Channel<FileOffer> OffersB { get; } = Channel.CreateUnbounded<FileOffer>();
        public Channel<string> TextB { get; } = Channel.CreateUnbounded<string>();

        public static async Task<Pair> CreateAsync()
        {
            var pair = new Pair();
            System.IO.Directory.CreateDirectory(pair.Downloads);
            pair.A.FileOffered += offer => pair.OffersA.Writer.TryWrite(offer);
            pair.B.FileOffered += offer => pair.OffersB.Writer.TryWrite(offer);
            pair.B.MessageReceived += text => pair.TextB.Writer.TryWrite(text);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.B.ConnectionChanged += peer => { if (peer is not null) connected.TrySetResult(); };
            pair.A.Start(0);
            pair.B.Start(0);
            await pair.A.ConnectAsync("127.0.0.1", pair.B.ListeningPort);
            await connected.Task;
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
