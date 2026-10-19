using System.Windows;
using System.Reflection;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Networking;

internal static class UiTrayChecks
{
    internal static async Task RunAsync(WinLanChat.App.App application)
    {
        var window = application.MainWindow;
        var viewModel = (MainViewModel)window.DataContext;
        var tray = application.SystemTray ?? throw new Exception("The application did not create a tray icon.");
        bool closed = false;
        void OnClosed(object? sender, EventArgs e) => closed = true;
        window.Closed += OnClosed;
        try
        {
            window.WindowState = WindowState.Normal;
            window.WindowState = WindowState.Minimized;
            if (window.IsVisible || closed || !viewModel.IsListening)
                throw new Exception("Minimizing did not hide the window and preserve the listener.");
            tray.RestoreWindow();
            if (!window.IsVisible || window.WindowState != WindowState.Normal)
                throw new Exception("The tray did not restore the normal window.");

            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            tray.RestoreWindow();
            if (!window.IsVisible || window.WindowState != WindowState.Maximized)
                throw new Exception("The tray lost the maximized window state.");

            window.Close();
            window.Close();
            if (window.IsVisible || closed || !viewModel.IsListening)
                throw new Exception("Closing the window shut down the messenger instead of hiding it.");

            await using (var peer = new ChatNode("Tray test peer"))
            {
                peer.Start(0);
                await peer.ConnectAsync("127.0.0.1", int.Parse(viewModel.LocalPort));
                await peer.SendAsync("Message received while hidden in the system tray");
                await UntilAsync(() => viewModel.Messages.Any(message =>
                    message.Text == "Message received while hidden in the system tray"));
                if (window.IsVisible || !viewModel.IsConnected)
                    throw new Exception("Background chat changed window visibility or lost its connection.");
                // Raise the native adapter's click event to exercise the actual subscription.
                object icon = typeof(SystemTrayService).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(tray)!;
                icon.GetType().GetMethod("OnBalloonTipClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(icon, null);
                if (!window.IsVisible || !viewModel.IsConnected)
                    throw new Exception("Clicking a notification did not restore the window and preserve the conversation.");
            }
            await UntilAsync(() => !viewModel.IsConnected);
        }
        finally
        {
            window.Closed -= OnClosed;
            tray.RestoreWindow();
            window.WindowState = WindowState.Normal;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
}
