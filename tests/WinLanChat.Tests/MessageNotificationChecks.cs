using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Networking;

internal static class MessageNotificationChecks
{
    internal static async Task RunAsync(Window owner)
    {
        var window = new Window { Owner = owner, Width = 360, Height = 240, ShowInTaskbar = false };
        using var available = new TcpListener(IPAddress.Loopback, 0);
        available.Start();
        int port = ((IPEndPoint)available.LocalEndpoint).Port;
        available.Stop();
        await using var viewModel = new MainViewModel(window.Dispatcher) { LocalPort = port.ToString() };
        await viewModel.InitializeAsync();
        var notifications = new List<(string Title, string Body)>();
        using var service = new MessageNotificationService(window, viewModel,
            (title, body) => notifications.Add((title, body)));
        await using var peer = new ChatNode(new string('А', 64));
        peer.Start(0);
        await peer.ConnectAsync("127.0.0.1", port);
        await UntilAsync(() => viewModel.IsConnected);
        string directory = Path.Combine(Path.GetTempPath(), $"WinLanChat-notifications-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            window.Hide();
            await ReceiveAsync("Привет 👋\nВторая строка\0");
            if (notifications.Count != 1 || notifications[0].Title != new string('А', 62) + "…"
                || notifications[0].Body != "Привет 👋 Вторая строка" || window.IsVisible)
                throw new Exception("A background message did not produce a safe sender/preview or opened the window.");

            viewModel.Draft = "Исходящее сообщение";
            viewModel.SendCommand.Execute(null);
            await UntilAsync(() => viewModel.Messages.Any(message => message.IsOutgoing));
            if (notifications.Count != 1)
                throw new Exception("An outgoing message produced a notification.");

            await ReceiveAsync(new string('x', 253) + "😀" + new string('y', 1000));
            if (notifications.Count != 2 || notifications[1].Body != new string('x', 253) + "…")
                throw new Exception("A long preview was not bounded or split an emoji surrogate pair.");

            string source = Path.Combine(directory, "Отчёт.txt");
            await File.WriteAllTextAsync(source, "File consent check");
            foreach (string caption in new[] { "", "Подпись к файлу" })
            {
                Guid id = Guid.NewGuid();
                int previous = notifications.Count;
                Task sending = peer.SendFileAsync(id, source, caption);
                await UntilAsync(() => viewModel.Messages.Any(message => message.Attachment?.Id == id));
                if (notifications.Count != previous + 1 || !notifications[^1].Body.Contains("Файл: Отчёт.txt")
                    || !notifications[^1].Body.Contains(caption) || Directory.GetFiles(directory).Length != 1)
                    throw new Exception("An incoming file did not produce one notification or was saved without consent.");
                if (caption.Length == 0 && notifications[^1].Body != "Файл: Отчёт.txt")
                    throw new Exception("A file-only notification contained an empty caption separator.");
                viewModel.Messages.Single(message => message.Attachment?.Id == id).Attachment!.RejectCommand.Execute(null);
                await sending;
                if (notifications.Count != previous + 1)
                    throw new Exception("A transfer status update produced a duplicate notification.");
            }

            window.Show();
            window.Activate();
            await UntilAsync(() => window.IsActive);
            int beforeForeground = notifications.Count;
            await ReceiveAsync("Сообщение в активном окне");
            if (notifications.Count != beforeForeground)
                throw new Exception("An active conversation produced a redundant notification.");

            var otherWindow = new Window { Owner = window, Width = 200, Height = 100, ShowInTaskbar = false };
            try
            {
                otherWindow.Show();
                otherWindow.Activate();
                await UntilAsync(() => !window.IsActive);
                await ReceiveAsync("Сообщение в неактивном, но видимом окне");
                if (notifications.Count != beforeForeground + 1)
                    throw new Exception("A visible inactive conversation did not produce a notification.");
            }
            finally { otherWindow.Close(); }

            int beforeMinimize = notifications.Count;
            window.WindowState = WindowState.Minimized;
            await ReceiveAsync("Сообщение при свёрнутом окне");
            if (notifications.Count != beforeMinimize + 1)
                throw new Exception("A minimized conversation did not produce a notification.");
            window.WindowState = WindowState.Normal;
            window.Hide();
            service.Dispose();
            int beforeDispose = notifications.Count;
            await ReceiveAsync("После отключения уведомлений");
            if (notifications.Count != beforeDispose)
                throw new Exception("A disposed notification service remained subscribed.");

            int failedAttempts = 0;
            using var unavailable = new MessageNotificationService(window, viewModel, (_, _) =>
            {
                failedAttempts++;
                throw new Win32Exception("Shell is unavailable");
            });
            await ReceiveAsync("Ошибка оболочки не прерывает переписку");
            await ReceiveAsync("Следующее сообщение также получено");
            if (failedAttempts != 2)
                throw new Exception("A shell failure interrupted later message processing.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            window.Close();
        }

        async Task ReceiveAsync(string text)
        {
            int count = viewModel.Messages.Count;
            await peer.SendAsync(text);
            await UntilAsync(() => viewModel.Messages.Count == count + 1);
            if (viewModel.Messages[^1].Text != text)
                throw new Exception("Notification formatting altered the original chat message.");
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
