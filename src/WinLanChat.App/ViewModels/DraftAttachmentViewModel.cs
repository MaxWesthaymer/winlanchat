namespace WinLanChat.App.ViewModels;

public sealed record DraftAttachmentViewModel(string Path, long Length)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string FileSize => FileAttachmentViewModel.FormatSize(Length);
}
