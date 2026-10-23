using System.ComponentModel;
using System.Drawing;
using System.Windows;
using WinLanChat.App.ViewModels;
using Forms = System.Windows.Forms;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

/// <summary>Keeps the messenger running while its window is hidden in the notification area.</summary>
internal sealed class SystemTrayService : IDisposable
{
    private readonly Window _window;
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly MessageNotificationService _messageNotifications;
    private WindowState _restoreState = WindowState.Normal;
    private bool _exitRequested;
    private bool _disposed;

    public SystemTrayService(Window window, MainViewModel viewModel)
    {
        _window = window;
        using var stream = Application.GetResourceStream(
            new Uri("pack://application:,,,/WinLanChat.App;component/Assets/WinLanChat.ico"))!.Stream;
        _icon = new Icon(stream, new System.Drawing.Size(32, 32));
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(Strings.OpenApplication, null, (_, _) => RestoreWindow());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Strings.Exit, null, (_, _) => RequestExit());
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = Strings.TrayTooltip,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        _notifyIcon.BalloonTipClicked += OnNotificationClicked;
        _messageNotifications = new MessageNotificationService(window, viewModel,
            (title, text) => _notifyIcon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info));
        _window.StateChanged += OnStateChanged;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
    }

    public void RestoreWindow()
    {
        if (_disposed || _exitRequested)
            return;

        // Set the state before showing to avoid restoring a minimized window to the taskbar.
        _window.WindowState = _restoreState;
        _window.Show();
        _window.Activate();
        _window.Focus();
    }

    public void RequestExit()
    {
        if (_disposed || _exitRequested)
            return;

        _exitRequested = true;
        _window.Close();
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            RestoreWindow();
    }

    private void OnNotificationClicked(object? sender, EventArgs e) => RestoreWindow();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized)
            _window.Hide();
        else
            _restoreState = _window.WindowState;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        _window.Hide();
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _messageNotifications.Dispose();
        _window.StateChanged -= OnStateChanged;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.BalloonTipClicked -= OnNotificationClicked;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
