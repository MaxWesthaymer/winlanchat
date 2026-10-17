using WinLanChat.App.ViewModels;

namespace WinLanChat.App.Models;

public sealed record ChatMessage(string Author, string Text, bool IsOutgoing, DateTimeOffset CreatedAt)
{
    public string Time => CreatedAt.ToLocalTime().ToString("HH:mm");
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public FileAttachmentViewModel? Attachment { get; init; }
    public bool HasAttachment => Attachment is not null;
}
