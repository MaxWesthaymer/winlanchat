using Microsoft.Win32;
using System.Windows;

namespace WinLanChat.App.Services;

public interface IFileDialogs
{
    string? ChooseAttachment();
    string? ChooseDestination(string fileName);
}

public sealed class FileDialogs : IFileDialogs
{
    public string? ChooseAttachment()
    {
        var dialog = new OpenFileDialog { Title = "Прикрепить файл", CheckFileExists = true, Multiselect = false };
        return Show(dialog) ? dialog.FileName : null;
    }

    public string? ChooseDestination(string fileName)
    {
        var dialog = new SaveFileDialog { Title = "Сохранить полученный файл", FileName = fileName,
            Filter = "Все файлы (*.*)|*.*", AddExtension = false, OverwritePrompt = true };
        return Show(dialog) ? dialog.FileName : null;
    }

    private static bool Show(CommonDialog dialog) => Application.Current?.MainWindow is { } owner
        ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
}
