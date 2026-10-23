using System.Diagnostics;
using System.Net.Sockets;
using WinLanChat.Core.Models;
using WinLanChat.Core.Localization;

namespace WinLanChat.Core.Networking;

public sealed partial class ChatNode
{
    public const int MaximumPendingTransfers = 16;
    private readonly Dictionary<Guid, OutgoingTransfer> _outgoingFiles = [];
    private readonly Dictionary<Guid, IncomingTransfer> _incomingFiles = [];

    public event Action<FileOffer>? FileOffered;
    public event Action<FileTransferUpdate>? FileTransferChanged;

    private sealed class OutgoingTransfer(TcpClient client, CancellationTokenSource cancellation)
    {
        public TcpClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class IncomingTransfer(TcpClient client, FileOffer offer)
    {
        public TcpClient Client { get; } = client;
        public FileOffer Offer { get; } = offer;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Idle { get; } = new();
        public FileStream? Stream { get; set; }
        public string? TemporaryPath { get; set; }
        public string? DestinationPath { get; set; }
        public bool Overwrite { get; set; }
        public bool Finished { get; set; }
        public long Received { get; set; }
        public long LastProgress { get; set; }
    }

    public Task SendFileAsync(Guid id, string path, string text = "", CancellationToken cancellationToken = default,
        long? expectedLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        if (id == Guid.Empty || text.Length > MaximumMessageLength)
            throw new ArgumentException(Strings.InvalidFileMessage);
        string name = Path.GetFileName(path);
        if (!ChatProtocol.IsValidFileName(name))
            throw new ArgumentException(Strings.InvalidFileName, nameof(path));
        OutgoingTransfer transfer;
        lock (_sync)
        {
            TcpClient client = _connectedClient ?? throw new InvalidOperationException(Strings.PeerNotConnected);
            if (!_peerSupportsFiles)
                throw new InvalidOperationException(Strings.PeerFileSupportRequired);
            if (_outgoingFiles.Count >= MaximumPendingTransfers || _outgoingFiles.ContainsKey(id) || _incomingFiles.ContainsKey(id))
                throw new InvalidOperationException(Strings.TooManyPendingFiles);
            transfer = new OutgoingTransfer(client, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken));
            _outgoingFiles.Add(id, transfer);
        }
        Task task = SendFileCoreAsync(id, path, name, text, transfer, expectedLength);
        Track(task);
        return task;
    }

    private async Task SendFileCoreAsync(Guid id, string path, string name, string text, OutgoingTransfer transfer, long? expectedLength)
    {
        using var cancellation = transfer.Cancellation;
        TcpClient client = transfer.Client;
        long sent = 0;
        try
        {
            // File opens can block on slow disks/network shares; keep them off the caller's UI thread.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            cancellation.Token.ThrowIfCancellationRequested();
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChatProtocol.FileChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > ChatProtocol.MaximumFileBytes)
                throw new ArgumentException(Strings.FileTooLarge, nameof(path));
            if (expectedLength is { } expected && file.Length != expected)
                throw new IOException(Strings.AttachedFileChanged);
            cancellation.Token.ThrowIfCancellationRequested();
            await WriteFileFrameAsync(client, new WireMessage("file-offer", Text: text,
                TransferId: id, FileName: name, FileSize: file.Length)).ConfigureAwait(false);
            FileTransferChanged?.Invoke(new(id, FileTransferState.Waiting));
            if (!await transfer.Accepted.Task.WaitAsync(cancellation.Token).ConfigureAwait(false))
            {
                FileTransferChanged?.Invoke(new(id, FileTransferState.Rejected));
                return;
            }
            FileTransferChanged?.Invoke(new(id, FileTransferState.Transferring));
            long lastProgress = Stopwatch.GetTimestamp();
            byte[] buffer = new byte[ChatProtocol.FileChunkBytes];
            while (sent < file.Length)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Length - sent)),
                    cancellation.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException(Strings.SourceFileChanged);
                cancellation.Token.ThrowIfCancellationRequested();
                await WriteFileFrameAsync(client, new WireMessage("file-chunk", TransferId: id,
                    Data: read == buffer.Length ? buffer : buffer[..read])).ConfigureAwait(false);
                sent += read;
                if (sent == file.Length || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 100)
                {
                    lastProgress = Stopwatch.GetTimestamp();
                    FileTransferChanged?.Invoke(new(id, FileTransferState.Transferring, sent));
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            await WriteFileFrameAsync(client, new WireMessage("file-end", TransferId: id)).ConfigureAwait(false);
            await transfer.Saved.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellation.Token).ConfigureAwait(false);
            FileTransferChanged?.Invoke(new(id, FileTransferState.Completed, sent));
        }
        catch (Exception ex)
        {
            await TrySendFileCancelAsync(client, id).ConfigureAwait(false);
            FileTransferChanged?.Invoke(new(id, ex is OperationCanceledException
                ? FileTransferState.Cancelled : FileTransferState.Failed, sent, ex.Message));
            throw;
        }
        finally
        {
            lock (_sync) _outgoingFiles.Remove(id);
        }
    }

    /// <summary>Call only after the user selects and approves the destination. Existing files are preserved unless overwrite is true.</summary>
    public Task AcceptFileAsync(Guid id, string destinationPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        IncomingTransfer transfer;
        lock (_sync)
            transfer = _incomingFiles.GetValueOrDefault(id)
                ?? throw new InvalidOperationException(Strings.FileOfferUnavailable);
        Task task = AcceptFileCoreAsync(transfer, Path.GetFullPath(destinationPath), overwrite, cancellationToken);
        Track(task);
        return task;
    }

    private async Task AcceptFileCoreAsync(IncomingTransfer transfer, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken, transfer.Idle.Token);
        await transfer.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool started = false;
        try
        {
            if (transfer.Finished || transfer.DestinationPath is not null)
                throw new InvalidOperationException(Strings.FileOfferAlreadyHandled);
            started = true;
            if (!overwrite && File.Exists(destination))
                throw new IOException(Strings.DestinationAlreadyExists);
            transfer.DestinationPath = destination;
            transfer.Overwrite = overwrite;
            transfer.TemporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, $".winlanchat-{Guid.NewGuid():N}.part");
            transfer.Stream = new FileStream(transfer.TemporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, ChatProtocol.FileChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            transfer.Idle.CancelAfter(TimeSpan.FromSeconds(45));
            await WriteFileFrameAsync(transfer.Client, new WireMessage("file-accept", TransferId: transfer.Offer.Id)).ConfigureAwait(false);
            FileTransferChanged?.Invoke(new(transfer.Offer.Id, FileTransferState.Transferring));
        }
        catch (Exception ex)
        {
            if (started)
            {
                await FinishIncomingAsync(transfer, FileTransferState.Failed, ex.Message).ConfigureAwait(false);
                await TrySendFileCancelAsync(transfer.Client, transfer.Offer.Id).ConfigureAwait(false);
            }
            throw;
        }
        finally { transfer.Gate.Release(); }
        try
        {
            await transfer.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelFileAsync(transfer.Offer.Id).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RejectFileAsync(Guid id)
    {
        IncomingTransfer? transfer;
        lock (_sync) transfer = _incomingFiles.GetValueOrDefault(id);
        if (transfer is null) return;
        await transfer.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (transfer.Finished || transfer.DestinationPath is not null) return;
            await WriteFileFrameAsync(transfer.Client, new WireMessage("file-reject", TransferId: id)).ConfigureAwait(false);
            await FinishIncomingAsync(transfer, FileTransferState.Rejected).ConfigureAwait(false);
        }
        finally { transfer.Gate.Release(); }
    }

    public async Task CancelFileAsync(Guid id)
    {
        IncomingTransfer? incoming;
        lock (_sync)
        {
            if (_outgoingFiles.TryGetValue(id, out var outgoing))
                outgoing.Cancellation.Cancel();
            incoming = _incomingFiles.GetValueOrDefault(id);
        }
        if (incoming is null) return;
        await incoming.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (incoming.Finished) return;
            await FinishIncomingAsync(incoming, FileTransferState.Cancelled).ConfigureAwait(false);
            await TrySendFileCancelAsync(incoming.Client, id).ConfigureAwait(false);
        }
        finally { incoming.Gate.Release(); }
    }

    private async Task<bool> HandleFileMessageAsync(TcpClient client, WireMessage message)
    {
        if (message.Kind is null || !message.Kind.StartsWith("file-", StringComparison.Ordinal)) return false;
        if (!PeerSupportsFiles || message.TransferId is not { } id || id == Guid.Empty)
            throw new InvalidDataException(Strings.InvalidTransferId);
        if (message.Kind == "file-offer")
        {
            if (!ChatProtocol.IsValidFileName(message.FileName) || message.FileSize is not (>= 0 and <= ChatProtocol.MaximumFileBytes)
                || message.Text is null || message.Text.Length > MaximumMessageLength || message.Data is not null)
                throw new InvalidDataException(Strings.InvalidFileOffer);
            var offer = new FileOffer(id, message.Text, message.FileName!, message.FileSize.Value);
            bool full;
            lock (_sync)
            {
                if (_incomingFiles.ContainsKey(id) || _outgoingFiles.ContainsKey(id))
                    throw new InvalidDataException(Strings.DuplicateTransferId);
                full = _incomingFiles.Count >= MaximumPendingTransfers;
                if (!full) _incomingFiles.Add(id, new IncomingTransfer(client, offer));
            }
            if (full)
                await WriteFileFrameAsync(client, new WireMessage("file-reject", TransferId: id)).ConfigureAwait(false);
            else
                FileOffered?.Invoke(offer);
            return true;
        }
        OutgoingTransfer? outgoing;
        IncomingTransfer? incoming;
        lock (_sync)
        {
            outgoing = _outgoingFiles.GetValueOrDefault(id);
            incoming = _incomingFiles.GetValueOrDefault(id);
        }
        switch (message.Kind)
        {
            case "file-accept": outgoing?.Accepted.TrySetResult(true); return true;
            case "file-reject":
                if (outgoing is not null && !outgoing.Accepted.TrySetResult(false)) outgoing.Cancellation.Cancel();
                return true;
            case "file-saved": outgoing?.Saved.TrySetResult(); return true;
            case "file-cancel":
                if (outgoing is not null) outgoing.Cancellation.Cancel();
                if (incoming is not null)
                {
                    await incoming.Gate.WaitAsync().ConfigureAwait(false);
                    try { await FinishIncomingAsync(incoming, FileTransferState.Cancelled).ConfigureAwait(false); }
                    finally { incoming.Gate.Release(); }
                }
                return true;
            case "file-chunk":
                if (message.Data is not { Length: > 0 and <= ChatProtocol.FileChunkBytes })
                    throw new InvalidDataException(Strings.InvalidFileChunk);
                break;
            case "file-end": break;
            default: return false;
        }
        // A cancelled transfer can still have frames in flight. Discard these bounded frames.
        if (incoming is null) return true;
        await incoming.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (incoming.Finished) return true;
            if (incoming.Stream is null)
                throw new InvalidDataException(Strings.FileConsentRequired);
            try
            {
                if (message.Kind == "file-chunk")
                {
                    if (message.Data!.Length > incoming.Offer.Length - incoming.Received)
                        throw new InvalidDataException(Strings.FileExceedsDeclaredSize);
                    await incoming.Stream.WriteAsync(message.Data, _shutdown.Token).ConfigureAwait(false);
                    incoming.Received += message.Data.Length;
                    incoming.Idle.CancelAfter(TimeSpan.FromSeconds(45));
                    if (incoming.Received == incoming.Offer.Length || Stopwatch.GetElapsedTime(incoming.LastProgress).TotalMilliseconds >= 100)
                    {
                        incoming.LastProgress = Stopwatch.GetTimestamp();
                        FileTransferChanged?.Invoke(new(id, FileTransferState.Transferring, incoming.Received));
                    }
                }
                else
                {
                    if (incoming.Received != incoming.Offer.Length)
                        throw new InvalidDataException(Strings.IncompleteFile);
                    await incoming.Stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                    await incoming.Stream.DisposeAsync().ConfigureAwait(false);
                    incoming.Stream = null;
                    File.Move(incoming.TemporaryPath!, incoming.DestinationPath!, incoming.Overwrite);
                    incoming.TemporaryPath = null;
                    await FinishIncomingAsync(incoming, FileTransferState.Completed).ConfigureAwait(false);
                    await WriteFileFrameAsync(client, new WireMessage("file-saved", TransferId: id)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FinishIncomingAsync(incoming, FileTransferState.Failed, ex.Message).ConfigureAwait(false);
                await TrySendFileCancelAsync(client, id).ConfigureAwait(false);
            }
        }
        finally { incoming.Gate.Release(); }
        return true;
    }

    // The caller holds Gate. Publish completion only after closing/removing the partial file.
    private async Task FinishIncomingAsync(IncomingTransfer transfer, FileTransferState state, string? error = null)
    {
        if (transfer.Finished) return;
        transfer.Finished = true;
        lock (_sync) _incomingFiles.Remove(transfer.Offer.Id);
        transfer.Idle.CancelAfter(Timeout.InfiniteTimeSpan);
        try
        {
            if (transfer.Stream is not null) await transfer.Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            transfer.Stream = null;
            if (transfer.TemporaryPath is not null)
            {
                try { File.Delete(transfer.TemporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
            }
            FileTransferChanged?.Invoke(new(transfer.Offer.Id, state, transfer.Received, error));
            transfer.Completion.TrySetResult();
        }
    }

    private async Task WriteFileFrameAsync(TcpClient client, WireMessage message)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
                if (!ReferenceEquals(_connectedClient, client)) throw new IOException(Strings.PeerDisconnected);
            // Complete each frame even when the user cancels a transfer, keeping the conversation usable.
            await ChatProtocol.WriteAsync(client.GetStream(), message, timeout.Token).ConfigureAwait(false);
        }
        catch { client.Dispose(); throw; }
        finally { _sendGate.Release(); }
    }

    private async Task TrySendFileCancelAsync(TcpClient client, Guid id)
    {
        try { await WriteFileFrameAsync(client, new WireMessage("file-cancel", TransferId: id)).ConfigureAwait(false); }
        catch (Exception ex) when (IsNetworkError(ex) || ex is InvalidOperationException) { }
    }

    private async Task CloseFileTransfersAsync(TcpClient client)
    {
        IncomingTransfer[] incoming;
        lock (_sync)
        {
            foreach (var outgoing in _outgoingFiles.Values.Where(t => ReferenceEquals(t.Client, client)))
                outgoing.Cancellation.Cancel();
            incoming = _incomingFiles.Values.Where(t => ReferenceEquals(t.Client, client)).ToArray();
        }
        foreach (var transfer in incoming)
        {
            await transfer.Gate.WaitAsync().ConfigureAwait(false);
            try { await FinishIncomingAsync(transfer, FileTransferState.Failed, Strings.PeerDisconnected).ConfigureAwait(false); }
            finally { transfer.Gate.Release(); }
        }
    }
}
