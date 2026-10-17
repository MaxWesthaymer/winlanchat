namespace WinLanChat.App.Models;

public sealed record ProfileSettings(string DisplayName, int ListeningPort)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 64 || DisplayName.Any(char.IsControl))
            throw new ArgumentException("Имя должно содержать от 1 до 64 символов без управляющих знаков.");
        if (ListeningPort is < 1 or > 65535)
            throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
    }
}
