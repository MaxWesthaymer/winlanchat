using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinLanChat.App.Models;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

internal static class UiSmoke
{
    internal static Task RunAsync(string? previewDirectory)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new WinLanChat.App.App();
                MainViewModel? runningViewModel = null;
                application.InitializeComponent();
                application.Startup += (_, _) => application.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var window = (WinLanChat.App.MainWindow)application.MainWindow;
                        var viewModel = (MainViewModel)window.DataContext;
                        runningViewModel = viewModel;
                        LocalizationChecks.CheckWindow(window);
                        MessageLinkChecks.CheckRendering(window);
                        // Exercise the actual application lifecycle without a start command.
                        await WaitUntilAsync(() => viewModel.IsListening || viewModel.HasError);
                        if (!viewModel.IsListening)
                        {
                            if (!viewModel.CanEditProfile)
                                throw new Exception("Failed automatic startup did not allow correcting the profile.");
                            using var available = new TcpListener(IPAddress.Any, 0);
                            available.Start();
                            viewModel.LocalPort = ((IPEndPoint)available.LocalEndpoint).Port.ToString();
                            available.Stop();
                            await viewModel.InitializeAsync();
                        }
                        await WaitUntilAsync(() => viewModel.IsListening && viewModel.RefreshPeersCommand.CanExecute(null));
                        if (!viewModel.CanEditProfile || !viewModel.LocalAddresses.Contains(viewModel.LocalPort))
                            throw new Exception("Automatic startup did not update the profile and local address.");

                        var content = (FrameworkElement)window.Content;
                        Render(content, 920, 570, previewDirectory, "window-empty.png");
                        viewModel.Messages.Add(new ChatMessage("MaxHome", "https://ipmsg.org/", false, DateTimeOffset.Now));
                        viewModel.Messages.Add(new ChatMessage("Вы", "жж", true, DateTimeOffset.Now));
                        viewModel.Messages.Add(new ChatMessage("Вы", "вав", true, DateTimeOffset.Now));
                        Render(content, 1100, 690, previewDirectory, "window-compact-messages.png");
                        MessageLinkChecks.CheckCompactBubbles(content);
                        Render(content, 920, 570, previewDirectory, "window-compact-messages-narrow.png");
                        MessageLinkChecks.CheckCompactBubbles(content);
                        viewModel.Messages.Clear();
                        viewModel.Peers.Add(new DiscoveredPeer(Guid.NewGuid(), "Анна", "DESKTOP-ANNA", "192.168.1.21", 45832));
                        viewModel.Peers.Add(new DiscoveredPeer(Guid.NewGuid(), "Борис — рабочий компьютер", "OFFICE-PC", "192.168.1.22", 45832));
                        viewModel.SelectedPeer = viewModel.Peers[0];
                        viewModel.Messages.Add(new ChatMessage("Компьютер Б", "Привет! Проверяем связь в локальной сети. 👋", false, DateTimeOffset.Now));
                        viewModel.Messages.Add(new ChatMessage("Вы", "Привет! Всё работает.\nСсылка: https://example.com/help?q=1\nЕщё сайт: www.example.org.", true, DateTimeOffset.Now));
                        viewModel.Messages.Add(new ChatMessage("Компьютер Б", new string('Ж', 200), false, DateTimeOffset.Now));
                        Render(content, 1100, 690, previewDirectory, "window-conversation.png");
                        viewModel.IsSettingsOpen = true;
                        viewModel.DisplayName += " — новое имя";
                        Render(content, 1100, 690, previewDirectory, "window-settings.png");
                        viewModel.ResetProfileCommand.Execute(null);
                        await UiFileTransferChecks.RunAsync(window, filename =>
                        {
                            Render(content, 1100, 690, previewDirectory, filename);
                            Render(content, 920, 570, previewDirectory, filename.Replace(".png", "-narrow.png"));
                        });
                        await CheckStartupRecoveryAsync(application.Dispatcher);
                        await ProfileChecks.RunAsync(application.Dispatcher);
                        await WindowsStartupChecks.UiAsync(application.Dispatcher);
                        await MessageNotificationChecks.RunAsync(window);
                        await UiTrayChecks.RunAsync(application);
                        application.SystemTray!.RequestExit();
                        application.SystemTray.RequestExit();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                        application.MainWindow?.Close();
                        application.Shutdown();
                    }
                });
                application.Run();
                if (runningViewModel is { IsListening: true } or { IsConnected: true })
                    throw new Exception("Exiting from the tray did not stop networking.");
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CheckStartupRecoveryAsync(System.Windows.Threading.Dispatcher dispatcher)
    {
        using var occupied = new TcpListener(IPAddress.Any, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var viewModel = new MainViewModel(dispatcher) { LocalPort = port.ToString() };
        await viewModel.InitializeAsync();
        if (viewModel.IsListening || !viewModel.HasError || !viewModel.CanEditProfile)
            throw new Exception("An occupied port was not reported as a recoverable startup error.");
        occupied.Stop();
        viewModel.ApplyProfileCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsListening && viewModel.RefreshPeersCommand.CanExecute(null));
        if (viewModel.HasError)
            throw new Exception($"Startup recovery failed: {viewModel.ErrorMessage}");
        // Repeated initialization must preserve the existing listener.
        await viewModel.InitializeAsync();
        await using var peer = new ChatNode("Startup check");
        peer.Start(0);
        await peer.ConnectAsync("127.0.0.1", port);
        await viewModel.DisposeAsync();
        await viewModel.InitializeAsync();
        using var replacement = new TcpListener(IPAddress.Any, port);
        replacement.Start();

        await using var closing = new MainViewModel(dispatcher) { LocalPort = port.ToString() };
        replacement.Stop();
        Task starting = closing.InitializeAsync();
        await closing.DisposeAsync();
        await starting;
        replacement.Start();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static void Render(FrameworkElement content, int width, int height, string? directory, string filename)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (directory is null)
            return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
    }
}
