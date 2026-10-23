using Microsoft.Win32;
using System.Windows;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

public interface IFileDialogs
{
    string[]? ChooseAttachments();
    string? ChooseDestination(string fileName);
}

public sealed class FileDialogs : IFileDialogs
{
    public string[]? ChooseAttachments()
    {
        var dialog = new OpenFileDialog { Title = Strings.AttachFiles, CheckFileExists = true, Multiselect = true };
        return Show(dialog) ? dialog.FileNames : null;
    }

    public string? ChooseDestination(string fileName)
    {
        var dialog = new SaveFileDialog { Title = Strings.SaveReceivedFile, FileName = fileName,
            Filter = Strings.AllFilesFilter, AddExtension = false, OverwritePrompt = true };
        return Show(dialog) ? dialog.FileName : null;
    }

    private static bool Show(CommonDialog dialog) => Application.Current?.MainWindow is { } owner
        ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
}
