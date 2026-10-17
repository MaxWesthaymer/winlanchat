using System.Diagnostics;

namespace WinLanChat.App.Services;

public static class BrowserLauncher
{
    public static bool IsWebAddress(Uri address) => address.IsAbsoluteUri
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(address.Host);

    public static void Open(Uri address)
    {
        if (!IsWebAddress(address))
            throw new ArgumentException("Можно открыть только веб-ссылку HTTP или HTTPS.", nameof(address));

        using Process? process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
