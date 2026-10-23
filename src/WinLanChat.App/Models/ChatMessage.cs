using WinLanChat.App.ViewModels;
using WinLanChat.Core.Localization;
using System.ComponentModel;

namespace WinLanChat.App.Models;

public sealed record ChatMessage(string Author, string Text, bool IsOutgoing, DateTimeOffset CreatedAt) : INotifyPropertyChanged
{
    public string DisplayAuthor => IsOutgoing ? Strings.SelfAuthor : Author;
    public string Time => CreatedAt.ToLocalTime().ToString(Strings.MessageTimeFormat);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public FileAttachmentViewModel? Attachment { get; init; }
    public bool HasAttachment => Attachment is not null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayAuthor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Time)));
    }
}
