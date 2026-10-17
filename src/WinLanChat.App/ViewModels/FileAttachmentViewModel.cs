using WinLanChat.App.Infrastructure;
using WinLanChat.Core.Models;

namespace WinLanChat.App.ViewModels;

public sealed class FileAttachmentViewModel : ObservableObject
{
    private FileTransferState _state = FileTransferState.Waiting;
    private long _bytesTransferred;
    private string? _error;
    private string? _savedPath;

    public FileAttachmentViewModel(FileOffer offer, bool isOutgoing, Func<Task> save, Func<Task> reject,
        Func<Task> cancel, Action<Exception> onError)
    {
        Offer = offer;
        IsOutgoing = isOutgoing;
        SaveCommand = new AsyncCommand(save, onError, () => CanAccept);
        RejectCommand = new AsyncCommand(reject, onError, () => CanAccept);
        CancelCommand = new AsyncCommand(cancel, onError, () => CanCancel);
    }

    public FileOffer Offer { get; }
    public Guid Id => Offer.Id;
    public string FileName => Offer.FileName;
    public string FileSize => FormatSize(Offer.Length);
    public bool IsOutgoing { get; }
    public FileTransferState State => _state;
    public bool CanAccept => !IsOutgoing && State == FileTransferState.Waiting;
    public bool CanCancel => State == FileTransferState.Transferring || IsOutgoing && State == FileTransferState.Waiting;
    public bool IsTransferring => State == FileTransferState.Transferring;
    public double Progress => Offer.Length == 0 ? State == FileTransferState.Completed ? 100 : 0
        : Math.Clamp(100d * _bytesTransferred / Offer.Length, 0, 100);
    public string? SavedPath { get => _savedPath; set { if (Set(ref _savedPath, value)) OnPropertyChanged(nameof(SavedLocation)); } }
    public string SavedLocation => State == FileTransferState.Completed && !IsOutgoing ? SavedPath ?? "" : "";
    public string Status => State switch
    {
        FileTransferState.Waiting => IsOutgoing ? "Ожидание согласия получателя…" : "Сохранить файл на этот компьютер?",
        FileTransferState.Transferring => $"{(IsOutgoing ? "Отправка" : "Получение")} · {Progress:0}% · {FormatSize(_bytesTransferred)} / {FileSize}",
        FileTransferState.Completed => IsOutgoing ? "Файл передан" : "Файл сохранён",
        FileTransferState.Rejected => "Файл отклонён",
        FileTransferState.Cancelled => "Передача отменена",
        _ => "Ошибка передачи: " + _error
    };
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RejectCommand { get; }
    public AsyncCommand CancelCommand { get; }

    public void Update(FileTransferUpdate update)
    {
        if (State is FileTransferState.Completed or FileTransferState.Rejected or FileTransferState.Cancelled or FileTransferState.Failed)
            return;
        _state = update.State;
        _bytesTransferred = update.BytesTransferred;
        _error = update.Error;
        foreach (string property in new[] { nameof(State), nameof(Status), nameof(Progress), nameof(CanAccept),
            nameof(CanCancel), nameof(IsTransferring), nameof(SavedLocation) })
            OnPropertyChanged(property);
        SaveCommand.Refresh();
        RejectCommand.Refresh();
        CancelCommand.Refresh();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} МБ",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} ГБ"
    };
}
