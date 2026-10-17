namespace WinLanChat.Core.Models;

public sealed record FileOffer(Guid Id, string Text, string FileName, long Length);

public enum FileTransferState { Waiting, Transferring, Completed, Rejected, Cancelled, Failed }

public sealed record FileTransferUpdate(Guid Id, FileTransferState State, long BytesTransferred = 0,
    string? Error = null);
