using System.ComponentModel;

namespace WinLanChat.App.ViewModels;

public sealed record DraftAttachmentViewModel(string Path, long Length) : INotifyPropertyChanged
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string FileSize => FileAttachmentViewModel.FormatSize(Length);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSize)));
}
