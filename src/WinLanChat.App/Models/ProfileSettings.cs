using WinLanChat.Core.Localization;

namespace WinLanChat.App.Models;

public sealed record ProfileSettings(string DisplayName, int ListeningPort)
{
    public string LanguageCode { get; init; } = "ru";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 64 || DisplayName.Any(char.IsControl))
            throw new ArgumentException(Strings.InvalidDisplayName);
        if (ListeningPort is < 1 or > 65535)
            throw new ArgumentException(Strings.InvalidPort);
        if (!Strings.IsSupportedLanguage(LanguageCode))
            throw new ArgumentException(Strings.InvalidLanguage);
    }
}
