using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using WinLanChat.App.Models;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

/// <summary>Forwards background messages to the Windows notification area on the UI dispatcher.</summary>
internal sealed class MessageNotificationService : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly Action<string, string> _showNotification;

    public MessageNotificationService(Window window, MainViewModel viewModel, Action<string, string> showNotification)
    {
        _window = window;
        _viewModel = viewModel;
        _showNotification = showNotification;
        _viewModel.IncomingMessageReceived += OnIncomingMessage;
    }

    private void OnIncomingMessage(ChatMessage message)
    {
        if (_window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized)
            return;

        string text = message.Text;
        if (message.Attachment is { } attachment)
            text = message.HasText ? Strings.Format(Strings.FileNotificationWithCaption, attachment.FileName, message.Text) : Strings.Format(Strings.FileNotification, attachment.FileName);
        try
        {
            // NOTIFYICONDATA reserves 64 UTF-16 characters for the title and 256 for the body,
            // including the terminating null. Treat peer text as plain text, never as markup.
            _showNotification(Preview(message.Author, 63, Strings.AppName), Preview(text, 255, Strings.NewMessage));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // An unavailable shell must not interrupt receiving or displaying messages.
            Debug.WriteLine($"Windows notification failed: {ex.Message}");
        }
    }

    private static string Preview(string value, int maximumLength, string fallback)
    {
        string text = string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        if (text.Length == 0)
            return fallback;
        if (text.Length <= maximumLength)
            return text;

        int length = maximumLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
            length--;
        return text[..length] + "…";
    }

    public void Dispose() => _viewModel.IncomingMessageReceived -= OnIncomingMessage;
}
