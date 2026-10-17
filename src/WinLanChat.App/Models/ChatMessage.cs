namespace WinLanChat.App.Models;

public sealed record ChatMessage(string Author, string Text, bool IsOutgoing, DateTimeOffset CreatedAt)
{
    public string Time => CreatedAt.ToLocalTime().ToString("HH:mm");
}
