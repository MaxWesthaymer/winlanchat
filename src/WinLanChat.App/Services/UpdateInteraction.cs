using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

public interface IUpdateInteraction
{
    bool ConfirmInstallation(string version);
    void StartInstaller(string path);
}

public sealed class UpdateInteraction : IUpdateInteraction
{
    public bool ConfirmInstallation(string version) => MessageBox.Show(Application.Current.MainWindow,
        Strings.Format(Strings.UpdateConfirmInstall, version), Strings.Updates,
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void StartInstaller(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{EBC38F35-3311-4F6C-9014-3AF4E43E3D51}_is1");
        string? installedDirectory = key?.GetValue("InstallLocation") as string;
        string? executable = Environment.ProcessPath;
        if (installedDirectory is null || executable is null
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedDirectory)),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(executable), "WinLanChat.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Strings.UpdateInstalledOnly);

        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.ArgumentList.Add("/UPDATE=1");
        start.ArgumentList.Add($"/WAITFORPID={Environment.ProcessId}");
        start.ArgumentList.Add($"/DIR={installedDirectory}");
        start.ArgumentList.Add(Strings.LanguageCode == "ru" ? "/LANG=russian" : "/LANG=english");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/SP-");
        using var process = Process.Start(start) ?? throw new IOException(Strings.UpdateLaunchFailed);
    }
}
